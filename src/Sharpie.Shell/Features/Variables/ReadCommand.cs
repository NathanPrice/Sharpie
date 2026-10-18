using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// Reads one line of standard input into shell variables.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which stores the variables.</param>
/// <param name="readKey">
/// Reads the next key without echoing it, like <see cref="Console.ReadKey(bool)" />, or <see langword="null" /> when
/// standard input isn't a terminal and lines are read from <see cref="Console.In" /> instead.
/// </param>
/// <remarks>
/// <c>read a b</c> splits the line on the characters in <c>IFS</c> and gives each name one field, the last name
/// getting the rest of the line. With no names the whole line goes into <c>REPLY</c> untouched. Without <c>-r</c>, a
/// backslash makes the next character plain text and a backslash at the end of the line joins the next line on.
/// <c>-p</c> writes a prompt to standard error and <c>-s</c> stops the typing from showing, both only on a terminal.
/// </remarks>
internal sealed class ReadCommand(DeclareCommand declare, Func<ConsoleKeyInfo>? readKey) : ICommand
{
	/// <summary>
	/// The status for a read that Ctrl+C ended, which is 128 plus SIGINT's number, as bash gives.
	/// </summary>
	internal const int InterruptedStatus = 130;

	/// <summary>
	/// Gets or sets where a redirect of stream 0 points <c>read</c>, as in <c>read x &lt; file</c> or
	/// <c>while read l; do ...; done &lt; file</c>.
	/// </summary>
	/// <value>
	/// The reader, shared by every <c>read</c> under the same redirect so none loses what another read ahead, or
	/// <see langword="null" /> to read the keyboard or the shell's own input.
	/// </value>
	/// <remarks>
	/// The reader reads ahead, so a program the loop runs that reads its standard input starts after what
	/// <c>read</c> took in; bash reads one byte at a time to avoid that.
	/// </remarks>
	internal static TextReader? RedirectedInput { get; set; }

	/// <inheritdoc />
	public string Name => "read";

	/// <inheritdoc />
	public string Description => "Reads a line of input into shell variables";

	/// <inheritdoc />
	/// <remarks>
	/// The status is 0 for a whole line, 1 when standard input ended first (the names still get what was read, as in
	/// bash) or a name isn't valid, 2 for a bad option, and <see cref="InterruptedStatus" /> after Ctrl+C.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (this.ParseOptions(args, out var raw, out var silent, out var prompt) is not { } first)
		{
			return 2;
		}

		var names = args[first..];

		// Checked before reading, so a bad name leaves the line for whatever reads next, as bash does.
		if (names.FirstOrDefault(name => !DeclareCommand.IsIdentifier(name)) is { } invalid)
		{
			new ShellError("sharpie::read::invalid_identifier", $"`{invalid}` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Write();

			return 1;
		}

		if (prompt is not null && readKey is not null && RedirectedInput is null)
		{
			Console.Error.Write(prompt);
		}

		bool complete;
		var text = new StringBuilder();
		var escaped = new List<bool>();

		try
		{
			complete = this.ReadInput(raw, silent, text, escaped);
		}
		catch (OperationCanceledException)
		{
			return InterruptedStatus;
		}

		if (names is [])
		{
			declare.Set("REPLY", text.ToString());
		}
		else
		{
			var separators = CommandLine.FieldSeparators(declare.Variables);
			var fields = Split(text.ToString(), [.. escaped], separators, names.Length);

			for (var index = 0; index < names.Length; index++)
			{
				declare.Set(names[index], fields[index]);
			}
		}

		return complete ? 0 : 1;
	}

	/// <summary>
	/// Splits <paramref name="text" /> into <paramref name="count" /> fields the way bash's <c>read</c> does.
	/// </summary>
	/// <param name="text">The line, with the backslashes that escaped characters already taken out.</param>
	/// <param name="escaped">For each character of <paramref name="text" />, whether a backslash made it plain text.</param>
	/// <param name="separators">The value of <c>IFS</c>. Empty means the line isn't split or trimmed at all.</param>
	/// <param name="count">How many names there are to fill, at least one.</param>
	/// <returns>
	/// One value per name. Names past the last field get an empty value. The last name gets the rest of the line with
	/// its separators, less trailing spaces, except that a single field followed by one separator loses the separator,
	/// as POSIX says.
	/// </returns>
	/// <remarks>
	/// Space, tab and newline in <c>IFS</c> run together, so <c>a  b</c> is two fields, and are trimmed from both ends.
	/// Any other <c>IFS</c> character ends a field on its own, so <c>a::b</c> with <c>IFS=:</c> holds an empty field.
	/// </remarks>
	internal static string[] Split(string text, bool[] escaped, string separators, int count)
	{
		_ = text.ArgumentNotNull();
		_ = escaped.ArgumentNotNull();
		_ = separators.ArgumentNotNull();

		var position = 0;
		var fields = new string[count];

		for (var index = 0; index < count - 1; index++)
		{
			fields[index] = NextField();
		}

		fields[^1] = Rest();

		return fields;

		bool IsSeparator(int at) => !escaped[at] && separators.Contains(text[at], StringComparison.Ordinal);

		bool IsSpace(int at) => IsSeparator(at) && text[at] is ' ' or '\t' or '\n';

		void SkipSpaces()
		{
			while (position < text.Length && IsSpace(position))
			{
				position++;
			}
		}

		// Reads one field, then its separator: a run of spaces that may hold one other separator, or one other
		// separator followed by spaces.
		string NextField()
		{
			SkipSpaces();

			var start = position;

			while (position < text.Length && !IsSeparator(position))
			{
				position++;
			}

			var field = text[start..position];

			if (position < text.Length)
			{
				var space = IsSpace(position);

				position++;
				SkipSpaces();

				if (space && position < text.Length && IsSeparator(position))
				{
					position++;
					SkipSpaces();
				}
			}

			return field;
		}

		string Rest()
		{
			SkipSpaces();

			var start = position;
			var field = NextField();

			if (position == text.Length)
			{
				return field;
			}

			var end = text.Length;

			while (IsSpace(end - 1))
			{
				end--;
			}

			return text[start..end];
		}
	}

	/// <summary>
	/// Reads the options in front of the names.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="raw">Set when <c>-r</c> is given, so backslashes stay as they are.</param>
	/// <param name="silent">Set when <c>-s</c> is given, so the typing isn't shown.</param>
	/// <param name="prompt">The text after <c>-p</c>, or <see langword="null" /> for none.</param>
	/// <returns>
	/// The index of the first name, or <see langword="null" /> after reporting a bad option.
	/// </returns>
	/// <remarks>
	/// Options can be joined, as in <c>-rs</c>, and the prompt can follow <c>-p</c> in the same word. <c>--</c> ends
	/// the options.
	/// </remarks>
	private int? ParseOptions(string[] args, out bool raw, out bool silent, out string? prompt)
	{
		raw = silent = false;
		prompt = null;

		var index = 0;

		for (; index < args.Length && args[index] is ['-', _, ..] option; index++)
		{
			if (option == "--")
			{
				return index + 1;
			}

			for (var letter = 1; letter < option.Length; letter++)
			{
				switch (option[letter])
				{
					case 'r':
						raw = true;
						break;
					case 's':
						silent = true;
						break;
					case 'p' when letter + 1 < option.Length:
						prompt = option[(letter + 1)..];
						letter = option.Length;
						break;
					case 'p' when index + 1 < args.Length:
						prompt = args[++index];
						break;
					case 'p':
						new ShellError("sharpie::read::missing_prompt", "`-p` needs the prompt text after it").Write();

						return null;
					default:
						new ShellError("sharpie::read::invalid_option", $"`-{option[letter]}` isn't an option of `{this.Name}`") { Help = "read takes -r, -s and -p prompt" }.Write();

						return null;
				}
			}
		}

		return index;
	}

	/// <summary>
	/// Reads one line, or more when a backslash ends a line, into <paramref name="text" />.
	/// </summary>
	/// <param name="raw"><see langword="true" /> to keep backslashes as they are.</param>
	/// <param name="silent"><see langword="true" /> to hide the typing on a terminal.</param>
	/// <param name="text">Gets the characters read, less the backslashes taken as escapes.</param>
	/// <param name="escaped">Gets, for each character in <paramref name="text" />, whether a backslash escaped it.</param>
	/// <returns>
	/// <see langword="true" /> when the input ended with a newline, <see langword="false" /> when it ended first.
	/// </returns>
	/// <exception cref="OperationCanceledException">Ctrl+C was pressed on a terminal.</exception>
	private bool ReadInput(bool raw, bool silent, StringBuilder text, List<bool> escaped)
	{
		while (true)
		{
			var complete = this.ReadLine(silent, out var line);
			var joined = false;

			for (var index = 0; index < line.Length; index++)
			{
				if (raw || line[index] != '\\')
				{
					_ = text.Append(line[index]);
					escaped.Add(false);
				}
				else if (index + 1 < line.Length)
				{
					_ = text.Append(line[++index]);
					escaped.Add(true);
				}
				else
				{
					joined = true;
				}
			}

			if (!joined || !complete)
			{
				return complete;
			}
		}
	}

	/// <summary>
	/// Reads up to the next newline, from the keyboard on a terminal or from <see cref="Console.In" /> otherwise.
	/// </summary>
	/// <param name="silent"><see langword="true" /> to hide the typing on a terminal.</param>
	/// <param name="line">Gets the text read, without the newline.</param>
	/// <returns>
	/// <see langword="true" /> when a newline ended the line, <see langword="false" /> when the input ended first.
	/// </returns>
	/// <exception cref="OperationCanceledException">Ctrl+C was pressed on a terminal.</exception>
	/// <remarks>
	/// Piped input is read a character at a time rather than with <see cref="TextReader.ReadLine" />, which can't tell
	/// a last line with no newline from a whole one. It's the same <see cref="Console.In" /> the shell reads its own
	/// lines from, so when the shell's input is piped, <c>read</c> takes the line after its own.
	/// </remarks>
	private bool ReadLine(bool silent, out string line)
	{
		var text = new StringBuilder();

		if (readKey is null || RedirectedInput is not null)
		{
			var input = RedirectedInput ?? Console.In;
			int character;

			while ((character = input.Read()) is not -1 and not '\n')
			{
				_ = text.Append((char)character);
			}

			line = text.ToString();

			return character == '\n';
		}

		// The shell's SIGINT handler would otherwise swallow Ctrl+C and leave read waiting.
		var treatControlC = Console.TreatControlCAsInput;

		Console.TreatControlCAsInput = true;

		try
		{
			var complete = ReadKeys(readKey, silent, text);

			line = text.ToString();

			return complete;
		}
		finally
		{
			Console.TreatControlCAsInput = treatControlC;
		}
	}

	/// <summary>
	/// Reads keys into <paramref name="text" /> until Enter, echoing them to standard error unless
	/// <paramref name="silent" /> is set.
	/// </summary>
	/// <param name="readKey">Reads the next key.</param>
	/// <param name="silent"><see langword="true" /> to show nothing, not even the newline, as <c>read -s</c> does.</param>
	/// <param name="text">Gets the characters typed.</param>
	/// <returns>
	/// <see langword="true" /> after Enter, <see langword="false" /> after Ctrl+D on an empty line.
	/// </returns>
	/// <exception cref="OperationCanceledException">Ctrl+C was pressed.</exception>
	private static bool ReadKeys(Func<ConsoleKeyInfo> readKey, bool silent, StringBuilder text)
	{
		while (true)
		{
			var key = readKey();
			var control = (key.Modifiers & ConsoleModifiers.Control) != 0;

			if (control && key.Key == ConsoleKey.C)
			{
				Console.Error.WriteLine();

				throw new OperationCanceledException();
			}

			if (control && key.Key == ConsoleKey.D && text.Length == 0)
			{
				return false;
			}

			if (key.Key == ConsoleKey.Enter)
			{
				if (!silent)
				{
					Console.Error.WriteLine();
				}

				return true;
			}

			var echo = string.Empty;

			if (key.Key == ConsoleKey.Backspace && text.Length > 0)
			{
				text.Length--;
				echo = "\b \b";
			}
			else if (!control && (key.KeyChar == '\t' || !char.IsControl(key.KeyChar)))
			{
				_ = text.Append(key.KeyChar);
				echo = key.KeyChar.ToString();
			}

			if (!silent)
			{
				Console.Error.Write(echo);
			}
		}
	}
}
