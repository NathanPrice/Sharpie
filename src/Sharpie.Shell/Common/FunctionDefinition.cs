using System.Text.RegularExpressions;
using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Common;

/// <summary>
/// Reads a shell function's definition: <c>name() { ...; }</c>, <c>name () {</c>, <c>function name {</c> or
/// <c>function name() {</c>.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="CommandList" />, which uses it to join a definition's commands back into one, so a
/// parser that reads compound commands can take it over whole.
/// </remarks>
internal static partial class FunctionDefinition
{
	/// <summary>
	/// Reads the name, body and what comes after the body out of a whole definition.
	/// </summary>
	/// <param name="command">One command, which <see cref="CommandList" /> has joined back together when its body ran over several commands.</param>
	/// <returns>
	/// The function's name, the text between its <c>{</c> and its last <c>}</c>, and the text after that <c>}</c>. Or
	/// <see langword="null" /> when <paramref name="command" /> doesn't start with a header and a <c>{</c> word, or has
	/// no <c>}</c> word to end the body.
	/// </returns>
	public static (string Name, string Body, string After)? Parse(string command)
	{
		var header = HeaderRegex().Match(command.ArgumentNotNull());

		if (!header.Groups["brace"].Success)
		{
			return null;
		}

		// The } that ends the body is the last one that starts a word.
		var close = command.Length - 1;

		while (close >= header.Length && (command[close] != '}' || !(char.IsWhiteSpace(command[close - 1]) || command[close - 1] == ';')))
		{
			close--;
		}

		// Stryker disable once Equality : close never stops at header.Length, where the character before it is the {.
		return close < header.Length ? null : (header.Groups["name"].Value, command[header.Length..close], command[(close + 1)..]);
	}

	/// <summary>
	/// Checks whether <paramref name="command" /> starts a function definition.
	/// </summary>
	/// <param name="command">One command, trimmed.</param>
	/// <returns>
	/// <see langword="true" /> for a header followed by a <c>{</c> word, or for a header with nothing after it, whose
	/// <c>{</c> comes in the next command.
	/// </returns>
	public static bool IsHeader(string command) =>
		HeaderRegex().Match(command.ArgumentNotNull()) is { Success: true } header
		&& (header.Groups["brace"].Success || header.Length == command.Length);

	/// <summary>
	/// Counts the braces that <paramref name="command" /> opens or closes at its start.
	/// </summary>
	/// <param name="command">One command, trimmed.</param>
	/// <returns>
	/// One for each function header with its <c>{</c> and each <c>{</c> word, less one for each <c>}</c> word, read
	/// from the start until some other word comes. So <c>f() { { a</c> gives 2 and <c>} &gt; out</c> gives -1.
	/// </returns>
	/// <remarks>
	/// Only the start counts, since a brace that stands anywhere else is an argument, as in <c>echo }</c>.
	/// </remarks>
	public static int Braces(string command)
	{
		_ = command.ArgumentNotNull();

		var count = 0;
		var position = 0;

		while (true)
		{
			while (position < command.Length && char.IsWhiteSpace(command[position]))
			{
				position++;
			}

			if (HeaderRegex().Match(command, position) is { Groups: var groups } header && groups["brace"].Success)
			{
				count++;
				position = header.Index + header.Length;

				continue;
			}

			var end = position;

			while (end < command.Length && !char.IsWhiteSpace(command[end]))
			{
				end++;
			}

			switch (command[position..end])
			{
				case "{":
					count++;
					break;
				case "}":
					count--;
					break;
				default:
					return count;
			}

			position = end;
		}
	}

	/// <summary>
	/// Matches a function header at the start of the text, and the <c>{</c> word after it when there is one.
	/// </summary>
	/// <returns>
	/// A compiled regex with the groups <c>name</c> and <c>brace</c>.
	/// </returns>
	/// <remarks>
	/// <c>\G</c> anchors it where the match starts, so <see cref="Braces" /> can look for a header partway in. A name
	/// can hold anything but blanks and the characters the shell gives a meaning, so <c>git-clean</c> works, as it
	/// does in bash. <c>=</c> is left out so an assignment like <c>a=(1)</c> is never read as one. The <c>{</c> must
	/// stand as its own word, so <c>f(){x</c> isn't a definition.
	/// </remarks>
	[GeneratedRegex("""\G(?:function\s+(?<name>[^\s()<>|&;'"$\\`={}]+)(?:\s*\(\s*\)|(?=\s|\z))|(?<name>[^\s()<>|&;'"$\\`={}]+)\s*\(\s*\))\s*(?<brace>\{(?=\s|\z))?""")]
	private static partial Regex HeaderRegex();
}
