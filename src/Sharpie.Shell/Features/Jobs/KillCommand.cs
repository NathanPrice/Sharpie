using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Features.Jobs;

/// <summary>
/// Sends a signal to jobs or processes, or lists the signals, as POSIX <c>kill</c> does.
/// </summary>
/// <remarks>
/// Windows has no signals, so there <c>kill</c> takes only TERM, KILL and INT, and each of them ends the program and
/// every program it started.
/// </remarks>
internal sealed class KillCommand : ICommand
{
	// SIGHUP and SIGTERM have the same numbers on every OS. TERM is what kill sends when no signal is named.
	private const int Hangup = 1;
	private const int Terminate = 15;

	// Each OS's signals from 1 up, in number order.
	private const string LinuxNames = "HUP INT QUIT ILL TRAP ABRT BUS FPE KILL USR1 SEGV USR2 PIPE ALRM TERM STKFLT CHLD CONT STOP TSTP TTIN TTOU URG XCPU XFSZ VTALRM PROF WINCH IO PWR SYS";
	private const string MacNames = "HUP INT QUIT ILL TRAP ABRT EMT FPE KILL BUS SEGV SYS PIPE ALRM TERM URG STOP TSTP CONT CHLD TTIN TTOU IO XCPU XFSZ VTALRM PROF WINCH INFO USR1 USR2";

	/// <inheritdoc />
	public string Name => "kill";

	/// <inheritdoc />
	public string Description => "Sends a signal to jobs or processes";

	/// <summary>
	/// Gets the signals this OS has.
	/// </summary>
	/// <value>
	/// Each signal's name without the <c>SIG</c> prefix, by number, in number order.
	/// </value>
	// Stryker disable once all : each OS only takes its own branch, so a test run on one OS can't tell them apart.
	internal static IReadOnlyDictionary<int, string> Signals { get; } = CreateTable(OperatingSystem.IsMacOS() ? OSPlatform.OSX : OperatingSystem.IsWindows() ? OSPlatform.Windows : OSPlatform.Linux);

	/// <inheritdoc />
	/// <remarks>
	/// Takes <c>kill [-s SIGNAL | -n NUMBER | -SIGNAL] TARGET...</c>, where each target is <c>%n</c> for job <c>n</c>
	/// or a process id, and sends TERM when no signal is named. With job control a job's whole process group gets the
	/// signal, and without it each of its programs. A job sent TERM or HUP also gets CONT so that a stopped job can
	/// end, which bash does for a job it knows is stopped. <c>kill -l</c> lists the signals, and <c>kill -l X</c> turns
	/// a number into a name or a name into a number. The status is 0 when any target got the signal, 1 when none did
	/// or a signal is unknown, and 2 with no targets, as bash gives.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args is ["-l", .. var specs])
		{
			return List(specs);
		}

		if (ReadOptions(args, out var signal, out var targets) is { } status)
		{
			return status;
		}

		var sent = false;

		foreach (var target in targets)
		{
			sent |= this.Send(target, signal);
		}

		return sent ? 0 : 1;
	}

	/// <summary>
	/// Builds the signal table for one OS.
	/// </summary>
	/// <param name="platform">Linux, macOS or Windows.</param>
	/// <returns>
	/// The table <see cref="Signals" /> describes.
	/// </returns>
	/// <remarks>
	/// Linux also gets the real-time signals, named the way bash lists them, from 34 since glibc keeps 32 and 33 for
	/// itself. Windows gets only the three signals that ask a program to end.
	/// </remarks>
	internal static SortedDictionary<int, string> CreateTable(OSPlatform platform)
	{
		var macOS = platform == OSPlatform.OSX;
		var names = (macOS ? MacNames : LinuxNames).Split(' ');
		var table = new SortedDictionary<int, string>();

		for (var index = 0; index < names.Length; index++)
		{
			table[index + 1] = names[index];
		}

		if (platform == OSPlatform.Windows)
		{
			return new(table.Where(signal => signal.Value is "INT" or "KILL" or "TERM").ToDictionary());
		}

		if (!macOS)
		{
			for (var number = 34; number <= 64; number++)
			{
				table[number] = number switch
				{
					34 => "RTMIN",
					64 => "RTMAX",
					< 50 => $"RTMIN+{number - 34}",
					_ => $"RTMAX-{64 - number}",
				};
			}
		}

		return table;
	}

	/// <summary>
	/// Reads a signal's name or number.
	/// </summary>
	/// <param name="spec">A number, or a name in any case, with or without the <c>SIG</c> prefix.</param>
	/// <param name="table">The signals to look in, or <see langword="null" /> for <see cref="Signals" />.</param>
	/// <returns>
	/// The signal's number, or <see langword="null" /> when this OS has no such signal.
	/// </returns>
	/// <remarks>
	/// Signal 0 sends nothing and only checks that the process exists, which Windows has no way to do.
	/// </remarks>
	internal static int? ParseSignal(string spec, IReadOnlyDictionary<int, string>? table = null)
	{
		table ??= Signals;

		if (int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
		{
			return table.ContainsKey(number) || (number == 0 && !OperatingSystem.IsWindows()) ? number : null;
		}

		var name = spec.StartsWith("SIG", StringComparison.OrdinalIgnoreCase) ? spec[3..] : spec;

		return table.Where(signal => signal.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(signal => (int?)signal.Key).FirstOrDefault();
	}

	/// <summary>
	/// Works out which ids <c>kill</c> signals for a job.
	/// </summary>
	/// <param name="job">The job.</param>
	/// <param name="jobControl"><see langword="true" /> when job control is on.</param>
	/// <returns>
	/// The job's process group as a negative id with job control, since only then does the job have a group of its
	/// own, and otherwise each program still running.
	/// </returns>
	internal static int[] Targets(Job job, bool jobControl) => jobControl ? [-job.ProcessId] : [.. job.Members];

	/// <summary>
	/// Reads the signal option and the targets after it.
	/// </summary>
	/// <param name="args">The arguments, not starting with <c>-l</c>.</param>
	/// <param name="signal">The signal named, or TERM when none is.</param>
	/// <param name="targets">The words after the option and any <c>--</c>.</param>
	/// <returns>
	/// <see langword="null" /> when the arguments are fine, or else the status to give after printing why.
	/// </returns>
	private static int? ReadOptions(string[] args, out int signal, out string[] targets)
	{
		signal = Terminate;
		targets = args;

		string? spec = null;

		if (args is ["-s" or "-n"])
		{
			new ShellError("sharpie::kill::missing_signal", $"`{args[0]}` needs a signal after it") { Help = "kill -l lists the signals" }.Write();

			return 1;
		}

		if (args is ["-s" or "-n", var named, .. var rest])
		{
			spec = named;
			targets = rest;
		}
		else if (args is [['-', not '-', ..] option, .. var after])
		{
			// As in bash, -sTERM is -s TERM, so -sigterm names "igterm", while -SIGTERM names TERM.
			spec = option is ['-', 's' or 'n', ..] ? option[2..] : option[1..];
			targets = after;
		}

		if (targets is ["--", .. var plain])
		{
			targets = plain;
		}

		if (spec is not null)
		{
			if (ParseSignal(spec) is not { } number)
			{
				InvalidSignal(spec);

				return 1;
			}

			signal = number;
		}

		if (targets.Length == 0)
		{
			new ShellError("sharpie::kill::missing_target", "kill needs a job or a process id") { Help = "kill [-s SIGNAL | -SIGNAL] %n|pid..., or kill -l to list the signals" }.Write();

			return 2;
		}

		return null;
	}

	/// <summary>
	/// Prints every signal five to a row as bash does, or turns each of <paramref name="specs" /> into a name or a
	/// number.
	/// </summary>
	/// <param name="specs">Signal numbers and names, or none to list them all.</param>
	/// <param name="table">The signals, or <see langword="null" /> for <see cref="Signals" />.</param>
	/// <returns>
	/// 0, or 1 when any of <paramref name="specs" /> isn't a signal.
	/// </returns>
	internal static int List(string[] specs, IReadOnlyDictionary<int, string>? table = null)
	{
		table ??= Signals;

		if (specs.Length == 0)
		{
			foreach (var row in table.Select(signal => $"{signal.Key,2}) SIG{signal.Value}").Chunk(5))
			{
				// bash ends a short last row with a tab too.
				Console.WriteLine(string.Join('\t', row) + (row.Length < 5 ? "\t" : string.Empty));
			}

			return 0;
		}

		var status = 0;

		foreach (var spec in specs)
		{
			// A number past 128 is read as an exit status, so 143 names TERM, as in bash.
			var answer = int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
				? table.GetValueOrDefault(number > 128 ? number - 128 : number)
				: ParseSignal(spec, table)?.ToString(CultureInfo.InvariantCulture);

			if (answer is null)
			{
				InvalidSignal(spec);
				status = 1;
			}
			else
			{
				Console.WriteLine(answer);
			}
		}

		return status;
	}

	/// <summary>
	/// Prints that <paramref name="spec" /> isn't a signal.
	/// </summary>
	/// <param name="spec">The word that was read as a signal.</param>
	private static void InvalidSignal(string spec) =>
		new ShellError("sharpie::kill::invalid_signal", $"`{spec}` isn't a signal") { Help = "kill -l lists the signals" }.Write();

	/// <summary>
	/// Sends <paramref name="signal" /> to every program in <paramref name="job" />.
	/// </summary>
	/// <param name="job">The job.</param>
	/// <param name="signal">The signal number.</param>
	/// <returns>
	/// <see langword="true" /> when the signal was sent.
	/// </returns>
	private static bool SendToJob(Job job, int signal)
	{
		// Stryker disable once Conditional : each OS only takes its own branch, so a test run on one OS can't tell them apart.
		if (OperatingSystem.IsWindows())
		{
			try
			{
				job.Process?.Kill(entireProcessTree: true);
			}
			// The program already exited.
			catch (InvalidOperationException)
			{
			}

			return true;
		}

		foreach (var pid in Targets(job, JobControl.Enabled))
		{
			if (!SendToProcess(pid, signal))
			{
				return false;
			}

			// The shell may not have seen a stop yet, as straight after kill -STOP, so this doesn't check the state first
			// the way bash does.
			if (signal is Terminate or Hangup)
			{
				_ = NativeMethods.Kill(pid, NativeMethods.SignalContinue);
			}
		}

		return true;
	}

	/// <summary>
	/// Sends <paramref name="signal" /> to one process, or on Windows ends it and every program it started.
	/// </summary>
	/// <param name="pid">The process id, or on Unix a negative process group id.</param>
	/// <param name="signal">The signal number.</param>
	/// <returns>
	/// <see langword="true" /> when the signal was sent, or <see langword="false" /> after printing why not.
	/// </returns>
	private static bool SendToProcess(int pid, int signal)
	{
		string reason;
		var arrivals = TrapCommand.Arrivals;

		// Stryker disable once Conditional : each OS only takes its own branch, so a test run on one OS can't tell them apart.
		if (OperatingSystem.IsWindows())
		{
			try
			{
				using var process = Process.GetProcessById(pid);

				process.Kill(entireProcessTree: true);

				return true;
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
			{
				reason = exception.Message;
			}
		}
		else if (NativeMethods.Kill(pid, signal) == 0)
		{
			// The shell's own handler runs on another thread, so its trap would otherwise run a command or more late.
			if (pid == Environment.ProcessId)
			{
				TrapCommand.WaitForArrival(signal, arrivals);
			}

			return true;
		}
		else
		{
			reason = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
		}

		new ShellError("sharpie::kill::failed", $"Can't signal {pid}: {reason}").Write();

		return false;
	}

	/// <summary>
	/// Sends <paramref name="signal" /> to one target.
	/// </summary>
	/// <param name="target"><c>%n</c> for job <c>n</c>, or a process id.</param>
	/// <param name="signal">The signal number.</param>
	/// <returns>
	/// <see langword="true" /> when the signal was sent, or <see langword="false" /> after printing why not.
	/// </returns>
	private bool Send(string target, int signal)
	{
		// Stryker disable once all : a mutant here can hand kill process id 0, the test run's own process group, which would end the mutation run.
		if (!JobsCommand.TryFindTarget(this.Name, target, out var pid, out var job))
		{
			return false;
		}

		// Stryker disable once all : a mutant here can hand kill process id 0, the test run's own process group, which would end the mutation run.
		if (!target.StartsWith('%'))
		{
			return SendToProcess(pid, signal);
		}

		if (job is not null)
		{
			return SendToJob(job, signal);
		}

		new ShellError("sharpie::kill::no_such_job", $"No job `{target}`") { Help = "jobs lists the running and stopped jobs" }.Write();

		return false;
	}
}
