using System.Globalization;
using FluentAssertions;
using Sharpie.Shell.Features.Time;

namespace Sharpie.Shell.Tests.Features.Time;

/// <summary>
/// Tests for <see cref="ShellTimeZone" />, which reads <c>TZ</c> the way glibc does.
/// </summary>
public sealed class ShellTimeZoneTests
{
	/// <summary>
	/// Checks the offset from UTC each kind of <c>TZ</c> value gives at fixed instants, on both sides of daylight
	/// saving switches.
	/// </summary>
	/// <param name="tz">The value.</param>
	/// <param name="utc">The instant, in UTC.</param>
	/// <param name="expectedHours">The offset from UTC, in hours.</param>
	[Theory]
	[InlineData("UTC", "2026-01-15T12:00:00Z", 0)]
	[InlineData("America/New_York", "2026-01-15T12:00:00Z", -5)]
	[InlineData("America/New_York", "2026-07-15T12:00:00Z", -4)]
	[InlineData(":Europe/London", "2026-07-15T12:00:00Z", 1)]
	[InlineData("/usr/share/zoneinfo/Asia/Tokyo", "2026-07-15T12:00:00Z", 9)]
	[InlineData("EST5EDT", "2026-07-15T12:00:00Z", -4)]
	[InlineData("XST5XDT", "2026-01-15T12:00:00Z", -5)]
	[InlineData("XST5XDT", "2026-03-08T06:59:00Z", -5)]
	[InlineData("XST5XDT", "2026-03-08T07:00:00Z", -4)]
	[InlineData("XST5XDT", "2026-11-01T05:59:00Z", -4)]
	[InlineData("XST5XDT", "2026-11-01T06:00:00Z", -5)]
	[InlineData("CET-1CEST,M3.5.0,M10.5.0/3", "2026-03-29T00:59:00Z", 1)]
	[InlineData("CET-1CEST,M3.5.0,M10.5.0/3", "2026-03-29T01:00:00Z", 2)]
	[InlineData("CET-1CEST,M3.5.0,M10.5.0/3", "2026-10-25T00:59:00Z", 2)]
	[InlineData("CET-1CEST,M3.5.0,M10.5.0/3", "2026-10-25T01:00:00Z", 1)]
	[InlineData("AEST-10AEDT,M10.1.0,M4.1.0/3", "2026-01-15T12:00:00Z", 11)]
	[InlineData("AEST-10AEDT,M10.1.0,M4.1.0/3", "2026-07-15T12:00:00Z", 10)]
	[InlineData("ABC+3DEF+1:30,J60/0,J300/0", "2026-02-28T12:00:00Z", -3)]
	[InlineData("ABC+3DEF+1:30,J60/0,J300/0", "2026-03-01T03:30:00Z", -1.5)]
	[InlineData("ABC+3DEF+1:30,J60/0,J300/0", "2026-06-15T12:00:00Z", -1.5)]
	[InlineData("ABC0DEF,59,299", "2026-02-28T12:00:00Z", 0)]
	[InlineData("ABC0DEF,59,299", "2026-06-15T12:00:00Z", 1)]
	[InlineData("<+0530>-5:30", "2026-07-15T12:00:00Z", 5.5)]
	[InlineData("JST-9", "2026-07-15T12:00:00Z", 9)]
	[InlineData("", "2026-07-15T12:00:00Z", 0)]
	[InlineData("junk", "2026-07-15T12:00:00Z", 0)]
	[InlineData("EST99", "2026-07-15T12:00:00Z", 0)]
	[InlineData("ABC0DEF,M3.5.0/-1,M10.5.0/25", "2026-07-15T12:00:00Z", 0)]
	[InlineData("ABC0DEF,M13.1.0,M10.5.0", "2026-07-15T12:00:00Z", 0)]
	public void Find_WithTz_GivesZoneOffset(string tz, string utc, double expectedHours)
	{
		// Arrange
		var instant = DateTime.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

		// Act
		var zone = ShellTimeZone.Find(tz);

		// Assert
		_ = zone.GetUtcOffset(instant).Should().Be(TimeSpan.FromHours(expectedHours));
	}

	/// <summary>
	/// Checks that an unset <c>TZ</c> gives the shell's own zone.
	/// </summary>
	[Fact]
	public void Find_WithoutTz_GivesLocalZone()
	{
		// Act
		var zone = ShellTimeZone.Find(null);

		// Assert
		_ = zone.Should().BeSameAs(TimeZoneInfo.Local);
	}
}
