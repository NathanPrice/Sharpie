using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="Escapes" />.
/// </summary>
public sealed class EscapesTests
{
	/// <summary>
	/// Checks that <see cref="Escapes.Decode" /> reads the escapes of <c>$'...'</c> as bash does.
	/// </summary>
	/// <param name="text">The text between the quotes.</param>
	/// <param name="expected">The text it stands for.</param>
	[Theory]
	[InlineData(@"a\tb\n", "a\tb\n")]
	[InlineData(@"\a\b\e\E\f\r\v\\", "\a\b\e\e\f\r\v\\")]
	[InlineData(@"it\'s \""q\"" \?", "it's \"q\" ?")]
	[InlineData(@"\101\0101\7", "A\b1\a")]
	[InlineData(@"\777", "\u00ff")]
	[InlineData(@"\x41\x4g\x", "A\u0004g\\x")]
	[InlineData(@"\xc3\xa9", "é")]
	[InlineData(@"\xff", "\u00ff")]
	[InlineData(@"\u00e9\U0001F600\u\U", "é😀\\u\\U")]
	[InlineData(@"\u00411", "A1")]
	[InlineData(@"\U0010FFFF", "\U0010FFFF")]
	[InlineData(@"\udfff", "\uFFFD")]
	[InlineData(@"\ud800", "\uFFFD")]
	[InlineData(@"\cA\ca\c?\c", "\u0001\u0001\u007f\\c")]
	[InlineData(@"\q\", "\\q\\")]
	public void Decode_AnsiC_GivesCharacters(string text, string expected)
	{
		// Act
		var decoded = Escapes.Decode(text, EscapeRules.AnsiC, out var stopped);

		// Assert
		_ = decoded.Should().Be(expected);
		_ = stopped.Should().BeFalse();
	}

	/// <summary>
	/// Checks that <see cref="Escapes.Decode" /> reads the escapes of <c>echo -e</c>, which differ from
	/// <c>$'...'</c> in octal, quotes and <c>\c</c>.
	/// </summary>
	/// <param name="text">The text printed.</param>
	/// <param name="expected">What it prints.</param>
	/// <param name="stops">Whether a <c>\c</c> cut it short.</param>
	[Theory]
	[InlineData(@"\0101\101\0", "A\\101\0", false)]
	[InlineData(@"\'\""\?", "\\'\\\"\\?", false)]
	[InlineData(@"\x41\u00e9\t", "Aé\t", false)]
	[InlineData(@"a\cb", "a", true)]
	public void Decode_Echo_GivesCharacters(string text, string expected, bool stops)
	{
		// Act
		var decoded = Escapes.Decode(text, EscapeRules.Echo, out var stopped);

		// Assert
		_ = decoded.Should().Be(expected);
		_ = stopped.Should().Be(stops);
	}
}
