using System.Globalization;

namespace Sharpie.Shell.Common;

/// <content>
/// Brace expansion, which turns <c>a{b,c}</c> into <c>ab</c> and <c>ac</c> and <c>{1..3}</c> into <c>1</c>,
/// <c>2</c> and <c>3</c>, following bash's braces.c.
/// </content>
internal static partial class CommandLine
{
	/// <summary>
	/// The unquoted characters that end a typed word.
	/// </summary>
	private const string WordEnds = " \t\n|&;<>()";

	/// <summary>
	/// Expands the braces in a word as typed, before any other expansion, as bash does.
	/// </summary>
	/// <param name="word">The word as typed, quotes and all.</param>
	/// <returns>
	/// The words it gives, quotes still in them, or <see langword="null" /> when it holds no brace expression.
	/// </returns>
	/// <remarks>
	/// <para>
	/// A <c>{</c> starts an expression when a later <c>}</c> closes it with a <c>,</c> or a sequence such as
	/// <c>1..5</c> between them, outside quotes, escapes, <c>${...}</c> and substitutions and outside nested braces.
	/// Otherwise it stays as typed and the next <c>{</c> is tried, so <c>{a{b,c}}</c> gives <c>{ab}</c> and
	/// <c>{ac}</c>. Each choice of a list is expanded again, and the words come out in order, not sorted.
	/// </para>
	/// <para>
	/// A sequence <c>x..y</c> or <c>x..y..step</c> counts between two whole numbers or two letters, either way, by the
	/// size of the step. A step of 0 counts by 1. When either number has a leading zero, as in <c>{01..10}</c>, each
	/// number is zero-padded to the longer one's width. A sequence that isn't valid, such as <c>{a..5}</c>, stays as
	/// typed, and the braces after it still expand.
	/// </para>
	/// </remarks>
	internal static string[]? ExpandBraces(string word)
	{
		var words = BraceWords(word);

		return words is [var only] && only == word ? null : [.. words];
	}

	/// <summary>
	/// Expands the first brace expression in <paramref name="text" />, and then the rest, as bash's
	/// <c>brace_expand</c> does.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <returns>
	/// The words, or <paramref name="text" /> alone when it holds no brace expression.
	/// </returns>
	private static List<string> BraceWords(string text)
	{
		int open, close;
		bool sequence;

		for (var from = 0; ; from = open + 1)
		{
			open = BraceGobble(text, from, '{', out _);

			if (open < 0)
			{
				return [text];
			}

			close = BraceGobble(text, open + 1, '}', out sequence);

			if (close >= 0 && (!sequence || IsSequenceStart(text, open + 1, close)))
			{
				break;
			}
		}

		var amble = text[(open + 1)..close];
		List<string> choices;

		if (!sequence)
		{
			choices = BraceChoices(amble);
		}
		else
		{
			// A sequence that isn't valid stays as typed, braces and all.
			choices = BraceSequence(amble) ?? [text[open..(close + 1)]];
		}

		var rest = BraceWords(text[(close + 1)..]);
		var preamble = text[..open];

		return [.. from choice in choices from after in rest select preamble + choice + after];
	}

	/// <summary>
	/// Splits the text between a list's braces at its top-level commas and expands each choice.
	/// </summary>
	/// <param name="amble">The text between the braces.</param>
	/// <returns>
	/// The words of each choice in turn.
	/// </returns>
	private static List<string> BraceChoices(string amble)
	{
		var choices = new List<string>();
		var start = 0;

		for (var comma = BraceGobble(amble, start, ',', out _); comma >= 0; comma = BraceGobble(amble, start, ',', out _))
		{
			choices.AddRange(BraceWords(amble[start..comma]));
			start = comma + 1;
		}

		choices.AddRange(BraceWords(amble[start..]));

		return choices;
	}

	/// <summary>
	/// Finds the first <paramref name="satisfy" /> outside quotes and nested braces, as bash's
	/// <c>brace_gobbler</c> does.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="start">Where to start looking, outside any quote.</param>
	/// <param name="satisfy">
	/// <c>{</c> for the next open brace, <c>,</c> for the next comma, or <c>}</c> for the close brace of a valid
	/// expression, which needs a comma or a <c>..</c> before it.
	/// </param>
	/// <param name="sequence">
	/// For <c>}</c>, <see langword="true" /> when a <c>..</c> came before any comma, so the braces hold a sequence.
	/// </param>
	/// <returns>
	/// Its position, or -1 when there is none.
	/// </returns>
	private static int BraceGobble(string text, int start, char satisfy, out bool sequence)
	{
		var level = 0;
		var valid = satisfy != '}';

		sequence = false;

		for (var index = start; (index = UnquotedIndex(text, index, "{},.")) >= 0; index++)
		{
			var character = text[index];

			if (character == satisfy && level == 0 && valid)
			{
				// Like bash, a { that stands alone or right before a } is just text.
				if (character != '{' || !((index == 0 || IsBlank(text[index - 1])) && (IsBlank(CharAt(text, index + 1)) || CharAt(text, index + 1) == '}')))
				{
					return index;
				}
			}
			else if (character == '{')
			{
				level++;
			}
			else if (character == '}' && level > 0)
			{
				level--;
			}
			else if (level == 0 && character == ',')
			{
				valid = true;
				sequence = false;
			}
			else if (level == 0 && !valid && text.AsSpan(index).StartsWith("..", StringComparison.Ordinal) && CharAt(text, index + 2) != '}')
			{
				valid = true;
				sequence = true;
			}
		}

		return -1;
	}

	/// <summary>
	/// Checks that the text between braces starts like a sequence, two numbers or two letters around <c>..</c>, as
	/// bash's <c>valid_seqterm</c> does, so the braces count as an expression.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="start">Where the text after the <c>{</c> starts.</param>
	/// <param name="close">The position of the <c>}</c>.</param>
	/// <returns>
	/// <see langword="true" /> when it does. A sequence that only starts right, such as <c>{1..2{a,b}}</c>, still
	/// counts, and then stays as typed.
	/// </returns>
	private static bool IsSequenceStart(string text, int start, int close)
	{
		// BraceGobble found a .. before the }, so there is one.
		var dots = text.IndexOf("..", start, close - start, StringComparison.Ordinal);
		var numbers = StartsNumber(text, start) && StartsNumber(text, dots + 2);
		var letters = char.IsAsciiLetter(text[start]) && CharAt(text, start + 1) == '.'
			&& char.IsAsciiLetter(text[dots + 2]) && CharAt(text, dots + 3) is '}' or '.';

		return numbers || letters;
	}

	/// <summary>
	/// Gives the words of a sequence, as bash's <c>expand_seqterm</c> and <c>mkseq</c> do.
	/// </summary>
	/// <param name="amble">The text between the braces, such as <c>1..10..2</c>.</param>
	/// <returns>
	/// The words, or <see langword="null" /> when it isn't a valid sequence or is too long to hold.
	/// </returns>
	private static List<string>? BraceSequence(string amble)
	{
		var dots = amble.IndexOf("..", StringComparison.Ordinal);
		var left = amble[..dots];
		var right = dots + 2;

		bool letters;
		int lastEnd;

		if (long.TryParse(left, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var first)
			&& ReadNumber(amble, right, out var last) is var end and > 0 && CharAt(amble, end) is '\0' or '.')
		{
			(letters, lastEnd) = (false, end);
		}
		else if (left.Length == 1 && char.IsAsciiLetter(left[0]))
		{
			(letters, first, last, lastEnd) = (true, left[0], amble[right], right + 1);
		}
		else
		{
			return null;
		}

		var step = 1L;
		var stepEnd = lastEnd;

		if (amble.AsSpan(lastEnd).StartsWith("..", StringComparison.Ordinal))
		{
			stepEnd = ReadNumber(amble, lastEnd + 2, out step);
		}

		// bash gives up when the step or the distance doesn't fit in its 64-bit numbers.
		var distance = Int128.Abs((Int128)last - first);

		if (stepEnd != amble.Length || step == long.MinValue || distance > long.MaxValue)
		{
			return null;
		}

		var size = Int128.Abs(step == 0 ? 1 : step);
		var count = (distance / size) + 1;

		if (count >= Array.MaxLength)
		{
			return null;
		}

		var width = letters ? 0 : PadWidth(left, amble[right..lastEnd]);
		var words = new List<string>((int)count);

		for (Int128 number = first, index = 0; index < count; index++, number += last < first ? -size : size)
		{
			words.Add(letters ? ((char)(int)number).ToString() : Padded(number, width));
		}

		return words;
	}

	/// <summary>
	/// Works out how wide each number of a sequence is padded, as bash does.
	/// </summary>
	/// <param name="left">The first number as typed.</param>
	/// <param name="right">The last number as typed.</param>
	/// <returns>
	/// The longer one's length when either starts with <c>0</c> or <c>-0</c> and has more digits after it, else 0.
	/// </returns>
	private static int PadWidth(string left, string right) =>
		HasLeadingZero(left) || HasLeadingZero(right) ? Math.Max(left.Length, right.Length) : 0;

	/// <summary>
	/// Checks whether a number as typed starts with a zero that pads, such as <c>01</c> or <c>-05</c>.
	/// </summary>
	/// <param name="number">The number.</param>
	/// <returns>
	/// <see langword="true" /> when it does. A lone <c>0</c> or <c>-0</c> doesn't.
	/// </returns>
	private static bool HasLeadingZero(string number) =>
		(number.Length > 1 && number[0] == '0') || (number.Length > 2 && number[0] == '-' && number[1] == '0');

	/// <summary>
	/// Writes a number zero-padded to a width, the zeros after any minus sign, as C's <c>%0*d</c> does.
	/// </summary>
	/// <param name="number">The number.</param>
	/// <param name="width">The width, or 0 for none.</param>
	/// <returns>
	/// The text.
	/// </returns>
	private static string Padded(Int128 number, int width) =>
		number < 0
			? "-" + Int128.Abs(number).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(width - 1, 0), '0')
			: number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

	/// <summary>
	/// Checks whether a whole number starts at <paramref name="index" />: a digit, or <c>+</c> or <c>-</c> and a
	/// digit.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="index">The position.</param>
	/// <returns>
	/// <see langword="true" /> when one does.
	/// </returns>
	private static bool StartsNumber(string text, int index) =>
		char.IsAsciiDigit(CharAt(text, index)) || (CharAt(text, index) is '+' or '-' && char.IsAsciiDigit(CharAt(text, index + 1)));

	/// <summary>
	/// Reads the whole number at <paramref name="start" />, a sign and digits, as C's <c>strtoimax</c> does.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="start">Where it starts.</param>
	/// <param name="value">The number, or 0 when there is none.</param>
	/// <returns>
	/// Just past its last digit, or -1 when there are no digits or it doesn't fit in a <see langword="long" />.
	/// </returns>
	private static int ReadNumber(string text, int start, out long value)
	{
		var end = CharAt(text, start) is '+' or '-' ? start + 1 : start;
		while (char.IsAsciiDigit(CharAt(text, end)))
		{
			end++;
		}

		return long.TryParse(text.AsSpan(start, end - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) ? end : -1;
	}

	/// <summary>
	/// Gets the character at <paramref name="index" />, or NUL past the end, as C reads a string.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="index">The position.</param>
	/// <returns>
	/// The character, or <c>'\0'</c>.
	/// </returns>
	private static char CharAt(string text, int index) => index < text.Length ? text[index] : '\0';

	/// <summary>
	/// Checks whether a character is a space, tab or newline, as bash's <c>brace_whitespace</c> does.
	/// </summary>
	/// <param name="character">The character.</param>
	/// <returns>
	/// <see langword="true" /> when it is.
	/// </returns>
	private static bool IsBlank(char character) => character is ' ' or '\t' or '\n';
}
