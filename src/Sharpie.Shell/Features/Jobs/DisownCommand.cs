using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Jobs;

/// <summary>
/// Takes jobs out of the job table, as bash's <c>disown</c> does. The programs keep running, but <c>jobs</c>,
/// <c>fg</c>, <c>wait</c> and <c>kill %n</c> no longer know them.
/// </summary>
internal sealed class DisownCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "disown";

	/// <inheritdoc />
	public string Description => "Removes jobs from the job table";

	/// <inheritdoc />
	/// <remarks>
	/// Takes the newest job when given nothing, <c>%n</c> job numbers or process ids, or <c>-a</c> for every job.
	/// <c>-h</c> is taken and leaves the jobs in the table, as in bash, where it only keeps the shell from sending
	/// them SIGHUP when it exits. Sharpie never sends its jobs SIGHUP, so it changes nothing here. The status is 1
	/// when any job isn't found and 2 for an unknown option.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var options = args.TakeWhile(arg => arg is ['-', _, ..] and not "--").ToArray();
		var flags = string.Concat(options.Select(option => option[1..]));
		var targets = args[options.Length..] is ["--", .. var rest] ? rest : args[options.Length..];

		if (flags.FirstOrDefault(flag => flag is not ('a' or 'h')) is var unknown and not default(char))
		{
			new ShellError("sharpie::disown::invalid_option", $"Unknown option `-{unknown}`") { Help = "disown takes -a, -h and %n job numbers or process ids" }.Write();

			return 2;
		}

		var status = 0;
		List<Job> picked = [];

		if (flags.Contains('a', StringComparison.Ordinal))
		{
			picked.AddRange(ExternalProgram.Jobs);
		}
		else if (targets.Length == 0)
		{
			if (ExternalProgram.Jobs.LastOrDefault() is not { } newest)
			{
				new ShellError("sharpie::disown::no_such_job", "There is no current job").Write();

				return 1;
			}

			picked.Add(newest);
		}
		else
		{
			foreach (var target in targets)
			{
				if (!JobsCommand.TryFindTarget(this.Name, target, out _, out var job))
				{
					status = 1;
				}
				else if (job is null)
				{
					new ShellError("sharpie::disown::no_such_job", $"No job `{target}`") { Help = "jobs lists the running and stopped jobs" }.Write();
					status = 1;
				}
				else
				{
					picked.Add(job);
				}
			}
		}

		if (!flags.Contains('h', StringComparison.Ordinal))
		{
			foreach (var job in picked)
			{
				_ = ExternalProgram.Jobs.Remove(job);
				job.Process?.Dispose();
			}
		}

		return status;
	}
}
