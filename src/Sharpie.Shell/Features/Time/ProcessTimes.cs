using System.Diagnostics;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Time;

/// <summary>
/// How long a timed pipeline took, as bash's <c>time</c> reports it.
/// </summary>
/// <param name="Real">The time that passed on the clock.</param>
/// <param name="User">The processor time spent running the shell's and its programs' own code.</param>
/// <param name="System">The processor time the system spent working for them.</param>
internal sealed record ElapsedTimes(TimeSpan Real, TimeSpan User, TimeSpan System);

/// <summary>
/// The clock and processor times when a timed pipeline started, to measure it against when it ends.
/// </summary>
/// <remarks>
/// The real time comes from <see cref="Stopwatch" />, which only counts up, so a daylight saving switch, a time zone
/// change or the clock being set while the pipeline runs doesn't change it.
/// </remarks>
internal sealed class ProcessTimes
{
	private readonly long started = Stopwatch.GetTimestamp();

	private readonly (TimeSpan User, TimeSpan System) processor = NativeMethods.ProcessorTimes();

	private ProcessTimes()
	{
	}

	/// <summary>
	/// Notes the times now.
	/// </summary>
	/// <returns>
	/// The times, for <see cref="Stop" />.
	/// </returns>
	public static ProcessTimes Start() => new();

	/// <summary>
	/// Measures the time since <see cref="Start" />.
	/// </summary>
	/// <returns>
	/// The real, user and system time that passed.
	/// </returns>
	public ElapsedTimes Stop()
	{
		var (user, system) = NativeMethods.ProcessorTimes();

		return new ElapsedTimes(Stopwatch.GetElapsedTime(this.started), user - this.processor.User, system - this.processor.System);
	}
}
