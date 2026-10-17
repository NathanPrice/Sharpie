using System.Text;
using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Common;

/// <summary>
/// Splits a line into the commands of a POSIX list, joined by <c>;</c>, newlines, <c>&amp;&amp;</c> and <c>||</c>,
/// and runs them in order.
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
	/// The status of a line that uses syntax <see cref="Split" /> refuses, which bash also gives a syntax error.
	/// </summary>
	internal const int UnsupportedStatus = 2;

	/// <summary>
	/// Cuts <paramref name="line" /> into its commands at every <c>;</c>, newline, <c>&amp;&amp;</c> and <c>||</c>
	/// outside quotes.
	/// </summary>
	/// <param name="line">The line, which may hold several lines when it came from <c>-c</c>.</param>
	/// <returns>
	/// Each command's text as typed, quotes kept, with the operator before it, which is <see cref="Sequence" /> for the
	/// first. Blank commands and comments are dropped. <see langword="null" /> when the line uses command substitution,
	/// <c>$(...)</c> or backquotes, outside single quotes.
	/// </returns>
	/// <remarks>
	/// Quotes and backslashes work as in <see cref="CommandLine.Split" />, so a quoted or escaped <c>;</c> stays in its
	/// command. A single <c>|</c> or <c>&amp;</c> also stays, for the command to read as a pipe or a background job.
	/// An operator at the end of a line carries over a newline, so <c>a &amp;&amp;</c> on one line and <c>b</c> on the
	/// next run as <c>a &amp;&amp; b</c>, as POSIX allows. Command substitution is refused, not passed on as text, so
	/// a command meant to get the output of another never runs with the wrong arguments.
	/// </remarks>
	public static IReadOnlyList<(string Operator, string Command)>? Split(string line) => SplitOrRefuse(line, out _);

	/// <summary>
	/// Cuts <paramref name="line" /> into its commands, as <see cref="Split" /> describes.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="refused">
	/// When this method returns <see langword="null" />, contains where the <c>$(</c> or backquote starts; otherwise,
	/// -1.
	/// </param>
	/// <returns>
	/// The commands, or <see langword="null" /> when the line uses command substitution.
	/// </returns>
	private static List<(string Operator, string Command)>? SplitOrRefuse(string line, out int refused)
	{
		_ = line.ArgumentNotNull();

		// Stryker disable once Unary : refused is only read when the line is refused, and every refusal sets it.
		refused = -1;

		var commands = new List<(string Operator, string Command)>();
		var command = new StringBuilder();
		var before = Sequence;
		char? quote = null;
		var escaped = false;

		// A comment starts only at the start of a word, as in CommandLine.Split, so the # in a#b is kept.
		var wordStart = true;

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

			if (character == '`' || (character == '$' && index + 1 < line.Length && line[index + 1] == '('))
			{
				refused = index;

				return null;
			}

			if (quote is null && character == '#' && wordStart)
			{
				index = line.IndexOf('\n', index) is var newline and >= 0 ? newline - 1 : line.Length;

				continue;
			}

			if (quote is null && OperatorAt(line, index) is { } typed)
			{
				Cut(typed == "\n" ? Sequence : typed);
				index += typed.Length - 1;

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

		return commands;

		// Ends the command being read. A blank one is dropped, and its operator only replaces the one before it when
		// it's && or ||, which keeps "a &&" followed by a newline joined to the next line.
		void Cut(string after)
		{
			var text = command.ToString().Trim();

			if (text.Length > 0)
			{
				commands.Add((before, text));
				before = after;
			}
			else if (after != Sequence)
			{
				before = after;
			}

			_ = command.Clear();
			wordStart = true;
		}
	}

	/// <summary>
	/// Reads the list operator that starts at <paramref name="index" />, outside quotes.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="index">The position to look at.</param>
	/// <returns>
	/// <c>;</c>, a newline, <see cref="And" /> or <see cref="Or" /> as typed, or <see langword="null" /> when none
	/// starts there. A single <c>&amp;</c> or <c>|</c> isn't one.
	/// </returns>
	private static string? OperatorAt(string line, int index) => line[index] switch
	{
		';' => Sequence,
		'\n' => "\n",
		'&' or '|' when index + 1 < line.Length && line[index + 1] == line[index] => line[index] == '&' ? And : Or,
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
	/// <see cref="Split" /> refuses the line.
	/// </returns>
	/// <remarks>
	/// POSIX gives <c>&amp;&amp;</c> and <c>||</c> the same precedence, read from the left, so in
	/// <c>false &amp;&amp; a || b</c> the skipped <c>a</c> keeps the status at 1 and <c>b</c> runs. POSIX doesn't let
	/// <c>set -e</c> act on a command followed by <c>&amp;&amp;</c> or <c>||</c>, so <c>false || b</c> is a test, not a
	/// failure.
	/// </remarks>
	public static int Run(string line, int status, Func<string, int> run, Action<int>? failed = null)
	{
		_ = run.ArgumentNotNull();

		if (SplitOrRefuse(line, out var refused) is not { } commands)
		{
			new ShellError("sharpie::parser::command_substitution", "Command substitution isn't supported")
			{
				Source = line,
				Start = refused,
				End = refused + (line[refused] == '`' ? 1 : 2),
				Label = "runs a command for its output",
				Help = "run the inner command on its own line, or quote it to pass it as text",
			}.Write();

			return UnsupportedStatus;
		}

		for (var index = 0; index < commands.Count; index++)
		{
			var (before, command) = commands[index];

			if (before != Sequence && before != (status == 0 ? And : Or))
			{
				continue;
			}

			status = run(command);

			if (status != 0 && (index + 1 == commands.Count || commands[index + 1].Operator == Sequence))
			{
				failed?.Invoke(status);
			}
		}

		return status;
	}
}
