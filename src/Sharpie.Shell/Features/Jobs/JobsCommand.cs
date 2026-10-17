using System.Globalization;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Jobs;

/// <summary>
/// Lists the background jobs the shell knows about.
/// </summary>
internal class JobsCommand : ICommand
{
	/// <summary>
	/// The width the status column is padded to, matching bash's <c>jobs</c> output.
	/// </summary>
	private const int StatusWidth = 24;

	/// <inheritdoc />
	public string Name => "jobs";
	/// <inheritdoc />
	public string Description => "Lists background and stopped jobs";

	/// <inheritdoc />
	/// <remarks>
	/// Reaps finished jobs like the shell does before each prompt, and also lists the ones still running or stopped,
	/// like <c>[1]+  Running                 sleep 10 &amp;</c>.
	/// </remarks>
	public int Run(string[] args)
	{
		Reap(listRunning: true);

		return 0;
	}

	/// <summary>
	/// Prints a <c>Done</c> line, or <c>Exit N</c> for a program that failed, for each job in <see cref="ExternalProgram.Jobs" /> whose program has exited, then
	/// removes those jobs from the table.
	/// </summary>
	/// <param name="listRunning">
	/// <see langword="true" /> to also print a <c>Running</c> or <c>Stopped</c> line for each job that hasn't exited.
	/// </param>
	/// <remarks>
	/// As in bash, <c>+</c> marks the newest job and <c>-</c> the one before it. Markers are worked out before any job
	/// is removed, so a finished job keeps the marker it had. A <c>Done</c> line has no trailing <c>&amp;</c>, and
	/// each job gets one only once because it leaves the table straight after.
	/// </remarks>
	internal static void Reap(bool listRunning)
	{
		var jobs = ExternalProgram.Jobs;
		var done = new List<Job>();

		for (var i = 0; i < jobs.Count; i++)
		{
			var marker = i == jobs.Count - 1 ? '+' : i == jobs.Count - 2 ? '-' : ' ';

			// Checked once per job and remembered, so a program that exits after its line is printed stays listed
			// as Running until the next jobs call instead of vanishing unreported.
			if (jobs[i].HasExited)
			{
				done.Add(jobs[i]);

				// Like bash, a program that failed shows its exit code instead of Done.
				var code = jobs[i].Process?.ExitCode ?? jobs[i].ExitCode;
				var status = code != 0 ? $"Exit {code}" : "Done";

				Console.WriteLine($@"[{jobs[i].Number}]{marker}  {status,-StatusWidth}{jobs[i].Command}");
			}
			else if (listRunning && jobs[i].State == JobState.Stopped)
			{
				Console.WriteLine($@"[{jobs[i].Number}]{marker}  {"Stopped",-StatusWidth}{jobs[i].Command}");
			}
			else if (listRunning)
			{
				Console.WriteLine($@"[{jobs[i].Number}]{marker}  {"Running",-StatusWidth}{jobs[i].Command} &");
			}
		}

		_ = jobs.RemoveAll(done.Contains);

		foreach (var job in done)
		{
			job.Process?.Dispose();
		}
	}

	/// <summary>
	/// Finds the job <c>fg</c> or <c>bg</c> should act on, printing why when there's none.
	/// </summary>
	/// <param name="command">The builtin's name, which starts any message.</param>
	/// <param name="args">Nothing for the newest job, or <c>%n</c> (or just <c>n</c>) for job <c>n</c>.</param>
	/// <returns>
	/// The job, or <see langword="null" /> after printing the reason.
	/// </returns>
	internal static Job? Find(string command, string[] args)
	{
		if (!JobControl.Enabled)
		{
			new ShellError($"sharpie::{command}::no_job_control", "Job control is off in this shell").Write();

			return null;
		}

		if (args.Length == 0)
		{
			if (ExternalProgram.Jobs.LastOrDefault() is { } newest)
			{
				return newest;
			}

			new ShellError($"sharpie::{command}::no_such_job", "There is no current job").Write();

			return null;
		}

		var number = int.TryParse(args[0].TrimStart('%'), out var parsed) ? parsed : -1;

		if (ExternalProgram.Jobs.Find(job => job.Number == number) is { } found)
		{
			return found;
		}

		new ShellError($"sharpie::{command}::no_such_job", $"No job `{args[0]}`") { Help = "jobs lists the running and stopped jobs" }.Write();

		return null;
	}

	/// <summary>
	/// Reads a job or process id the way <c>wait</c>, <c>kill</c> and <c>disown</c> take one, which works with job
	/// control off too.
	/// </summary>
	/// <param name="command">The builtin's name, which starts the error code.</param>
	/// <param name="target"><c>%n</c> for job <c>n</c>, or a process id.</param>
	/// <param name="pid">The process id, or 0 for <c>%n</c>.</param>
	/// <param name="job">
	/// The job numbered <c>n</c>, or the one the process belongs to, or <see langword="null" /> when there's none.
	/// </param>
	/// <returns>
	/// <see langword="false" /> after printing why, when <paramref name="target" /> is neither a <c>%n</c> nor a
	/// whole number.
	/// </returns>
	/// <remarks>
	/// Unlike <see cref="Find" />, a plain number is a process id here, as bash reads it for these three builtins.
	/// </remarks>
	internal static bool TryFindTarget(string command, string target, out int pid, out Job? job)
	{
		pid = 0;
		job = null;

		var number = 0;
		// Stryker disable once all : a mutant here can hand kill process id 0, the test run's own process group, which would end the mutation run.
		var valid = target.StartsWith('%')
			? int.TryParse(target[1..], NumberStyles.None, CultureInfo.InvariantCulture, out number)
			: int.TryParse(target, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out pid);

		// Stryker disable once all : a mutant here can hand kill process id 0, the test run's own process group, which would end the mutation run.
		if (!valid)
		{
			new ShellError($"sharpie::{command}::invalid_target", $"`{target}` isn't a job or a process id") { Help = "use %n for job n, or a process id" }.Write();

			return false;
		}

		if (target.StartsWith('%'))
		{
			job = ExternalProgram.Jobs.Find(found => found.Number == number);

			return true;
		}

		var id = pid;

		job = ExternalProgram.Jobs.Find(found => found.ProcessId == id || found.Members.Contains(id));

		return true;
	}
}
