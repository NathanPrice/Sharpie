using System.Diagnostics;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Jobs;

/// <summary>
/// Whether a job's programs are running, stopped by a signal like Ctrl+Z, or done.
/// </summary>
internal enum JobState
{
	/// <summary>
	/// At least one program is still running.
	/// </summary>
	Running,

	/// <summary>
	/// The job was stopped and waits for <c>fg</c> or <c>bg</c>.
	/// </summary>
	Stopped,

	/// <summary>
	/// Every program has exited.
	/// </summary>
	Done,
}

/// <summary>
/// The programs started for one command line, which the shell waits for, stops, and resumes together.
/// </summary>
/// <param name="number">
/// The job number, one more than the newest job still in the table, or 1 when the table is empty, as in bash.
/// </param>
/// <param name="processId">
/// The process group the programs share on Unix with job control, or the first program's id otherwise.
/// </param>
/// <param name="command">The command line as <c>jobs</c> shows it, without a trailing <c>&amp;</c>.</param>
/// <param name="process">
/// The program, when it was started through <see cref="System.Diagnostics.Process" /> as on Windows, or
/// <see langword="null" />.
/// </param>
internal sealed class Job(int number, int processId, string command, Process? process = null)
{
	// The ids of the programs still running, for jobs started with posix_spawn. Empty for the others.
	private readonly List<int> members = [];

	/// <summary>
	/// Gets or sets the job number.
	/// </summary>
	/// <value>
	/// The number <c>jobs</c>, <c>fg</c> and <c>bg</c> show and take after <c>%</c>.
	/// </value>
	public int Number { get; set; } = number;

	/// <summary>
	/// Gets the process group id, or the first program's id when the job has no group of its own.
	/// </summary>
	/// <value>
	/// What <c>&amp;</c> prints after the job number.
	/// </value>
	public int ProcessId { get; } = processId;

	/// <summary>
	/// Gets the command line the job runs.
	/// </summary>
	/// <value>
	/// The words as the shell split them, joined by spaces.
	/// </value>
	public string Command { get; } = command;

	/// <summary>
	/// Gets the program started through <see cref="System.Diagnostics.Process" />.
	/// </summary>
	/// <value>
	/// The program, or <see langword="null" /> for jobs started with <c>posix_spawn</c>.
	/// </value>
	public Process? Process { get; } = process;

	/// <summary>
	/// Gets or sets the job's state.
	/// </summary>
	/// <value>
	/// <see cref="JobState.Running" /> until a stop or the last program's exit is seen.
	/// </value>
	public JobState State { get; set; }

	/// <summary>
	/// Gets or sets the exit code of the job's last program.
	/// </summary>
	/// <value>
	/// The code, or 128 plus the signal number when a signal ended it, as in bash.
	/// </value>
	public int ExitCode { get; set; }

	/// <summary>
	/// Gets or sets the terminal modes the job had when it stopped, so <c>fg</c> can give them back.
	/// </summary>
	/// <value>
	/// The saved <c>termios</c>, or <see langword="null" /> when the job never stopped in the foreground.
	/// </value>
	public byte[]? Modes { get; set; }

	/// <summary>
	/// Gets a value indicating whether every program has exited, without waiting for it.
	/// </summary>
	/// <value>
	/// <see langword="true" /> once the job is done; <see langword="false" /> while it runs or is stopped, and always
	/// for a job with neither a <see cref="Process" /> nor programs of its own.
	/// </value>
	/// <remarks>
	/// The .NET runtime already reaps every child started through <see cref="System.Diagnostics.Process" /> from its
	/// own SIGCHLD handler, so calling <c>waitpid</c> on those would race it. <see cref="Process.HasExited" /> reads the
	/// state the runtime recorded. Children started with <c>posix_spawn</c> are unknown to the runtime and are polled
	/// with <c>waitpid</c> instead.
	/// </remarks>
	public bool HasExited
	{
		get
		{
			if (this.Process is not null)
			{
				return this.Process.HasExited;
			}

			if (this.members.Count > 0)
			{
				this.Poll();
			}

			return this.State == JobState.Done;
		}
	}

	/// <summary>
	/// Gets the ids of the programs that haven't exited yet.
	/// </summary>
	/// <value>
	/// The live members, last stage last.
	/// </value>
	internal IReadOnlyList<int> Members => this.members;

	/// <summary>
	/// Adds a program started with <c>posix_spawn</c> to the job.
	/// </summary>
	/// <param name="pid">The program's process id.</param>
	internal void Add(int pid) => this.members.Add(pid);

	/// <summary>
	/// Records a status <c>waitpid</c> returned for one of the job's programs.
	/// </summary>
	/// <param name="pid">The program the status belongs to.</param>
	/// <param name="status">The encoded status.</param>
	internal void Record(int pid, int status)
	{
		// The low seven bits are 0x7F for a stop or a continue, with the signal in the next byte, and 0 for a normal exit,
		// with the exit code in the next byte. Anything else is the signal that ended the program. Linux reports a continue
		// as 0xFFFF.
		var low = status & 0x7F;
		var high = (status >> 8) & 0xFF;

		if (status == 0xFFFF || (low == 0x7F && high == NativeMethods.SignalContinue))
		{
			this.State = JobState.Running;

			return;
		}

		if (low == 0x7F)
		{
			this.State = JobState.Stopped;

			return;
		}

		var last = this.members.Count > 0 && this.members[^1] == pid;

		_ = this.members.Remove(pid);

		if (last)
		{
			this.ExitCode = low == 0 ? high : 128 + low;
		}

		if (this.members.Count == 0)
		{
			this.State = JobState.Done;
		}
	}

	/// <summary>
	/// Collects any state changes of the job's programs without waiting.
	/// </summary>
	internal void Poll()
	{
		foreach (var pid in this.members.ToArray())
		{
			if (NativeMethods.WaitPid(pid, out var status, NativeMethods.NoHang | NativeMethods.Untraced | NativeMethods.Continued) == pid)
			{
				this.Record(pid, status);
			}
		}
	}
}
