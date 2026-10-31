using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Reads <c>printf</c>'s number arguments and formats them as C's <c>printf</c> does.
/// </summary>
/// <remarks>
/// Decimals are rounded from the exact binary value, half to even, as the C library does, so <c>%.0f</c> of 2.5 gives
/// 2 and <c>%.2f</c> of 2.675 gives 2.67. shortcut: values are <see langword="double" />, where bash uses
/// <c>long double</c>, so a precision past about 17 digits or a value past 1e308 prints differently; read the
/// argument into a wider type if a script needs that.
/// </remarks>
internal static partial class PrintfNumbers
{
	/// <summary>
	/// 2^64, which a negative argument wraps around for <c>%u</c>, <c>%o</c> and <c>%x</c>.
	/// </summary>
	private static readonly BigInteger Wrap = BigInteger.One << 64;

	/// <summary>
	/// Reads an integer argument as <c>strtoimax</c> with base 0 does.
	/// </summary>
	/// <param name="text">The argument.</param>
	/// <param name="value">Gets the number, or what its valid start spells when the rest isn't a number.</param>
	/// <returns>
	/// <see langword="true" /> when the whole argument is a number.
	/// </returns>
	/// <remarks>
	/// Blanks may come first, then a sign. <c>0x</c> starts hexadecimal and a leading 0 octal. A <c>'</c> or <c>"</c>
	/// first gives the code of the character after it, or 0 when there is none.
	/// </remarks>
	public static bool TryParseInteger(string text, out BigInteger value)
	{
		if (text is ['\'' or '"', ..])
		{
			value = text.Length > 1 ? Rune.GetRuneAt(text, 1).Value : 0;

			return true;
		}

		var index = 0;

		while (index < text.Length && char.IsWhiteSpace(text[index]))
		{
			index++;
		}

		var negative = index < text.Length && text[index] == '-';

		index += index < text.Length && text[index] is '+' or '-' ? 1 : 0;

		var radix = 10;

		if (index + 2 < text.Length && text[index] == '0' && text[index + 1] is 'x' or 'X' && Uri.IsHexDigit(text[index + 2]))
		{
			radix = 16;
			index += 2;
		}
		else if (index < text.Length && text[index] == '0')
		{
			radix = 8;
		}

		var start = index;

		value = BigInteger.Zero;

		while (index < text.Length && "0123456789abcdef".IndexOf(char.ToLowerInvariant(text[index]), StringComparison.Ordinal) is var digit and >= 0 && digit < radix)
		{
			value = (value * radix) + digit;
			index++;
		}

		value = negative ? -value : value;

		return index > start && index == text.Length;
	}

	/// <summary>
	/// Reads a decimal argument as <c>strtold</c> does.
	/// </summary>
	/// <param name="text">The argument.</param>
	/// <param name="value">Gets the number, or what its valid start spells when the rest isn't a number.</param>
	/// <returns>
	/// <see langword="true" /> when the whole argument is a number.
	/// </returns>
	/// <remarks>
	/// Takes <c>inf</c>, <c>infinity</c> and <c>nan</c> in any case, and an integer in any form
	/// <see cref="TryParseInteger" /> takes.
	/// </remarks>
	public static bool TryParseDecimal(string text, out double value)
	{
		if (DecimalRegex().Match(text) is not { Success: true } match || match.Groups["hex"].Success || match.Groups["quote"].Success)
		{
			var whole = TryParseInteger(text, out var integer);

			value = (double)integer;

			return whole;
		}

		var number = match.Groups["number"].Value;

		value = number.ToUpperInvariant() switch
		{
			['I', ..] => double.PositiveInfinity,
			['N', ..] => double.NaN,
			_ => double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture),
		};

		value = match.Groups["sign"].Value == "-" ? -value : value;

		return match.Length == text.Length;
	}

	/// <summary>
	/// Formats an integer for <c>%d</c>, <c>%i</c>, <c>%o</c>, <c>%u</c>, <c>%x</c> or <c>%X</c>.
	/// </summary>
	/// <param name="value">The number, already within the conversion's range.</param>
	/// <param name="conversion">The conversion.</param>
	/// <param name="width">The minimum width.</param>
	/// <param name="precision">The fewest digits, or <see langword="null" /> for none.</param>
	/// <returns>
	/// The text, with <c>+</c> and space ignored for the unsigned letters and <c>0</c> ignored when there is a
	/// precision, as in C.
	/// </returns>
	public static string FormatInteger(BigInteger value, ConversionPart conversion, int width, int? precision)
	{
		var letter = conversion.Letter;
		var signed = letter is 'd' or 'i';
		var magnitude = signed ? BigInteger.Abs(value) : value < 0 ? value + Wrap : value;

		var digits = letter switch
		{
			'o' => ToRadix(magnitude, 8),
			'x' => ToRadix(magnitude, 16),
			'X' => ToRadix(magnitude, 16).ToUpperInvariant(),
			_ => magnitude.ToString(CultureInfo.InvariantCulture),
		};

		digits = precision == 0 && magnitude.IsZero ? string.Empty : digits.PadLeft(precision ?? 0, '0');

		var prefix = !conversion.Has('#') ? string.Empty
			: letter == 'o' && !digits.StartsWith('0') ? "0"
			: letter is 'x' or 'X' && !magnitude.IsZero ? (letter == 'x' ? "0x" : "0X")
			: string.Empty;

		var sign = !signed ? string.Empty : value < 0 ? "-" : Sign(conversion);

		return Pad(sign + prefix, digits, width, conversion.Has('-'), conversion.Has('0') && precision is null);
	}

	/// <summary>
	/// Formats a number for <c>%e</c>, <c>%E</c>, <c>%f</c>, <c>%F</c>, <c>%g</c> or <c>%G</c>.
	/// </summary>
	/// <param name="value">The number.</param>
	/// <param name="conversion">The conversion.</param>
	/// <param name="width">The minimum width.</param>
	/// <param name="precision">The digits after the point, or for <c>%g</c> the significant digits; 6 when <see langword="null" />.</param>
	/// <returns>
	/// The text. Infinity and NaN print as <c>inf</c> and <c>nan</c>, in capitals for the capital letters.
	/// </returns>
	public static string FormatDecimal(double value, ConversionPart conversion, int width, int? precision)
	{
		var letter = char.ToLowerInvariant(conversion.Letter);
		var magnitude = Math.Abs(value);
		var sign = double.IsNegative(value) && !double.IsNaN(value) ? "-" : Sign(conversion);

		var body = !double.IsFinite(value) ? (double.IsNaN(value) ? "nan" : "inf")
			: letter == 'f' ? Fixed(magnitude, precision ?? 6, conversion.Has('#'))
			: letter == 'e' ? Scientific(magnitude, precision ?? 6, conversion.Has('#'))
			: General(magnitude, precision ?? 6, conversion.Has('#'));

		body = char.IsUpper(conversion.Letter) ? body.ToUpperInvariant() : body;

		return Pad(sign, body, width, conversion.Has('-'), conversion.Has('0') && double.IsFinite(value));
	}

	/// <summary>
	/// Puts <paramref name="prefix" /> and <paramref name="body" /> together, padded to <paramref name="width" />.
	/// </summary>
	/// <param name="prefix">The sign and any <c>0x</c>, which zero padding goes after.</param>
	/// <param name="body">The digits or text.</param>
	/// <param name="width">The minimum width.</param>
	/// <param name="left"><see langword="true" /> to pad on the right, as the <c>-</c> flag does.</param>
	/// <param name="zeros"><see langword="true" /> to pad with zeros, as the <c>0</c> flag does when <c>-</c> isn't given.</param>
	/// <returns>
	/// The padded text.
	/// </returns>
	public static string Pad(string prefix, string body, int width, bool left, bool zeros)
	{
		var padding = Math.Max(0, width - prefix.Length - body.Length);

		return left ? prefix + body + new string(' ', padding)
			: zeros ? prefix + new string('0', padding) + body
			: new string(' ', padding) + prefix + body;
	}

	/// <summary>
	/// Gives the sign a number that isn't negative gets.
	/// </summary>
	/// <param name="conversion">The conversion.</param>
	/// <returns>
	/// <c>+</c> for the <c>+</c> flag, a space for the space flag, or nothing.
	/// </returns>
	private static string Sign(ConversionPart conversion) => conversion.Has('+') ? "+" : conversion.Has(' ') ? " " : string.Empty;

	/// <summary>
	/// Writes <paramref name="value" /> in base 8 or 16, in lowercase.
	/// </summary>
	/// <param name="value">A number that isn't negative.</param>
	/// <param name="radix">8 or 16.</param>
	/// <returns>
	/// The digits.
	/// </returns>
	private static string ToRadix(BigInteger value, int radix)
	{
		if (value.IsZero)
		{
			return "0";
		}

		var digits = new StringBuilder();

		for (; !value.IsZero; value /= radix)
		{
			_ = digits.Insert(0, "0123456789abcdef"[(int)(value % radix)]);
		}

		return digits.ToString();
	}

	/// <summary>
	/// Writes <paramref name="magnitude" /> with <paramref name="precision" /> digits after the point, as <c>%f</c> does.
	/// </summary>
	/// <param name="magnitude">A finite number that isn't negative.</param>
	/// <param name="precision">The digits after the point.</param>
	/// <param name="point"><see langword="true" /> to keep the point with no digits after it, as the <c>#</c> flag does.</param>
	/// <returns>
	/// The digits.
	/// </returns>
	private static string Fixed(double magnitude, int precision, bool point)
	{
		var digits = Scaled(magnitude, precision).ToString(CultureInfo.InvariantCulture).PadLeft(precision + 1, '0');

		return precision > 0 ? $"{digits[..^precision]}.{digits[^precision..]}" : point ? digits + "." : digits;
	}

	/// <summary>
	/// Writes <paramref name="magnitude" /> as one digit, <paramref name="precision" /> more after the point, and an
	/// exponent, as <c>%e</c> does.
	/// </summary>
	/// <param name="magnitude">A finite number that isn't negative.</param>
	/// <param name="precision">The digits after the point.</param>
	/// <param name="point"><see langword="true" /> to keep the point with no digits after it.</param>
	/// <returns>
	/// The text, with at least two exponent digits.
	/// </returns>
	private static string Scientific(double magnitude, int precision, bool point)
	{
		var (digits, exponent) = Significant(magnitude, precision + 1);
		var mantissa = precision > 0 || point ? $"{digits[0]}.{digits[1..]}" : digits;

		return $"{mantissa}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture)}";
	}

	/// <summary>
	/// Writes <paramref name="magnitude" /> as <c>%g</c> does: <c>%e</c> for a very small or large exponent, <c>%f</c>
	/// otherwise, with <paramref name="precision" /> significant digits and the zeros at the end taken off.
	/// </summary>
	/// <param name="magnitude">A finite number that isn't negative.</param>
	/// <param name="precision">The significant digits, where 0 counts as 1.</param>
	/// <param name="keep"><see langword="true" /> to keep the zeros at the end and the point, as the <c>#</c> flag does.</param>
	/// <returns>
	/// The text.
	/// </returns>
	private static string General(double magnitude, int precision, bool keep)
	{
		precision = Math.Max(precision, 1);

		var (_, exponent) = Significant(magnitude, precision);
		var text = exponent < -4 || exponent >= precision
			? Scientific(magnitude, precision - 1, keep)
			: Fixed(magnitude, precision - 1 - exponent, keep);

		if (keep || !text.Contains('.', StringComparison.Ordinal))
		{
			return text;
		}

		var end = text.IndexOf('e', StringComparison.Ordinal) is var e and >= 0 ? e : text.Length;

		return text[..end].TrimEnd('0').TrimEnd('.') + text[end..];
	}

	/// <summary>
	/// Rounds <paramref name="magnitude" /> to <paramref name="count" /> significant digits.
	/// </summary>
	/// <param name="magnitude">A finite number that isn't negative.</param>
	/// <param name="count">How many digits, at least one.</param>
	/// <returns>
	/// The digits, and the power of ten of the first one. Zero gives zeros and 0.
	/// </returns>
	private static (string Digits, int Exponent) Significant(double magnitude, int count)
	{
		if (magnitude == 0)
		{
			return (new string('0', count), 0);
		}

		var exponent = (int)Math.Floor(Math.Log10(magnitude));

		// The logarithm can be one off either way, and rounding up can add a digit, so the guess is checked.
		while (true)
		{
			var scaled = Scaled(magnitude, count - 1 - exponent);

			if (scaled >= BigInteger.Pow(10, count))
			{
				exponent++;
			}
			else if (scaled < BigInteger.Pow(10, count - 1))
			{
				exponent--;
			}
			else
			{
				return (scaled.ToString(CultureInfo.InvariantCulture), exponent);
			}
		}
	}

	/// <summary>
	/// Rounds <paramref name="magnitude" /> times 10^<paramref name="power" /> to a whole number, half to even, from
	/// the exact binary value.
	/// </summary>
	/// <param name="magnitude">A finite number that isn't negative.</param>
	/// <param name="power">The power of ten, which may be negative.</param>
	/// <returns>
	/// The whole number.
	/// </returns>
	private static BigInteger Scaled(double magnitude, int power)
	{
		var bits = BitConverter.DoubleToInt64Bits(magnitude);
		var biased = (int)((bits >> 52) & 0x7FF);
		var fraction = bits & ((1L << 52) - 1);

		// A double is mantissa * 2^exponent, with the leading 1 left out of the bits unless it's subnormal.
		var mantissa = new BigInteger(biased == 0 ? fraction : fraction | (1L << 52));
		var exponent = (biased == 0 ? 1 : biased) - 1075;

		var numerator = mantissa * (power >= 0 ? BigInteger.Pow(10, power) : 1);
		var denominator = power < 0 ? BigInteger.Pow(10, -power) : BigInteger.One;

		numerator = exponent >= 0 ? numerator << exponent : numerator;
		denominator = exponent < 0 ? denominator << -exponent : denominator;

		var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
		var twice = remainder * 2;

		return twice > denominator || (twice == denominator && !quotient.IsEven) ? quotient + 1 : quotient;
	}

	/// <summary>
	/// Matches the start of a decimal argument as <c>strtold</c> reads it.
	/// </summary>
	/// <returns>
	/// A regex with groups <c>sign</c> and <c>number</c>, and <c>hex</c> or <c>quote</c> for the forms
	/// <see cref="TryParseInteger" /> reads instead.
	/// </returns>
	[GeneratedRegex(@"^\s*(?<sign>[+-]?)(?:(?<hex>0[xX][0-9a-fA-F])|(?<number>inf(?:inity)?|nan|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?))|^(?<quote>['""])", RegexOptions.IgnoreCase)]
	private static partial Regex DecimalRegex();
}
