namespace Sharpie.Shell.Common;

/// <content>
/// Where a single quote ends, the one rule every scanner of the line uses, since a <c>$'...'</c> quote can hold an
/// escaped <c>\'</c>.
/// </content>
internal static partial class CommandLine
{
	/// <summary>
	/// Finds the <c>'</c> that closes the single quote opening at <paramref name="index" />.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="index">The position of a <c>'</c> outside quotes.</param>
	/// <returns>
	/// The position of the closing <c>'</c>, or -1 when there is none. Inside a <c>$'...'</c> quote a backslash
	/// escapes the character after it, so <c>$'it\'s'</c> ends at its last <c>'</c>.
	/// </returns>
	internal static int QuoteEnd(string text, int index)
	{
		if (!OpensAnsiQuote(text, index))
		{
			return text.IndexOf('\'', index + 1);
		}

		for (var end = index + 1; end < text.Length; end++)
		{
			if (text[end] == '\\')
			{
				end++;
			}
			else if (text[end] == '\'')
			{
				return end;
			}
		}

		return -1;
	}

	/// <summary>
	/// Checks whether the <c>'</c> at <paramref name="index" /> opens a <c>$'...'</c> quote.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="index">The position of a <c>'</c> outside quotes.</param>
	/// <returns>
	/// <see langword="true" /> when a <c>$</c> of its own comes right before it. In a run of <c>$</c> each pair is a
	/// <c>$$</c>, and an odd run of backslashes before the run escapes its first <c>$</c>, so <c>$$'a'</c> and
	/// <c>\$'a'</c> are plain quotes and <c>$$$'a'</c> isn't.
	/// </returns>
	/// <remarks>
	/// A <c>$</c> right before an unquoted <c>'</c> is itself unquoted, since any quote it was in would have to close
	/// between them.
	/// </remarks>
	private static bool OpensAnsiQuote(string text, int index)
	{
		var before = text.AsSpan(0, index);
		var dollars = before.Length - before.TrimEnd('$').Length;
		var rest = before[..^dollars];
		var backslashes = rest.Length - rest.TrimEnd('\\').Length;

		return (dollars - (backslashes % 2)) % 2 == 1;
	}
}
