using System.Text;

namespace Sharpie.Shell.Common;

/// <summary>
/// Which backslash escapes <see cref="Escapes.Decode" /> understands.
/// </summary>
internal enum EscapeRules
{
	/// <summary>
	/// <c>echo -e</c>: octal only as <c>\0nnn</c>, <c>\c</c> stops the output, and <c>\'</c>, <c>\"</c> and <c>\?</c>
	/// stay as written.
	/// </summary>
	Echo,

	/// <summary>
	/// <c>$'...'</c>: octal as <c>\nnn</c>, <c>\cX</c> is a control character, and <c>\'</c>, <c>\"</c> and <c>\?</c>
	/// give the character.
	/// </summary>
	AnsiC,
}

/// <summary>
/// Decodes backslash escapes such as <c>\n</c> and <c>\x41</c>, as bash's <c>echo -e</c> and <c>$'...'</c> do.
/// </summary>
internal static class Escapes
{
	/// <summary>
	/// Turns the bytes of <c>\xHH</c> and octal escapes into text, refusing bytes that aren't UTF-8.
	/// </summary>
	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	/// <summary>
	/// Replaces the backslash escapes in <paramref name="text" /> with the characters they stand for.
	/// </summary>
	/// <param name="text">The text to decode.</param>
	/// <param name="rules">Which escapes to understand.</param>
	/// <param name="stopped">
	/// Set to <see langword="true" /> when a <c>\c</c> cut the text short under <see cref="EscapeRules.Echo" />.
	/// </param>
	/// <returns>
	/// The decoded text, ending just before the <c>\c</c> when there is one. Unknown escapes, escapes with no digits
	/// and a backslash at the very end are kept as written. A run of byte escapes that spells UTF-8, as
	/// <c>\xc3\xa9</c> does, gives its characters, and any other run gives one character per byte.
	/// </returns>
	/// <remarks>
	/// shortcut: a byte that isn't UTF-8, such as <c>\xff</c>, becomes the matching Unicode character and prints as
	/// UTF-8, not as the raw byte bash writes; write bytes to the output stream if a script needs raw bytes.
	/// Bash's <c>\x{HH}</c> form isn't read either.
	/// </remarks>
	public static string Decode(ReadOnlySpan<char> text, EscapeRules rules, out bool stopped)
	{
		var result = new StringBuilder(text.Length);
		var bytes = new List<byte>();
		stopped = false;

		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] != '\\' || i + 1 == text.Length)
			{
				_ = Flush(result, bytes).Append(text[i]);

				continue;
			}

			var escape = text[++i];

			if (ReadByte(text, ref i, rules) is byte value)
			{
				bytes.Add(value);

				continue;
			}

			_ = Flush(result, bytes);

			switch (escape)
			{
				case 'c' when rules == EscapeRules.Echo:
					stopped = true;

					return result.ToString();
				case 'c' when i + 1 < text.Length:
					// As bash's TOCTRL: \c? is DEL, and any other letter gives its control character.
					var control = text[++i];
					_ = result.Append(control == '?' ? '\x7f' : (char)(char.ToUpperInvariant(control) & 0x1F));

					break;
				case 'u' or 'U' when ReadNumber(text, ref i, 16, escape == 'u' ? 4 : 8) is int code:
					_ = code is <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? result.Append(char.ConvertFromUtf32(code)) : result.Append('\uFFFD');

					break;
				case '\'' or '"' or '?' when rules == EscapeRules.AnsiC:
					_ = result.Append(escape);

					break;
				default:
					_ = Simple(escape) is char simple ? result.Append(simple) : result.Append('\\').Append(escape);

					break;
			}
		}

		_ = Flush(result, bytes);

		return result.ToString();
	}

	/// <summary>
	/// Reads an octal or <c>\xHH</c> escape, whose letter or first digit is just before <paramref name="index" />.
	/// </summary>
	/// <param name="text">The text being decoded.</param>
	/// <param name="index">The position of the escape letter; moved to the last digit read.</param>
	/// <param name="rules">Which escapes to understand.</param>
	/// <returns>
	/// The byte, keeping only the low eight bits as bash does, so <c>\777</c> gives 0xFF; or <see langword="null" />
	/// when the escape isn't one of these.
	/// </returns>
	private static byte? ReadByte(ReadOnlySpan<char> text, ref int index, EscapeRules rules)
	{
		var escape = text[index];

		var value = escape switch
		{
			'x' => ReadNumber(text, ref index, 16, 2),
			'0' when rules == EscapeRules.Echo => ReadNumber(text, ref index, 8, 3) ?? 0,
			>= '0' and <= '7' when rules == EscapeRules.AnsiC => ReadNumber(text, ref index, 8, 2, escape - '0'),
			_ => null,
		};

		return value is int found ? (byte)(found & 0xFF) : null;
	}

	/// <summary>
	/// Reads the digits that follow <paramref name="index" />.
	/// </summary>
	/// <param name="text">The text being decoded.</param>
	/// <param name="index">The position of the escape letter; moved to the last digit read.</param>
	/// <param name="radix">8 for octal or 16 for hexadecimal.</param>
	/// <param name="maxDigits">The most digits the escape may take.</param>
	/// <param name="first">The value of a digit already read, as the first digit of an octal escape is.</param>
	/// <returns>
	/// The number the digits spell, or <paramref name="first" /> when no digit follows.
	/// </returns>
	private static int? ReadNumber(ReadOnlySpan<char> text, ref int index, int radix, int maxDigits, int? first = null)
	{
		var value = first;

		for (var count = 0; count < maxDigits && index + 1 < text.Length; count++)
		{
			var digit = "0123456789abcdef".IndexOf(char.ToLowerInvariant(text[index + 1]), StringComparison.Ordinal);

			if (digit < 0 || digit >= radix)
			{
				break;
			}

			value = ((value ?? 0) * radix) + digit;
			index++;
		}

		return value;
	}

	/// <summary>
	/// Gives the character a one-letter escape such as <c>\n</c> stands for.
	/// </summary>
	/// <param name="escape">The letter after the backslash.</param>
	/// <returns>
	/// The character, or <see langword="null" /> for a letter that isn't one of these escapes.
	/// </returns>
	private static char? Simple(char escape) => escape switch
	{
		'\\' => '\\',
		'a' => '\a',
		'b' => '\b',
		'e' or 'E' => '\e',
		'f' => '\f',
		'n' => '\n',
		'r' => '\r',
		't' => '\t',
		'v' => '\v',
		_ => null,
	};

	/// <summary>
	/// Adds the bytes gathered so far to <paramref name="result" /> and clears them.
	/// </summary>
	/// <param name="result">The decoded text.</param>
	/// <param name="bytes">The bytes of the escapes just read.</param>
	/// <returns>
	/// <paramref name="result" />.
	/// </returns>
	private static StringBuilder Flush(StringBuilder result, List<byte> bytes)
	{
		if (bytes.Count == 0)
		{
			return result;
		}

		try
		{
			_ = result.Append(StrictUtf8.GetString([.. bytes]));
		}
		catch (DecoderFallbackException)
		{
			_ = result.Append(Encoding.Latin1.GetString([.. bytes]));
		}

		bytes.Clear();

		return result;
	}
}
