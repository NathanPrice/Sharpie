using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// Reads one line of standard input, or up to a delimiter, into shell variables.
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
/// <c>-d DELIM</c> reads up to the first character of <c>DELIM</c> instead of a newline, or up to a NUL when it's
/// empty, and <c>-a NAME</c> stores every field as an element of the array <c>NAME</c>.
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

		if (this.ParseOptions(args) is not { } options)
		{
			return 2;
		}

		// bash fills only the array with -a and ignores any other name.
		var names = options.Array is { } array ? [array] : args[options.First..];

		// Checked before reading, so a bad name leaves the line for whatever reads next, as bash does.
		if (names.FirstOrDefault(name => !DeclareCommand.IsIdentifier(name)) is { } invalid)
		{
			new ShellError("sharpie::read::invalid_identifier", $"`{invalid}` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Write();

			return 1;
		}

		if (options.Prompt is not null && readKey is not null && RedirectedInput is null)
		{
			Console.Error.Write(options.Prompt);
		}

		bool complete;
		var text = new StringBuilder();
		var escaped = new List<bool>();

		try
		{
			complete = this.ReadInput(options, text, escaped);
		}
		catch (OperationCanceledException)
		{
			return InterruptedStatus;
		}

		var separators = CommandLine.FieldSeparators(declare.Variables);

		if (options.Array is not null)
		{
			DeclareCommand.AssignArray(declare.Variables, options.Array, Split(text.ToString(), [.. escaped], separators, count: null));
		}
		else if (names is [])
		{
			declare.Set("REPLY", text.ToString());
		}
		else
		{
			var fields = Split(text.ToString(), [.. escaped], separators, names.Length);

			for (var index = 0; index < names.Length; index++)
			{
				declare.Set(names[index], fields[index]);
			}
		}

		return complete ? 0 : 1;
	}

	/// <summary>
	/// Splits <paramref name="text" /> into <paramref name="count" /> fields, or into every field, the way bash's
	/// <c>read</c> does.
	/// </summary>
	/// <param name="text">The line, with the backslashes that escaped characters already taken out.</param>
	/// <param name="escaped">For each character of <paramref name="text" />, whether a backslash made it plain text.</param>
	/// <param name="separators">The value of <c>IFS</c>. Empty means the line isn't split or trimmed at all.</param>
	/// <param name="count">
	/// How many names there are to fill, at least one, or <see langword="null" /> for every field, as <c>read -a</c>
	/// takes them.
	/// </param>
	/// <returns>
	/// One value per name. Names past the last field get an empty value. The last name gets the rest of the line with
	/// its separators, less trailing spaces, except that a single field followed by one separator loses the separator,
	/// as POSIX says. With no count, each field, so a separator at the end makes no empty field after it.
	/// </returns>
	/// <remarks>
	/// Space, tab and newline in <c>IFS</c> run together, so <c>a  b</c> is two fields, and are trimmed from both ends.
	/// Any other <c>IFS</c> character ends a field on its own, so <c>a::b</c> with <c>IFS=:</c> holds an empty field.
	/// </remarks>
	internal static string[] Split(string text, bool[] escaped, string separators, int? count)
	{
		_ = text.ArgumentNotNull();
		_ = escaped.ArgumentNotNull();
		_ = separators.ArgumentNotNull();

		var position = 0;

		if (count is null)
		{
			var all = new List<string>();

			while (position < text.Length)
			{
				all.Add(NextField());
			}

			return [.. all];
		}

		var fields = new string[count.Value];

		for (var index = 0; index < fields.Length - 1; index++)
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
	/// <returns>
	/// The options, or <see langword="null" /> after reporting a bad one.
	/// </returns>
	/// <remarks>
	/// Options can be joined, as in <c>-rs</c>, and the text of <c>-p</c>, <c>-d</c> or <c>-a</c> can follow it in the
	/// same word, as in <c>-d:</c>, or be the next word, as in <c>-rd ''</c>. <c>--</c> ends the options.
	/// </remarks>
	private Options? ParseOptions(string[] args)
	{
		var options = new Options(args.Length, Raw: false, Silent: false, Prompt: null, Delimiter: '\n', Array: null);
		var index = 0;

		for (; index < args.Length && args[index] is ['-', _, ..] option; index++)
		{
			if (option == "--")
			{
				return options with { First = index + 1 };
			}

			for (var letter = 1; letter < option.Length; letter++)
			{
				if (option[letter] is 'r' or 's')
				{
					options = option[letter] == 'r' ? options with { Raw = true } : options with { Silent = true };

					continue;
				}

				if (option[letter] is not ('p' or 'd' or 'a'))
				{
					new ShellError("sharpie::read::invalid_option", $"`-{option[letter]}` isn't an option of `{this.Name}`") { Help = "read takes -r, -s, -p prompt, -d delim and -a array" }.Write();

					return null;
				}

				var value = letter + 1 < option.Length ? option[(letter + 1)..] : index + 1 < args.Length ? args[++index] : null;

				if (value is null)
				{
					new ShellError("sharpie::read::missing_argument", $"`-{option[letter]}` needs {OptionValues[option[letter]]} after it").Write();

					return null;
				}

				options = option[letter] switch
				{
					'p' => options with { Prompt = value },
					'd' => options with { Delimiter = value is [] ? '\0' : value[0] },
					_ => options with { Array = value },
				};

				letter = option.Length;
			}
		}

		return options with { First = index };
	}

	/// <summary>
	/// Reads one line, or more when a backslash ends a line, into <paramref name="text" />.
	/// </summary>
	/// <param name="options">The options: raw backslashes, hidden typing, and the delimiter.</param>
	/// <param name="text">Gets the characters read, less the backslashes taken as escapes.</param>
	/// <param name="escaped">Gets, for each character in <paramref name="text" />, whether a backslash escaped it.</param>
	/// <returns>
	/// <see langword="true" /> when the input ended with the delimiter, <see langword="false" /> when it ended first.
	/// </returns>
	/// <exception cref="OperationCanceledException">Ctrl+C was pressed on a terminal.</exception>
	/// <remarks>
	/// Without <c>-r</c>, a backslash before a newline takes both out, as a line joined to the next. A backslash before
	/// another delimiter keeps the delimiter as plain text and reads on past it, as bash does.
	/// </remarks>
	private bool ReadInput(Options options, StringBuilder text, List<bool> escaped)
	{
		while (true)
		{
			var complete = this.ReadLine(options.Silent, options.Delimiter, out var line);
			var joined = false;

			for (var index = 0; index < line.Length; index++)
			{
				if (options.Raw || line[index] != '\\')
				{
					_ = text.Append(line[index]);
					escaped.Add(false);
				}
				else if (index + 1 < line.Length)
				{
					if (line[++index] != '\n')
					{
						_ = text.Append(line[index]);
						escaped.Add(true);
					}
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

			if (options.Delimiter != '\n')
			{
				_ = text.Append(options.Delimiter);
				escaped.Add(true);
			}
		}
	}

	/// <summary>
	/// Reads up to the next delimiter, from the keyboard on a terminal or from <see cref="Console.In" /> otherwise.
	/// </summary>
	/// <param name="silent"><see langword="true" /> to hide the typing on a terminal.</param>
	/// <param name="delimiter">The character that ends the line, a newline unless <c>-d</c> gave another.</param>
	/// <param name="line">Gets the text read, without the delimiter.</param>
	/// <returns>
	/// <see langword="true" /> when the delimiter ended the line, <see langword="false" /> when the input ended first.
	/// </returns>
	/// <exception cref="OperationCanceledException">Ctrl+C was pressed on a terminal.</exception>
	/// <remarks>
	/// Piped input is read a character at a time rather than with <see cref="TextReader.ReadLine" />, which can't tell
	/// a last line with no newline from a whole one. It's the same <see cref="Console.In" /> the shell reads its own
	/// lines from, so when the shell's input is piped, <c>read</c> takes the line after its own.
	/// </remarks>
	private bool ReadLine(bool silent, char delimiter, out string line)
	{
		var text = new StringBuilder();

		if (readKey is null || RedirectedInput is not null)
		{
			var input = RedirectedInput ?? Console.In;
			int character;

			while ((character = input.Read()) != -1 && character != delimiter)
			{
				_ = text.Append((char)character);
			}

			line = text.ToString();

			return character == delimiter;
		}

		// The shell's SIGINT handler would otherwise swallow Ctrl+C and leave read waiting.
		var treatControlC = Console.TreatControlCAsInput;

		Console.TreatControlCAsInput = true;

		try
		{
			var complete = ReadKeys(readKey, silent, delimiter, text);

			line = text.ToString();

			return complete;
		}
		finally
		{
			Console.TreatControlCAsInput = treatControlC;
		}
	}

	/// <summary>
	/// Reads keys into <paramref name="text" /> until the delimiter, echoing them to standard error unless
	/// <paramref name="silent" /> is set.
	/// </summary>
	/// <param name="readKey">Reads the next key.</param>
	/// <param name="silent"><see langword="true" /> to show nothing, not even the newline, as <c>read -s</c> does.</param>
	/// <param name="delimiter">
	/// The character that ends the read. For a newline that's Enter. For any other, Enter adds a newline to the text.
	/// </param>
	/// <param name="text">Gets the characters typed.</param>
	/// <returns>
	/// <see langword="true" /> after the delimiter, <see langword="false" /> after Ctrl+D on an empty line.
	/// </returns>
	/// <exception cref="OperationCanceledException">Ctrl+C was pressed.</exception>
	/// <remarks>
	/// A NUL delimiter needs Ctrl held, as Ctrl+Space or Ctrl+@ type it, since keys with no character, like the arrows,
	/// give a NUL too.
	/// </remarks>
	private static bool ReadKeys(Func<ConsoleKeyInfo> readKey, bool silent, char delimiter, StringBuilder text)
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

			if (key.Key != ConsoleKey.Enter && key.KeyChar == delimiter && (delimiter != '\0' || control))
			{
				if (!silent && !char.IsControl(delimiter))
				{
					Console.Error.Write(delimiter);
				}

				return true;
			}

			if (key.Key == ConsoleKey.Enter)
			{
				if (!silent)
				{
					Console.Error.WriteLine();
				}

				if (delimiter == '\n')
				{
					return true;
				}

				_ = text.Append('\n');

				continue;
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

	/// <summary>
	/// The text each option that takes one needs, as the error for a missing one names it.
	/// </summary>
	private static readonly Dictionary<char, string> OptionValues = new()
	{
		['p'] = "the prompt text",
		['d'] = "a delimiter",
		['a'] = "an array name",
	};

	/// <summary>
	/// The options a <c>read</c> was given.
	/// </summary>
	/// <param name="First">The index of the first name in the arguments.</param>
	/// <param name="Raw">Set by <c>-r</c>, so backslashes stay as they are.</param>
	/// <param name="Silent">Set by <c>-s</c>, so the typing isn't shown.</param>
	/// <param name="Prompt">The text after <c>-p</c>, or <see langword="null" /> for none.</param>
	/// <param name="Delimiter">The character that ends the input, a newline unless <c>-d</c> gave another.</param>
	/// <param name="Array">The array <c>-a</c> fills, or <see langword="null" /> to fill the names.</param>
	private sealed record Options(int First, bool Raw, bool Silent, string? Prompt, char Delimiter, string? Array);
}
