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
		this.sut.Run([]);

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
		this.sut.Run([]);

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
		this.sut.Run([]);

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
		this.sut.Run([]);

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
		this.sut.Run([]);

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
		this.sut.Run([]);

		// Act
		this.sut.Run([]);

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
		this.sut.Run([]);

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
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
