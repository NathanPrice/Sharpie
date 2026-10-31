using System.Globalization;
using System.Text;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// One part of a <c>printf</c> format: text to print as it is, a conversion, or the error that ends the format.
/// </summary>
internal abstract record FormatPart;

/// <summary>
/// Text between conversions, with its backslash escapes already decoded.
/// </summary>
/// <param name="Text">The text.</param>
internal sealed record LiteralPart(string Text) : FormatPart;

/// <summary>
/// A conversion such as <c>%-5.2f</c>, which formats one argument.
/// </summary>
/// <param name="Flags">The flag characters typed, from <c>-+ #0</c>.</param>
/// <param name="Width">The minimum width, <see langword="null" /> for none, or -1 for <c>*</c>, which reads it from an argument.</param>
/// <param name="Precision">The precision, <see langword="null" /> for none, or -1 for <c>.*</c>, which reads it from an argument.</param>
/// <param name="Letter">The conversion letter, such as <c>d</c> or <c>s</c>.</param>
internal sealed record ConversionPart(string Flags, int? Width, int? Precision, char Letter) : FormatPart
{
	/// <summary>
	/// Checks whether a flag was typed.
	/// </summary>
	/// <param name="flag">The flag character.</param>
	/// <returns>
	/// <see langword="true" /> when it was.
	/// </returns>
	public bool Has(char flag) => this.Flags.Contains(flag, StringComparison.Ordinal);
}

/// <summary>
/// A conversion bash doesn't accept, where printing stops with status 1.
/// </summary>
/// <param name="Error">The report.</param>
internal sealed record BadPart(ShellError Error) : FormatPart;

/// <summary>
/// Reads a <c>printf</c> format into its parts.
/// </summary>
internal static class PrintfFormat
{
	/// <summary>
	/// The conversion letters <c>printf</c> takes.
	/// </summary>
	internal const string Letters = "diouxXeEfFgGcsbq";

	/// <summary>
	/// Reads <paramref name="format" />.
	/// </summary>
	/// <param name="format">The format, as typed.</param>
	/// <returns>
	/// The parts in order. Printing stops at the first <see cref="BadPart" />, so what follows one is never used.
	/// </returns>
	/// <remarks>
	/// <c>%%</c> is a <c>%</c>. A backslash before a <c>%</c> is printed and the <c>%</c> still starts a conversion,
	/// as in bash, so <c>\%d</c> prints a backslash and a number. The length letters C allows, such as the <c>l</c> in
	/// <c>%ld</c>, are skipped as bash skips them.
	/// </remarks>
	public static List<FormatPart> Parse(string format)
	{
		var parts = new List<FormatPart>();
		var literal = new StringBuilder();

		for (var index = 0; index < format.Length; index++)
		{
			var character = format[index];

			if (character == '\\' && index + 1 < format.Length && format[index + 1] != '%')
			{
				_ = literal.Append(character).Append(format[++index]);

				continue;
			}

			if (character != '%')
			{
				_ = literal.Append(character);

				continue;
			}

			if (index + 1 < format.Length && format[index + 1] == '%')
			{
				_ = literal.Append('%');
				index++;

				continue;
			}

			Flush();
			parts.Add(ReadConversion(format, ref index));
		}

		Flush();

		return parts;

		void Flush()
		{
			if (literal.Length > 0)
			{
				parts.Add(new LiteralPart(Escapes.Decode(literal.ToString(), EscapeRules.PrintfFormat, out _)));
				_ = literal.Clear();
			}
		}
	}

	/// <summary>
	/// Reads the conversion whose <c>%</c> is at <paramref name="index" />.
	/// </summary>
	/// <param name="format">The format.</param>
	/// <param name="index">The position of the <c>%</c>; moved to the conversion letter.</param>
	/// <returns>
	/// The conversion, or a <see cref="BadPart" /> for a letter <c>printf</c> doesn't take or a format that ends first.
	/// </returns>
	private static FormatPart ReadConversion(string format, ref int index)
	{
		var start = index++;

		var flags = Take(format, ref index, "-+ #0");
		var width = ReadNumber(format, ref index);
		int? precision = null;

		if (index < format.Length && format[index] == '.')
		{
			index++;
			precision = ReadNumber(format, ref index) ?? 0;
		}

		_ = Take(format, ref index, "hjlLtz");

		if (index >= format.Length)
		{
			index = format.Length;

			return new BadPart(new ShellError("sharpie::printf::missing_format", "The format ends with a `%` that has no letter after it")
			{
				Help = "write %% for a % sign",
			});
		}

		var letter = format[index];

		return Letters.Contains(letter, StringComparison.Ordinal)
			? new ConversionPart(flags, width, precision, letter)
			: new BadPart(new ShellError("sharpie::printf::invalid_format", $"`{format[start..(index + 1)]}` isn't a format printf knows")
			{
				Help = "printf takes %d %i %o %u %x %X %e %E %f %F %g %G %c %s %b %q and %%",
			});
	}

	/// <summary>
	/// Reads a width or precision: digits, or <c>*</c> for one read from an argument.
	/// </summary>
	/// <param name="format">The format.</param>
	/// <param name="index">The position to read at; moved past what was read.</param>
	/// <returns>
	/// The number, -1 for <c>*</c>, or <see langword="null" /> when neither is there. A number too big for an
	/// <see langword="int" /> is cut to the largest one.
	/// </returns>
	private static int? ReadNumber(string format, ref int index)
	{
		if (index < format.Length && format[index] == '*')
		{
			index++;

			return -1;
		}

		var digits = Take(format, ref index, "0123456789");

		return digits.Length == 0 ? null : int.TryParse(digits, CultureInfo.InvariantCulture, out var number) ? number : int.MaxValue;
	}

	/// <summary>
	/// Reads the run of characters from <paramref name="allowed" /> at <paramref name="index" />.
	/// </summary>
	/// <param name="format">The format.</param>
	/// <param name="index">The position to read at; moved past the run.</param>
	/// <param name="allowed">The characters the run may hold.</param>
	/// <returns>
	/// The run, empty when there is none.
	/// </returns>
	private static string Take(string format, ref int index, string allowed)
	{
		var start = index;

		while (index < format.Length && allowed.Contains(format[index], StringComparison.Ordinal))
		{
			index++;
		}

		return format[start..index];
	}
}
