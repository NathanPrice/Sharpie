using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Jobs;

/// <summary>
/// Brings a stopped or background job to the foreground and waits for it, as POSIX <c>fg</c> does.
/// </summary>
internal class FgCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "fg";
	/// <inheritdoc />
	public string Description => "Brings a job to the foreground";

	/// <inheritdoc />
	/// <remarks>
	/// Takes <c>%n</c> for job <c>n</c>, or nothing for the newest job. Prints the job's command line first, as bash
	/// does.
	/// </remarks>
	public void Run(string[] args)
	{
		if (JobsCommand.Find(this.Name, args.ArgumentNotNull()) is not { } job)
		{
			return;
		}

		Console.WriteLine(job.Command);
		_ = ExternalProgram.Jobs.Remove(job);
		JobControl.WaitInForeground(job, resume: true);

		if (job.State == JobState.Stopped)
		{
			ExternalProgram.AddJob(job);
			Console.WriteLine($@"{Environment.NewLine}[{job.Number}]+  {"Stopped",-24}{job.Command}");
		}
	}
}
