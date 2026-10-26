using System.Globalization;
using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Jobs;

namespace Sharpie.Shell.Tests.Features.Jobs;

/// <summary>
/// Tests for <see cref="WaitCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class WaitCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly WaitCommand sut = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="WaitCommandTests" /> class with an empty job table.
	/// </summary>
	public WaitCommandTests() => ExternalProgram.Jobs.Clear();

	/// <summary>
	/// Checks that <c>wait</c> with no jobs returns 0 at once, with or without <c>--</c>.
	/// </summary>
	/// <param name="args">The arguments, split on spaces.</param>
	[Theory]
	[InlineData("")]
	[InlineData("--")]
	public void Run_WithNoJobs_GivesZero(string args)
	{
		// Act
		var status = this.sut.Run(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

		// Assert
		_ = (status, this.environment.Error).Should().Be((0, string.Empty));
	}

	/// <summary>
	/// Checks the statuses bash gives for a target <c>wait</c> can't wait for: 127 for an unknown job or process, 1
	/// for a word that is neither, and 2 for an option.
	/// </summary>
	/// <param name="target">The argument.</param>
	/// <param name="expected">The status.</param>
	/// <param name="message">The error printed.</param>
	[Theory]
	[InlineData("%5", 127, "Error: sharpie::wait::no_such_job\n\n  × No job `%5`\n  help: jobs lists the running and stopped jobs\n")]
	[InlineData("2147483647", 127, "Error: sharpie::wait::not_a_child\n\n  × Process 2147483647 isn't a job of this shell\n")]
	[InlineData("abc", 1, "Error: sharpie::wait::invalid_target\n\n  × `abc` isn't a job or a process id\n  help: use %n for job n, or a process id\n")]
	[InlineData("-x", 2, "Error: sharpie::wait::invalid_option\n\n  × Unknown option `-x`\n  help: wait takes %n job numbers or process ids\n")]
	public void Run_WithUnknownTarget_GivesBashStatus(string target, int expected, string message)
	{
		// Act
		var status = this.sut.Run([target]);

		// Assert
		_ = (status, this.environment.Error).Should().Be((expected, message));
	}

	/// <summary>
	/// Checks that waiting for a job by <c>%n</c> or by process id gives its exit code and takes it out of the table,
	/// without a <c>Done</c> line.
	/// </summary>
	/// <param name="byPid"><see langword="true" /> to name the job by its process id.</param>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Run_WithJob_GivesItsExitCodeAndForgetsIt(bool byPid)
	{
		// Arrange
		var job = BackgroundJobs.Start("sh", "-c", "exit 3");
		var target = byPid ? job.ProcessId.ToString(CultureInfo.InvariantCulture) : $"%{job.Number}";
		var before = this.environment.Output;

		// Act
		var status = this.sut.Run([target]);

		// Assert
		_ = status.Should().Be(3);
		_ = ExternalProgram.Jobs.Should().BeEmpty();
		_ = this.environment.Output.Should().Be(before);
	}

	/// <summary>
	/// Checks that waiting for several jobs gives the last one's status, and that an unknown one among them doesn't
	/// stop the others being waited for.
	/// </summary>
	[Fact]
	public void Run_WithSeveralTargets_GivesLastStatus()
	{
		// Arrange
		var first = BackgroundJobs.Start("sh", "-c", "exit 3");
		var second = BackgroundJobs.Start("sh", "-c", "exit 4");

		// Act
		var status = this.sut.Run([$"%{second.Number}", "%9", $"%{first.Number}"]);

		// Assert
		_ = status.Should().Be(3);
		_ = ExternalProgram.Jobs.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>wait</c> with no arguments waits for every job, gives 0 even when they failed, and empties the
	/// table.
	/// </summary>
	[Fact]
	public void Run_WithNoArgumentsAndJobs_WaitsForAllAndGivesZero()
	{
		// Arrange
		_ = BackgroundJobs.Start("sh", "-c", "sleep 0.2; exit 3");
		_ = BackgroundJobs.Start("sh", "-c", "exit 4");

		// Act
		var status = this.sut.Run([]);

		// Assert
		_ = status.Should().Be(0);
		_ = ExternalProgram.Jobs.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>wait</c> with no arguments doesn't wait forever for a stopped job, and leaves it in the table
	/// for <c>fg</c> or <c>bg</c>.
	/// </summary>
	[Fact]
	public void Run_WithNoArgumentsAndStoppedJob_ReturnsAndKeepsIt()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var job = BackgroundJobs.Start("sleep", "30");
		// SIGSTOP, since the system drops Ctrl+Z's SIGTSTP for a process in an orphaned process group, which the test
		// run's group can be.
		_ = NativeMethods.Kill(job.ProcessId, KillCommand.ParseSignal("STOP")!.Value);

		// Act
		var status = this.sut.Run([]);

		// Assert
		_ = status.Should().Be(0);
		_ = ExternalProgram.Jobs.Should().Equal(job);
		_ = job.State.Should().Be(JobState.Stopped);
	}

	/// <summary>
	/// Checks that Ctrl+C, which the shell catches, ends the wait with 130 and leaves the job running in the table.
	/// </summary>
	/// <param name="args">The arguments, split on spaces; <c>%1</c> names the job.</param>
	[Theory]
	[InlineData("")]
	[InlineData("%1")]
	public async Task Run_WhenInterrupted_GivesInterruptedAndKeepsJob(string args)
	{
		// Arrange
		var job = BackgroundJobs.Start("sleep", "30");
		var waiting = Task.Run(() => this.sut.Run(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

		// Act
		// Sent until the wait sees it, since the wait only notices a signal that arrives after it started.
		while (!waiting.IsCompleted)
		{
			TrapCommand.Arrived(2, endsShell: false);
			await Task.Delay(50).ConfigureAwait(true);
		}

		// Empties the queue, which in an interactive shell does nothing with Ctrl+C, so a later test can't end the test
		// run with it.
		TrapCommand.RunPending(interactive: true, 0);

		// Assert
		_ = (await waiting.ConfigureAwait(true)).Should().Be(130);
		_ = ExternalProgram.Jobs.Should().Equal(job);
	}

	/// <summary>
	/// Ends any job a test left running and restores the console.
	/// </summary>
	public void Dispose()
	{
		BackgroundJobs.EndAll();
		this.environment.Dispose();
	}
}
