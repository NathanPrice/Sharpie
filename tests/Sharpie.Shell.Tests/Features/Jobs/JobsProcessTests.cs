using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Jobs;

namespace Sharpie.Shell.Tests.Features.Jobs;

/// <summary>
/// Runs background programs and <see cref="JobsCommand" /> through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class JobsProcessTests
{
	/// <summary>
	/// Checks that a program started with a trailing <c>&amp;</c> writes to the shell's own standard output instead
	/// of a pipe the shell never reads.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundJob_JobOutputReachesShellOutput()
	{
		// Arrange
		var file = Path.GetTempFileName();
		await File.WriteAllTextAsync(file, "Hello from background\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"cat {file} &\nexit\n").ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Contain("Hello from background\n");
		}
		finally
		{
			File.Delete(file);
		}
	}

	/// <summary>
	/// Checks that <c>jobs</c> lists a program started with <c>&amp;</c> as job 1, running, with its command line.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundSleep_JobsListsIt()
	{
		// Act
		// Short enough that reading the output, which also waits for sleep to let go of the pipe, doesn't hold up the
		// run.
		var run = await ShellProcess.RunAsync("sleep 1 &\njobs\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().MatchRegex(@"^\$ \$ \[1\]\+  Running                 sleep 1 &\n\$ $");
	}

	/// <summary>
	/// Checks that <c>&amp;</c> ends a command like <c>;</c>, so the command after it runs at once on the same line.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundThenCommand_RunsBoth()
	{
		// Act
		var run = await ShellProcess.RunAsync("sleep 1 & echo next\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().MatchRegex(@"^\$ next\n\$ $");
	}

	/// <summary>
	/// Checks that a builtin run with <c>&amp;</c> runs in a copy of the shell, so <c>cd</c> doesn't move the shell
	/// itself.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundCd_KeepsFolder()
	{
		// Arrange
		var folder = Path.GetTempPath().TrimEnd('/');

		// Act
		var run = await ShellProcess.RunAsync("cd / &\nsleep 1\npwd\nexit\n", folder).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().EndWith($"{folder}\n$ ");
	}

	/// <summary>
	/// Checks that <c>NAME=VALUE</c> run with <c>&amp;</c> sets the variable in a copy of the shell, not this one.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundAssignment_LeavesVariableUnset()
	{
		// Act
		var run = await ShellProcess.RunAsync("SAMPLEVAR=1 &\nsleep 1\necho x$SAMPLEVAR\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Contain("x\n").And.NotContain("x1");
	}

	/// <summary>
	/// Checks that <c>&amp;</c> after an and-or list runs the whole list in the background, so <c>echo late</c> waits
	/// for <c>sleep</c> while the shell goes on to <c>echo early</c>.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundAndList_RunsWholeListInBackground()
	{
		// Act
		var run = await ShellProcess.RunAsync("sleep 1 && echo late & echo early\nsleep 2\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().MatchRegex(@"(?s)early\n.*late\n");
	}

	/// <summary>
	/// Checks that <c>jobs</c> is registered with the running shell and prints nothing when no jobs are running.
	/// </summary>
	[Fact]
	public async Task Shell_WithTypeJobsAndJobs_ReportsBuiltinAndPrintsNothing()
	{
		// Act
		var run = await ShellProcess.RunAsync("type jobs\njobs\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ jobs is a shell builtin\n$ $ ");
		_ = run.Exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that a background program that fails gets an <c>Exit</c> line with its exit code before the next
	/// prompt, without anyone typing <c>jobs</c>.
	/// </summary>
	[Fact]
	public async Task Shell_WithFailedBackgroundJob_ReportsExitCodeBeforeNextPrompt()
	{
		// Act
		// false is a builtin, which runs in the foreground, so a cat of a missing file is the failing program. The
		// foreground sleep gives it time to exit before the shell checks for finished jobs.
		var run = await ShellProcess.RunAsync("cat sample-missing-file &\nsleep 1\nexit\n").ConfigureAwait(true);

		// Assert
		// The Exit line lands before the sleep prompt or the exit prompt, depending on whether cat had already
		// exited when the shell first checked.
		_ = run.Output.Should().MatchRegex(@"^\$ (\[1\]\+  Exit 1 +cat sample-missing-file\n\$ \$ |\$ \[1\]\+  Exit 1 +cat sample-missing-file\n\$ )$");
		_ = run.Exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>kill %1</c> ends a background job in a shell without job control, and that <c>wait %1</c> then
	/// gives 143 for SIGTERM, as in bash. With <c>-s INT</c> it gives 130. Both are on one line, since the shell reports
	/// and forgets a finished job between lines, which a busy machine could let happen before <c>wait</c> runs.
	/// </summary>
	/// <param name="option">The signal option, or empty for the default TERM.</param>
	/// <param name="expected">The status <c>wait</c> gives.</param>
	[Theory]
	[InlineData("", 143)]
	[InlineData("-s INT ", 130)]
	public async Task Shell_WithKillAndWait_GivesSignalStatus(string option, int expected)
	{
		// Act
		var run = await ShellProcess.RunAsync($"sleep 30 &\nkill {option}%1; wait %1; echo $?\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().MatchRegex($@"^\$ \$ {expected}\n\$ $");
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>wait</c> with no arguments returns once the job ends, and that the job leaves the table without a
	/// <c>Done</c> line.
	/// </summary>
	[Fact]
	public async Task Shell_WithWaitForAllJobs_ReturnsAfterJobAndForgetsIt()
	{
		// Act
		var run = await ShellProcess.RunAsync("sleep 0.2 &\nwait\necho done $?\njobs\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().MatchRegex(@"^\$ \$ \$ done 0\n\$ \$ $");
	}

	/// <summary>
	/// Checks that SIGINT, what Ctrl+C sends, ends <c>wait</c> with 130 and leaves the job running for <c>kill</c>.
	/// </summary>
	[Fact]
	public async Task Shell_WhenInterruptedDuringWait_GivesInterruptedAndKeepsJob()
	{
		// Arrange
		// Windows has no SIGINT to send to the shell.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		using var shell = ShellProcess.Start();

		await shell.StandardInput.WriteAsync("sleep 30 &\nwait\necho $?\nkill %1\necho $?\nexit\n").ConfigureAwait(true);

		// Act
		// Gives the shell time to start waiting before the signal arrives.
		await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
		ExternalProgram.SendInterrupt(shell.Id);

		var output = shell.StandardOutput.ReadToEndAsync();
		var exited = await ShellProcess.ExitsWithinAsync(shell, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

		// Assert
		_ = exited.Should().BeTrue();
		// The job's Exit 143 line lands before the prompt after kill or the one after echo, depending on when the shell
		// sees it end.
		_ = (await output.ConfigureAwait(true)).Should().MatchRegex(@"^\$ \$ \$ 130\n\$ (\[1\]\+  Exit 143 +sleep 30\n)?\$ 0\n");
	}

	/// <summary>
	/// Checks that <c>wait</c> for a process that isn't a job gives 127, as in bash.
	/// </summary>
	[Fact]
	public async Task Shell_WithWaitForUnknownPid_GivesNotFound()
	{
		// Act
		var run = await ShellProcess.RunAsync("wait 2147483647\necho $?\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ 127\n$ ");
		_ = run.Error.Should().Contain("sharpie::wait::not_a_child");
	}

	/// <summary>
	/// Checks that <c>kill -l</c> is a builtin in the running shell and starts the list the way bash does.
	/// </summary>
	[Fact]
	public async Task Shell_WithKillList_PrintsSignals()
	{
		// Act
		var run = await ShellProcess.RunAsync("kill -l\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().StartWith("$  1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 4) SIGILL\t 5) SIGTRAP\n");
	}

	/// <summary>
	/// Checks that after <c>disown %1</c>, <c>jobs</c> no longer lists the job.
	/// </summary>
	[Fact]
	public async Task Shell_WithDisown_JobsPrintsNothing()
	{
		// Act
		// Short, since the disowned sleep keeps the output open until it ends.
		var run = await ShellProcess.RunAsync("sleep 1 &\ndisown %1\njobs\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().MatchRegex(@"^\$ \$ \$ \$ $");
	}
}
