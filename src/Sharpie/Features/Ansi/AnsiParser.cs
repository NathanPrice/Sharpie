using System.Text;
using DotNetTips.Spargine.Core;

namespace Sharpie.Features.Ansi;

/// <summary>
/// A terminal color, either one of the 16 standard colors or an exact RGB value.
/// </summary>
/// <param name="Index">
/// The standard color from 0 to 15, which the window maps to its own theme, or <see langword="null" /> when
/// <paramref name="Rgb" /> holds the color.
/// </param>
/// <param name="Rgb">The color as <c>0xRRGGBB</c>. Unused when <paramref name="Index" /> is set.</param>
internal readonly record struct AnsiColor(int? Index, uint Rgb);

/// <summary>
/// How a stretch of program output is drawn.
/// </summary>
/// <param name="Foreground">The text color, or <see langword="null" /> for the default.</param>
/// <param name="Background">The background color, or <see langword="null" /> for none.</param>
/// <param name="Bold"><see langword="true" /> when the text is bold; otherwise, <see langword="false" />.</param>
/// <param name="Underline"><see langword="true" /> when the text is underlined; otherwise, <see langword="false" />.</param>
/// <param name="Inverse">
/// <see langword="true" /> when the text and background colors are swapped; otherwise, <see langword="false" />.
/// </param>
/// <param name="Hidden"><see langword="true" /> when the text isn't drawn; otherwise, <see langword="false" />.</param>
internal readonly record struct AnsiStyle(AnsiColor? Foreground, AnsiColor? Background, bool Bold, bool Underline = false, bool Inverse = false, bool Hidden = false);

/// <summary>
/// A styled part of the plain text <see cref="AnsiParser.Parse" /> writes out.
/// </summary>
/// <param name="Start">The index of the first character in the plain text.</param>
/// <param name="Length">The number of characters covered.</param>
/// <param name="Style">How the characters are drawn.</param>
internal readonly record struct AnsiRun(int Start, int Length, AnsiStyle Style);

/// <summary>
/// Takes the color codes out of program output and reports which parts of the remaining text they styled.
/// </summary>
/// <remarks>
/// Only colors and bold are understood. Every other <c>ESC [</c> sequence, like cursor moves or erasing a line, is
/// dropped, because the window has no screen grid to apply it to. <c>ESC ]</c> sequences are kept, since the window
/// reads the working directory from OSC 7 itself. Backspace overstrike, which <c>man</c> prints for bold and
/// underline when it writes to a pipe with <c>MAN_KEEP_FORMATTING</c> set, is turned into colored runs.
/// </remarks>
internal sealed class AnsiParser
{
	// Most programs use fewer than six parameters. Extra ones are ignored instead of growing the buffer.
	private const int MaxParameters = 32;

	// An escape sequence cut off at the end of the last chunk, finished by the next one. Output is read in chunks of
	// a few thousand characters, so a sequence can straddle two of them.
	private readonly StringBuilder pending = new();

	// Overstruck text is drawn in the window's theme rather than the reader's LESS_TERMCAP colors, so man pages match
	// the rest of the shell. Bold (headings, names, flags) gets the accent blue the prompt header uses, and underline
	// (arguments like FILE) gets the yellow the input highlighter gives strings. Underline itself isn't drawn, since
	// AnsiStyle has no flag for it.
	private static readonly AnsiStyle OverstrikeBold = new(new AnsiColor(4, 0), null, Bold: true);
	private static readonly AnsiStyle OverstrikeUnderline = new(new AnsiColor(3, 0), null, Bold: false);

	/// <summary>
	/// Gets the style that applies to the next text, which carries over from one call to the next as in a terminal.
	/// </summary>
	/// <value>
	/// The default style, with no colors and not bold, until a color code arrives.
	/// </value>
	public AnsiStyle Style { get; private set; }

	/// <summary>
	/// Appends <paramref name="text" /> to <paramref name="plain" /> without its <c>ESC [</c> sequences, and adds a
	/// run to <paramref name="runs" /> for each part drawn in a style other than the default.
	/// </summary>
	/// <param name="text">The next chunk of program output.</param>
	/// <param name="plain">Receives the text to show.</param>
	/// <param name="runs">Receives the styled parts, with offsets into <paramref name="plain" />.</param>
	public void Parse(ReadOnlySpan<char> text, StringBuilder plain, List<AnsiRun> runs)
	{
		_ = plain.ArgumentNotNull();
		_ = runs.ArgumentNotNull();

		if (this.pending.Length > 0)
		{
			var joined = this.pending.Append(text).ToString();

			_ = this.pending.Clear();

			text = joined;
		}

		var runStart = plain.Length;
		var sawBackspace = false;

		while (true)
		{
			var escape = text.IndexOfAny('\e', '\b');

			if (escape < 0)
			{
				// The last character is held back when this chunk had overstrike in it, because the next chunk may
				// start with the backspace that overstrikes it. A newline is never overstruck, so it goes out at once.
				if (sawBackspace && text.Length > 0 && text[^1] != '\n')
				{
					_ = plain.Append(text[..^1]);
					_ = this.pending.Append(text[^1]);
				}
				else
				{
					_ = plain.Append(text);
				}

				break;
			}

			if (text[escape] == '\b')
			{
				sawBackspace = true;

				// Nothing before it in this chunk to overstrike, as after a color code.
				if (escape == 0)
				{
					text = text[1..];

					continue;
				}

				_ = plain.Append(text[..(escape - 1)]);
				text = text[(escape - 1)..];

				if (!this.ParseOverstrike(ref text, plain, runs, ref runStart))
				{
					_ = this.pending.Append(text);

					break;
				}

				continue;
			}

			_ = plain.Append(text[..escape]);
			text = text[escape..];

			if (text.Length == 1)
			{
				_ = this.pending.Append(text);

				break;
			}

			if (text[1] != '[')
			{
				_ = plain.Append('\e');
				text = text[1..];

				continue;
			}

			// A CSI sequence ends at its first character from '@' to '~'.
			var end = 2;

			while (end < text.Length && text[end] is < '@' or > '~')
			{
				end++;
			}

			if (end == text.Length)
			{
				_ = this.pending.Append(text);

				break;
			}

			if (text[end] == 'm')
			{
				this.CloseRun(plain, runs, ref runStart);
				this.Style = ApplySgr(this.Style, text[2..end]);
			}

			text = text[(end + 1)..];
		}

		this.CloseRun(plain, runs, ref runStart);
	}

	/// <summary>
	/// Adds a run for the text appended since <paramref name="runStart" />, when it has any and isn't in the default
	/// style, then moves <paramref name="runStart" /> to the end of <paramref name="plain" />.
	/// </summary>
	/// <param name="plain">The plain text written so far.</param>
	/// <param name="runs">The list to add the run to.</param>
	/// <param name="runStart">Where the text in the current style starts.</param>
	private void CloseRun(StringBuilder plain, List<AnsiRun> runs, ref int runStart)
	{
		if (this.Style != default && plain.Length > runStart)
		{
			// Overstrike styles one character at a time, so a bold word would otherwise be one run per letter.
			if (runs.Count > 0 && runs[^1].Style == this.Style && runs[^1].Start + runs[^1].Length == runStart)
			{
				runs[^1] = runs[^1] with { Length = plain.Length - runs[^1].Start };
			}
			else
			{
				runs.Add(new AnsiRun(runStart, plain.Length - runStart, this.Style));
			}
		}

		runStart = plain.Length;
	}

	/// <summary>
	/// Reads one overstruck character from the start of <paramref name="text" />, such as <c>N\bN</c> for a bold
	/// <c>N</c> or <c>_\bx</c> for an underlined <c>x</c>, and appends it to <paramref name="plain" /> as its own run.
	/// </summary>
	/// <param name="text">
	/// Output starting with a character followed by a backspace. Moved past the characters read.
	/// </param>
	/// <param name="plain">Receives the character that ends up on screen.</param>
	/// <param name="runs">Receives the run for that character.</param>
	/// <param name="runStart">Where the text in the current style starts.</param>
	/// <returns>
	/// <see langword="false" /> when the overstrike runs to the end of <paramref name="text" /> and may go on in the
	/// next chunk. Nothing was appended then, and <paramref name="text" /> is unchanged.
	/// </returns>
	/// <remarks>
	/// Bold and underline can be stacked, as in <c>_\bX\bX</c>, and bold wins. Any other pair, like <c>+\bo</c>,
	/// shows the last character in the current style.
	/// </remarks>
	private bool ParseOverstrike(ref ReadOnlySpan<char> text, StringBuilder plain, List<AnsiRun> runs, ref int runStart)
	{
		var shown = text[0];
		var used = 1;
		var bold = false;
		var underline = false;

		while (used + 1 < text.Length && text[used] == '\b')
		{
			var next = text[used + 1];

			bold |= next == shown;
			underline |= shown == '_';
			shown = next;
			used += 2;
		}

		if (used == text.Length || text[used] == '\b')
		{
			return false;
		}

		this.CloseRun(plain, runs, ref runStart);

		var style = this.Style;
		this.Style = bold ? OverstrikeBold : underline ? OverstrikeUnderline : style;

		_ = plain.Append(shown);

		this.CloseRun(plain, runs, ref runStart);
		this.Style = style;

		text = text[used..];

		return true;
	}

	/// <summary>
	/// Applies the codes of one SGR sequence, the part between <c>ESC [</c> and <c>m</c>.
	/// </summary>
	/// <param name="style">The style before the sequence.</param>
	/// <param name="parameters">The codes, separated by <c>;</c> or <c>:</c>. Empty means 0, a reset.</param>
	/// <returns>
	/// The style after the sequence.
	/// </returns>
	private static AnsiStyle ApplySgr(AnsiStyle style, ReadOnlySpan<char> parameters)
	{
		Span<int> codes = stackalloc int[MaxParameters];
		var count = 0;
		var value = 0;

		foreach (var character in parameters)
		{
			if (character is ';' or ':')
			{
				if (count < codes.Length)
				{
					codes[count++] = value;
				}

				value = 0;
			}
			else if (char.IsAsciiDigit(character))
			{
				// Capped so a long run of digits can't overflow. Every real code is far below this.
				value = Math.Min((value * 10) + (character - '0'), 100_000);
			}
		}

		if (count < codes.Length)
		{
			codes[count++] = value;
		}

		return ApplySgr(style, codes[..count]);
	}

	/// <summary>
	/// Applies already split SGR codes, which the terminal screen shares with this parser.
	/// </summary>
	/// <param name="style">The style before the sequence.</param>
	/// <param name="codes">The codes in order. A code left empty counts as 0.</param>
	/// <returns>
	/// The style after the sequence. Codes this doesn't know, like italic or blink, leave it unchanged.
	/// </returns>
	internal static AnsiStyle ApplySgr(AnsiStyle style, ReadOnlySpan<int> codes)
	{
		for (var i = 0; i < codes.Length; i++)
		{
			var code = Math.Max(codes[i], 0);

			switch (code)
			{
				case 0:
					style = default;
					break;
				case 1:
					style = style with { Bold = true };
					break;
				case 4:
					style = style with { Underline = true };
					break;
				case 7:
					style = style with { Inverse = true };
					break;
				case 8:
					style = style with { Hidden = true };
					break;
				case 22:
					style = style with { Bold = false };
					break;
				case 24:
					style = style with { Underline = false };
					break;
				case 27:
					style = style with { Inverse = false };
					break;
				case 28:
					style = style with { Hidden = false };
					break;
				case >= 30 and <= 37:
					style = style with { Foreground = new AnsiColor(code - 30, 0) };
					break;
				case 39:
					style = style with { Foreground = null };
					break;
				case >= 40 and <= 47:
					style = style with { Background = new AnsiColor(code - 40, 0) };
					break;
				case 49:
					style = style with { Background = null };
					break;
				case >= 90 and <= 97:
					style = style with { Foreground = new AnsiColor(code - 90 + 8, 0) };
					break;
				case >= 100 and <= 107:
					style = style with { Background = new AnsiColor(code - 100 + 8, 0) };
					break;
				case 38 or 48:
					var color = ReadExtendedColor(codes[(i + 1)..], out var used);
					i += used;

					if (color is not null)
					{
						style = code == 38 ? style with { Foreground = color } : style with { Background = color };
					}

					break;
				default:
					break;
			}
		}

		return style;
	}

	/// <summary>
	/// Reads the color after a 38 or 48 code: <c>5;n</c> for one of 256 colors, or <c>2;r;g;b</c> for an exact color.
	/// </summary>
	/// <param name="codes">The codes after the 38 or 48.</param>
	/// <param name="used">How many of <paramref name="codes" /> belong to the color, all of them when the form is unknown.</param>
	/// <returns>
	/// The color, or <see langword="null" /> when the codes don't form one.
	/// </returns>
	private static AnsiColor? ReadExtendedColor(ReadOnlySpan<int> codes, out int used)
	{
		if (codes is [5, var number, ..])
		{
			used = 2;
			return number <= 255 ? FromPalette256(number) : null;
		}

		if (codes is [2, var red, var green, var blue, ..])
		{
			used = 4;
			return new AnsiColor(null, (uint)((Math.Min(red, 255) << 16) | (Math.Min(green, 255) << 8) | Math.Min(blue, 255)));
		}

		used = codes.Length;
		return null;
	}

	/// <summary>
	/// Turns a color from the 256-color palette into a standard color or an RGB value.
	/// </summary>
	/// <param name="number">The palette number, from 0 to 255.</param>
	/// <returns>
	/// A standard color for 0 to 15, so it follows the window's theme like codes 30 to 37 do, and an RGB value for the
	/// rest.
	/// </returns>
	/// <remarks>
	/// 16 to 231 are a 6 by 6 by 6 color cube and 232 to 255 are 24 grays, using the levels xterm uses.
	/// </remarks>
	private static AnsiColor FromPalette256(int number)
	{
		if (number < 16)
		{
			return new AnsiColor(number, 0);
		}

		if (number >= 232)
		{
			var gray = (uint)(8 + (10 * (number - 232)));

			return new AnsiColor(null, (gray << 16) | (gray << 8) | gray);
		}

		var cube = number - 16;

		return new AnsiColor(null, (Level(cube / 36) << 16) | (Level(cube / 6 % 6) << 8) | Level(cube % 6));

		static uint Level(int step) => step == 0 ? 0 : (uint)(55 + (40 * step));
	}
}
