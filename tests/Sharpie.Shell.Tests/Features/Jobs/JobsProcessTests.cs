using FluentAssertions;
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
		_ = run.Output.Should().MatchRegex(@"^\$ \[1\] \d+\n\$ \[1\]\+  Running                 sleep 1 &\n\$ $");
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
		// The foreground sleep gives false time to exit before the shell checks for finished jobs.
		var run = await ShellProcess.RunAsync("false &\nsleep 1\nexit\n").ConfigureAwait(true);

		// Assert
		// The Exit line lands before the sleep prompt or the exit prompt, depending on whether false had already
		// exited when the shell first checked.
		_ = run.Output.Should().MatchRegex(@"^\$ \[1\] \d+\n(\[1\]\+  Exit 1 +false\n\$ \$ |\$ \[1\]\+  Exit 1 +false\n\$ )$");
		_ = run.Exited.Should().BeTrue();
	}
}
