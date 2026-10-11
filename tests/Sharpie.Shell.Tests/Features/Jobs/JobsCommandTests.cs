using System.Diagnostics;
using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Jobs;
using Sharpie.Shell.Tests.Common;

namespace Sharpie.Shell.Tests.Features.Jobs;

/// <summary>
/// Tests for <see cref="JobsCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class JobsCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly JobsCommand sut = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="JobsCommandTests" /> class with an empty job table.
	/// </summary>
	/// <remarks>
	/// <see cref="ExternalProgramTests" /> starts background jobs and leaves them in the shared table.
	/// </remarks>
	public JobsCommandTests() => ExternalProgram.Jobs.Clear();

	/// <summary>
	/// Checks that <see cref="JobsCommand.Run" /> prints nothing when there are no background jobs.
	/// </summary>
	[Fact]
	public void Run_WithNoJobs_PrintsNothing()
	{
		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a single job is listed as the current job, with the status padded to 24 characters.
	/// </summary>
	[Fact]
	public void Run_WithOneJob_PrintsItAsCurrentAndRunning()
	{
		// Arrange
		ExternalProgram.Jobs.Add(new Job(1, 123, "sleep 10"));

		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().Be("[1]+  Running                 sleep 10 &\n");
	}

	/// <summary>
	/// Checks that the newest job gets <c>+</c>, the one before it <c>-</c>, and older jobs a space.
	/// </summary>
	[Fact]
	public void Run_WithThreeJobs_MarksNewestPlusAndPreviousMinus()
	{
		// Arrange
		ExternalProgram.Jobs.Add(new Job(1, 101, "sleep 1"));
		ExternalProgram.Jobs.Add(new Job(2, 102, "sleep 2"));
		ExternalProgram.Jobs.Add(new Job(3, 103, "sleep 3"));

		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().Be(
			"[1]   Running                 sleep 1 &\n" +
			"[2]-  Running                 sleep 2 &\n" +
			"[3]+  Running                 sleep 3 &\n");
	}

	/// <summary>
	/// Checks that a job whose program has exited is listed as <c>Done</c> without the trailing <c>&amp;</c>.
	/// </summary>
	[Fact]
	public void Run_WithExitedJob_PrintsDoneWithoutAmpersand()
	{
		// Arrange
		ExternalProgram.Jobs.Add(StartExitedJob());

		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().Be("[1]+  Done                    true\n");
	}

	/// <summary>
	/// Checks that a job whose program failed is listed with its exit code instead of <c>Done</c>, as in bash.
	/// </summary>
	[Fact]
	public void Run_WithFailedJob_PrintsExitCode()
	{
		// Arrange
		var process = Process.Start("false");
		process.WaitForExit();
		ExternalProgram.Jobs.Add(new Job(1, process.Id, "false", process));

		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().Be("[1]+  Exit 1                  false\n");
	}

	/// <summary>
	/// Checks that a job reported as <c>Done</c> is removed, so the next call adds nothing to the output.
	/// </summary>
	[Fact]
	public void Run_AfterJobReportedDone_RemovesIt()
	{
		// Arrange
		ExternalProgram.Jobs.Add(StartExitedJob());
		_ = this.sut.Run([]);

		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().Be("[1]+  Done                    true\n");
		_ = ExternalProgram.Jobs.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that reaping without listing running jobs prints only the finished job and leaves the running one in
	/// the table.
	/// </summary>
	[Fact]
	public void Reap_WithoutListRunning_PrintsOnlyDoneJobs()
	{
		// Arrange
		ExternalProgram.Jobs.Add(new Job(1, 101, "sleep 10"));
		ExternalProgram.Jobs.Add(StartExitedJob(2));

		// Act
		JobsCommand.Reap(listRunning: false);

		// Assert
		_ = this.environment.Output.Should().Be("[2]+  Done                    true\n");
		_ = ExternalProgram.Jobs.Should().ContainSingle().Which.Number.Should().Be(1);
	}

	/// <summary>
	/// Checks that a finished job older than the newest keeps its <c>-</c> marker, since markers are worked out
	/// before removal.
	/// </summary>
	[Fact]
	public void Reap_WithExitedJobBeforeNewest_MarksItMinus()
	{
		// Arrange
		ExternalProgram.Jobs.Add(StartExitedJob(1));
		ExternalProgram.Jobs.Add(new Job(2, 102, "sleep 10"));

		// Act
		JobsCommand.Reap(listRunning: false);

		// Assert
		_ = this.environment.Output.Should().Be("[1]-  Done                    true\n");
		_ = ExternalProgram.Jobs.Should().ContainSingle().Which.Number.Should().Be(2);
	}

	/// <summary>
	/// Checks that a job already reaped before the prompt isn't listed again by <see cref="JobsCommand.Run" />, and
	/// that the remaining job becomes the current one.
	/// </summary>
	[Fact]
	public void Run_AfterReap_DoesNotListReapedJob()
	{
		// Arrange
		ExternalProgram.Jobs.Add(new Job(1, 101, "sleep 10"));
		ExternalProgram.Jobs.Add(StartExitedJob(2));
		JobsCommand.Reap(listRunning: false);

		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().Be(
			"[2]+  Done                    true\n" +
			"[1]+  Running                 sleep 10 &\n");
	}

	/// <summary>
	/// Runs <c>true</c> to completion and wraps it in a job.
	/// </summary>
	/// <param name="number">The job number to give it.</param>
	/// <returns>
	/// A job whose <see cref="Job.HasExited" /> is already <see langword="true" />.
	/// </returns>
	private static Job StartExitedJob(int number = 1)
	{
		var process = Process.Start("true");
		process.WaitForExit();
		return new Job(number, process.Id, "true", process);
	}

	/// <summary>
	/// Checks that a stopped job is listed as Stopped, without the <c>&amp;</c> a running one gets.
	/// </summary>
	[Fact]
	public void Run_WithStoppedJob_PrintsStopped()
	{
		// Arrange
		ExternalProgram.Jobs.Add(new Job(1, 123, "vim notes") { State = JobState.Stopped });

		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().Be("[1]+  Stopped                 vim notes\n");
	}

	/// <summary>
	/// Checks that <c>fg</c> and <c>bg</c> say so when the shell has no job control, instead of looking for a job.
	/// </summary>
	[Fact]
	public void Find_WithoutJobControl_ReportsIt()
	{
		// Arrange
		ExternalProgram.Jobs.Add(new Job(1, 123, "sleep 10"));

		// Act
		var job = JobsCommand.Find("fg", []);

		// Assert
		_ = this.environment.Output.Should().Be("fg: no job control\n");
		_ = job.Should().BeNull();
	}

	/// <summary>
	/// Checks which job <c>fg</c> and <c>bg</c> pick: the newest with no argument, else the one numbered, with or
	/// without <c>%</c>, and a message for anything else.
	/// </summary>
	/// <param name="args">The arguments, separated by spaces.</param>
	/// <param name="number">The job number expected, or 0 for none.</param>
	/// <param name="message">What should be printed.</param>
	[Theory]
	[InlineData("", 2, "")]
	[InlineData("%1", 1, "")]
	[InlineData("2", 2, "")]
	[InlineData("3", 0, "fg: 3: no such job\n")]
	[InlineData("x", 0, "fg: x: no such job\n")]
	public void Find_WithJobControl_PicksJob(string args, int number, string message)
	{
		// Arrange
		JobControl.Enabled = true;
		ExternalProgram.Jobs.Add(new Job(1, 101, "sleep 1"));
		ExternalProgram.Jobs.Add(new Job(2, 102, "sleep 2"));

		// Act
		var job = JobsCommand.Find("fg", args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

		// Assert
		_ = this.environment.Output.Should().Be(message);
		_ = (job?.Number ?? 0).Should().Be(number);
	}

	/// <summary>
	/// Checks that <c>fg</c> with no argument and no jobs says there's no current job.
	/// </summary>
	[Fact]
	public void Find_WithJobControlAndNoJobs_ReportsNoCurrentJob()
	{
		// Arrange
		JobControl.Enabled = true;

		// Act
		var job = JobsCommand.Find("fg", []);

		// Assert
		_ = this.environment.Output.Should().Be("fg: current: no such job\n");
		_ = job.Should().BeNull();
	}

	/// <summary>
	/// Turns job control back off and restores the console after each test.
	/// </summary>
	public void Dispose()
	{
		JobControl.Enabled = false;
		this.environment.Dispose();
	}
}
