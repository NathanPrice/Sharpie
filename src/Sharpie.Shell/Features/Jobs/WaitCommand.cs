using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Features.Jobs;

/// <summary>
/// Waits for background jobs to finish, as POSIX <c>wait</c> does.
/// </summary>
/// <remarks>
/// The jobs stay in the background and never get the terminal, unlike with <c>fg</c>. A signal the shell catches,
/// like Ctrl+C's or a trapped one, ends the wait with 128 plus its number, as in bash, and leaves them running.
/// </remarks>
internal sealed class WaitCommand : ICommand
{
	// How often the jobs are checked: soon enough that the wait ends right after the job, rarely enough to cost nothing.
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

	/// <summary>
	/// Gets the status for a wait that a signal ended.
	/// </summary>
	/// <value>
	/// 128 plus the signal's number: 130 for Ctrl+C.
	/// </value>
	private static int Interrupted => 128 + TrapCommand.LastSignal;

	/// <inheritdoc />
	public string Name => "wait";

	/// <inheritdoc />
	public string Description => "Waits for background jobs to finish";

	/// <inheritdoc />
	/// <remarks>
	/// With no arguments it waits for every job that isn't stopped and gives 0. With <c>%n</c> job numbers or process
	/// ids it waits for each in turn and gives the last one's status. Finished jobs leave the table without a
	/// <c>Done</c> line. As in bash, a job or process the shell doesn't know gives 127, a word that is neither gives 1,
	/// and an option gives 2.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args is [['-', _, ..] option, ..] && option != "--")
		{
			new ShellError("sharpie::wait::invalid_option", $"Unknown option `{option}`") { Help = "wait takes %n job numbers or process ids" }.Write();

			return 2;
		}

		var targets = args is ["--", .. var rest] ? rest : args;

		if (targets.Length == 0)
		{
			return Wait([.. ExternalProgram.Jobs]) is null ? Interrupted : 0;
		}

		var status = 0;

		foreach (var target in targets)
		{
			if (!JobsCommand.TryFindTarget(this.Name, target, out var pid, out var job))
			{
				status = 1;
			}
			else if (job is null)
			{
				var error = target.StartsWith('%')
					? new ShellError("sharpie::wait::no_such_job", $"No job `{target}`") { Help = "jobs lists the running and stopped jobs" }
					: new ShellError("sharpie::wait::not_a_child", $"Process {pid} isn't a job of this shell");

				error.Write();
				status = ExternalProgram.NotFound;
			}
			else if (Wait([job]) is { } code)
			{
				status = code;
			}
			else
			{
				return Interrupted;
			}
		}

		return status;
	}

	/// <summary>
	/// Waits until every job in <paramref name="jobs" /> has finished or stopped, then takes the finished ones out of
	/// the job table.
	/// </summary>
	/// <param name="jobs">The jobs to wait for.</param>
	/// <returns>
	/// The last job's status, or <see langword="null" /> when a signal ended the wait first.
	/// </returns>
	private static int? Wait(IReadOnlyList<Job> jobs)
	{
		var arrivals = TrapCommand.Arrivals;

		while (jobs.Any(job => !job.HasExited && job.State != JobState.Stopped))
		{
			if (TrapCommand.Arrivals != arrivals)
			{
				return null;
			}

			Thread.Sleep(PollInterval);
		}

		var status = 0;

		foreach (var job in jobs)
		{
			if (job.State == JobState.Stopped)
			{
				// The job doesn't keep which signal stopped it, so SIGSTOP gives the same status as Ctrl+Z's SIGTSTP.
				// Job.Record would have to keep the signal to tell them apart.
				status = 128 + NativeMethods.SignalStop;

				continue;
			}

			status = job.Process?.ExitCode ?? job.ExitCode;
			_ = ExternalProgram.Jobs.Remove(job);
			job.Process?.Dispose();
		}

		return status;
	}
}
