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
			Console.WriteLine($@"{command}: no job control");

			return null;
		}

		if (args.Length == 0)
		{
			if (ExternalProgram.Jobs.LastOrDefault() is { } newest)
			{
				return newest;
			}

			Console.WriteLine($@"{command}: current: no such job");

			return null;
		}

		var number = int.TryParse(args[0].TrimStart('%'), out var parsed) ? parsed : -1;

		if (ExternalProgram.Jobs.Find(job => job.Number == number) is { } found)
		{
			return found;
		}

		Console.WriteLine($@"{command}: {args[0]}: no such job");

		return null;
	}
}
