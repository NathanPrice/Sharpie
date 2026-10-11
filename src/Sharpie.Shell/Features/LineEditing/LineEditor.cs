using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.Highlighting;

namespace Sharpie.Shell.Features.LineEditing;

/// <summary>
/// Reads one command line from the terminal a key at a time, coloring it as it's typed, with Tab completion,
/// Up and Down through the history lines that start with what's typed, a dimmed suggestion from history after the
/// cursor, and the usual editing keys.
/// </summary>
/// <param name="readKey">Reads the next key without echoing it, like <see cref="Console.ReadKey(bool)" />.</param>
/// <param name="output">Where the prompt and the line are drawn.</param>
/// <param name="columns">Gives the terminal's width in columns, read again on every redraw so a resize is picked up.</param>
/// <param name="completer">Decides what each Tab press does.</param>
/// <param name="history">The earlier lines, oldest first, which Up and Down search.</param>
/// <param name="builtins">The builtin command names, which the coloring counts as commands.</param>
/// <remarks>
/// The whole input is redrawn after every key. The cursor first goes back up to the prompt's row, everything below
/// is erased, and the prompt, the colored line and any list of matches are written again. Characters are counted as
/// one column each, so a line holding wide characters like CJK or emoji puts the cursor short of where it should be.
/// </remarks>
internal sealed class LineEditor(
	Func<ConsoleKeyInfo> readKey,
	TextWriter output,
	Func<int> columns,
	TabCompleter completer,
	IReadOnlyList<string> history,
	IReadOnlyCollection<string> builtins)
{
	// The translucent accent the window draws selections with, mixed onto its background, for the picked name in a
	// list of matches.
	private const string SelectedName = "\e[48;2;56;74;102m";

	private readonly StringBuilder line = new();

	private int cursor;
	private int cursorRow;
	private int recalled;
	private string draft = string.Empty;
	private IReadOnlyList<string>? listed;
	private int? selected;
	private string suggestion = string.Empty;

	/// <summary>
	/// Gets a value indicating whether the line still shows a history line that Up or Down put there, unedited.
	/// </summary>
	/// <value>
	/// <see langword="false" /> once any key changes the line, which makes the next Up search again from the changed text.
	/// </value>
	private bool Browsing => this.recalled < history.Count && this.line.ToString() == history[this.recalled];

	/// <summary>
	/// Draws <paramref name="header" /> and the prompt, then reads keys until Enter.
	/// </summary>
	/// <param name="header">The text printed once above the prompt, ending with a line break.</param>
	/// <returns>
	/// The line, an empty line after Ctrl+C, or <see langword="null" /> after Ctrl+D on an empty line, which ends
	/// the shell as end of input does.
	/// </returns>
	public string? ReadLine(string header)
	{
		_ = header.ArgumentNotNull();

		_ = this.line.Clear();
		this.cursor = 0;
		this.cursorRow = 0;
		this.recalled = history.Count;
		this.listed = null;

		output.Write(header);
		this.Render();

		while (true)
		{
			var key = readKey();

			if (key.Key != ConsoleKey.Tab)
			{
				completer.Reset();
				this.listed = null;
			}

			var outcome = this.Handle(key, header);

			if (outcome == Outcome.Continue)
			{
				this.suggestion = this.Suggest();
				this.Render();

				continue;
			}

			// ^C is shown like bash, so it's clear the line was dropped, not run.
			return outcome == Outcome.Submit ? this.Finish(string.Empty, this.line.ToString())
				: outcome == Outcome.Cancel ? this.Finish("^C", string.Empty)
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
		return (key.Modifiers & ConsoleModifiers.Control) != 0
			? key.Key switch
			{
				ConsoleKey.C => Outcome.Cancel,
				ConsoleKey.D when this.line.Length == 0 => Outcome.EndOfInput,
				ConsoleKey.D => this.DeleteAt(this.cursor),
				ConsoleKey.A => this.MoveTo(0),
				ConsoleKey.E => this.Accept(),
				ConsoleKey.U => this.DeleteRange(0, this.cursor),
				ConsoleKey.K => this.DeleteRange(this.cursor, this.line.Length),
				ConsoleKey.W => this.DeleteRange(this.WordStartBefore(this.cursor), this.cursor),
				ConsoleKey.L => this.ClearScreen(header),
				_ => Outcome.Continue,
			}
			: key.Key switch
			{
				ConsoleKey.Enter => Outcome.Submit,
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
				_ when !char.IsControl(key.KeyChar) => this.Insert(key.KeyChar),
				_ => Outcome.Continue,
			};
	}

	/// <summary>
	/// Puts <paramref name="character" /> into the line at the cursor and moves past it.
	/// </summary>
	/// <param name="character">The typed character.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome Insert(char character)
	{
		_ = this.line.Insert(this.cursor, character);
		this.cursor++;

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
	/// Finds the rest of the newest history line that starts with the line typed so far, as fish suggests it.
	/// </summary>
	/// <returns>
	/// The part of that history line past the typed text, or an empty string when the line is empty, the cursor isn't
	/// at its end, a list of matches is showing, Up or Down is stepping through history, or no longer history line starts
	/// with it.
	/// </returns>
	private string Suggest()
	{
		// Hidden while browsing, because the next Up searches by the text typed before the first Up, not by the line
		// on screen, and the dimmed text must always be what Up gives.
		if (this.line.Length == 0 || this.cursor != this.line.Length || this.listed is not null || this.Browsing)
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

		return string.Empty;
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
	/// Finds where the word before <paramref name="index" /> starts, skipping spaces first, which is what Ctrl+W
	/// removes in bash.
	/// </summary>
	/// <param name="index">The position to look back from.</param>
	/// <returns>
	/// The position of the word's first character, or 0.
	/// </returns>
	private int WordStartBefore(int index)
	{
		while (index > 0 && this.line[index - 1] == ' ')
		{
			index--;
		}

		while (index > 0 && this.line[index - 1] != ' ')
		{
			index--;
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

		// Same match as the suggestion, so the first Up fills in exactly the dimmed text.
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

		var screen = new StringBuilder();

		if (this.cursorRow > 0)
		{
			_ = screen.Append(CultureInfo.InvariantCulture, $"\e[{this.cursorRow}A");
		}

		_ = screen.Append("\r\e[J").Append(PromptHeader.Symbol);
		this.AppendColored(screen);

		if (this.suggestion.Length > 0)
		{
			// The dimmed color the prompt header uses for the parent folder.
			_ = screen.Append("\e[30m").Append(this.suggestion).Append("\e[0m");
		}

		var end = PromptHeader.SymbolWidth + this.line.Length + this.suggestion.Length;
		var endRow = end / width;
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

		var target = PromptHeader.SymbolWidth + this.cursor;
		var targetRow = target / width;

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
	/// Appends the line with each part in the color the window used to give it: commands green, unknown commands red,
	/// quoted text yellow, redirects cyan, and missing paths red and underlined.
	/// </summary>
	/// <param name="screen">The text being built for the terminal.</param>
	private void AppendColored(StringBuilder screen)
	{
		var text = this.line.ToString();
		var copied = 0;

		foreach (var token in Highlighter.Highlight(text, builtins))
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
}
