using System.Text;
using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Common;

/// <summary>
/// Splits a line into the commands of a POSIX list, joined by <c>;</c>, newlines, <c>&amp;</c>, <c>&amp;&amp;</c>
/// and <c>||</c>, and runs them in order.
/// </summary>
internal static class CommandList
{
	/// <summary>
	/// The operator before a command that always runs: <c>;</c>, a newline, or nothing for the first command.
	/// </summary>
	internal const string Sequence = ";";

	/// <summary>
	/// The operator before a command that runs only when the status so far is 0.
	/// </summary>
	internal const string And = "&&";

	/// <summary>
	/// The operator before a command that runs only when the status so far isn't 0.
	/// </summary>
	internal const string Or = "||";

	/// <summary>
	/// The operator that ends a command to run in the background. It stays at the end of that command's text, and the
	/// next command always runs, as after <c>;</c>.
	/// </summary>
	internal const string Background = "&";

	/// <summary>
	/// The status of a line that uses syntax <see cref="Split" /> refuses, which bash also gives a syntax error.
	/// </summary>
	internal const int UnsupportedStatus = 2;

	/// <summary>
	/// Cuts <paramref name="line" /> into its commands at every <c>;</c>, newline, <c>&amp;</c>, <c>&amp;&amp;</c> and
	/// <c>||</c> outside quotes.
	/// </summary>
	/// <param name="line">The line, which may hold several lines when it came from <c>-c</c>.</param>
	/// <returns>
	/// Each command's text as typed, quotes kept, with the operator before it, which is <see cref="Sequence" /> for the
	/// first. Blank commands and comments are dropped. <see langword="null" /> when a <c>$(</c> or backquote outside
	/// single quotes is never closed, or the line uses arithmetic expansion, <c>$((...))</c>.
	/// </returns>
	/// <remarks>
	/// Quotes and backslashes work as in <see cref="CommandLine.Split" />, so a quoted or escaped <c>;</c> stays in its
	/// command. A single <c>|</c> also stays, for the command to read as a pipe, and so does an <c>&amp;</c> that's part
	/// of a redirect like <c>2&gt;&amp;1</c> or <c>&amp;&gt;</c>. A command cut at <c>&amp;</c> keeps it at its end; when
	/// it ends an and-or list, as in <c>a &amp;&amp; b &amp;</c>, the whole list becomes that one command, which bash
	/// also runs in the background as a whole.
	/// An operator at the end of a line carries over a newline, so <c>a &amp;&amp;</c> on one line and <c>b</c> on the
	/// next run as <c>a &amp;&amp; b</c>, as POSIX allows. A command substitution, <c>$(...)</c> or backquotes, stays
	/// in its command whole, so a <c>;</c>, quote or <c>#</c> inside it neither cuts nor ends the command around it.
	/// The refusals are reported rather than passed on as text, so a command meant to get the output of another
	/// never runs with the wrong arguments. Between a <c>[[</c> that starts a command and its <c>]]</c>,
	/// <c>&amp;&amp;</c> and <c>||</c> join tests, so they stay in the command, as in bash.
	/// <para>
	/// A function definition comes back as one command, from its header to the <c>}</c> that closes its body, for
	/// <see cref="FunctionDefinition.Parse" /> to read.
	/// </para>
	/// <para>
	/// At a newline, the lines after it up to each delimiter line are the bodies of the here-documents the commands
	/// since the last newline start. They're added, newlines and delimiter lines included, to the end of the command
	/// that owns them, for <see cref="CommandLine.Split" /> to read, and skip the quote, comment, substitution and
	/// operator rules, so <c>don't</c>, <c>#</c>, <c>;</c> and an unclosed <c>$(</c> in them are text.
	/// </para>
	/// </remarks>
	public static IReadOnlyList<(string Operator, string Command)>? Split(string line) =>
		SplitOrRefuse(line, out _, out _, out _, out _)?.Select(command => (command.Operator, command.Command)).ToArray();

	/// <summary>
	/// Finds what <paramref name="line" /> leaves open at its end, which the next line of a file goes on with, as
	/// POSIX reads a script.
	/// </summary>
	/// <param name="line">The line, or several joined by newlines.</param>
	/// <returns>
	/// The quote that's still open, <c>(</c> for a <c>$(</c> or <c>`</c> for a backquote that's never closed, else
	/// <c>{</c> for a function whose body is never closed, or <see langword="null" /> when nothing is. Arithmetic
	/// expansion gives <see langword="null" />, since it's refused however it ends. A quote inside a comment doesn't
	/// count, so <c># don't</c> is complete.
	/// </returns>
	internal static char? Unfinished(string line) =>
		SplitOrRefuse(line, out var refused, out var quote, out _, out var definition) is not null ? quote ?? (definition ? '{' : null)
		: line.AsSpan(refused).StartsWith("$((", StringComparison.Ordinal) ? null
		: line[refused] == '`' ? '`' : '(';

	/// <summary>
	/// Checks whether <paramref name="text" /> ends inside a function definition, so a reader should add the next
	/// line before running it.
	/// </summary>
	/// <param name="text">The lines read so far, joined by newlines.</param>
	/// <returns>
	/// <see langword="true" /> when the last definition's body isn't closed, even when a quote inside it is open too,
	/// or its header ends the text.
	/// </returns>
	public static bool IsDefinitionOpen(string text)
	{
		_ = SplitOrRefuse(text, out _, out _, out _, out var open);

		return open;
	}

	/// <summary>
	/// Checks whether <paramref name="text" /> ends inside a here-document's body, so a reader should add the next
	/// line before running it.
	/// </summary>
	/// <param name="text">The lines read so far, joined by newlines.</param>
	/// <returns>
	/// <see langword="true" /> when some here-document has no delimiter line yet.
	/// </returns>
	public static bool IsHereDocumentOpen(string text)
	{
		_ = SplitOrRefuse(text, out _, out _, out var unclosed, out _);

		return unclosed is not null;
	}

	/// <summary>
	/// Cuts <paramref name="line" /> into its commands, as <see cref="Split" /> describes.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="refused">
	/// When this method returns <see langword="null" />, contains where the unclosed <c>$(</c> or backquote, or the
	/// <c>$((</c>, starts; otherwise, -1.
	/// </param>
	/// <param name="openQuote">
	/// When this method returns the commands, contains the quote the line ends inside, or <see langword="null" />.
	/// </param>
	/// <param name="unclosed">
	/// The delimiter of a here-document whose body runs to the end of <paramref name="line" />, or
	/// <see langword="null" /> when every body was closed.
	/// </param>
	/// <param name="definitionOpen">
	/// When this method returns the commands, contains <see langword="true" /> when the line ends inside a function
	/// definition.
	/// </param>
	/// <returns>
	/// The commands, each with where it starts in <paramref name="line" />, or <see langword="null" /> when the line is
	/// refused. A list joined at its <c>&amp;</c> starts where its first command does.
	/// </returns>
	private static List<(string Operator, string Command, int Start)>? SplitOrRefuse(string line, out int refused, out char? openQuote, out string? unclosed, out bool definitionOpen)
	{
		_ = line.ArgumentNotNull();

		// Stryker disable once Unary : refused is only read when the line is refused, and every refusal sets it.
		refused = -1;
		openQuote = null;
		definitionOpen = false;
		unclosed = null;

		// The first command since the last newline, whose here-documents' bodies start after the next one.
		var lineStart = 0;

		var commands = new List<(string Operator, string Command, int Start)>();
		var command = new StringBuilder();
		var before = Sequence;

		// Where the text of the command being read starts, right after the operator before it.
		var textStart = 0;

		// Where the and-or list being read starts in commands, for a & at its end to join it into one command.
		var listStart = 0;
		char? quote = null;
		var escaped = false;

		// A comment starts only at the start of a word, as in CommandLine.Split, so the # in a#b is kept.
		var wordStart = true;

		// True from a [[ that starts a command to its ]], where && and || join tests instead of commands.
		var inCondition = false;

		for (var index = 0; index < line.Length; index++)
		{
			var character = line[index];

			if (escaped || quote == '\'')
			{
				escaped = false;
				quote = quote == '\'' && character == '\'' ? null : quote;
				_ = command.Append(character);

				continue;
			}

			if (character == '`' || line.AsSpan(index).StartsWith("$(", StringComparison.Ordinal))
			{
				if (CommandLine.SubstitutionEnd(line, index) is not (var close and >= 0))
				{
					refused = index;

					return null;
				}

				_ = command.Append(line, index, close - index + 1);
				index = close;
				wordStart = false;

				continue;
			}

			if (quote is null && character == '#' && wordStart)
			{
				index = CommandLine.LineEnd(line, index) - 1;

				continue;
			}

			inCondition = IsInCondition(line, index, inCondition, quote is null && wordStart, command);

			if (quote is null && OperatorAt(line, index, inCondition) is { } typed)
			{
				if (typed == Background)
				{
					_ = command.Append('&');
				}

				Cut(typed);
				index += typed.Length - 1;

				if (character == '\n')
				{
					index = ReadBodies(line, index + 1, commands, lineStart, ref unclosed) - 1;
					lineStart = commands.Count;
				}

				textStart = index + 1;

				continue;
			}

			if (character == '\\')
			{
				escaped = true;
			}
			else if (quote is null && character is '\'' or '"')
			{
				quote = character;
			}
			else if (character == quote)
			{
				quote = null;
			}

			// A blank inside double quotes sets this too, but the closing quote clears it before an unquoted # can read it.
			_ = command.Append(character);
			wordStart = character is ' ' or '\t';
		}

		Cut(Sequence);
		openQuote = quote;

		// At the end there are no lines left, so this only marks the here-documents of the last line as unclosed.
		_ = ReadBodies(line, line.Length, commands, lineStart, ref unclosed);

		return JoinDefinitions(commands, out definitionOpen);

		// Ends the command being read. A blank one is dropped, and its operator only replaces the one before it when
		// it's && or ||, which keeps "a &&" followed by a newline joined to the next line. After a &, the commands of
		// the and-or list it ends are joined into one, and the next command always runs.
		void Cut(string after)
		{
			var typed = command.ToString();
			var text = typed.Trim();

			if (text.Length > 0)
			{
				// Blanks are kept as typed, so skipping the leading ones finds the command's first character in the line.
				commands.Add((before, text, textStart + typed.Length - typed.TrimStart().Length));
				before = after;

				if (after == Background)
				{
					var list = commands[listStart..];

					commands.RemoveRange(listStart, list.Count);
					commands.Add((list[0].Operator, list[0].Command + string.Concat(list.Skip(1).Select(next => $" {next.Operator} {next.Command}")), list[0].Start));
					before = Sequence;
				}

				if (before == Sequence)
				{
					listStart = commands.Count;
				}
			}
			else if (after != Sequence)
			{
				before = after;
			}

			_ = command.Clear();
			wordStart = true;
			inCondition = false;
		}
	}

	/// <summary>
	/// Works out whether the character at <paramref name="index" /> is between a <c>[[</c> and its <c>]]</c>.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="index">The position of the character.</param>
	/// <param name="inCondition">Whether the character before it was.</param>
	/// <param name="wordStart"><see langword="true" /> when a word starts here, outside quotes.</param>
	/// <param name="command">The command read so far, which a <c>[[</c> has to start.</param>
	/// <returns>
	/// The opposite of <paramref name="inCondition" /> when the word here is the bracket that starts or ends the
	/// condition, followed by a blank, an operator character or the end of the line; otherwise,
	/// <paramref name="inCondition" />.
	/// </returns>
	private static bool IsInCondition(string line, int index, bool inCondition, bool wordStart, StringBuilder command) =>
		wordStart && line.AsSpan(index).StartsWith(inCondition ? "]]" : "[[", StringComparison.Ordinal)
		&& (index + 2 == line.Length || line[index + 2] is ' ' or '\t' or '\n' or '|' or '&' or ';' or '<' or '>' or '(' or ')')
		&& (inCondition || string.IsNullOrWhiteSpace(command.ToString()))
			? !inCondition
			: inCondition;

	/// <summary>
	/// Joins the commands of each function definition back into one command, from its header to the <c>}</c> that
	/// closes its body.
	/// </summary>
	/// <param name="commands">The commands, cut at every operator.</param>
	/// <param name="open">
	/// Set to <see langword="true" /> when the last definition's body is still open at the end, or its header is the
	/// last command and the <c>{</c> hasn't come yet.
	/// </param>
	/// <returns>
	/// The commands, with each definition as one that starts where its header does.
	/// </returns>
	/// <remarks>
	/// The commands are joined by a newline where a <c>;</c> or newline cut them, so a here-document's body still ends
	/// on its own line, and by the operator otherwise. A header alone, as in <c>f()</c>, takes the next command when it
	/// starts with <c>{</c>, as bash allows.
	/// </remarks>
	private static List<(string Operator, string Command, int Start)> JoinDefinitions(List<(string Operator, string Command, int Start)> commands, out bool open)
	{
		var joined = new List<(string Operator, string Command, int Start)>();

		// The braces the definition being joined still has open, 0 after a header alone, or -1 outside a definition.
		var depth = -1;

		foreach (var (before, command, start) in commands)
		{
			var braces = FunctionDefinition.Braces(command);

			if (depth > 0 || (depth == 0 && braces > 0))
			{
				joined[^1] = joined[^1] with { Command = joined[^1].Command + (before == Sequence ? "\n" : $" {before} ") + command };
				depth = depth + braces > 0 ? depth + braces : -1;
			}
			else
			{
				joined.Add((before, command, start));
				depth = FunctionDefinition.IsHeader(command) ? braces : -1;
			}
		}

		open = depth >= 0;

		return joined;
	}

	/// <summary>
	/// Reads the bodies of the here-documents that the commands since the last newline start, and adds each to the end
	/// of the command that owns it.
	/// </summary>
	/// <param name="line">The whole text being split.</param>
	/// <param name="position">Where the line after that newline starts.</param>
	/// <param name="commands">The commands so far.</param>
	/// <param name="first">The first command since the last newline.</param>
	/// <param name="unclosed">Set to a here-document's delimiter when its body runs to the end of the text.</param>
	/// <returns>
	/// Where the line after the last body starts.
	/// </returns>
	private static int ReadBodies(string line, int position, List<(string Operator, string Command, int Start)> commands, int first, ref string? unclosed)
	{
		for (var index = first; index < commands.Count; index++)
		{
			var (before, owner, start) = commands[index];

			foreach (var (delimiter, _, stripTabs) in CommandLine.HereDocuments(owner))
			{
				var (_, end, closed) = CommandLine.ReadHereDocument(line, position, delimiter, stripTabs);
				var body = line[position..end];

				// The newline after the delimiter line belongs to the list, not the body.
				owner += body.Length > 0 ? "\n" + (body.EndsWith('\n') ? body[..^1] : body) : string.Empty;
				unclosed = closed ? unclosed : delimiter;
				position = end;
			}

			commands[index] = (before, owner, start);
		}

		return position;
	}

	/// <summary>
	/// Reads the list operator that starts at <paramref name="index" />, outside quotes.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="index">The position to look at.</param>
	/// <param name="inCondition">
	/// <see langword="true" /> between <c>[[</c> and <c>]]</c>, where only <c>;</c> and a newline end the command.
	/// </param>
	/// <returns>
	/// <see cref="Sequence" /> for <c>;</c> or a newline, <see cref="Background" />, <see cref="And" /> or
	/// <see cref="Or" />, or <see langword="null" /> when none starts there. A single <c>|</c> isn't one, and neither is an <c>&amp;</c> in
	/// <c>&amp;&gt;</c>, <c>&gt;&amp;</c>, <c>&lt;&amp;</c> or <c>|&amp;</c>, which <see cref="CommandLine.Split" /> reads.
	/// </returns>
	private static string? OperatorAt(string line, int index, bool inCondition) => line[index] switch
	{
		';' or '\n' => Sequence,
		_ when inCondition => null,
		'&' or '|' when index + 1 < line.Length && line[index + 1] == line[index] => line[index] == '&' ? And : Or,
		'&' when (index + 1 == line.Length || line[index + 1] != '>') && (index == 0 || line[index - 1] is not ('>' or '<' or '|')) => Background,
		_ => null,
	};

	/// <summary>
	/// Runs the commands of <paramref name="line" /> in order, skipping each one its operator rules out.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="status">The status before the line, which a line with no commands gives back unchanged.</param>
	/// <param name="run">Runs one command and gives its status.</param>
	/// <param name="failed">
	/// Called with the status when a command that ran fails and is the last of its <c>&amp;&amp;</c> and <c>||</c>
	/// chain, which is when <c>set -e</c> ends the shell, or <see langword="null" /> for nothing.
	/// </param>
	/// <returns>
	/// The status of the last command that ran, or <see cref="UnsupportedStatus" /> after printing an error when
	/// <see cref="Split" /> refuses the line or a function's body is never closed, before any of it runs.
	/// </returns>
	/// <remarks>
	/// POSIX gives <c>&amp;&amp;</c> and <c>||</c> the same precedence, read from the left, so in
	/// <c>false &amp;&amp; a || b</c> the skipped <c>a</c> keeps the status at 1 and <c>b</c> runs. POSIX doesn't let
	/// <c>set -e</c> act on a command followed by <c>&amp;&amp;</c> or <c>||</c>, so <c>false || b</c> is a test, not a
	/// failure. A here-document whose delimiter line never comes, at the end of a <c>-c</c> command or a file, gets the
	/// lines that were there, after a warning, as in bash.
	/// </remarks>
	public static int Run(string line, int status, Func<string, int> run, Action<int>? failed = null)
	{
		_ = run.ArgumentNotNull();

		return Run(line, status, (command, _) => run(command), failed);
	}

	/// <summary>
	/// Runs the commands of <paramref name="line" /> in order, as the other overload does, telling
	/// <paramref name="run" /> which commands are tested.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="status">The status before the line.</param>
	/// <param name="run">
	/// Runs one command and gives its status. Its second argument is <see langword="true" /> when the command is
	/// followed by <c>&amp;&amp;</c> or <c>||</c>, so a failure is a test, and <c>set -e</c> ignores it even inside a
	/// line that <c>eval</c> or <c>.</c> runs for the command.
	/// </param>
	/// <param name="failed">Called with the status of a failed command that isn't tested, or <see langword="null" />.</param>
	/// <param name="starting">
	/// Called before each command runs with the number of line breaks in <paramref name="line" /> before it, for
	/// <c>$LINENO</c>, or <see langword="null" /> for nothing. Here-document bodies count.
	/// </param>
	/// <returns>
	/// The status of the last command that ran, or <see cref="UnsupportedStatus" /> when the line is refused.
	/// </returns>
	public static int Run(string line, int status, Func<string, bool, int> run, Action<int>? failed = null, Action<int>? starting = null)
	{
		_ = run.ArgumentNotNull();

		if (SplitOrRefuse(line, out var refused, out _, out var unclosed, out var definitionOpen) is not { } commands)
		{
			var arithmetic = line.AsSpan(refused).StartsWith("$((", StringComparison.Ordinal);
			var backquote = line[refused] == '`';

			var report = arithmetic
				? new ShellError("sharpie::parser::arithmetic", "Arithmetic expansion isn't supported")
				{
					End = refused + 3,
					Label = "works out a number",
					Help = "run a program such as expr for the number, or quote it to pass it as text",
				}
				: new ShellError("sharpie::parser::unclosed_substitution", backquote ? "The backquote is never closed" : "`$(` is never closed")
				{
					End = refused + (backquote ? 1 : 2),
					Label = "runs a command for its output",
					Help = backquote ? "add the closing backquote" : "add the missing `)`",
				};

			(report with { Source = line, Start = refused }).Write();

			return UnsupportedStatus;
		}

		if (definitionOpen)
		{
			new ShellError("sharpie::parser::unclosed_function", "The function's body is never closed")
			{
				Help = "end it with a line holding only }",
			}.Write();

			return UnsupportedStatus;
		}

		if (unclosed is not null)
		{
			new ShellError("sharpie::parser::unclosed_here_document", $"The here-document never got its `{unclosed}` line")
			{
				Help = $"end it with a line holding only {unclosed}; it ran with the lines it had",
			}.Write();
		}

		for (var index = 0; index < commands.Count; index++)
		{
			var (before, command, start) = commands[index];

			if (before != Sequence && before != (status == 0 ? And : Or))
			{
				continue;
			}

			var tested = index + 1 < commands.Count && commands[index + 1].Operator != Sequence;

			starting?.Invoke(line.AsSpan(0, start).Count('\n'));
			status = run(command, tested);

			if (status != 0 && !tested)
			{
				failed?.Invoke(status);
			}
		}

		return status;
	}
}
