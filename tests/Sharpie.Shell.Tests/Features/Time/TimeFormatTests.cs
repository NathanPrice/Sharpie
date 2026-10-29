using FluentAssertions;
using Sharpie.Shell.Features.Time;

namespace Sharpie.Shell.Tests.Features.Time;

/// <summary>
/// Tests for <see cref="TimeFormat" />, checked against what bash 5.3 prints for the same layouts.
/// </summary>
public sealed class TimeFormatTests
{
	// 61.500775 seconds real, 0.09075 user and 1.999999 system.
	private static readonly ElapsedTimes Times = new(TimeSpan.FromMicroseconds(61_500_775), TimeSpan.FromMicroseconds(90_750), TimeSpan.FromMicroseconds(1_999_999));

	/// <summary>
	/// Checks each code a layout can have: precision rounds half up, except 0, which cuts to whole seconds; <c>l</c>
	/// adds minutes; <c>%P</c> is the whole percentage; and a line break ends the text.
	/// </summary>
	/// <param name="format">The layout.</param>
	/// <param name="expected">The text without its line break.</param>
	[Theory]
	[InlineData(TimeFormat.Default, "\nreal\t1m1.501s\nuser\t0m0.091s\nsys\t0m2.000s")]
	[InlineData(TimeFormat.Posix, "real 61.50\nuser 0.09\nsys 2.00")]
	[InlineData("%R", "61.501")]
	[InlineData("%0R", "61")]
	[InlineData("%0lR", "1m1s")]
	[InlineData("%1R", "61.5")]
	[InlineData("%4U", "0.0908")]
	[InlineData("%5U", "0.09075")]
	[InlineData("%6R", "61.500775")]
	[InlineData("%9R", "61.500775")]
	[InlineData("%2lS", "0m2.00s")]
	[InlineData("%P", "3.00")]
	[InlineData("100%% of %3S", "100% of 2.000")]
	[InlineData("ends in %", "ends in %")]
	[InlineData("", "")]
	public void Format_WithLayout_FillsItIn(string format, string expected)
	{
		// Act
		var text = TimeFormat.Format(format, Times, out var invalid);

		// Assert
		_ = text.Should().Be(expected + "\n");
		_ = invalid.Should().Be('\0');
	}

	/// <summary>
	/// Checks that rounding carries into the seconds and minutes, and that no real time gives 0 percent.
	/// </summary>
	[Fact]
	public void Format_WithFractionRoundingUp_CarriesIntoMinutes()
	{
		// Arrange
		var times = new ElapsedTimes(TimeSpan.Zero, TimeSpan.FromMicroseconds(59_999_600), TimeSpan.Zero);

		// Act
		var text = TimeFormat.Format("%lU %P", times, out _);

		// Assert
		_ = text.Should().Be("1m0.000s 0.00\n");
	}

	/// <summary>
	/// Checks that a <c>%</c> code bash doesn't know makes the whole layout fail, naming the character, which is
	/// <c>\0</c> when the layout ends inside the code.
	/// </summary>
	/// <param name="format">The layout.</param>
	/// <param name="expected">The character reported.</param>
	[Theory]
	[InlineData("a %x b", 'x')]
	[InlineData("%3", '\0')]
	[InlineData("%l", '\0')]
	[InlineData("%l3R", '3')]
	[InlineData("%0P", 'P')]
	[InlineData("%R %Q", 'Q')]
	public void Format_WithUnknownCode_FailsNamingIt(string format, char expected)
	{
		// Act
		var text = TimeFormat.Format(format, Times, out var invalid);

		// Assert
		_ = text.Should().BeNull();
		_ = invalid.Should().Be(expected);
	}
}
