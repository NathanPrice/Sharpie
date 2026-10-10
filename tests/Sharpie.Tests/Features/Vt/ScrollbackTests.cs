using FluentAssertions;
using Sharpie.Features.Vt;

namespace Sharpie.Tests.Features.Vt;

/// <summary>
/// Tests for <see cref="Scrollback" />.
/// </summary>
public sealed class ScrollbackTests
{
	/// <summary>
	/// Checks that adding hands back nothing while there's room, then the oldest row it dropped.
	/// </summary>
	[Fact]
	public void Add_WhenFull_ReturnsOldestRow()
	{
		// Arrange
		var scrollback = new Scrollback(2);
		var oldest = TerminalLine.Blank(1, 0);

		// Act
		var first = scrollback.Add(oldest);
		var second = scrollback.Add(TerminalLine.Blank(1, 0));
		var third = scrollback.Add(TerminalLine.Blank(1, 0));

		// Assert
		_ = first.Should().BeNull();
		_ = second.Should().BeNull();
		_ = third.Should().BeSameAs(oldest);
		_ = scrollback.Count.Should().Be(2);
	}
}
