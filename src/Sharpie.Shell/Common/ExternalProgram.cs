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

	// The id of the program running now, or 0 between programs, so Ctrl+C knows what to stop. Written by the main
	// thread and read by the signal handler's thread. Only used without job control, where the terminal can't send the
	// signal to the program itself.
	private static volatile int sRunningId;

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
	/// <returns>
	/// The program's exit status, 128 plus the signal number when a signal ended it, 0 for a background job, or 126
	/// when it was found but couldn't start (that error is printed here). <see langword="null" /> when the program
	/// isn't on <c>PATH</c>, and nothing was printed.
	/// </returns>
	public static int? Run(string commandName, string[] arguments, Stream? output = null, Stream? error = null, bool background = false, Stream? input = null)
	{
		_ = commandName.ArgumentNotNull();
		_ = arguments.ArgumentNotNull();

		var fullPath = PathFinder.FindExecutable(commandName);

		// Stryker disable once Conditional : each runner only runs on its own OS, so a test run on one OS can't tell them apart.
		return fullPath is null ? null
			: OperatingSystem.IsWindows() ? RunWithProcess(commandName, fullPath, arguments, output, error, background, input)
			: RunWithSpawn(commandName, fullPath, arguments, output, error, background, input);
	}

	/// <summary>
	/// Runs <paramref name="stages" /> as a pipeline, feeding each program's standard output to the next program's
	/// standard input, and waits for all of them to finish.
	/// </summary>
	/// <param name="stages">Each program's name followed by its arguments, in pipeline order.</param>
	/// <remarks>
	/// If any program isn't on <c>PATH</c>, its name is reported and none of them run. Bash would still start the
	/// others.
	/// </remarks>
	/// <returns>
	/// The last program's status as <see cref="Run" /> gives it, 127 when a program isn't on <c>PATH</c>, or 126 when
	/// one couldn't start.
	/// </returns>
	public static int RunPipeline(string[][] stages)
	{
		_ = stages.ArgumentNotNull();

		var paths = stages.Select(stage => PathFinder.FindExecutable(stage[0])).ToArray();

		if (paths.Contains(null))
		{
			for (var index = 0; index < stages.Length; index++)
			{
				if (paths[index] is null)
				{
					Console.WriteLine($@"{stages[index][0]}: command not found");
				}
			}

			return NotFound;
		}

		// Stryker disable once Conditional : each runner only runs on its own OS, so a test run on one OS can't tell them apart.
		return OperatingSystem.IsWindows() ? RunPipelineWithProcesses(stages, paths!) : RunPipelineWithSpawn(stages, paths!);
	}

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
