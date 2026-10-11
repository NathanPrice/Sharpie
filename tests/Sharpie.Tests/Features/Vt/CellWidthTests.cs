using FluentAssertions;
using Sharpie.Features.Vt;

namespace Sharpie.Tests.Features.Vt;

/// <summary>
/// Tests for <see cref="CellWidth" />.
/// </summary>
public sealed class CellWidthTests
{
	/// <summary>
	/// Checks the column counts for marks, ordinary and wide characters, including the first and last code point of a
	/// wide range and the one just past it.
	/// </summary>
	/// <param name="codePoint">The code point.</param>
	/// <param name="expected">The number of columns it should take.</param>
	[Theory]
	[InlineData('a', 1)]
	[InlineData(0x0301, 0)]
	[InlineData(0x200B, 0)]
	[InlineData(0x1100, 2)]
	[InlineData(0x115F, 2)]
	[InlineData(0x1160, 1)]
	[InlineData(0x4E00, 2)]
	[InlineData(0x1F600, 2)]
	[InlineData(0x3FFFD, 2)]
	[InlineData(0x40000, 1)]
	[InlineData(0x110000, 1)]
	public void Of_CodePoint_ReturnsColumns(int codePoint, int expected)
	{
		// Act
		var result = CellWidth.Of(codePoint);

		// Assert
		_ = result.Should().Be(expected);
	}
}
