using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Prints its arguments joined by single spaces, following bash's <c>echo</c>.
/// </summary>
/// <remarks>
/// Leading words made only of <c>-</c> and the letters <c>n</c>, <c>e</c> and <c>E</c> are options:
/// <c>-n</c> leaves off the trailing newline, <c>-e</c> turns on backslash escapes and <c>-E</c> turns
/// them off again, with the last one winning. The first word that is not an option, and every word
/// after it, is printed as is.
/// </remarks>
internal class EchoCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "echo";
	/// <inheritdoc />
	public string Description => "Prints the text you give it (-n: no newline, -e: read backslash escapes)";

	/// <inheritdoc />
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var newline = true;
		var escapes = false;
		var first = 0;
		for (; first < args.Length && IsOption(args[first]); first++)
		{
			foreach (var letter in args[first].AsSpan(1))
			{
				switch (letter)
				{
					case 'n':
						newline = false;
						break;
					case 'e':
						escapes = true;
						break;
					default:
						escapes = false;
						break;
				}
			}
		}

		var text = string.Join(" ", args, first, args.Length - first);
		if (escapes)
		{
			text = Expand(text, out var stopped);
			newline &= !stopped;
		}

		Console.Write(text);
		if (newline)
		{
			Console.WriteLine();
		}

		return 0;
	}

	/// <summary>
	/// Checks whether a word is an echo option such as <c>-n</c>, <c>-e</c> or <c>-neE</c>.
	/// </summary>
	/// <param name="word">The word to check.</param>
	/// <returns>
	/// <see langword="true" /> when <paramref name="word" /> is a <c>-</c> followed by one or more of
	/// the letters <c>n</c>, <c>e</c> and <c>E</c>; <see langword="false" /> for <c>-</c>, <c>--</c> and any other word.
	/// </returns>
	private static bool IsOption(string word) =>
		word.Length > 1 && word[0] == '-' && word.AsSpan(1).IndexOfAnyExcept("neE") < 0;

	/// <summary>
	/// Replaces the backslash escapes that <c>echo -e</c> understands with the characters they stand for.
	/// </summary>
	/// <param name="text">The joined words to expand.</param>
	/// <param name="stopped">
	/// Set to <see langword="true" /> when a <c>\c</c> cut the output short, which also drops the trailing newline.
	/// </param>
	/// <returns>
	/// The expanded text, ending just before the <c>\c</c> when there is one. Unknown escapes and a
	/// backslash at the very end are kept as written.
	/// </returns>
	/// <remarks>
	/// shortcut: no \u/\U Unicode escapes, add them if a script needs them.
	/// </remarks>
	private static string Expand(string text, out bool stopped)
	{
		var result = new StringBuilder(text.Length);
		stopped = false;

		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] != '\\' || i + 1 == text.Length)
			{
				_ = result.Append(text[i]);
				continue;
			}

			var escape = text[++i];
			switch (escape)
			{
				case '\\':
					_ = result.Append('\\');
					break;
				case 'a':
					_ = result.Append('\a');
					break;
				case 'b':
					_ = result.Append('\b');
					break;
				case 'e':
				case 'E':
					_ = result.Append('\e');
					break;
				case 'f':
					_ = result.Append('\f');
					break;
				case 'n':
					_ = result.Append('\n');
					break;
				case 'r':
					_ = result.Append('\r');
					break;
				case 't':
					_ = result.Append('\t');
					break;
				case 'v':
					_ = result.Append('\v');
					break;
				case 'c':
					stopped = true;
					return result.ToString();
				case '0':
					_ = result.Append(ToByteChar(ReadNumber(text, ref i, 8, 3) ?? 0));
					break;
				case 'x' when ReadNumber(text, ref i, 16, 2) is int hex:
					_ = result.Append(ToByteChar(hex));
					break;
				default:
					_ = result.Append('\\').Append(escape);
					break;
			}
		}

		return result.ToString();
	}

	/// <summary>
	/// Reads the digits of a <c>\0nnn</c> or <c>\xHH</c> escape that follow <paramref name="index" />.
	/// </summary>
	/// <param name="text">The text being expanded.</param>
	/// <param name="index">The position of the escape letter; moved to the last digit read.</param>
	/// <param name="radix">8 for octal or 16 for hexadecimal.</param>
	/// <param name="maxDigits">The most digits the escape may take.</param>
	/// <returns>
	/// The number the digits spell, or <see langword="null" /> when no digit follows the escape letter.
	/// </returns>
	private static int? ReadNumber(string text, ref int index, int radix, int maxDigits)
	{
		int? value = null;
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
	/// Turns the number from a <c>\0nnn</c> or <c>\xHH</c> escape into the character to print.
	/// </summary>
	/// <param name="value">The number the escape spelled.</param>
	/// <returns>
	/// The character for the low byte of <paramref name="value" />, since bash keeps only that byte, so <c>\0777</c> gives 0xFF.
	/// </returns>
	/// <remarks>
	/// shortcut: codes above 0x7F print as the matching Unicode character in UTF-8, not as the raw byte bash
	/// writes, write bytes to the output stream if a script needs raw bytes.
	/// </remarks>
	private static char ToByteChar(int value) => (char)(value & 0xFF);
}
