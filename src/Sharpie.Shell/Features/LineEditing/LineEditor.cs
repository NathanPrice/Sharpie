using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.Highlighting;

namespace Sharpie.Shell.Features.LineEditing;

/// <summary>
/// Reads one command line from the terminal a key at a time, coloring it as it's typed, with Tab completion,
/// Up and Down through the history lines that start with what's typed, a dimmed suggestion after the cursor from
/// history or from the command names, Ctrl+R and Ctrl+S to search the history as in bash, and the usual editing keys.
/// Text the terminal marks as pasted goes into the line whole, line breaks included, and waits for Enter.
/// </summary>
/// <param name="readKey">Reads the next key without echoing it, like <see cref="Console.ReadKey(bool)" />.</param>
/// <param name="output">Where the prompt and the line are drawn.</param>
/// <param name="columns">Gives the terminal's width in columns, read again on every redraw so a resize is picked up.</param>
/// <param name="completer">Decides what each Tab press does.</param>
/// <param name="history">The earlier lines, oldest first, which Up and Down search.</param>
/// <param name="builtins">
/// The builtin command names, which the coloring counts as commands and the suggestion offers along with the programs
/// on <c>PATH</c>.
/// </param>
/// <param name="editInEditor">
/// Opens the line in the user's editor for Ctrl+X Ctrl+E and gives back the edited text, or <see langword="null" />
/// when the editor failed. Without it, Ctrl+X Ctrl+E does nothing.
/// </param>
/// <remarks>
/// The whole input is redrawn after every key. The cursor first goes back up to the prompt's row, everything below
/// is erased, and the prompt, the colored line and any list of matches are written again. A line break in the line
/// starts the next row at the left edge. Characters, tabs included, are counted as one column each, so a line holding
/// wide characters like CJK or emoji puts the cursor short of where it should be.
/// </remarks>
internal sealed partial class LineEditor(
	Func<ConsoleKeyInfo> readKey,
	TextWriter output,
	Func<int> columns,
	TabCompleter completer,
	IReadOnlyList<string> history,
	IReadOnlyCollection<string> builtins,
	Func<string, string?>? editInEditor = null)
{
	// The translucent accent the window draws selections with, mixed onto its background, for the picked name in a
	// list of matches.
	private const string SelectedName = "\e[48;2;56;74;102m";

	// What bash's PS2 shows before each line of a here-document's body.
	internal const string ContinuationPrompt = "> ";

	private readonly StringBuilder line = new();

	// Kept across redraws, so a redraw doesn't grow a new builder from scratch on every key.
	private readonly StringBuilder screen = new();

	// The line and cursor before each change to this line, newest on top, for Ctrl+_.
	private readonly Stack<(string Line, int Cursor)> undo = new();

	// Whether each command name typed on this line is a command. The highlighter would otherwise search PATH for it on
	// every key. Cleared for each new line, so a program installed since the last prompt is found.
	private readonly Dictionary<string, bool> knownCommands = new(StringComparer.Ordinal);

	private int cursor;
	private int cursorRow;
	private int recalled;
	private string draft = string.Empty;
	private IReadOnlyList<string>? listed;
	private int? selected;
	private string suggestion = string.Empty;
	private bool continuation;

	// The prompt this line is read after, markers removed, and the columns it takes.
	private string prompt = string.Empty;
	private int promptWidth;

	// What the key before this one left for it to act on.
	private LastKey last;

	// What the last kill removed, kept across lines as readline's kill ring is, for Ctrl+Y.
	private string killed = string.Empty;

	// Set by Ctrl+_, so putting the old line back isn't saved as a change of its own.
	private bool undoing;

	// The history line Alt+. last took a word from, and the length of that word, so a second Alt+. can swap it for
	// the last word of an older line.
	private int lastArgumentLine;
	private int lastArgumentLength;

	// The command names that start with commandPrefix. Narrowed in memory as the command word grows, so suggesting a
	// command doesn't search PATH on every key. Cleared for each new line, so a program installed since is found.
	private List<string>? commandMatches;
	private string commandPrefix = string.Empty;

	/// <summary>
	/// Gets a value indicating whether the line still shows a history line that Up or Down put there, unedited.
	/// </summary>
	/// <value>
	/// <see langword="false" /> once any key changes the line, which makes the next Up search again from the changed text.
	/// </value>
	private bool Browsing => this.recalled < history.Count && this.line.Equals(history[this.recalled].AsSpan());

	/// <summary>
	/// Draws <paramref name="header" /> and the prompt, then reads keys until Enter.
	/// </summary>
	/// <param name="header">The text printed once above the prompt, ending with a line break.</param>
	/// <param name="continuation">
	/// <see langword="true" /> for a line of a here-document's body, which gets <c>&gt; </c> as its prompt and no
	/// coloring or suggestion, since it's text rather than a command.
	/// </param>
	/// <param name="prompt">
	/// What's drawn in front of the line, with any text that takes no columns between <c>\x01</c> and <c>\x02</c>.
	/// Unused for a <paramref name="continuation" /> line.
	/// </param>
	/// <returns>
	/// The line, an empty line after Ctrl+C, or <see langword="null" /> after Ctrl+D on an empty line, which ends
	/// the shell as end of input does. For a <paramref name="continuation" /> line, Ctrl+C also gives
	/// <see langword="null" />, so the caller can drop the whole command.
	/// </returns>
	public string? ReadLine(string header, bool continuation = false, string prompt = PromptHeader.Symbol)
	{
		_ = header.ArgumentNotNull();

		this.continuation = continuation;
		(this.prompt, this.promptWidth) = PromptString.Visible(continuation ? ContinuationPrompt : prompt);
		_ = this.line.Clear();
		this.cursor = 0;
		this.cursorRow = 0;
		this.recalled = history.Count;
		this.listed = null;
		this.last = LastKey.Other;
		this.undo.Clear();
		this.query = null;
		this.knownCommands.Clear();
		this.commandMatches = null;

		output.Write(header);
		this.Render();

		var escaped = false;

		while (true)
		{
			var key = readKey();

			if (key.Key != ConsoleKey.Tab)
			{
				completer.Reset();
				this.listed = null;
			}

			var before = this.line.ToString();
			var cursorBefore = this.cursor;

			// Checked here rather than in Handle, because the search swallows Escape on purpose and would then type the
			// rest of the sequence into the line. .NET gives ESC [ as Alt+[ when they are the only two bytes of a read.
			var outcome = key.KeyChar == '[' && (escaped || (key.Modifiers & ConsoleModifiers.Alt) != 0) ? this.ReadSequence()
				: this.Handle(key, header);

			escaped = key.Key == ConsoleKey.Escape;
			this.SaveUndo(before, cursorBefore);

			if (outcome == Outcome.Continue)
			{
				this.suggestion = this.Suggest();
				this.Render();

				continue;
			}

			// ^C is shown like bash, so it's clear the line was dropped, not run.
			return outcome == Outcome.Submit ? this.Finish(string.Empty, this.line.ToString())
				: outcome == Outcome.Cancel ? this.Finish("^C", this.continuation ? null : string.Empty)
				: this.Finish(string.Empty, null);
		}
	}

	/// <summary>
	/// Applies one key to the line.
	/// </summary>
	/// <param name="key">The key pressed.</param>
	/// <param name="header">The header, drawn again after Ctrl+L clears the screen.</param>
	/// <returns>
	/// Whether the line is done, and how.
	/// </returns>
	[SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "ConsoleKey has a member for every key on the keyboard. Only the keys the line editor handles need an arm.")]
	private Outcome Handle(ConsoleKeyInfo key, string header)
	{
		if (this.query is not null && this.Search(key) is { } searched)
		{
			return searched;
		}

		var last = this.last;
		var control = (key.Modifiers & ConsoleModifiers.Control) != 0;

		// ESC and a key read as two keys still count as Alt and that key, as readline reads ESC. .NET returns them as
		// two keys when more input arrives in the same read.
		var alt = (key.Modifiers & ConsoleModifiers.Alt) != 0 || last == LastKey.Escape;

		this.last = key.Key == ConsoleKey.Escape ? LastKey.Escape : LastKey.Other;

		// bash ignores a key with no binding after Ctrl+X, and Ctrl+E is the only one bound here.
		return last == LastKey.CtrlX ? (control && key.Key == ConsoleKey.E ? this.EditInEditor() : Outcome.Continue)
			: control ? key.Key switch
			{
				ConsoleKey.C => Outcome.Cancel,
				ConsoleKey.D when this.line.Length == 0 => Outcome.EndOfInput,
				ConsoleKey.D => this.DeleteAt(this.cursor),
				ConsoleKey.A => this.MoveTo(0),
				ConsoleKey.E => this.Accept(),
				ConsoleKey.B => this.MoveTo(this.cursor - 1),
				ConsoleKey.F when this.suggestion.Length > 0 => this.Accept(),
				ConsoleKey.F => this.MoveTo(this.cursor + 1),
				ConsoleKey.P => this.Recall(-1),
				ConsoleKey.N => this.Recall(1),
				ConsoleKey.LeftArrow => this.MoveTo(this.WordStartBefore(this.cursor, char.IsLetterOrDigit)),
				ConsoleKey.RightArrow => this.MoveTo(this.WordEndAfter(this.cursor)),
				ConsoleKey.T => this.Transpose(),
				ConsoleKey.U => this.Kill(0, this.cursor, last),
				ConsoleKey.K => this.Kill(this.cursor, this.line.Length, last),
				ConsoleKey.W => this.Kill(this.WordStartBefore(this.cursor, character => character != ' '), this.cursor, last),
				ConsoleKey.Y => this.Insert(this.killed),
				ConsoleKey.X => this.Expect(LastKey.CtrlX),

				// Ctrl+_ and Ctrl+/ send 0x1F, which .NET on Unix reads as Ctrl+7. The Windows console reports the key
				// itself.
				ConsoleKey.D7 or ConsoleKey.OemMinus => this.Undo(),
				ConsoleKey.L => this.ClearScreen(header),
				ConsoleKey.R => this.StartSearch(reverse: true),
				ConsoleKey.S => this.StartSearch(reverse: false),
				_ => Outcome.Continue,
			}
			: key.Key switch
			{
				ConsoleKey.Enter => Outcome.Submit,
				ConsoleKey.Backspace when alt => this.Kill(this.WordStartBefore(this.cursor, char.IsLetterOrDigit), this.cursor, last),
				ConsoleKey.Backspace when this.cursor > 0 => this.DeleteRange(this.cursor - 1, this.cursor),
				ConsoleKey.Delete => this.DeleteAt(this.cursor),
				ConsoleKey.RightArrow when this.suggestion.Length > 0 => this.Accept(),
				ConsoleKey.LeftArrow => this.MoveTo(this.cursor - 1),
				ConsoleKey.RightArrow => this.MoveTo(this.cursor + 1),
				ConsoleKey.Home => this.MoveTo(0),
				ConsoleKey.End => this.Accept(),
				ConsoleKey.UpArrow => this.Recall(-1),
				ConsoleKey.DownArrow => this.Recall(1),
				ConsoleKey.Tab => this.Complete(),
				ConsoleKey.B when alt => this.MoveTo(this.WordStartBefore(this.cursor, char.IsLetterOrDigit)),
				ConsoleKey.F when alt => this.MoveTo(this.WordEndAfter(this.cursor)),
				ConsoleKey.D when alt => this.Kill(this.cursor, this.WordEndAfter(this.cursor), last),
				ConsoleKey.OemPeriod when alt => this.LastArgument(last),

				// Other Alt keys do nothing. Typing their letter would put a stray character in the line.
				_ when alt => Outcome.Continue,
				_ when !char.IsControl(key.KeyChar) => this.Insert(key.KeyChar.ToString()),
				_ => Outcome.Continue,
			};
	}

	/// <summary>
	/// Puts <paramref name="text" /> into the line at the cursor and moves past it.
	/// </summary>
	/// <param name="text">The typed character, or the text Ctrl+Y or Alt+. puts back.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome Insert(string text)
	{
		_ = this.line.Insert(this.cursor, text);
		this.cursor += text.Length;

		return Outcome.Continue;
	}

	/// <summary>
	/// Reads the rest of an escape sequence that started with ESC <c>[</c>, and reads a paste when the sequence starts
	/// one.
	/// </summary>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	/// <remarks>
	/// .NET only knows the sequences with one or two digits before <c>~</c>, so the paste markers and others like focus
	/// reports come in as Escape and then a key per character. Read that way, everything but a paste is dropped, so it
	/// doesn't end up typed into the line. Any search ends, as it does for any key that isn't part of it.
	/// </remarks>
	private Outcome ReadSequence()
	{
		this.last = LastKey.Other;
		_ = this.EndSearch();

		var parameters = string.Empty;
		ConsoleKeyInfo key;

		// Parameter and intermediate bytes run from space to ?, and the first character past them ends the sequence.
		while ((key = readKey()).KeyChar is >= ' ' and <= '?')
		{
			parameters += key.KeyChar;
		}

		return parameters == "200" && key.KeyChar == '~' ? this.Insert(this.ReadPaste()) : Outcome.Continue;
	}

	/// <summary>
	/// Reads pasted text up to the <c>ESC [201~</c> the terminal sends after it.
	/// </summary>
	/// <returns>
	/// The text, with every line break as <c>\n</c> and control characters other than tabs left out, since drawing them
	/// would act on the terminal.
	/// </returns>
	/// <remarks>
	/// Inserted as one piece, the paste waits for Enter like typed text, and one Ctrl+_ takes all of it back.
	/// </remarks>
	private string ReadPaste()
	{
		const string End = "\e[201~";

		var text = new StringBuilder();

		while (text.Length < End.Length || text[^1] != '~' || !text.ToString(text.Length - End.Length, End.Length).Equals(End, StringComparison.Ordinal))
		{
			var key = readKey();

			// .NET reads ESC and the key after it as one Alt key, which may be the end marker's ESC [.
			if ((key.Modifiers & ConsoleModifiers.Alt) != 0)
			{
				_ = text.Append('\e');
			}

			_ = text.Append(key.KeyChar);
		}

		var pasted = text.ToString(0, text.Length - End.Length).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

		return string.Concat(pasted.Where(character => character is '\n' or '\t' || !char.IsControl(character)));
	}

	/// <summary>
	/// Leaves <paramref name="next" /> for the next key to act on.
	/// </summary>
	/// <param name="next">What the next key should see as the key before it.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome Expect(LastKey next)
	{
		this.last = next;

		return Outcome.Continue;
	}

	/// <summary>
	/// Adds the suggestion on screen to the end of the line and moves the cursor after it.
	/// </summary>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	/// <remarks>
	/// With no suggestion this only moves the cursor to the end, which is all End and Ctrl+E do then.
	/// </remarks>
	private Outcome Accept()
	{
		_ = this.line.Append(this.suggestion);
		this.cursor = this.line.Length;

		return Outcome.Continue;
	}

	/// <summary>
	/// Finds the rest of the newest history line that starts with the line typed so far, as fish suggests it, or else
	/// what Tab would add to a command name being typed.
	/// </summary>
	/// <returns>
	/// The part of that history line past the typed text, or Tab's addition without its trailing space. An empty string
	/// when the line is empty, the cursor isn't at its end, a list of matches is showing, Up or Down is stepping through
	/// history, or neither a longer history line nor a command name fits.
	/// </returns>
	private string Suggest()
	{
		// Hidden while browsing, because the next Up searches by the text typed before the first Up, not by the line
		// on screen, so a suggestion there would promise a line Up doesn't give.
		if (this.continuation || this.query is not null || this.line.Length == 0 || this.cursor != this.line.Length || this.listed is not null || this.Browsing)
		{
			return string.Empty;
		}

		var typed = this.line.ToString();

		for (var i = history.Count - 1; i >= 0; i--)
		{
			if (history[i].Length > typed.Length && history[i].StartsWith(typed, StringComparison.Ordinal))
			{
				return history[i][typed.Length..];
			}
		}

		return typed.Contains(' ', StringComparison.Ordinal) ? string.Empty : this.SuggestCommand(typed);
	}

	/// <summary>
	/// Works out what Tab would add to the command name typed so far, so the dimmed text always matches Tab.
	/// </summary>
	/// <param name="typed">The line, which is still the command word.</param>
	/// <returns>
	/// The rest of the only matching name, or the part all matching names share, with no trailing space. Empty when
	/// <paramref name="typed" /> is already a whole name or the names share nothing more.
	/// </returns>
	private string SuggestCommand(string typed)
	{
		if (this.commandMatches is null || !typed.StartsWith(this.commandPrefix, StringComparison.Ordinal))
		{
			this.commandPrefix = typed;
			this.commandMatches = [.. Completions.FindMatches(typed, builtins)];
		}
		else
		{
			_ = this.commandMatches.RemoveAll(name => !name.StartsWith(typed, StringComparison.Ordinal));
			this.commandPrefix = typed;
		}

		return Completions.Complete(typed, this.commandMatches)?.TrimEnd() ?? string.Empty;
	}

	/// <summary>
	/// Moves the cursor to <paramref name="index" />, kept within the line.
	/// </summary>
	/// <param name="index">The position to move to.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome MoveTo(int index)
	{
		this.cursor = Math.Clamp(index, 0, this.line.Length);

		return Outcome.Continue;
	}

	/// <summary>
	/// Clears the screen, leaving the scrollback alone as Ctrl+L does in bash, and draws the header again at the top.
	/// </summary>
	/// <param name="header">The header to draw.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome ClearScreen(string header)
	{
		output.Write("\e[H\e[2J");
		output.Write(header);
		this.cursorRow = 0;

		return Outcome.Continue;
	}

	/// <summary>
	/// Removes the character at <paramref name="index" />, when there is one.
	/// </summary>
	/// <param name="index">The position in the line.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome DeleteAt(int index) => this.DeleteRange(index, Math.Min(index + 1, this.line.Length));

	/// <summary>
	/// Removes the characters from <paramref name="start" /> up to <paramref name="end" />, a range that always starts
	/// or ends at the cursor, and puts the cursor where the range started.
	/// </summary>
	/// <param name="start">The first position removed.</param>
	/// <param name="end">The position after the last one removed.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome DeleteRange(int start, int end)
	{
		_ = this.line.Remove(start, end - start);
		this.cursor = start;

		return Outcome.Continue;
	}

	/// <summary>
	/// Removes the characters from <paramref name="start" /> up to <paramref name="end" /> into the kill buffer, which
	/// Ctrl+Y puts back.
	/// </summary>
	/// <param name="start">The first position removed.</param>
	/// <param name="end">The position after the last one removed.</param>
	/// <param name="last">What the key before this one left, which tells whether it was a kill too.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	/// <remarks>
	/// Kills in a row build up one piece of text in line order, as in readline: one that removes text before the cursor
	/// adds it in front, and one that removes text after the cursor adds it at the end. Removing nothing leaves the
	/// buffer alone.
	/// </remarks>
	private Outcome Kill(int start, int end, LastKey last)
	{
		if (end > start)
		{
			var text = this.line.ToString(start, end - start);

			this.killed = last != LastKey.Kill ? text
				: start < this.cursor ? text + this.killed
				: this.killed + text;
		}

		this.last = LastKey.Kill;

		return this.DeleteRange(start, end);
	}

	/// <summary>
	/// Swaps the character before the cursor with the one under it and moves past both, as readline's Ctrl+T does. At
	/// the end of the line, the last two characters swap.
	/// </summary>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	/// <remarks>
	/// Does nothing at the start of the line or on a line shorter than two characters.
	/// </remarks>
	private Outcome Transpose()
	{
		if (this.cursor == 0 || this.line.Length < 2)
		{
			return Outcome.Continue;
		}

		if (this.cursor == this.line.Length)
		{
			this.cursor--;
		}

		(this.line[this.cursor - 1], this.line[this.cursor]) = (this.line[this.cursor], this.line[this.cursor - 1]);
		this.cursor++;

		return Outcome.Continue;
	}

	/// <summary>
	/// Puts back the line and cursor from before the last change to this line.
	/// </summary>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome Undo()
	{
		if (this.undo.TryPop(out var step))
		{
			_ = this.line.Clear().Append(step.Line);
			this.cursor = step.Cursor;
			this.undoing = true;
		}

		return Outcome.Continue;
	}

	/// <summary>
	/// Saves the line from before a key as a step Ctrl+_ can go back to, when the key changed the line.
	/// </summary>
	/// <param name="before">The line before the key.</param>
	/// <param name="cursorBefore">The cursor before the key.</param>
	/// <remarks>
	/// A character typed where the last typed run ends joins that run's step, up to 20 characters, as readline's
	/// <c>rl_insert_text</c> joins them, so one Ctrl+_ takes back a typed word rather than one letter.
	/// </remarks>
	private void SaveUndo(string before, int cursorBefore)
	{
		if (this.undoing)
		{
			this.undoing = false;

			return;
		}

		if (this.line.Equals(before.AsSpan()))
		{
			return;
		}

		if (this.undo.TryPeek(out var step)
			&& this.cursor == cursorBefore + 1
			&& cursorBefore - step.Cursor is > 0 and < 20
			&& this.line.ToString().Remove(cursorBefore, 1) == before
			&& before.Remove(step.Cursor, cursorBefore - step.Cursor) == step.Line)
		{
			return;
		}

		this.undo.Push((before, cursorBefore));
	}

	/// <summary>
	/// Puts the last word of the newest history line at the cursor, or right after another Alt+., swaps the word
	/// that one put in for the last word of the next older line, as readline's <c>yank-last-arg</c> does.
	/// </summary>
	/// <param name="last">What the key before this one left, which tells whether it was Alt+. too.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	/// <remarks>
	/// History lines with no words are skipped, and once the oldest line has given its word, more presses leave the
	/// line alone. Words are split as the coloring splits them, so nothing in them is expanded.
	/// </remarks>
	private Outcome LastArgument(LastKey last)
	{
		var again = last == LastKey.LastArgument;
		var index = (again ? this.lastArgumentLine : history.Count) - 1;

		while (index >= 0 && Highlighter.SplitRaw(history[index]).Count == 0)
		{
			index--;
		}

		if (again)
		{
			this.last = LastKey.LastArgument;
		}

		if (index < 0)
		{
			return Outcome.Continue;
		}

		var (start, end, _) = Highlighter.SplitRaw(history[index])[^1];

		if (again)
		{
			_ = this.DeleteRange(this.cursor - this.lastArgumentLength, this.cursor);
		}

		this.last = LastKey.LastArgument;
		this.lastArgumentLine = index;
		this.lastArgumentLength = end - start;

		return this.Insert(history[index][start..end]);
	}

	/// <summary>
	/// Hands the line to the editor and runs what comes back, as bash's Ctrl+X Ctrl+E does.
	/// </summary>
	/// <returns>
	/// <see cref="Outcome.Submit" /> with the edited text as the line, or <see cref="Outcome.Continue" /> with the line
	/// unchanged when the editor failed, there's no editor, or this is a line of a here-document's body.
	/// </returns>
	private Outcome EditInEditor()
	{
		if (this.continuation || editInEditor?.Invoke(this.line.ToString()) is not { } edited)
		{
			return Outcome.Continue;
		}

		// Editors end the file with a line break, and Windows ones write CRLF.
		_ = this.line.Clear().Append(edited.ReplaceLineEndings("\n").TrimEnd('\n'));

		return Outcome.Submit;
	}

	/// <summary>
	/// Finds where the word before <paramref name="index" /> starts, skipping the characters that aren't part of a
	/// word first.
	/// </summary>
	/// <param name="index">The position to look back from.</param>
	/// <param name="isWord">
	/// Whether a character is part of a word: anything but a space for Ctrl+W, as bash's <c>unix-word-rubout</c>
	/// counts it, or a letter or digit for Alt+B, Alt+Backspace and Ctrl+Left, as readline counts it.
	/// </param>
	/// <returns>
	/// The position of the word's first character, or 0.
	/// </returns>
	private int WordStartBefore(int index, Func<char, bool> isWord)
	{
		while (index > 0 && !isWord(this.line[index - 1]))
		{
			index--;
		}

		while (index > 0 && isWord(this.line[index - 1]))
		{
			index--;
		}

		return index;
	}

	/// <summary>
	/// Finds where the word after <paramref name="index" /> ends, skipping anything but letters and digits first,
	/// which is where Alt+F and Ctrl+Right move to and what Alt+D removes in bash.
	/// </summary>
	/// <param name="index">The position to look forward from.</param>
	/// <returns>
	/// The position just past the word's last letter or digit, or the end of the line.
	/// </returns>
	private int WordEndAfter(int index)
	{
		while (index < this.line.Length && !char.IsLetterOrDigit(this.line[index]))
		{
			index++;
		}

		while (index < this.line.Length && char.IsLetterOrDigit(this.line[index]))
		{
			index++;
		}

		return index;
	}

	/// <summary>
	/// Shows the next older or newer history line that starts with the text typed or edited before Up, in place of the
	/// line, or that text again after Down past the newest match. With nothing typed, every history line matches.
	/// </summary>
	/// <param name="step"><c>-1</c> to go to older lines, <c>1</c> to go to newer ones.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome Recall(int step)
	{
		// The typed line is kept so Down past the newest entry brings it back instead of an empty line. An edit since
		// the last Up or Down starts a new search from the edited text, so Up never carries on with a stale prefix.
		if (!this.Browsing)
		{
			this.recalled = history.Count;
			this.draft = this.line.ToString();
		}

		var index = this.recalled + step;

		// Same match as the history suggestion, so the first Up fills in exactly the dimmed text.
		while (index >= 0 && index < history.Count && !(history[index].Length > this.draft.Length && history[index].StartsWith(this.draft, StringComparison.Ordinal)))
		{
			index += step;
		}

		// Up stops at the oldest match.
		if (index < 0 || index > history.Count)
		{
			return Outcome.Continue;
		}

		this.recalled = index;
		_ = this.line.Clear().Append(index == history.Count ? this.draft : history[index]);
		this.cursor = this.line.Length;

		return Outcome.Continue;
	}

	/// <summary>
	/// Runs one Tab press through the completer and shows its list of matches, if any.
	/// </summary>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome Complete()
	{
		var result = completer.Press(this.line.ToString());

		_ = this.line.Clear().Append(result.Line);
		this.cursor = this.line.Length;
		this.listed = result.Listed;
		this.selected = result.Selected;

		if (result.Bell)
		{
			output.Write('\a');
		}

		return Outcome.Continue;
	}

	/// <summary>
	/// Redraws the line without its list, moves below it, and gives back the result.
	/// </summary>
	/// <param name="marker">Text to show after the line, like <c>^C</c>.</param>
	/// <param name="result">The value <see cref="ReadLine" /> returns.</param>
	/// <returns>
	/// <paramref name="result" />.
	/// </returns>
	private string? Finish(string marker, string? result)
	{
		this.listed = null;
		this.suggestion = string.Empty;
		this.cursor = this.line.Length;
		this.Render();
		output.Write($"{marker}\r\n");
		output.Flush();

		return result;
	}

	/// <summary>
	/// Draws the prompt, the colored line and any list of matches over what was drawn last time, then puts the cursor
	/// where it belongs in the line.
	/// </summary>
	private void Render()
	{
		var width = columns();

		// A terminal that reports no size gets the usual default.
		width = width > 0 ? width : 80;

		var screen = this.screen.Clear();

		if (this.cursorRow > 0)
		{
			_ = screen.Append(CultureInfo.InvariantCulture, $"\e[{this.cursorRow}A");
		}

		var (prompt, promptWidth) = this.query is not null ? (this.SearchPrompt, this.SearchPrompt.Length) : (this.prompt, this.promptWidth);

		_ = screen.Append("\r\e[J").Append(prompt);

		var lineStart = screen.Length;

		if (this.continuation)
		{
			_ = screen.Append(this.line);
		}
		else
		{
			this.AppendColored(screen);
		}

		if (this.suggestion.Length > 0)
		{
			// The dimmed color the prompt header uses for the parent folder.
			_ = screen.Append("\e[30m").Append(this.suggestion).Append("\e[0m");
		}

		// A tab is drawn as the one column the maths counts it as, and a line break needs a carriage return to start its
		// row at the left. The color codes hold neither.
		_ = screen.Replace('\t', ' ', lineStart, screen.Length - lineStart).Replace("\n", "\r\n", lineStart, screen.Length - lineStart);

		var (endSegmentRow, end) = this.Locate(this.line.Length + this.suggestion.Length, promptWidth, width);
		var endRow = endSegmentRow + (end / width);
		var endColumn = end % width;

		WrapIfFull(screen, end, width);

		if (this.listed is { } names)
		{
			_ = screen.Append("\r\n");

			var listLength = this.AppendList(screen, names);

			WrapIfFull(screen, listLength, width);
			_ = screen.Append(CultureInfo.InvariantCulture, $"\e[{1 + (listLength / width)}A\r");

			if (endColumn > 0)
			{
				_ = screen.Append(CultureInfo.InvariantCulture, $"\e[{endColumn}C");
			}
		}

		var (targetSegmentRow, target) = this.Locate(this.cursor, promptWidth, width);
		var targetRow = targetSegmentRow + (target / width);

		if (endRow > targetRow)
		{
			_ = screen.Append(CultureInfo.InvariantCulture, $"\e[{endRow - targetRow}A");
		}

		_ = screen.Append('\r');

		if (target % width > 0)
		{
			_ = screen.Append(CultureInfo.InvariantCulture, $"\e[{target % width}C");
		}

		this.cursorRow = targetRow;
		output.Write(screen.ToString());
		output.Flush();

		// Text that exactly fills its last row leaves the cursor parked on that row's last column instead of at the
		// start of the next, and terminals differ on where the next move starts from. A space forces the wrap, and the
		// carriage return takes the cursor back over it, so the row maths above always holds.
		static void WrapIfFull(StringBuilder screen, int length, int width)
		{
			if (length > 0 && length % width == 0)
			{
				_ = screen.Append(" \r");
			}
		}
	}

	/// <summary>
	/// Works out where <paramref name="index" /> in the line, followed by the suggestion, is drawn.
	/// </summary>
	/// <param name="index">The position in the line and suggestion.</param>
	/// <param name="promptWidth">How many columns the prompt before the line takes.</param>
	/// <param name="width">The terminal's width in columns.</param>
	/// <returns>
	/// The row, counted from the prompt's, where the piece of text holding <paramref name="index" /> starts after the
	/// line break before it, and the column in that piece as if it never wrapped, with the prompt counted in for the
	/// first piece. Dividing by <paramref name="width" /> gives the rows and column within the piece.
	/// </returns>
	/// <remarks>
	/// Each piece before a line break takes as many rows as it fills, and at least one. One that exactly fills its last
	/// row still takes only that many, because the carriage return drawn before the line break clears the terminal's
	/// pending wrap.
	/// </remarks>
	private (int Row, int Column) Locate(int index, int promptWidth, int width)
	{
		var row = 0;
		var column = promptWidth;

		for (var i = 0; i < index; i++)
		{
			if ((i < this.line.Length ? this.line[i] : this.suggestion[i - this.line.Length]) != '\n')
			{
				column++;

				continue;
			}

			row += Math.Max(1, (column + width - 1) / width);
			column = 0;
		}

		return (row, column);
	}

	/// <summary>
	/// Appends the line with each part in the color the window used to give it: commands green, unknown commands red,
	/// quoted text yellow, redirects cyan, and missing paths red and underlined.
	/// </summary>
	/// <param name="screen">The text being built for the terminal.</param>
	private void AppendColored(StringBuilder screen)
	{
		var text = this.line.ToString();
		var copied = 0;

		foreach (var token in Highlighter.Highlight(text, builtins, this.knownCommands))
		{
			var color = token.Kind switch
			{
				TokenKind.Command => "\e[32m",
				TokenKind.Quoted => "\e[33m",
				TokenKind.Redirect => "\e[36m",
				TokenKind.MissingPath => "\e[31;4m",
				TokenKind.UnknownCommand or _ => "\e[31m",
			};

			_ = screen.Append(text, copied, token.Start - copied)
				.Append(color)
				.Append(text, token.Start, token.Length)
				.Append("\e[0m");
			copied = token.Start + token.Length;
		}

		_ = screen.Append(text, copied, text.Length - copied);
	}

	/// <summary>
	/// Appends the matches separated by two spaces, as bash lists them, with the picked one highlighted.
	/// </summary>
	/// <param name="screen">The text being built for the terminal.</param>
	/// <param name="names">The matches.</param>
	/// <returns>
	/// How many columns the list takes.
	/// </returns>
	private int AppendList(StringBuilder screen, IReadOnlyList<string> names)
	{
		var length = 0;

		for (var i = 0; i < names.Count; i++)
		{
			if (i > 0)
			{
				_ = screen.Append("  ");
				length += 2;
			}

			_ = i == this.selected
				? screen.Append(SelectedName).Append(names[i]).Append("\e[49m")
				: screen.Append(names[i]);
			length += names[i].Length;
		}

		return length;
	}

	/// <summary>
	/// What a key did to the line.
	/// </summary>
	private enum Outcome
	{
		/// <summary>
		/// The line is still being edited.
		/// </summary>
		Continue,

		/// <summary>
		/// Enter was pressed.
		/// </summary>
		Submit,

		/// <summary>
		/// Ctrl+C dropped the line.
		/// </summary>
		Cancel,

		/// <summary>
		/// Ctrl+D on an empty line asked to end the shell.
		/// </summary>
		EndOfInput,
	}

	/// <summary>
	/// What a key leaves for the key after it.
	/// </summary>
	private enum LastKey
	{
		/// <summary>
		/// Nothing. The next key acts on its own.
		/// </summary>
		Other,

		/// <summary>
		/// Escape was pressed, so the next key counts as Alt and that key.
		/// </summary>
		Escape,

		/// <summary>
		/// Ctrl+X was pressed, so Ctrl+E next opens the editor.
		/// </summary>
		CtrlX,

		/// <summary>
		/// A kill, so another kill adds to the kill buffer instead of replacing it.
		/// </summary>
		Kill,

		/// <summary>
		/// Alt+., so another Alt+. swaps in the last word of an older history line.
		/// </summary>
		LastArgument,
	}
}
