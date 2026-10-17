using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Jobs;

namespace Sharpie.Shell.Tests.Features.Jobs;

/// <summary>
/// Tests for <see cref="KillCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class KillCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly KillCommand kill = new();
	private readonly WaitCommand wait = new();
	private readonly List<Process> started = [];

	/// <summary>
	/// Initializes a new instance of the <see cref="KillCommandTests" /> class with an empty job table.
	/// </summary>
	public KillCommandTests() => ExternalProgram.Jobs.Clear();

	/// <summary>
	/// Checks the Linux table against the numbers bash lists, real-time signals included and 32 and 33 left out.
	/// </summary>
	[Fact]
	public void CreateTable_ForLinux_MatchesBashNumbers()
	{
		// Act
		var table = KillCommand.CreateTable(OSPlatform.Linux);

		// Assert
		_ = table.Count.Should().Be(62);
		_ = (table[1], table[7], table[10], table[15], table[16], table[17], table[18], table[19], table[20], table[31])
			.Should().Be(("HUP", "BUS", "USR1", "TERM", "STKFLT", "CHLD", "CONT", "STOP", "TSTP", "SYS"));
		_ = table.Keys.Should().NotContain([32, 33]);
		_ = (table[34], table[35], table[49], table[50], table[63], table[64])
			.Should().Be(("RTMIN", "RTMIN+1", "RTMIN+15", "RTMAX-14", "RTMAX-1", "RTMAX"));
	}

	/// <summary>
	/// Checks the macOS table against the numbers in its <c>signal.h</c>, where several differ from Linux.
	/// </summary>
	[Fact]
	public void CreateTable_ForMacOS_MatchesItsNumbers()
	{
		// Act
		var table = KillCommand.CreateTable(OSPlatform.OSX);

		// Assert
		_ = table.Count.Should().Be(31);
		_ = (table[7], table[10], table[12], table[16], table[17], table[18], table[19], table[20], table[29], table[30], table[31])
			.Should().Be(("EMT", "BUS", "SYS", "URG", "STOP", "TSTP", "CONT", "CHLD", "INFO", "USR1", "USR2"));
	}

	/// <summary>
	/// Checks that Windows only gets the three signals that ask a program to end.
	/// </summary>
	[Fact]
	public void CreateTable_ForWindows_HasOnlyIntKillAndTerm()
	{
		// Act
		var table = KillCommand.CreateTable(OSPlatform.Windows);

		// Assert
		_ = table.Should().Equal(new Dictionary<int, string> { [2] = "INT", [9] = "KILL", [15] = "TERM" });
	}

	/// <summary>
	/// Checks that this OS's table agrees with the signal numbers the shell already uses for job control.
	/// </summary>
	[Fact]
	public void Signals_OnUnix_AgreeWithJobControlNumbers()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var names = (KillCommand.Signals[NativeMethods.SignalContinue], KillCommand.Signals[NativeMethods.SignalStop], KillCommand.Signals[NativeMethods.SignalChild], KillCommand.Signals[NativeMethods.SignalTerminalInput]);

		// Assert
		_ = names.Should().Be(("CONT", "TSTP", "CHLD", "TTIN"));
	}

	/// <summary>
	/// Checks that a signal is read from its number, or its name in any case with or without <c>SIG</c>.
	/// </summary>
	/// <param name="spec">The signal as typed.</param>
	/// <param name="expected">Its number, or <see langword="null" /> when it isn't a signal.</param>
	[Theory]
	[InlineData("TERM", 15)]
	[InlineData("term", 15)]
	[InlineData("SIGTERM", 15)]
	[InlineData("sigkill", 9)]
	[InlineData("9", 9)]
	[InlineData("0", 0)]
	[InlineData("FOO", null)]
	[InlineData("SIG", null)]
	[InlineData("99", null)]
	[InlineData("-1", null)]
	public void ParseSignal_WithSpec_GivesNumber(string spec, int? expected)
	{
		// Act
		var number = KillCommand.ParseSignal(spec);

		// Assert
		_ = number.Should().Be(OperatingSystem.IsWindows() && expected is not (2 or 9 or 15) ? null : expected);
	}

	/// <summary>
	/// Checks that a job is signalled as its whole process group with job control, since only then has it a group of
	/// its own, and program by program without.
	/// </summary>
	/// <param name="jobControl">Whether job control is on.</param>
	/// <param name="expected">The ids that get the signal.</param>
	[Theory]
	[InlineData(true, new[] { -11 })]
	[InlineData(false, new[] { 11, 12 })]
	public void Targets_ForJob_DependsOnJobControl(bool jobControl, int[] expected)
	{
		// Arrange
		var job = new Job(1, 11, "a | b");
		job.Add(11);
		job.Add(12);

		// Act
		var targets = KillCommand.Targets(job, jobControl);

		// Assert
		_ = targets.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <c>kill -l</c> lists the signals five to a row with bash's numbering and tabs, ending a short last row
	/// with a tab as bash does.
	/// </summary>
	[Fact]
	public void Run_WithListOption_PrintsSignalsLikeBash()
	{
		// Arrange
		if (!OperatingSystem.IsLinux())
		{
			return;
		}

		// Act
		var status = this.kill.Run(["-l"]);

		// Assert
		var lines = this.environment.Output.Split('\n');
		_ = status.Should().Be(0);
		_ = lines[0].Should().Be(" 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 4) SIGILL\t 5) SIGTRAP");
		_ = lines[6].Should().Be("31) SIGSYS\t34) SIGRTMIN\t35) SIGRTMIN+1\t36) SIGRTMIN+2\t37) SIGRTMIN+3");
		_ = lines[12].Should().Be("63) SIGRTMAX-1\t64) SIGRTMAX\t");
		_ = lines.Length.Should().Be(14);
	}

	/// <summary>
	/// Checks that <c>kill -l</c> with arguments turns numbers into names, reading a number past 128 as an exit
	/// status, and names into numbers, as bash does.
	/// </summary>
	/// <param name="spec">The argument after <c>-l</c>.</param>
	/// <param name="expected">What is printed.</param>
	[Theory]
	[InlineData("15", "TERM\n")]
	[InlineData("143", "TERM\n")]
	[InlineData("TERM", "15\n")]
	[InlineData("sigint", "2\n")]
	public void Run_WithListAndSpec_PrintsTheOtherForm(string spec, string expected)
	{
		// Act
		var status = this.kill.Run(["-l", spec]);

		// Assert
		_ = (status, this.environment.Output).Should().Be((0, expected));
	}

	/// <summary>
	/// Checks that <c>kill -l</c> still prints the signals it knows when one argument isn't a signal, and gives 1.
	/// </summary>
	[Fact]
	public void Run_WithListAndUnknownSpec_PrintsTheRestAndGivesOne()
	{
		// Act
		var status = this.kill.Run(["-l", "99", "15"]);

		// Assert
		_ = (status, this.environment.Output).Should().Be((1, "TERM\n"));
		_ = this.environment.Error.Should().Contain("sharpie::kill::invalid_signal").And.Contain("`99`");
	}

	/// <summary>
	/// Checks the statuses bash gives for arguments <c>kill</c> can't act on, and what is printed for each.
	/// </summary>
	/// <param name="args">The arguments, split on spaces.</param>
	/// <param name="expected">The status.</param>
	/// <param name="message">The error printed.</param>
	[Theory]
	[InlineData("", 2, "Error: sharpie::kill::missing_target\n\n  × kill needs a job or a process id\n  help: kill [-s SIGNAL | -SIGNAL] %n|pid..., or kill -l to list the signals\n")]
	[InlineData("-s TERM", 2, "Error: sharpie::kill::missing_target\n\n  × kill needs a job or a process id\n  help: kill [-s SIGNAL | -SIGNAL] %n|pid..., or kill -l to list the signals\n")]
	[InlineData("-9 --", 2, "Error: sharpie::kill::missing_target\n\n  × kill needs a job or a process id\n  help: kill [-s SIGNAL | -SIGNAL] %n|pid..., or kill -l to list the signals\n")]
	[InlineData("-s", 1, "Error: sharpie::kill::missing_signal\n\n  × `-s` needs a signal after it\n  help: kill -l lists the signals\n")]
	[InlineData("-n", 1, "Error: sharpie::kill::missing_signal\n\n  × `-n` needs a signal after it\n  help: kill -l lists the signals\n")]
	[InlineData("-s FOO 1", 1, "Error: sharpie::kill::invalid_signal\n\n  × `FOO` isn't a signal\n  help: kill -l lists the signals\n")]
	[InlineData("-FOO 1", 1, "Error: sharpie::kill::invalid_signal\n\n  × `FOO` isn't a signal\n  help: kill -l lists the signals\n")]
	[InlineData("-sigterm 1", 1, "Error: sharpie::kill::invalid_signal\n\n  × `igterm` isn't a signal\n  help: kill -l lists the signals\n")]
	[InlineData("abc", 1, "Error: sharpie::kill::invalid_target\n\n  × `abc` isn't a job or a process id\n  help: use %n for job n, or a process id\n")]
	[InlineData("%", 1, "Error: sharpie::kill::invalid_target\n\n  × `%` isn't a job or a process id\n  help: use %n for job n, or a process id\n")]
	[InlineData("%5", 1, "Error: sharpie::kill::no_such_job\n\n  × No job `%5`\n  help: jobs lists the running and stopped jobs\n")]
	[InlineData("2147483647", 1, "Error: sharpie::kill::failed\n\n  × Can't signal 2147483647: No such process\n")]
	public void Run_WithBadArguments_GivesBashStatus(string args, int expected, string message)
	{
		// Arrange
		// Windows words a missing process its own way.
		if (OperatingSystem.IsWindows() && args == "2147483647")
		{
			return;
		}

		// Act
		var status = this.kill.Run(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

		// Assert
		_ = (status, this.environment.Error).Should().Be((expected, message));
	}

	/// <summary>
	/// Checks that every target gets the signal and the status is 0 when they all did.
	/// </summary>
	[Fact]
	public void Run_WithTwoGoodTargets_SignalsBothAndGivesZero()
	{
		// Arrange
		var first = this.StartSleep();
		var second = this.StartSleep();

		// Act
		var status = this.kill.Run([first.Id.ToString(CultureInfo.InvariantCulture), second.Id.ToString(CultureInfo.InvariantCulture)]);

		// Assert
		_ = status.Should().Be(0);
		_ = (first.WaitForExit(TimeSpan.FromSeconds(10)), second.WaitForExit(TimeSpan.FromSeconds(10))).Should().Be((true, true));
	}

	/// <summary>
	/// Checks that each way of naming a signal sends that signal to a process id, which then ends with 128 plus its
	/// number.
	/// </summary>
	/// <param name="option">The signal option, split on spaces, or empty for the default TERM.</param>
	/// <param name="expected">The status the process ends with.</param>
	[Theory]
	[InlineData("", 143)]
	[InlineData("-9", 137)]
	[InlineData("-KILL", 137)]
	[InlineData("-SIGKILL", 137)]
	[InlineData("-s kill", 137)]
	[InlineData("-sKILL", 137)]
	[InlineData("-n 9", 137)]
	[InlineData("-n9", 137)]
	[InlineData("-s INT --", 130)]
	public void Run_WithSignalOption_SendsItToProcess(string option, int expected)
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var process = this.StartSleep();

		// Act
		var status = this.kill.Run([.. option.Split(' ', StringSplitOptions.RemoveEmptyEntries), process.Id.ToString(CultureInfo.InvariantCulture)]);

		// Assert
		_ = status.Should().Be(0);
		_ = process.WaitForExit(TimeSpan.FromSeconds(10)).Should().BeTrue();
		_ = process.ExitCode.Should().Be(expected);
	}

	/// <summary>
	/// Checks that one target that got the signal is enough for status 0, as in bash, while the others are still
	/// reported.
	/// </summary>
	[Fact]
	public void Run_WithOneGoodAndOneMissingTarget_GivesZero()
	{
		// Arrange
		var process = this.StartSleep();

		// Act
		var status = this.kill.Run(["2147483647", process.Id.ToString(CultureInfo.InvariantCulture)]);

		// Assert
		_ = status.Should().Be(0);
		_ = process.WaitForExit(TimeSpan.FromSeconds(10)).Should().BeTrue();
		_ = this.environment.Error.Should().Contain("sharpie::kill::failed");
	}

	/// <summary>
	/// Checks that <c>kill %n</c> ends a background job without job control and <c>wait %n</c> then gives 143, as
	/// <c>sleep 30 &amp; kill %1; wait %1</c> does in bash, and takes the job out of the table.
	/// </summary>
	[Fact]
	public void Run_WithJob_EndsItAndWaitGivesTerminated()
	{
		// Arrange
		var job = BackgroundJobs.Start("sleep", "30");

		// Act
		var killed = this.kill.Run([$"%{job.Number}"]);
		var status = this.wait.Run([$"%{job.Number}"]);

		// Assert
		_ = (killed, status).Should().Be((0, 143));
		_ = ExternalProgram.Jobs.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a stopped job sent TERM is woken with SIGCONT so it can end, instead of waiting forever with the
	/// signal pending.
	/// </summary>
	[Fact]
	public void Run_WithStoppedJob_WakesItSoItEnds()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var job = BackgroundJobs.Start("sleep", "30");
		_ = this.kill.Run(["-STOP", $"%{job.Number}"]);
		var stopped = this.wait.Run([$"%{job.Number}"]);

		// Act
		var killed = this.kill.Run([$"%{job.Number}"]);
		var status = this.wait.Run([$"%{job.Number}"]);

		// Assert
		_ = (stopped, killed, status).Should().Be((128 + NativeMethods.SignalStop, 0, 143));
	}

	/// <summary>
	/// Ends any program a test left running and restores the console.
	/// </summary>
	public void Dispose()
	{
		BackgroundJobs.EndAll();

		foreach (var process in this.started)
		{
			if (!process.HasExited)
			{
				process.Kill();
			}

			process.Dispose();
		}

		this.environment.Dispose();
	}

	/// <summary>
	/// Starts <c>sleep 30</c> outside the job table.
	/// </summary>
	/// <returns>
	/// The running process, ended by <see cref="Dispose" /> if a test doesn't end it.
	/// </returns>
	private Process StartSleep()
	{
		var process = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
		this.started.Add(process);

		return process;
	}
}
