using System.Globalization;
using System.Text.RegularExpressions;

namespace Sharpie.Shell.Features.Time;

/// <summary>
/// The time zone the exported <c>TZ</c> names, which the prompt's clock follows as bash's does after <c>tzset</c>.
/// </summary>
/// <remarks>
/// .NET works out <see cref="TimeZoneInfo.Local" /> once, when the shell starts, and Windows never reads <c>TZ</c>, so
/// <c>export TZ=Asia/Tokyo</c> wouldn't move <see cref="DateTime.Now" />. Daylight saving comes from the zone's rules:
/// the system's time zone data for a named zone, or the rule in a POSIX <c>TZ</c> string.
/// </remarks>
internal static partial class ShellTimeZone
{
	/// <summary>
	/// Gets the time now in the zone <c>TZ</c> names.
	/// </summary>
	/// <returns>
	/// The local time there.
	/// </returns>
	internal static DateTime Now() => TimeZoneInfo.ConvertTime(DateTime.UtcNow, Find(Environment.GetEnvironmentVariable("TZ")));

	/// <summary>
	/// Finds the zone a <c>TZ</c> value names, as glibc reads it.
	/// </summary>
	/// <param name="tz">
	/// The value: an IANA or Windows zone id, with or without a leading <c>:</c>, a path to a file under a
	/// <c>zoneinfo</c> folder, or a POSIX string like <c>CET-1CEST,M3.5.0,M10.5.0/3</c>.
	/// </param>
	/// <returns>
	/// The zone. The shell's own when <paramref name="tz" /> is <see langword="null" />, and UTC when it's empty or can't
	/// be read, as glibc has it.
	/// </returns>
	/// <remarks>
	/// shortcut: once <c>TZ</c> is unset this gives the zone .NET found at startup, which is the startup <c>TZ</c> if
	/// one was set rather than <c>/etc/localtime</c>; read <c>/etc/localtime</c> here if that case matters.
	/// </remarks>
	internal static TimeZoneInfo Find(string? tz)
	{
		if (tz is null)
		{
			return TimeZoneInfo.Local;
		}

		var id = tz.StartsWith(':') ? tz[1..] : tz;

		id = id.LastIndexOf("zoneinfo/", StringComparison.Ordinal) is >= 0 and var at ? id[(at + "zoneinfo/".Length)..] : id;

		return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : FromPosixRule(tz) ?? TimeZoneInfo.Utc;
	}

	/// <summary>
	/// Builds a zone from a POSIX <c>TZ</c> string: <c>std offset [dst [offset] [,start[/time],end[/time]]]</c>.
	/// </summary>
	/// <param name="tz">The string.</param>
	/// <returns>
	/// The zone, or <see langword="null" /> when the string doesn't follow the form.
	/// </returns>
	/// <remarks>
	/// Names are 3 or more letters, or anything inside <c>&lt;&gt;</c>. Offsets count hours west of UTC, so
	/// <c>EST5</c> is UTC-5. Daylight time is an hour ahead unless its offset is given, and without a rule it follows
	/// glibc's default, <c>M3.2.0,M11.1.0</c>. A switch happens at 02:00 unless its time is given.
	/// </remarks>
	private static TimeZoneInfo? FromPosixRule(string tz)
	{
		var match = PosixRule().Match(tz);

		if (!match.Success)
		{
			return null;
		}

		var offset = -Offset(match.Groups["offset"].Value);

		try
		{
			if (!match.Groups["dst"].Success)
			{
				return TimeZoneInfo.CreateCustomTimeZone(tz, offset, tz, tz);
			}

			var delta = (match.Groups["dstoffset"].Success ? -Offset(match.Groups["dstoffset"].Value) : offset + TimeSpan.FromHours(1)) - offset;
			var start = match.Groups["start"].Success ? match.Groups["start"].Value : "M3.2.0";
			var end = match.Groups["end"].Success ? match.Groups["end"].Value : "M11.1.0";
			var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
				DateTime.MinValue.Date,
				DateTime.MaxValue.Date,
				delta,
				Transition(start, match.Groups["starttime"].Value),
				Transition(end, match.Groups["endtime"].Value));

			return TimeZoneInfo.CreateCustomTimeZone(tz, offset, tz, tz, tz, [rule]);
		}
		catch (ArgumentException)
		{
			// An offset beyond 14 hours, one with seconds, or a switch time outside 00:00 to 24:00, which .NET's rules
			// can't hold; glibc would take the last two.
			return null;
		}
	}

	/// <summary>
	/// Reads a POSIX offset or switch time, <c>[+-]hh[:mm[:ss]]</c>.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <returns>
	/// The time it gives.
	/// </returns>
	private static TimeSpan Offset(string text)
	{
		var sign = text.StartsWith('-') ? -1 : 1;
		var parts = text.TrimStart('+', '-').Split(':').Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();

		return sign * new TimeSpan(parts[0], parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2));
	}

	/// <summary>
	/// Builds the date a daylight saving switch happens on.
	/// </summary>
	/// <param name="date">
	/// <c>Mm.w.d</c> for day <c>d</c> (0 is Sunday) of week <c>w</c> (5 is the last) of month <c>m</c>, <c>Jn</c> for
	/// day <c>n</c> of the year from 1, never counting February 29, or <c>n</c> for day <c>n</c> from 0, counting it.
	/// </param>
	/// <param name="time">The time of day, or empty for 02:00.</param>
	/// <returns>
	/// The switch.
	/// </returns>
	private static TimeZoneInfo.TransitionTime Transition(string date, string time)
	{
		var at = DateTime.MinValue + (time.Length > 0 ? Offset(time) : TimeSpan.FromHours(2));

		if (date.StartsWith('M'))
		{
			var parts = date[1..].Split('.').Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();

			return TimeZoneInfo.TransitionTime.CreateFloatingDateRule(at, parts[0], parts[1], (DayOfWeek)parts[2]);
		}

		// shortcut: a zero-based day is taken as the same day of a year with no February 29, so it lands a day late from
		// March on in leap years. .NET's rules can't hold a day that moves with leap years; nothing in tzdata uses it.
		var day = date.StartsWith('J') ? int.Parse(date[1..], CultureInfo.InvariantCulture) : int.Parse(date, CultureInfo.InvariantCulture) + 1;
		var fixedDate = new DateTime(2001, 1, 1).AddDays(day - 1);

		return TimeZoneInfo.TransitionTime.CreateFixedDateRule(at, fixedDate.Month, fixedDate.Day);
	}

	[GeneratedRegex("""
		^([A-Za-z]{3,}|<[A-Za-z0-9+-]+>)(?<offset>[+-]?\d{1,2}(:\d{1,2}){0,2})
		((?<dst>[A-Za-z]{3,}|<[A-Za-z0-9+-]+>)(?<dstoffset>[+-]?\d{1,2}(:\d{1,2}){0,2})?
		(,(?<start>M\d{1,2}\.[1-5]\.[0-6]|J?\d{1,3})(/(?<starttime>[+-]?\d{1,3}(:\d{1,2}){0,2}))?
		,(?<end>M\d{1,2}\.[1-5]\.[0-6]|J?\d{1,3})(/(?<endtime>[+-]?\d{1,3}(:\d{1,2}){0,2}))?)?)?$
		""", RegexOptions.IgnorePatternWhitespace | RegexOptions.ExplicitCapture)]
	private static partial Regex PosixRule();
}
