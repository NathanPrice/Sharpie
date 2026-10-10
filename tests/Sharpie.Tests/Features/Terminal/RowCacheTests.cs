using FluentAssertions;
using Sharpie.Features.Terminal;
using Sharpie.Features.Vt;

namespace Sharpie.Tests.Features.Terminal;

/// <summary>
/// Tests for <see cref="RowCache" />.
/// </summary>
public sealed class RowCacheTests
{
	private readonly RowCache cache = new();
	private readonly TerminalLine line = TerminalLine.Blank(4, 0);
	private readonly ShapedRun[] runs = [];

	/// <summary>
	/// Checks that a row drawn last frame and unchanged since gets back the same runs.
	/// </summary>
	[Fact]
	public void Find_AfterNextFrameWithSameCells_ReturnsStoredRuns()
	{
		// Arrange
		this.cache.Add(this.line, this.line.Cells, this.runs);
		this.cache.NextFrame();

		// Act
		var found = this.cache.Find(this.line, this.line.Cells);

		// Assert
		_ = found.Should().BeSameAs(this.runs);
	}

	/// <summary>
	/// Checks that a row whose character or style changed in place is laid out again.
	/// </summary>
	/// <param name="character">The new character in the first column.</param>
	/// <param name="style">The new style number in the first column.</param>
	[Theory]
	[InlineData('x', 0)]
	[InlineData(' ', 1)]
	public void Find_WhenACellChanged_ReturnsNull(char character, int style)
	{
		// Arrange
		this.cache.Add(this.line, this.line.Cells, this.runs);
		this.cache.NextFrame();
		this.line.Cells[0] = new Cell(character, '\0', 1, style);

		// Act
		var found = this.cache.Find(this.line, this.line.Cells);

		// Assert
		_ = found.Should().BeNull();
	}

	/// <summary>
	/// Checks that a row drawn with fewer columns after a resize is laid out again.
	/// </summary>
	[Fact]
	public void Find_WhenRowLengthChanged_ReturnsNull()
	{
		// Arrange
		this.cache.Add(this.line, this.line.Cells, this.runs);
		this.cache.NextFrame();

		// Act
		var found = this.cache.Find(this.line, this.line.Cells.AsSpan(0, 3));

		// Assert
		_ = found.Should().BeNull();
	}

	/// <summary>
	/// Checks that a row left out of a frame is dropped, so the cache never holds more than the last frame's rows.
	/// </summary>
	[Fact]
	public void Find_ForRowNotDrawnLastFrame_ReturnsNull()
	{
		// Arrange
		this.cache.Add(this.line, this.line.Cells, this.runs);
		this.cache.NextFrame();
		this.cache.NextFrame();

		// Act
		var found = this.cache.Find(this.line, this.line.Cells);

		// Assert
		_ = found.Should().BeNull();
	}

	/// <summary>
	/// Checks that a row found once stays cached for the frame after.
	/// </summary>
	[Fact]
	public void Find_ForRowFoundLastFrame_ReturnsStoredRuns()
	{
		// Arrange
		this.cache.Add(this.line, this.line.Cells, this.runs);
		this.cache.NextFrame();
		_ = this.cache.Find(this.line, this.line.Cells);
		this.cache.NextFrame();

		// Act
		var found = this.cache.Find(this.line, this.line.Cells);

		// Assert
		_ = found.Should().BeSameAs(this.runs);
	}

	/// <summary>
	/// Checks that a row never drawn has nothing cached.
	/// </summary>
	[Fact]
	public void Find_ForUnknownLine_ReturnsNull()
	{
		// Act
		var found = this.cache.Find(this.line, this.line.Cells);

		// Assert
		_ = found.Should().BeNull();
	}
}
