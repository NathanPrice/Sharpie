using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Common;

/// <summary>
/// Parses a line into a POSIX list, joined by <c>;</c>, newlines, <c>&amp;</c>, <c>&amp;&amp;</c> and <c>||</c>, with
/// its compound commands, and runs it.
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
	/// The status of a line that <see cref="Split" /> refuses, which bash also gives a syntax error.
	/// </summary>
	internal const int UnsupportedStatus = 2;

	/// <summary>
	/// Cuts <paramref name="line" /> into the commands of its top-level list.
	/// </summary>
	/// <param name="line">The line, which may hold several lines when it came from <c>-c</c>.</param>
	/// <returns>
	/// Each pipeline's text as typed, quotes kept, with the operator before it, which is <see cref="Sequence" /> for the
	/// first of each and-or list. A compound command is one item with its whole text. Blank commands and comments are
	/// dropped. <see langword="null" /> when <see cref="CommandParser.Parse" /> gives an error: a <c>$(</c> or backquote
	/// outside single quotes is never closed, it has a syntax error, or it isn't finished.
	/// </returns>
	/// <remarks>
	/// Quotes and backslashes work as in <see cref="CommandLine.Split" />, so a quoted or escaped <c>;</c> stays in its
	/// command. A <c>|</c> between simple commands also stays, for the command to read as a pipe, and so does an
	/// <c>&amp;</c> that's part of a redirect like <c>2&gt;&amp;1</c> or <c>&amp;&gt;</c>. A command cut at <c>&amp;</c>
	/// keeps it at its end; when it ends an and-or list, as in <c>a &amp;&amp; b &amp;</c>, the whole list becomes that
	/// one command, which bash also runs in the background as a whole.
	/// An operator at the end of a line carries over a newline, so <c>a &amp;&amp;</c> on one line and <c>b</c> on the
	/// next run as <c>a &amp;&amp; b</c>, as POSIX allows. A command substitution, <c>$(...)</c> or backquotes, stays
	/// in its command whole, so a <c>;</c>, quote or <c>#</c> inside it neither cuts nor ends the command around it.
	/// <para>
	/// At a newline, the lines after it up to each delimiter line are the bodies of the here-documents the commands
	/// since the last newline start. They're added, newlines and delimiter lines included, to the end of the command
	/// that owns them, for <see cref="CommandLine.Split" /> to read, and skip the quote, comment, substitution and
	/// operator rules, so <c>don't</c>, <c>#</c>, <c>;</c> and an unclosed <c>$(</c> in them are text.
	/// </para>
	/// </remarks>
	public static IReadOnlyList<(string Operator, string Command)>? Split(string line)
	{
		if (CommandParser.Parse(line).List is not { } list)
		{
			return null;
		}

		var commands = new List<(string Operator, string Command)>();

		foreach (var (command, background, text) in list.Items)
		{
			if (background)
			{
				commands.Add((Sequence, command.Rest is [] ? text : command.First.Text + string.Concat(command.Rest.Select(next => $" {next.Operator} {next.Pipeline.Text}")) + " &"));

				continue;
			}

			commands.Add((Sequence, command.First.Text));
			commands.AddRange(command.Rest.Select(next => (next.Operator, next.Pipeline.Text)));
		}

		return commands;
	}

	/// <summary>
	/// Finds what <paramref name="line" /> leaves open at its end, which the next line of a file goes on with, as
	/// POSIX reads a script.
	/// </summary>
	/// <param name="line">The line, or several joined by newlines.</param>
	/// <returns>
	/// The quote that's still open, <c>(</c> for a <c>$(</c> or <c>$((</c> or <c>`</c> for a backquote that's never
	/// closed, or <see langword="null" /> when nothing is. A quote inside a comment doesn't count, so <c># don't</c> is
	/// complete.
	/// </returns>
	internal static char? Unfinished(string line)
	{
		var (_, _, _, quote, refused, _) = CommandParser.Parse(line);

		return refused < 0 ? quote : line[refused] == '`' ? '`' : '(';
	}

	/// <summary>
	/// Checks whether <paramref name="text" /> ends inside a here-document's body.
	/// </summary>
	/// <param name="text">The lines read so far, joined by newlines.</param>
	/// <returns>
	/// <see langword="true" /> when some here-document has no delimiter line yet.
	/// </returns>
	public static bool IsHereDocumentOpen(string text) => CommandParser.Parse(text).Unclosed is not null;

	/// <summary>
	/// Checks whether a reader should add the next line to <paramref name="text" /> before running it.
	/// </summary>
	/// <param name="text">The lines read so far, joined by newlines.</param>
	/// <returns>
	/// <see langword="true" /> when some here-document has no delimiter line yet, or the text ends inside a compound
	/// command or right after <c>&amp;&amp;</c>, <c>||</c> or <c>|</c>.
	/// </returns>
	public static bool NeedsMoreLines(string text) => CommandParser.Parse(text) is { Incomplete: true } or { Unclosed: not null };

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
	/// <see cref="Split" /> refuses the line, before any of it runs.
	/// </returns>
	/// <remarks>
	/// POSIX doesn't let <c>set -e</c> act on a command followed by <c>&amp;&amp;</c> or <c>||</c>, so
	/// <c>false || b</c> is a test, not a failure. A here-document whose delimiter line never comes, at the end of a
	/// <c>-c</c> command or a file, gets the lines that were there, after a warning, as in bash.
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
	/// tested, as one followed by <c>&amp;&amp;</c> or <c>||</c> is, so <c>set -e</c> ignores a failure even inside a
	/// line that <c>eval</c> or <c>.</c> runs for the command.
	/// </param>
	/// <param name="failed">Called with the status of a failed command that isn't tested, or <see langword="null" />.</param>
	/// <param name="starting">
	/// Called right before each command runs with how many lines of <paramref name="line" /> come before it, for
	/// <c>$LINENO</c>, or <see langword="null" />.
	/// </param>
	/// <returns>
	/// The status of the last command that ran, or <see cref="UnsupportedStatus" /> when the line is refused.
	/// </returns>
	public static int Run(string line, int status, Func<string, bool, int> run, Action<int>? failed = null, Action<int>? starting = null) =>
		Run(line, status, new ShellHooks(run) { Failed = failed ?? (_ => { }) }, starting);

	/// <summary>
	/// Parses <paramref name="line" /> and runs it through <see cref="CommandRunner" /> with <paramref name="hooks" />.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="status">The status before the line, which a line with no commands gives back unchanged.</param>
	/// <param name="hooks">What the runner asks the shell to do.</param>
	/// <param name="starting">
	/// Called right before each command runs with how many lines of <paramref name="line" /> come before it, for
	/// <c>$LINENO</c>, or <see langword="null" />.
	/// </param>
	/// <returns>
	/// The status of the last command that ran, or <see cref="UnsupportedStatus" /> after printing the error when the
	/// line can't be parsed, before any of it runs. A line that isn't finished is such an error, as bash's
	/// "unexpected end of file" is.
	/// </returns>
	public static int Run(string line, int status, ShellHooks hooks, Action<int>? starting = null)
	{
		_ = line.ArgumentNotNull();

		var parsed = CommandParser.Parse(line);

		if (parsed.Error is { } error)
		{
			error.Write();

			return UnsupportedStatus;
		}

		if (parsed.Unclosed is { } unclosed)
		{
			new ShellError("sharpie::parser::unclosed_here_document", $"The here-document never got its `{unclosed}` line")
			{
				Help = $"end it with a line holding only {unclosed}; it ran with the lines it had",
			}.Write();
		}

		return new CommandRunner(hooks, starting is null ? null : start => starting(line.AsSpan(0, start).Count('\n'))).Run(parsed.List!, status);
	}
}
