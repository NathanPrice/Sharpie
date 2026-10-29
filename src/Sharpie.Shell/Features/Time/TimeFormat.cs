using System.Globalization;
using System.Text;

namespace Sharpie.Shell.Features.Time;

/// <summary>
/// Writes the times of a timed pipeline by a <c>TIMEFORMAT</c> layout, as bash 5.3's <c>print_formatted_time</c>
/// does.
/// </summary>
internal static class TimeFormat
{
	/// <summary>
	/// The layout used when <c>TIMEFORMAT</c> isn't set.
	/// </summary>
	internal const string Default = "\nreal\t%3lR\nuser\t%3lU\nsys\t%3lS";

	/// <summary>
	/// The layout of <c>time -p</c>, which POSIX gives.
	/// </summary>
	internal const string Posix = "real %2R\nuser %2U\nsys %2S";

	/// <summary>
	/// Fills in a layout.
	/// </summary>
	/// <param name="format">
	/// The layout: <c>%%</c> for a <c>%</c>, <c>%P</c> for the processor time as a percentage of the real time, and
	/// <c>%[p][l]R</c>, <c>U</c> or <c>S</c> for the real, user or system seconds with <c>p</c> digits after the point,
	/// 3 by default and at most 6, and with <c>l</c> as minutes and seconds, like <c>1m2.345s</c>.
	/// </param>
	/// <param name="times">The times.</param>
	/// <param name="invalid">
	/// Set to the character after <c>%</c> that isn't one of the above, or to <c>\0</c> when the layout ends there.
	/// </param>
	/// <returns>
	/// The text with a line break after it, or <see langword="null" /> when the layout has a bad <c>%</c>, which bash
	/// reports instead of showing anything.
	/// </returns>
	internal static string? Format(string format, ElapsedTimes times, out char invalid)
	{
		var text = new StringBuilder();

		invalid = '\0';

		for (var index = 0; index < format.Length; index++)
		{
			// A % at the very end is kept as typed, as bash keeps it.
			if (format[index] != '%' || index == format.Length - 1)
			{
				_ = text.Append(format[index]);

				continue;
			}

			var code = At(++index);

			if (code is '%' or 'P')
			{
				_ = code == '%' ? text.Append('%') : text.Append(Percent(times));

				continue;
			}

			var precision = char.IsAsciiDigit(code) ? Math.Min(code - '0', 6) : 3;

			code = char.IsAsciiDigit(code) ? At(++index) : code;

			var minutes = code == 'l';

			code = minutes ? At(++index) : code;

			TimeSpan? value = code switch
			{
				'R' => times.Real,
				'U' => times.User,
				'S' => times.System,
				_ => null,
			};

			if (value is not { } seconds)
			{
				invalid = code;

				return null;
			}

			_ = text.Append(Seconds(seconds, precision, minutes));
		}

		return text.Append('\n').ToString();

		char At(int index) => index < format.Length ? format[index] : '\0';
	}

	/// <summary>
	/// Writes a time in seconds.
	/// </summary>
	/// <param name="time">The time.</param>
	/// <param name="precision">How many digits to write after the point, 0 to 6.</param>
	/// <param name="minutes"><see langword="true" /> to write whole minutes first, like <c>1m2.345s</c>.</param>
	/// <returns>
	/// The time, rounded half up to <paramref name="precision" /> digits, or cut to whole seconds for 0, as bash 5.3
	/// does.
	/// </returns>
	private static string Seconds(TimeSpan time, int precision, bool minutes)
	{
		var micro = Math.Max(time.Ticks / TimeSpan.TicksPerMicrosecond, 0);
		var unit = (long)Math.Pow(10, 6 - precision);

		micro = precision == 0 ? micro / unit * unit : (micro + (unit / 2)) / unit * unit;

		var seconds = micro / 1_000_000;
		var fraction = precision == 0 ? string.Empty : "." + (micro % 1_000_000).ToString("D6", CultureInfo.InvariantCulture)[..precision];

		return minutes
			? string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}m{seconds % 60}{fraction}s")
			: string.Create(CultureInfo.InvariantCulture, $"{seconds}{fraction}");
	}

	/// <summary>
	/// Writes the processor time as a percentage of the real time.
	/// </summary>
	/// <param name="times">The times.</param>
	/// <returns>
	/// The whole percentage, rounded down, with <c>.00</c> after it, which is what bash 5.3 writes; 0.00 when no real
	/// time passed.
	/// </returns>
	private static string Percent(ElapsedTimes times) =>
		string.Create(CultureInfo.InvariantCulture, $"{(times.Real.Ticks == 0 ? 0 : (times.User.Ticks + times.System.Ticks) * 100 / times.Real.Ticks)}.00");
}
