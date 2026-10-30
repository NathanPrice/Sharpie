using System.Globalization;
using System.Text;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Highlighting;

namespace Sharpie.Shell.Features.History;

/// <summary>
/// Replaces bash's history references in a typed line, like <c>!!</c>, <c>!$</c>, <c>!n</c>, <c>!-n</c>,
/// <c>!string</c> and a leading <c>^old^new</c>, with the earlier lines they name.
/// </summary>
/// <remarks>
/// shortcut: word designators (<c>!!:1</c>, <c>!*</c>, <c>!^</c>), modifiers (<c>:p</c>, <c>:s</c>, <c>:h</c>),
/// <c>!?str?</c>, <c>!#</c> and <c>&amp;</c> in <c>^old^new</c> aren't supported, and a <c>:</c> after an event is
/// reported rather than guessed at; add them when asked.
/// </remarks>
internal static class HistoryExpansion
{
	/// <summary>
	/// The characters that end a <c>!string</c> search, besides whitespace. Bash's
	/// <c>history_search_delimiter_chars</c> plus the <c>:</c> that starts a modifier.
	/// </summary>
	private const string SearchEnds = ":;&()|<>";

	/// <summary>
	/// Expands the history references in <paramref name="line" />.
	/// </summary>
	/// <param name="line">The line as typed.</param>
	/// <param name="history">The lines before this one, oldest first, numbered from 1 as <c>history</c> lists them.</param>
	/// <param name="error">Why the expansion failed, or <see langword="null" /> when it didn't.</param>
	/// <returns>
	/// The expanded line, <paramref name="line" /> itself when it has no references, or <see langword="null" /> when
	/// a reference names no line.
	/// </returns>
	/// <remarks>
	/// Every reference in the line names the history before it, so <c>echo !$!$</c> repeats the same word. A <c>!</c>
	/// stays as typed inside single quotes, after a backslash, before whitespace, <c>=</c>, <c>(</c> or the end of the
	/// line, before the <c>"</c> closing a double-quoted string, and in <c>$!</c>, <c>${!x}</c> and <c>[!a]</c>. Bash
	/// leaves <c>[!a]</c> alone only when the line has no other reference; here it always does.
	/// </remarks>
	internal static string? Expand(string line, IReadOnlyList<string> history, out ShellError? error)
	{
		error = null;
		var output = new StringBuilder();
		var index = 0;

		if (line.StartsWith('^'))
		{
			if (Substitute(line, history, out index, out error) is not { } substituted)
			{
				return null;
			}

			_ = output.Append(substituted);
		}

		var changed = index > 0;
		// Where the text after the last reference starts, so the $ of a !$ isn't read as the $ of a $! after it.
		var from = index;
		var inDouble = false;

		for (; index < line.Length; index++)
		{
			var character = line[index];

			if (character == '\\' && index + 1 < line.Length)
			{
				_ = output.Append(character).Append(line[++index]);

				continue;
			}

			// A ! in single quotes, $'...' too, is kept as typed, up to the end when the quote never closes.
			if (character == '\'' && !inDouble)
			{
				var stop = CommandLine.QuoteEnd(line, index) is var close and >= 0 ? close + 1 : line.Length;
				_ = output.Append(line, index, stop - index);
				index = stop - 1;

				continue;
			}

			inDouble ^= character == '"';

			if (character != '!' || EventEnd(line, index, from, inDouble) is not (var end and >= 0))
			{
				_ = output.Append(character);

				continue;
			}

			var designator = line[(index + 1)..end];

			if (Resolve(designator, history) is not { } found)
			{
				error = Fail("sharpie::history::event_not_found", $"History event `!{designator}` not found", line, index, end, "nothing in history matches this");

				return null;
			}

			if (end < line.Length && line[end] == ':')
			{
				error = Fail("sharpie::history::unsupported", "Word designators and modifiers after a history event aren't supported", line, end, end + 1, "not supported");

				return null;
			}

			_ = output.Append(found);
			changed = true;
			from = end;
			index = end - 1;
		}

		return changed ? output.ToString() : line;
	}

	/// <summary>
	/// Finds where the history reference starting at the <c>!</c> at <paramref name="bang" /> ends.
	/// </summary>
	/// <param name="line">The typed line.</param>
	/// <param name="bang">The index of a <c>!</c> outside single quotes and not after a backslash.</param>
	/// <param name="from">Where the typed text before the <c>!</c> starts, after any earlier reference.</param>
	/// <param name="inDouble"><see langword="true" /> when the <c>!</c> is inside double quotes.</param>
	/// <returns>
	/// One past the reference's last character, or -1 when the <c>!</c> stays as typed.
	/// </returns>
	private static int EventEnd(string line, int bang, int from, bool inDouble)
	{
		var next = bang + 1;
		var before = line.AsSpan(from, bang - from);

		if (next == line.Length || line[next] is '=' or '(' || (inDouble && line[next] == '"')
			|| before is [.., '$'] or [.., '$', '{']
			|| (before is [.., '['] && line.AsSpan(next).Contains(']')))
		{
			return -1;
		}

		if (line[next] is '!' or '$')
		{
			return next + 1;
		}

		var digits = line[next] == '-' ? next + 1 : next;
		var end = digits;

		while (end < line.Length && char.IsAsciiDigit(line[end]))
		{
			end++;
		}

		if (end > digits)
		{
			return end;
		}

		end = next;

		while (end < line.Length && !char.IsWhiteSpace(line[end]) && !SearchEnds.Contains(line[end], StringComparison.Ordinal) && !(inDouble && line[end] == '"'))
		{
			end++;
		}

		// Bash leaves a lone ! before whitespace, ; or | as typed.
		return end > next ? end : -1;
	}

	/// <summary>
	/// Finds the text a history reference names.
	/// </summary>
	/// <param name="designator">What follows the <c>!</c>: <c>!</c>, <c>$</c>, a number, or the start of a line.</param>
	/// <param name="history">The lines before this one, oldest first.</param>
	/// <returns>
	/// The line or word named, or <see langword="null" /> when there's none.
	/// </returns>
	private static string? Resolve(string designator, IReadOnlyList<string> history)
	{
		if (history.Count == 0)
		{
			return null;
		}

		var last = history[^1];

		if (designator == "!")
		{
			return last;
		}

		if (designator == "$")
		{
			return Highlighter.SplitRaw(last) is [.., var (start, end, _)] ? last[start..end] : null;
		}

		if (designator[0] != '+' && int.TryParse(designator, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
		{
			// !0 and !-0 both land past the newest line, so neither names one.
			// Stryker disable once Equality : at 0 both sides give an index out of range.
			var at = number > 0 ? number - 1 : history.Count + number;

			return at >= 0 && at < history.Count ? history[at] : null;
		}

		for (var at = history.Count - 1; at >= 0; at--)
		{
			if (history[at].StartsWith(designator, StringComparison.Ordinal))
			{
				return history[at];
			}
		}

		return null;
	}

	/// <summary>
	/// Applies the <c>^old^new^</c> at the start of <paramref name="line" /> to the previous line.
	/// </summary>
	/// <param name="line">The typed line, starting with <c>^</c>.</param>
	/// <param name="history">The lines before this one, oldest first.</param>
	/// <param name="rest">Where the text after the closing <c>^</c> starts, which is expanded as usual.</param>
	/// <param name="error">Why it failed, or <see langword="null" />.</param>
	/// <returns>
	/// The previous line with the first <c>old</c> replaced, or <see langword="null" /> when it can't be.
	/// </returns>
	/// <remarks>
	/// Without a second or third <c>^</c>, <c>new</c> or <c>old</c> runs to the end of the line, as in bash.
	/// </remarks>
	private static string? Substitute(string line, IReadOnlyList<string> history, out int rest, out ShellError? error)
	{
		var parts = line[1..].Split('^', 3);
		var old = parts[0];
		var replacement = parts.Length > 1 ? parts[1] : string.Empty;

		rest = parts.Length > 2 ? line.Length - parts[2].Length : line.Length;
		error = null;

		if (history.Count == 0)
		{
			error = Fail("sharpie::history::event_not_found", "History event `!!` not found", line, 0, rest, "history is empty");

			return null;
		}

		var previous = history[^1];
		var at = old.Length == 0 ? -1 : previous.IndexOf(old, StringComparison.Ordinal);

		if (at < 0)
		{
			error = Fail("sharpie::history::substitution_failed", $"Substitution `{line[..rest]}` failed", line, 0, rest,
				old.Length == 0 ? "nothing to replace" : $"`{old}` isn't in the last line");

			return null;
		}

		return string.Concat(previous.AsSpan(0, at), replacement, previous.AsSpan(at + old.Length));
	}

	/// <summary>
	/// Builds an error that underlines part of the typed line.
	/// </summary>
	/// <param name="code">The error's code.</param>
	/// <param name="summary">What went wrong.</param>
	/// <param name="line">The typed line.</param>
	/// <param name="start">Where the underline starts.</param>
	/// <param name="end">One past where it ends.</param>
	/// <param name="label">The note under it.</param>
	/// <returns>
	/// The error.
	/// </returns>
	private static ShellError Fail(string code, string summary, string line, int start, int end, string label) =>
		new(code, summary) { Source = line, Start = start, End = end, Label = label };
}
