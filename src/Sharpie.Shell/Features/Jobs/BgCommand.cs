using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Jobs;

/// <summary>
/// Lets a stopped job carry on in the background, as POSIX <c>bg</c> does.
/// </summary>
internal class BgCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "bg";
	/// <inheritdoc />
	public string Description => "Resumes a stopped job in the background";

	/// <inheritdoc />
	/// <remarks>
	/// Takes <c>%n</c> for job <c>n</c>, or nothing for the newest job, and prints it the way bash does, like
	/// <c>[1]+ sleep 100 &amp;</c>.
	/// </remarks>
	public void Run(string[] args)
	{
		if (JobsCommand.Find(this.Name, args.ArgumentNotNull()) is not { } job)
		{
			return;
		}

		_ = NativeMethods.Kill(-job.ProcessId, NativeMethods.SignalContinue);
		job.State = JobState.Running;
		Console.WriteLine($@"[{job.Number}]+ {job.Command} &");
	}
}
