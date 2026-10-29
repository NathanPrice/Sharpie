using System.Text;
using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Features.Completion;

/// <summary>
/// One word of the line up to the cursor, as Tab sees it.
/// </summary>
/// <param name="Start">Where the word starts in the line, quotes included.</param>
/// <param name="End">Where the word ends.</param>
/// <param name="Value">
/// The text Tab completes, with quotes and backslashes taken out: the whole word, or only the part after a quote that
/// is still open.
/// </param>
/// <param name="ReplaceFrom">
/// Where the text Tab replaces starts: <paramref name="Start" />, or just after a quote that is still open, as readline
/// does.
/// </param>
/// <param name="Quote">The quote still open at the end of the word, or <see langword="null" />.</param>
/// <param name="IsBreak">
/// <see langword="true" /> for a run of word-break characters such as <c>=</c> or <c>:</c>, which bash keeps as a word
/// of its own.
/// </param>
internal readonly record struct CompletionWord(int Start, int End, string Value, int ReplaceFrom, char? Quote, bool IsBreak = false);

/// <summary>
/// Splits a line into words the way bash's readline does for Tab, honoring quotes and <c>COMP_WORDBREAKS</c>.
/// </summary>
internal static class CompletionWords
{
	/// <summary>
	/// Bash's default <c>COMP_WORDBREAKS</c>: blanks plus the characters that end a word for completion.
	/// </summary>
	public const string DefaultBreaks = " \t\n\"'><=;|&(:";

	/// <summary>
	/// Splits <paramref name="line" /> into words.
	/// </summary>
	/// <param name="line">The line up to the cursor.</param>
	/// <param name="breaks">The word-break characters, as in <c>COMP_WORDBREAKS</c>.</param>
	/// <returns>
	/// The words in order, each run of non-blank break characters as a word of its own. The quote characters always
	/// quote, even when <paramref name="breaks" /> lists them, as in readline. Ends with an empty word at the end of
	/// the line when the line is empty or ends in a blank or a break, so the last word is always the one Tab completes.
	/// </returns>
	public static List<CompletionWord> Split(string line, string breaks)
	{
		_ = line.ArgumentNotNull();
		_ = breaks.ArgumentNotNull();

		var words = new List<CompletionWord>();
		var index = 0;

		while (index < line.Length)
		{
			var character = line[index];

			if (char.IsWhiteSpace(character))
			{
				index++;
			}
			else if (IsBreak(character, breaks))
			{
				var start = index;

				while (index < line.Length && !char.IsWhiteSpace(line[index]) && IsBreak(line[index], breaks))
				{
					index++;
				}

				words.Add(new CompletionWord(start, index, line[start..index], start, null, IsBreak: true));
			}
			else
			{
				words.Add(ReadWord(line, ref index, breaks));
			}
		}

		if (words is [] or [.., { IsBreak: true }] || (words is [.., var last] && last.End < line.Length))
		{
			words.Add(new CompletionWord(line.Length, line.Length, string.Empty, line.Length, null));
		}

		return words;
	}

	/// <summary>
	/// Reads one ordinary word starting at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="index">Where the word starts. Moved past its end.</param>
	/// <param name="breaks">The word-break characters.</param>
	/// <returns>
	/// The word.
	/// </returns>
	private static CompletionWord ReadWord(string line, ref int index, string breaks)
	{
		var start = index;
		var value = new StringBuilder();
		char? quote = null;
		var quoteAt = 0;
		var quotedFrom = 0;

		for (; index < line.Length; index++)
		{
			var character = line[index];

			if (quote == '\'')
			{
				if (character == '\'')
				{
					quote = null;
				}
				else
				{
					_ = value.Append(character);
				}
			}
			else if (quote == '"')
			{
				// Inside double quotes a backslash only escapes the characters that are special there.
				if (character == '"')
				{
					quote = null;
				}
				else
				{
					_ = value.Append(character == '\\' && index + 1 < line.Length && line[index + 1] is '$' or '`' or '"' or '\\' or '\n' ? line[++index] : character);
				}
			}
			else if (character == '\\')
			{
				// A backslash at the very end has nothing to escape yet, so it adds nothing.
				if (index + 1 < line.Length)
				{
					_ = value.Append(line[++index]);
				}
			}
			else if (character is '\'' or '"')
			{
				quote = character;
				quoteAt = index;
				quotedFrom = value.Length;
			}
			else if (char.IsWhiteSpace(character) || IsBreak(character, breaks))
			{
				break;
			}
			else
			{
				_ = value.Append(character);
			}
		}

		return quote is null
			? new CompletionWord(start, index, value.ToString(), start, null)
			: new CompletionWord(start, index, value.ToString(quotedFrom, value.Length - quotedFrom), quoteAt + 1, quote);
	}

	/// <summary>
	/// Checks whether <paramref name="character" /> ends a word outside quotes.
	/// </summary>
	/// <param name="character">The character.</param>
	/// <param name="breaks">The word-break characters.</param>
	/// <returns>
	/// <see langword="true" /> when <paramref name="breaks" /> lists it and it isn't a quote.
	/// </returns>
	private static bool IsBreak(char character, string breaks) => character is not ('\'' or '"') && breaks.Contains(character, StringComparison.Ordinal);
}
