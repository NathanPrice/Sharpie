using DotNetTips.Spargine.Core;
using Sharpie.Shell.Features.Jobs;

namespace Sharpie.Shell.Common;

/// <summary>
/// Starts programs found on <c>PATH</c>, either waiting for them to finish or leaving them running in the background.
/// </summary>
/// <remarks>
/// Linux and macOS start programs with <see cref="PosixSpawn" />, which gives each job its own process group and real
/// pipes. Windows has neither, so it starts them through <see cref="System.Diagnostics.Process" />.
/// </remarks>
internal static partial class ExternalProgram
{
	/// <summary>
	/// The status POSIX gives a command that isn't a builtin or a program on <c>PATH</c>.
	/// </summary>
	internal const int NotFound = 127;

	/// <summary>
	/// The status POSIX gives a program that was found but couldn't be started.
	/// </summary>
	internal const int CannotRun = 126;

	// SIGINT is 2 on both Linux and macOS.
	private const int Interrupt = 2;

	/// <summary>
	/// Gets the jobs that are running in the background or stopped, oldest first.
	/// </summary>
	/// <value>
	/// The job table that <c>jobs</c> lists and <c>fg</c> and <c>bg</c> pick from. Only the main thread touches it. A
	/// finished job stays in it until <c>jobs</c> has reported it as done once.
	/// </value>
	internal static List<Job> Jobs { get; } = [];

	/// <summary>
	/// Gets or sets the process id of the last program started in the background.
	/// </summary>
	/// <value>
	/// The id <c>$!</c> expands to, or <see langword="null" /> before any program ran in the background, which leaves
	/// <c>$!</c> unset. Only the main thread touches it.
	/// </value>
	internal static int? LastBackground { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether a job started in the background is announced with its number and process
	/// id, as <c>[1] 4242</c>.
	/// </summary>
	/// <value>
	/// <see langword="true" /> in an interactive shell. bash says nothing in a script or <c>-c</c>, though the job is
	/// still tracked for <c>jobs</c>, <c>wait</c> and <c>$!</c>.
	/// </value>
	internal static bool AnnounceJobs { get; set; }

	/// <summary>
	/// Gets the output copies of the background programs that command substitutions started, each with the stream
	/// it fills.
	/// </summary>
	/// <value>
	/// What <see cref="WaitForDetached" /> waits on. Only the main thread touches it.
	/// </value>
	private static List<(Stream Output, Task Copy)> Detached { get; } = [];

	/// <summary>
	/// Cancels <see cref="WaitForDetached" /> when Ctrl+C is pressed, or <see langword="null" /> when nothing waits.
	/// </summary>
	private static volatile CancellationTokenSource? sDetachedWait;

	// The id of the program running now, or 0 between programs, so Ctrl+C knows what to stop. Written by the main
	// thread and read by the signal handler's thread. Only used without job control, where the terminal can't send the
	// signal to the program itself.
	private static volatile int sRunningId;

	/// <summary>
	/// Gets a value indicating whether the shell is waiting on a program it started without job control.
	/// </summary>
	/// <value>
	/// <see langword="true" /> while it waits, read by the signal handler's thread to tell whether Ctrl+C was meant
	/// for the program.
	/// </value>
	internal static bool ProgramRunning => sRunningId != 0;

	/// <summary>
	/// Runs <paramref name="commandName" /> as a child process if an executable with that name is on <c>PATH</c>.
	/// </summary>
	/// <param name="commandName">The program name as the user typed it.</param>
	/// <param name="arguments">The arguments to pass to the program, each one as a separate argument.</param>
	/// <param name="output">
	/// Where the program's standard output goes, or <see langword="null" /> to let it write to the terminal.
	/// </param>
	/// <param name="error">
	/// Where the program's standard error goes, or <see langword="null" /> to let it write to the terminal.
	/// </param>
	/// <param name="background">
	/// <see langword="true" /> to print the job number and process id and return without waiting, as for a command
	/// ending in <c>&amp;</c>.
	/// </param>
	/// <param name="input">
	/// What the program reads as standard input, closed once it's all written, or <see langword="null" /> to let it
	/// read the terminal.
	/// </param>
	/// <param name="assignments">
	/// <c>NAME=VALUE</c> words that only this program gets in its environment, as in <c>LANG=C sort</c>, or
	/// <see langword="null" /> for none. An assigned <c>PATH</c> is also where the program is looked for, as in bash.
	/// </param>
	/// <param name="redirection">
	/// The command's redirects, applied over <paramref name="input" />, <paramref name="output" /> and
	/// <paramref name="error" />, which are its own streams 0, 1 and 2. <see langword="null" /> for none.
	/// </param>
	/// <param name="listJob">
	/// <see langword="false" /> for a background program inside a command substitution. Like one in bash's
	/// subshell, it isn't added to <see cref="Jobs" /> or announced, and its output still goes to
	/// <paramref name="output" />, which <see cref="WaitForDetached" /> waits for.
	/// </param>
	/// <returns>
	/// The program's exit status, 128 plus the signal number when a signal ended it, 0 for a background job, or 126
	/// when it was found but couldn't start (that error is printed here). <see langword="null" /> when the program
	/// isn't on <c>PATH</c>, and nothing was printed.
	/// </returns>
	public static int? Run(string commandName, string[] arguments, Stream? output = null, Stream? error = null, bool background = false, Stream? input = null, IReadOnlyList<string>? assignments = null, Redirection? redirection = null, bool listJob = true)
	{
		_ = commandName.ArgumentNotNull();
		_ = arguments.ArgumentNotNull();

		assignments ??= [];

		var fullPath = PathFinder.FindExecutable(commandName, AssignedPath(assignments));
		// Stryker disable once Conditional : each runner only runs on its own OS, so a test run on one OS can't tell them apart.
		return fullPath is null ? null
			: OperatingSystem.IsWindows() ? RunWithProcess(commandName, fullPath, arguments, output, error, background, input, assignments, redirection, listJob)
			: RunWithSpawn(commandName, fullPath, arguments, output, error, background, input, assignments, redirection?.Targets ?? Redirection.Standard, listJob);
	}

	/// <summary>
	/// Runs <paramref name="stages" /> as a pipeline, feeding each program's standard output to the next program's
	/// standard input, and waits for all of them to finish.
	/// </summary>
	/// <param name="stages">Each program's name followed by its arguments, in pipeline order.</param>
	/// <param name="notFound">Reports the stage, by its index, whose program isn't on <c>PATH</c>.</param>
	/// <param name="assignments">
	/// Each stage's <c>NAME=VALUE</c> words, in the same order, which only that stage's program gets as <see cref="Run" />
	/// describes, or <see langword="null" /> for none.
	/// </param>
	/// <param name="pipeFail">
	/// <see langword="true" /> for <c>set -o pipefail</c>, which gives the status of the last program that failed.
	/// </param>
	/// <param name="output">
	/// Where the last program's standard output goes, or <see langword="null" /> to let it write to the terminal.
	/// </param>
	/// <param name="redirections">
	/// Each stage's redirects, in the same order, applied over the pipe ends that are its own streams, or
	/// <see langword="null" /> for none.
	/// </param>
	/// <param name="input">
	/// What the first program reads as standard input, closed once it's all written, or <see langword="null" /> to
	/// let it read the terminal.
	/// </param>
	/// <remarks>
	/// If any program isn't on <c>PATH</c>, <paramref name="notFound" /> is called for it and none of them run. Bash
	/// would still start the others.
	/// </remarks>
	/// <returns>
	/// The last program's status as <see cref="Run" /> gives it, or with <paramref name="pipeFail" /> the status of
	/// the rightmost program that didn't give 0. 127 when a program isn't on <c>PATH</c>, or 126 when one couldn't
	/// start.
	/// </returns>
	public static int RunPipeline(string[][] stages, Action<int> notFound, string[][]? assignments = null, bool pipeFail = false, Stream? output = null, IReadOnlyList<Redirection>? redirections = null, Stream? input = null)
	{
		_ = stages.ArgumentNotNull();
		_ = notFound.ArgumentNotNull();

		assignments ??= [.. stages.Select(_ => Array.Empty<string>())];

		var targets = stages.Select((_, index) => redirections?[index].Targets ?? Redirection.Standard).ToArray();

		var paths = stages.Select((stage, index) => PathFinder.FindExecutable(stage[0], AssignedPath(assignments[index]))).ToArray();

		if (paths.Contains(null))
		{
			for (var index = 0; index < stages.Length; index++)
			{
				if (paths[index] is null)
				{
					notFound(index);
				}
			}

			return NotFound;
		}

		// Stryker disable once Conditional : each runner only runs on its own OS, so a test run on one OS can't tell them apart.
		return OperatingSystem.IsWindows() ? RunPipelineWithProcesses(stages, paths!, assignments, targets, pipeFail, output, input) : RunPipelineWithSpawn(stages, paths!, assignments, targets, pipeFail, output, input);
	}

	/// <summary>
	/// Waits until the background programs that a command substitution started have closed their output, as bash
	/// waits for the end of the substitution's pipe.
	/// </summary>
	/// <param name="output">The substitution's stream.</param>
	/// <remarks>
	/// A program that sends its output elsewhere, like <c>server &gt; log &amp;</c>, isn't waited for. Ctrl+C, through
	/// <see cref="StopWaiting" />, stops the wait and leaves the programs running.
	/// </remarks>
	public static void WaitForDetached(Stream output)
	{
		var copies = Detached.Where(entry => entry.Output == output).Select(entry => entry.Copy).ToArray();

		_ = Detached.RemoveAll(entry => entry.Output == output);

		using var wait = new CancellationTokenSource();

		sDetachedWait = wait;

		try
		{
			Task.WaitAll(copies, wait.Token);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			sDetachedWait = null;
		}
	}

	/// <summary>
	/// Stops <see cref="WaitForDetached" />, for Ctrl+C. Does nothing when nothing waits.
	/// </summary>
	public static void StopWaiting()
	{
		try
		{
			sDetachedWait?.Cancel();
		}
		// The wait ended and disposed it between the read and the cancel, on the main thread.
		catch (ObjectDisposedException)
		{
		}
	}

	/// <summary>
	/// Works out a pipeline's status from its programs' statuses.
	/// </summary>
	/// <param name="codes">Each program's status, in pipeline order.</param>
	/// <param name="pipeFail"><see langword="true" /> for <c>set -o pipefail</c>.</param>
	/// <returns>
	/// The last status, or with <paramref name="pipeFail" /> the rightmost one that isn't 0, and 0 when all are.
	/// </returns>
	internal static int PipelineStatus(IReadOnlyList<int> codes, bool pipeFail) =>
		pipeFail ? codes.LastOrDefault(code => code != 0) : codes[^1];

	/// <summary>
	/// Gets the value of the last <c>PATH=</c> word in <paramref name="assignments" />.
	/// </summary>
	/// <param name="assignments">A program's <c>NAME=VALUE</c> words.</param>
	/// <returns>
	/// The assigned <c>PATH</c>, or <see langword="null" /> when none of the words sets it.
	/// </returns>
	internal static string? AssignedPath(IReadOnlyList<string> assignments) =>
		assignments.LastOrDefault(assignment => assignment.StartsWith("PATH=", StringComparison.Ordinal))?["PATH=".Length..];

	/// <summary>
	/// Stops the program <see cref="Run" /> is waiting on, the way Ctrl+C does in other shells. Does nothing when
	/// no program is running.
	/// </summary>
	/// <remarks>
	/// On Unix the program gets SIGINT rather than being killed, so it can finish up the same as after a real Ctrl+C,
	/// like ping printing its summary. Windows has no SIGINT to send, so the program and its children are killed
	/// there. If the program exits at the same moment, its id could already belong to another process; the window for
	/// that is tiny.
	/// </remarks>
	public static void InterruptRunning()
	{
		var id = sRunningId;

		if (id == 0)
		{
			return;
		}

		if (OperatingSystem.IsWindows())
		{
			try
			{
				System.Diagnostics.Process.GetProcessById(id).Kill(entireProcessTree: true);
			}
			// The program already exited.
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
			{
			}

			return;
		}

		SendInterrupt(id);
	}

	/// <summary>
	/// Sends SIGINT to the process with id <paramref name="processId" />. Does nothing on Windows.
	/// </summary>
	/// <param name="processId">The id of the process to interrupt.</param>
	/// <remarks>
	/// .NET has no API for sending a signal to another process, so this calls the C library's <c>kill</c>.
	/// </remarks>
	public static void SendInterrupt(int processId)
	{
		if (!OperatingSystem.IsWindows())
		{
			_ = NativeMethods.Kill(processId, Interrupt);
		}
	}

	/// <summary>
	/// Adds <paramref name="job" /> to <see cref="Jobs" /> with the next free number.
	/// </summary>
	/// <param name="job">The job, not yet in the table.</param>
	internal static void AddJob(Job job)
	{
		job.Number = (Jobs.LastOrDefault()?.Number ?? 0) + 1;
		Jobs.Add(job);
	}
}
