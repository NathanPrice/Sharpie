using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Jobs;

namespace Sharpie.Shell.Tests.Features.Jobs;

/// <summary>
/// Starts real background jobs for the <c>wait</c> and <c>kill</c> tests, and ends whatever they leave running.
/// </summary>
internal static class BackgroundJobs
{
	/// <summary>
	/// Starts a program as a background job, the way a line ending in <c>&amp;</c> does.
	/// </summary>
	/// <param name="program">The program, found on <c>PATH</c>.</param>
	/// <param name="arguments">Its arguments.</param>
	/// <returns>
	/// The job, already in <see cref="ExternalProgram.Jobs" />.
	/// </returns>
	public static Job Start(string program, params string[] arguments)
	{
		_ = ExternalProgram.Run(program, arguments, background: true);

		return ExternalProgram.Jobs[^1];
	}

	/// <summary>
	/// Kills every job still in the table and empties it.
	/// </summary>
	public static void EndAll()
	{
		foreach (var job in ExternalProgram.Jobs)
		{
			if (job.Process is { } process)
			{
				process.Kill();
				process.Dispose();
			}
			else
			{
				_ = NativeMethods.Kill(job.ProcessId, 9);
				_ = NativeMethods.WaitPid(job.ProcessId, out _, 0);
			}
		}

		ExternalProgram.Jobs.Clear();
	}
}
