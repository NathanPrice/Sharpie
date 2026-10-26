using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Runs <c>trap</c> through the built shell as a real process, with real signals.
/// </summary>
/// <remarks>
/// A program left running when the shell ends sends its output to <c>/dev/null</c>, so reading the shell's output
/// doesn't wait for it.
/// </remarks>
[Collection(ShellProcess.Collection)]
public sealed class TrapProcessTests
{
	// SIGHUP, SIGINT and SIGTERM have the same numbers on Linux and macOS.
	private const int Hangup = 1;
	private const int Interrupt = 2;
	private const int Terminate = 15;

	// Long enough for the shell to start and reach the command the signal is meant to land in.
	private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(1.5);

	/// <summary>
	/// Gets scripts whose <c>EXIT</c> trap runs, each with what it prints and the status it ends with, as in bash.
	/// </summary>
	/// <value>
	/// The end of the script, <c>exit</c> with <c>$?</c> in the trap, <c>exit</c> inside the trap, which wins,
	/// <c>set -e</c>, an unset variable under <c>set -u</c>, and a function as the action.
	/// </value>
	public static TheoryData<string, string, int> ExitTraps => new()
	{
		{ "trap 'echo bye $?' EXIT; echo hi", "hi\nbye 0\n", 0 },
		{ "trap 'echo bye $?' EXIT; exit 3; echo no", "bye 3\n", 3 },
		{ "trap 'echo in; exit 5' EXIT; exit 3", "in\n", 5 },
		{ "set -e; trap 'echo bye $?' EXIT; false; echo no", "bye 1\n", 1 },
		{ "set -u; trap 'echo bye $?' EXIT; echo $SHARPIE_SAMPLE_UNSET; echo no", "bye 127\n", 127 },
		{ "f() { echo fn; }; trap f EXIT", "fn\n", 0 },
	};

	/// <summary>
	/// Gets scripts whose subshell sets traps of its own, each with what it prints.
	/// </summary>
	/// <value>
	/// A subshell's and a substitution's <c>EXIT</c> trap run at their end, the second captured, and a substitution
	/// still lists the shell's traps, as bash's does. A builtin in a pipeline is a subshell too, and its trap writes
	/// into the pipe.
	/// </value>
	public static TheoryData<string, string> SubshellTraps => new()
	{
		{ "trap 'echo out' EXIT; ( trap 'echo in' EXIT; echo sub ); echo after", "sub\nin\nafter\nout\n" },
		{ "x=$(trap 'echo in' EXIT; echo sub); echo \"[$x]\"", "[sub\nin]\n" },
		{ "trap 'echo P' EXIT; echo \"$(trap)\"", "trap -- 'echo P' EXIT\nP\n" },
		{ "( trap 'echo in; exit 4' EXIT ); echo $?", "in\n4\n" },
		{ "trap 'echo in' EXIT | tr a-z A-Z; echo after", "IN\nafter\n" },
	};

	/// <summary>
	/// Checks that the <c>EXIT</c> trap runs when the shell ends, with <c>$?</c> holding the status it ends with.
	/// </summary>
	/// <param name="script">The <c>-c</c> script.</param>
	/// <param name="expected">What it prints.</param>
	/// <param name="code">The status it ends with.</param>
	[Theory]
	[MemberData(nameof(ExitTraps))]
	public async Task Shell_WithExitTrap_RunsItAtTheEnd(string script, string expected, int code)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
		_ = run.ExitCode.Should().Be(code);
	}

	/// <summary>
	/// Checks that a subshell's own <c>EXIT</c> trap runs at its end and the shell's traps come back after.
	/// </summary>
	/// <param name="script">The <c>-c</c> script.</param>
	/// <param name="expected">What it prints.</param>
	[Theory]
	[MemberData(nameof(SubshellTraps))]
	public async Task Shell_WithSubshellTrap_RunsItAtSubshellEnd(string script, string expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that a trapped signal the shell sends itself runs its action right after <c>kill</c>, and the shell goes
	/// on.
	/// </summary>
	[Fact]
	public async Task Shell_WithTrappedSignalToItself_RunsActionAndGoesOn()
	{
		// Arrange
		// Windows has no signals to send.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "trap 'echo T $?' TERM; false; kill -TERM $$; echo after"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("T 0\nafter\n");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>trap '' INT</c> makes the programs the shell starts ignore SIGINT too, and that <c>trap - INT</c>
	/// or the end of the subshell that set it undoes it, for the programs and the shell itself, as in bash.
	/// </summary>
	/// <param name="script">The <c>-c</c> script.</param>
	/// <param name="expected">What it prints.</param>
	[Theory]
	[InlineData("trap '' INT; sh -c 'kill -INT $$; echo survived'; trap - INT; sh -c 'kill -INT $$; echo again'; echo end", "survived\nend\n")]
	[InlineData("( trap '' INT ); sh -c 'kill -INT $$; echo survived'; kill -INT $$; echo end", "")]
	public async Task Shell_WithIgnoredInterrupt_ProgramsIgnoreItToo(string script, string expected)
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
	}

	/// <summary>
	/// Checks that TERM from outside, while a program runs, ends the shell at once after the <c>EXIT</c> trap, without
	/// waiting for the program, as bash does.
	/// </summary>
	[Fact]
	public async Task Shell_WhenTerminatedDuringProgram_RunsExitTrapAndEnds()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var (output, code) = await SignalAsync("trap 'echo cleanup $?' EXIT; sleep 30 > /dev/null; echo after", Terminate).ConfigureAwait(true);

		// Assert
		_ = output.Should().Be("cleanup 0\n");
		_ = code.Should().Be(128 + Terminate);
	}

	/// <summary>
	/// Checks that HUP from outside, as when the terminal closes, ends the shell after the <c>EXIT</c> trap.
	/// </summary>
	[Fact]
	public async Task Shell_WhenHungUp_RunsExitTrapAndEnds()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var (output, code) = await SignalAsync("trap 'echo cleanup' EXIT; sleep 30 > /dev/null; echo after", Hangup).ConfigureAwait(true);

		// Assert
		_ = output.Should().Be("cleanup\n");
		_ = code.Should().Be(128 + Hangup);
	}

	/// <summary>
	/// Checks that SIGINT ends a script, whether it lands in a program, which dies of it too, or between commands,
	/// after the <c>EXIT</c> trap and with no further commands, as Ctrl+C ends a bash script.
	/// </summary>
	/// <param name="loop">A loop that runs until the signal comes.</param>
	/// <param name="expected">What the <c>EXIT</c> trap prints.</param>
	[Theory]
	[InlineData("for i in 1 2 3 4 5; do sleep 1; done", "cleanup 130\n")]
	[InlineData("while :; do :; done", "cleanup 0\n")]
	public async Task Shell_WhenScriptInterrupted_RunsExitTrapAndEnds(string loop, string expected)
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var (output, code) = await SignalAsync($"trap 'echo cleanup $?' EXIT; {loop}; echo after", Interrupt).ConfigureAwait(true);

		// Assert
		_ = output.Should().Be(expected);
		_ = code.Should().Be(128 + Interrupt);
	}

	/// <summary>
	/// Checks that a script goes on after SIGINT when the program it landed in catches it and exits normally, as
	/// bash's script goes on after ping prints its summary.
	/// </summary>
	[Fact]
	public async Task Shell_WhenInterruptedProgramExitsNormally_GoesOn()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var (output, code) = await SignalAsync("sh -c 'trap \"exit 0\" INT; sleep 5 > /dev/null & wait'; echo goes on $?", Interrupt).ConfigureAwait(true);

		// Assert
		_ = output.Should().Be("goes on 0\n");
		_ = code.Should().Be(0);
	}

	/// <summary>
	/// Checks that a trapped signal ends <c>wait</c> with 128 plus its number and runs its action, as in bash.
	/// </summary>
	[Fact]
	public async Task Shell_WhenTrappedSignalDuringWait_EndsWaitAndRunsAction()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var (output, code) = await SignalAsync("trap 'echo T' TERM; sleep 30 > /dev/null & wait; echo w=$?; kill %1", Terminate).ConfigureAwait(true);

		// Assert
		_ = output.Should().EndWith("T\nw=143\n");
		_ = code.Should().Be(0);
	}

	/// <summary>
	/// Checks that a signal arriving inside a substitution waits for the shell itself: a trap's output doesn't land in
	/// the substitution, and a fatal one ends the shell after it, by the signal, even when the <c>EXIT</c> trap runs
	/// <c>exit</c>, as in bash.
	/// </summary>
	/// <param name="script">The <c>-c</c> script.</param>
	/// <param name="expected">What it prints.</param>
	/// <param name="code">The status it ends with.</param>
	[Theory]
	[InlineData("trap 'echo got' USR1; x=$(kill -USR1 $$; echo hi); echo \"x=[$x]\"", "got\nx=[hi]\n", 0)]
	[InlineData("trap 'echo bye; exit 3' EXIT; x=$(kill $$; echo a); echo alive", "bye\n", 128 + Terminate)]
	public async Task Shell_WithSignalInSubstitution_HandlesItInTheShell(string script, string expected, int code)
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
		_ = run.ExitCode.Should().Be(code);
	}

	/// <summary>
	/// Checks that a second TERM ends the shell at once while its <c>EXIT</c> trap is still running, as bash does, so a
	/// trap that hangs can't keep the shell alive.
	/// </summary>
	[Fact]
	public async Task Shell_WhenTerminatedTwice_EndsDuringExitTrap()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Arrange
		using var shell = ShellProcess.Start(arguments: ["-c", "trap 'echo cleanup; sleep 30 > /dev/null' EXIT; sleep 30 > /dev/null"]);

		await Task.Delay(StartDelay).ConfigureAwait(true);
		_ = NativeMethods.Kill(shell.Id, Terminate);
		await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);

		// Act
		_ = NativeMethods.Kill(shell.Id, Terminate);

		var output = shell.StandardOutput.ReadToEndAsync();
		var exited = await ShellProcess.ExitsWithinAsync(shell, TimeSpan.FromSeconds(5)).ConfigureAwait(true);

		if (!exited)
		{
			shell.Kill(entireProcessTree: true);
		}

		// Assert
		_ = exited.Should().BeTrue();
		_ = shell.ExitCode.Should().Be(128 + Terminate);
		_ = (await output.ConfigureAwait(true)).Should().Be("cleanup\n");
	}

	/// <summary>
	/// Starts the shell on <paramref name="script" />, sends it <paramref name="signal" /> once it's running, and
	/// collects what it prints.
	/// </summary>
	/// <param name="script">The <c>-c</c> script.</param>
	/// <param name="signal">The signal number.</param>
	/// <returns>
	/// The shell's standard output, and its exit code, or <see langword="null" /> when it didn't end within a few
	/// seconds of the signal.
	/// </returns>
	private static async Task<(string Output, int? Code)> SignalAsync(string script, int signal)
	{
		using var shell = ShellProcess.Start(arguments: ["-c", script]);

		await Task.Delay(StartDelay).ConfigureAwait(true);
		_ = NativeMethods.Kill(shell.Id, signal);

		var output = shell.StandardOutput.ReadToEndAsync();
		var exited = await ShellProcess.ExitsWithinAsync(shell, TimeSpan.FromSeconds(5)).ConfigureAwait(true);

		if (!exited)
		{
			shell.Kill(entireProcessTree: true);
		}

		return (await output.ConfigureAwait(true), exited ? shell.ExitCode : null);
	}
}
