using System.Globalization;
using System.Text;
using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Common;

/// <summary>
/// An error the shell reports in the layout Nushell uses: a code, a message, the typed text with the bad part
/// underlined and labeled when there is one, and a hint.
/// </summary>
/// <param name="Code">The error's code, like <c>sharpie::shell::command_not_found</c>.</param>
/// <param name="Summary">What went wrong, as one sentence, shown after the ×.</param>
/// <remarks>
/// Nushell draws its errors with the Rust <c>miette</c> crate. This copies its default layout:
/// <code>
/// Error: sharpie::shell::command_not_found
///
///   × Command `gti` not found
///    ╭─[1:1]
///  1 │ gti status
///    · ─┬─
///    ·  ╰── not a builtin, alias or program on PATH
///    ╰────
///   help: did you mean `git`?
/// </code>
/// </remarks>
internal sealed record ShellError(string Code, string Summary)
{
	private const string Red = "\e[31m";

	private const string Magenta = "\e[35m";

	private const string Cyan = "\e[36m";

	private const string Reset = "\e[0m";

	/// <summary>
	/// Gets or sets a value that indicates whether <see cref="Write" /> colors the report.
	/// </summary>
	/// <value>
	/// <see langword="true" /> while standard error goes to a terminal; otherwise, <see langword="false" />. The
	/// default is <see langword="false" />.
	/// </value>
	public static bool UseColor { get; set; }

	/// <summary>
	/// Gets the hint shown on the <c>help:</c> line.
	/// </summary>
	/// <value>
	/// The hint, or <see langword="null" /> for no <c>help:</c> line.
	/// </value>
	public string? Help { get; init; }

	/// <summary>
	/// Gets the text the user typed, which <see cref="Start" /> and <see cref="End" /> point into.
	/// </summary>
	/// <value>
	/// The typed text, or <see langword="null" /> when the error isn't about a place in it.
	/// </value>
	public string? Source { get; init; }

	/// <summary>
	/// Gets where the underlined part starts in <see cref="Source" />.
	/// </summary>
	/// <value>
	/// A character index into <see cref="Source" />.
	/// </value>
	public int Start { get; init; }

	/// <summary>
	/// Gets where the underlined part ends in <see cref="Source" />.
	/// </summary>
	/// <value>
	/// One past the last underlined character. At or before <see cref="Start" />, one character is underlined.
	/// </value>
	public int End { get; init; }

	/// <summary>
	/// Gets the note drawn under the underlined part.
	/// </summary>
	/// <value>
	/// The note, or <see langword="null" /> for none.
	/// </value>
	public string? Label { get; init; }

	/// <summary>
	/// Finds the names in <paramref name="candidates" /> that look like a typo of <paramref name="name" />.
	/// </summary>
	/// <param name="name">The name the user typed.</param>
	/// <param name="candidates">The names that exist, which may repeat.</param>
	/// <returns>
	/// The close names, closest first, each once. Among equally close names, one made of the same letters comes
	/// first, then the order of <paramref name="candidates" />. Empty when none is close.
	/// </returns>
	/// <remarks>
	/// A name is close when it is at most a third of <paramref name="name" />'s length of edits away, and at least one.
	/// That is the rule rustc uses, which Nushell's <c>did_you_mean</c> follows. Swapping two neighboring letters counts
	/// as one edit, and the same-letters rule breaks ties, so <c>gti</c> suggests <c>git</c> rather than <c>gtf</c>.
	/// Candidates whose length alone puts them past the limit are skipped before any comparing, which is most of what's
	/// on <c>PATH</c>.
	/// </remarks>
	public static IReadOnlyList<string> DidYouMean(string name, IEnumerable<string> candidates)
	{
		_ = name.ArgumentNotNull();
		_ = candidates.ArgumentNotNull();

		var limit = Math.Max(1, name.Length / 3);
		var matches = new List<(int Distance, bool OtherLetters, string Name)>();

		foreach (var candidate in candidates)
		{
			if (Math.Abs(candidate.Length - name.Length) <= limit && candidate != name
				&& EditDistance(name, candidate, limit) is var distance && distance <= limit)
			{
				matches.Add((distance, candidate.Length != name.Length || !candidate.Order().SequenceEqual(name.Order()), candidate));
			}
		}

		return [.. matches.OrderBy(match => match.Distance).ThenBy(match => match.OtherLetters).Select(match => match.Name).Distinct(StringComparer.Ordinal)];
	}

	/// <summary>
	/// Writes the report to <see cref="Console.Error" />, colored when <see cref="UseColor" /> is set.
	/// </summary>
	public void Write() => Console.Error.Write(this.Render(UseColor));

	/// <summary>
	/// Builds the report.
	/// </summary>
	/// <param name="color"><see langword="true" /> to add terminal colors; otherwise, <see langword="false" />.</param>
	/// <returns>
	/// The report's lines, each ending in a newline.
	/// </returns>
	public string Render(bool color)
	{
		var (red, magenta, cyan, reset) = color ? (Red, Magenta, Cyan, Reset) : (string.Empty, string.Empty, string.Empty, string.Empty);
		var text = new StringBuilder();

		_ = text.Append("Error: ").Append(red).Append(this.Code).Append(reset).Append("\n\n");
		_ = text.Append("  ").Append(red).Append('×').Append(reset).Append(' ').Append(this.Summary).Append('\n');

		if (this.Source is { } source)
		{
			// shortcut: columns count UTF-16 characters, so a tab or a wide character before the span shifts the
			// underline; measure display width if that shows up in real errors.
			var start = Math.Clamp(this.Start, 0, source.Length);
			var lineStart = start == 0 ? 0 : source.LastIndexOf('\n', start - 1) + 1;
			var lineEnd = source.IndexOf('\n', start) is var newline and >= 0 ? newline : source.Length;
			var lineNumber = (source.AsSpan(0, lineStart).Count('\n') + 1).ToString(CultureInfo.InvariantCulture);
			var column = start - lineStart;
			var length = Math.Max(1, Math.Min(this.End, lineEnd) - start);
			var middle = length / 2;
			var gutter = new string(' ', lineNumber.Length + 2);

			_ = text.Append(gutter).Append("╭─[").Append(lineNumber).Append(':').Append(column + 1).Append("]\n");
			_ = text.Append(' ').Append(lineNumber).Append(" │ ").Append(source, lineStart, lineEnd - lineStart).Append('\n');
			_ = text.Append(gutter).Append("· ").Append(' ', column).Append(magenta)
				.Append('─', middle).Append('┬').Append('─', length - middle - 1).Append(reset).Append('\n');
			_ = text.Append(gutter).Append("· ").Append(' ', column + middle).Append(magenta)
				.Append("╰── ").Append(this.Label).Append(reset).Append('\n');
			_ = text.Append(gutter).Append("╰────\n");
		}

		if (this.Help is { } help)
		{
			_ = text.Append("  ").Append(cyan).Append("help:").Append(reset).Append(' ').Append(help).Append('\n');
		}

		return text.ToString();
	}

	/// <summary>
	/// Counts the single-character inserts, deletes, replacements and swaps of two neighbors that turn
	/// <paramref name="first" /> into <paramref name="second" />, stopping once the count is sure to pass
	/// <paramref name="limit" />.
	/// </summary>
	/// <param name="first">One name.</param>
	/// <param name="second">The other name.</param>
	/// <param name="limit">The largest count the caller accepts.</param>
	/// <returns>
	/// The count, or some number above <paramref name="limit" /> when it is larger.
	/// </returns>
	/// <remarks>
	/// A swapped pair is never edited again, which is the restricted form of Damerau-Levenshtein. It needs only the
	/// last three rows of the usual table.
	/// </remarks>
	private static int EditDistance(string first, string second, int limit)
	{
		var width = second.Length + 1;

		// File names on PATH are short, so the rows nearly always fit on the stack.
		// Stryker disable once Conditional,Equality : the stack and the heap give the same rows; only the speed differs.
		var rows = width <= 256 ? stackalloc int[width * 3] : new int[width * 3];
		var twoBack = rows[..width];
		var previous = rows[width..(width * 2)];
		var current = rows[(width * 2)..];

		for (var column = 0; column < width; column++)
		{
			previous[column] = column;
		}

		for (var line = 1; line <= first.Length; line++)
		{
			var smallest = current[0] = line;

			for (var column = 1; column < width; column++)
			{
				var value = Math.Min(
					Math.Min(previous[column], current[column - 1]) + 1,
					previous[column - 1] + (first[line - 1] == second[column - 1] ? 0 : 1));

				if (line > 1 && column > 1 && first[line - 1] == second[column - 2] && first[line - 2] == second[column - 1])
				{
					value = Math.Min(value, twoBack[column - 2] + 1);
				}

				current[column] = value;
				smallest = Math.Min(smallest, value);
			}

			// Each row's smallest value is at least the one before it, so no later row can come back under the limit.
			// Stryker disable once Block : without the early stop the count is the same, just slower to reach.
			if (smallest > limit)
			{
				return smallest;
			}

			var spare = twoBack;

			twoBack = previous;
			previous = current;
			current = spare;
		}

		return previous[^1];
	}
}
