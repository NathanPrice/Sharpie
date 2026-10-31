using System.Numerics;
using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Arithmetic;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Prints its arguments through a format, as bash's <c>printf</c> builtin does.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which <c>-v</c> stores the text in.</param>
/// <remarks>
/// The format's conversions are <c>%d %i %o %u %x %X %e %E %f %F %g %G %c %s %b %q</c> and <c>%%</c>, with C's flags,
/// width, precision and <c>*</c>. The format is used again while arguments are left, and a missing argument counts as
/// empty or 0. <c>%b</c> decodes an argument's backslash escapes, and a <c>\c</c> in it stops all output. <c>%q</c>
/// quotes an argument so the shell reads it back as the same word. <c>-v NAME</c> stores the text in a variable, or an
/// element as in <c>-v 'a[i]'</c>, instead of printing it. Width and precision count characters, where bash counts
/// bytes, which only differs for text outside ASCII.
/// </remarks>
internal sealed class PrintfCommand(DeclareCommand declare) : ICommand
{
	/// <inheritdoc />
	public string Name => "printf";

	/// <inheritdoc />
	public string Description => "Prints its arguments through a format";

	/// <inheritdoc />
	/// <remarks>
	/// The status is 0, 1 when an argument isn't a number or is out of range (the rest is still printed, as in bash), or
	/// the format holds a conversion <c>printf</c> doesn't know (printing stops there), and 2 for a bad option, a bad
	/// <c>-v</c> name or no format.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (this.ParseOptions(args, out var variable) is not { } first)
		{
			return 2;
		}

		if (first >= args.Length)
		{
			new ShellError("sharpie::printf::missing_format", "printf needs a format") { Help = "printf [-v var] format [arguments]" }.Write();

			return 2;
		}

		var output = new StringBuilder();
		var status = Format(args[first], args[(first + 1)..], output);

		if (variable is null)
		{
			Console.Out.Write(output.ToString());

			return status;
		}

		try
		{
			declare.Set(variable, output.ToString());
		}
		catch (ArithmeticExpression.Failure failure)
		{
			failure.Error.Write();

			return 1;
		}

		return status;
	}

	/// <summary>
	/// Formats <paramref name="arguments" /> through <paramref name="format" />, using the format again while
	/// arguments are left.
	/// </summary>
	/// <param name="format">The format.</param>
	/// <param name="arguments">The arguments.</param>
	/// <param name="output">Gets the text.</param>
	/// <returns>
	/// The status, as <see cref="Run" /> describes.
	/// </returns>
	/// <remarks>
	/// A format with no conversions is printed once, whatever the arguments.
	/// </remarks>
	internal static int Format(string format, IReadOnlyList<string> arguments, StringBuilder output)
	{
		var parts = PrintfFormat.Parse(format);
		var reader = new Arguments(arguments);

		do
		{
			var start = reader.Next;

			foreach (var part in parts)
			{
				switch (part)
				{
					case LiteralPart literal:
						_ = output.Append(literal.Text);
						break;
					case BadPart bad:
						bad.Error.Write();

						return 1;
					default:
						if (!Convert((ConversionPart)part, reader, output))
						{
							return reader.Status;
						}

						break;
				}
			}

			if (reader.Next == start)
			{
				break;
			}
		}
		while (reader.Next < arguments.Count);

		return reader.Status;
	}

	/// <summary>
	/// Quotes <paramref name="text" /> as <c>%q</c> does, so the shell reads it back as one word with the same text.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <returns>
	/// <c>''</c> for empty text, a <c>$'...'</c> word when the text holds a control character, and otherwise the text
	/// with a backslash before each character the shell would treat specially, as bash writes them.
	/// </returns>
	internal static string Quote(string text)
	{
		_ = text.ArgumentNotNull();

		if (text.Length == 0)
		{
			return "''";
		}

		if (text.Any(char.IsControl))
		{
			return $"$'{string.Concat(text.Select(AnsiEscape))}'";
		}

		var quoted = new StringBuilder();

		for (var index = 0; index < text.Length; index++)
		{
			var character = text[index];

			// A ~ expands only at the start of a word or after = or :, and a # starts a comment only at the start.
			var special = character switch
			{
				'~' => index == 0 || text[index - 1] is '=' or ':',
				'#' => index == 0,
				_ => " \t'\"\\|&;()<>!{}*[?]^$`,".Contains(character, StringComparison.Ordinal),
			};

			_ = special ? quoted.Append('\\').Append(character) : quoted.Append(character);
		}

		return quoted.ToString();
	}

	/// <summary>
	/// Formats one argument through <paramref name="conversion" />.
	/// </summary>
	/// <param name="conversion">The conversion.</param>
	/// <param name="reader">The arguments, which this takes from.</param>
	/// <param name="output">Gets the text.</param>
	/// <returns>
	/// <see langword="false" /> when a <c>\c</c> in a <c>%b</c> argument stops all output.
	/// </returns>
	private static bool Convert(ConversionPart conversion, Arguments reader, StringBuilder output)
	{
		var width = conversion.Width == -1 ? reader.TakeCount() : conversion.Width ?? 0;
		var precision = conversion.Precision == -1 ? reader.TakeCount() : conversion.Precision;

		// A negative width from * means the - flag, and a negative precision from .* means none, as in C.
		if (width < 0)
		{
			conversion = conversion with { Flags = conversion.Flags + "-" };
			width = -width;
		}

		precision = precision < 0 ? null : precision;

		var argument = reader.Take();
		var stopped = false;

		_ = output.Append(conversion.Letter switch
		{
			'd' or 'i' or 'o' or 'u' or 'x' or 'X' => PrintfNumbers.FormatInteger(reader.Integer(argument, conversion.Letter is 'd' or 'i'), conversion, width, precision),
			'e' or 'E' or 'f' or 'F' or 'g' or 'G' => PrintfNumbers.FormatDecimal(reader.Decimal(argument), conversion, width, precision),
			'c' => Text(argument is null or "" ? "\0" : char.ConvertFromUtf32(Rune.GetRuneAt(argument, 0).Value), conversion, width, null),
			'b' => Text(Escapes.Decode(argument ?? string.Empty, EscapeRules.PrintfArgument, out stopped), conversion, width, precision),
			'q' => Text(Quote(argument ?? string.Empty), conversion, width, precision),
			_ => Text(argument ?? string.Empty, conversion, width, precision),
		});

		return !stopped;
	}

	/// <summary>
	/// Cuts <paramref name="text" /> to <paramref name="precision" /> characters and pads it to <paramref name="width" />.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="conversion">The conversion, whose <c>-</c> flag pads on the right.</param>
	/// <param name="width">The minimum width.</param>
	/// <param name="precision">The most characters, or <see langword="null" /> for no limit.</param>
	/// <returns>
	/// The text, padded with spaces, since C ignores the <c>0</c> flag for text.
	/// </returns>
	private static string Text(string text, ConversionPart conversion, int width, int? precision) =>
		PrintfNumbers.Pad(string.Empty, precision < text.Length ? text[..precision.Value] : text, width, conversion.Has('-'), zeros: false);

	/// <summary>
	/// Writes one character of a <c>$'...'</c> word.
	/// </summary>
	/// <param name="character">The character.</param>
	/// <returns>
	/// The escape bash writes for it, or the character itself.
	/// </returns>
	private static string AnsiEscape(char character) => character switch
	{
		'\a' => @"\a",
		'\b' => @"\b",
		'\t' => @"\t",
		'\n' => @"\n",
		'\v' => @"\v",
		'\f' => @"\f",
		'\r' => @"\r",
		'\e' => @"\E",
		'\'' => @"\'",
		'\\' => @"\\",
		_ when char.IsControl(character) => @"\" + System.Convert.ToString(character, 8).PadLeft(3, '0'),
		_ => character.ToString(),
	};

	/// <summary>
	/// Reads the options in front of the format.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="variable">Gets the name after <c>-v</c>, or <see langword="null" /> to print.</param>
	/// <returns>
	/// The index of the format, or <see langword="null" /> after reporting a bad option or name.
	/// </returns>
	/// <remarks>
	/// The name can follow <c>-v</c> in the same word, and <c>--</c> ends the options, so <c>printf -- -%s x</c> prints
	/// a format that starts with <c>-</c>.
	/// </remarks>
	private int? ParseOptions(string[] args, out string? variable)
	{
		variable = null;

		var index = 0;

		for (; index < args.Length && args[index] is ['-', _, ..] option; index++)
		{
			if (option == "--")
			{
				return index + 1;
			}

			if (!option.StartsWith("-v", StringComparison.Ordinal))
			{
				new ShellError("sharpie::printf::invalid_option", $"`{option}` isn't an option of `{this.Name}`") { Help = "printf [-v var] format [arguments]" }.Write();

				return null;
			}

			variable = option.Length > 2 ? option[2..] : index + 1 < args.Length ? args[++index] : null;

			if (variable is null)
			{
				new ShellError("sharpie::printf::missing_variable", "`-v` needs a variable name after it").Write();

				return null;
			}

			if (!DeclareCommand.IsIdentifier(variable) && !DeclareCommand.TrySplitElement(variable, out _, out _))
			{
				new ShellError("sharpie::printf::invalid_identifier", $"`{variable}` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Write();

				return null;
			}
		}

		return index;
	}

	/// <summary>
	/// The arguments left to format, and the status the ones read so far give.
	/// </summary>
	/// <param name="arguments">The arguments after the format.</param>
	private sealed class Arguments(IReadOnlyList<string> arguments)
	{
		/// <summary>
		/// Gets the index of the next argument.
		/// </summary>
		/// <value>
		/// The index, which may be past the last argument.
		/// </value>
		public int Next { get; private set; }

		/// <summary>
		/// Gets the status so far.
		/// </summary>
		/// <value>
		/// 0, or 1 once an argument wasn't a number or was out of range.
		/// </value>
		public int Status { get; private set; }

		/// <summary>
		/// Takes the next argument.
		/// </summary>
		/// <returns>
		/// The argument, or <see langword="null" /> when none is left.
		/// </returns>
		public string? Take() => this.Next < arguments.Count ? arguments[this.Next++] : null;

		/// <summary>
		/// Takes the next argument as a width or precision, for <c>*</c>.
		/// </summary>
		/// <returns>
		/// The number, cut to the range of an <see langword="int" />.
		/// </returns>
		public int TakeCount() => (int)BigInteger.Clamp(this.Integer(this.Take(), signed: true), int.MinValue, int.MaxValue);

		/// <summary>
		/// Reads an argument as an integer, reporting one that isn't a number or is out of range.
		/// </summary>
		/// <param name="argument">The argument, or <see langword="null" /> when none was left, which is 0.</param>
		/// <param name="signed">
		/// <see langword="true" /> for <c>%d</c> and <c>%i</c>, which take a 64-bit signed number, or
		/// <see langword="false" /> for the unsigned letters, which take up to 2^64 - 1 and wrap a negative one.
		/// </param>
		/// <returns>
		/// The number, its valid start when the rest isn't a number, or the nearest one in range.
		/// </returns>
		public BigInteger Integer(string? argument, bool signed)
		{
			if (argument is null)
			{
				return BigInteger.Zero;
			}

			if (!PrintfNumbers.TryParseInteger(argument, out var value))
			{
				this.Invalid(argument);
			}

			var (low, high) = signed ? ((BigInteger)long.MinValue, (BigInteger)long.MaxValue) : (-(BigInteger)ulong.MaxValue, (BigInteger)ulong.MaxValue);

			if (value < low || value > high)
			{
				new ShellError("sharpie::printf::out_of_range", $"`{argument}` is out of range") { Help = $"a number here has to be between {low} and {high}" }.Write();
				this.Status = 1;
			}

			return BigInteger.Clamp(value, low, high);
		}

		/// <summary>
		/// Reads an argument as a decimal number, reporting one that isn't a number.
		/// </summary>
		/// <param name="argument">The argument, or <see langword="null" /> when none was left, which is 0.</param>
		/// <returns>
		/// The number, or its valid start when the rest isn't a number.
		/// </returns>
		public double Decimal(string? argument)
		{
			if (argument is null)
			{
				return 0;
			}

			if (!PrintfNumbers.TryParseDecimal(argument, out var value))
			{
				this.Invalid(argument);
			}

			return value;
		}

		/// <summary>
		/// Reports an argument that isn't a number, which makes the status 1.
		/// </summary>
		/// <param name="argument">The argument.</param>
		private void Invalid(string argument)
		{
			new ShellError("sharpie::printf::invalid_number", $"`{argument}` isn't a number").Write();
			this.Status = 1;
		}
	}
}
