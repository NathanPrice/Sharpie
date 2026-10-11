using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Jobs;

namespace Sharpie.Shell.Tests.Features.Jobs;

/// <summary>
/// Tests for <see cref="Job" />.
/// </summary>
public sealed class JobTests
{
	private readonly Job sut = new(1, 100, "sleep 1");

	/// <summary>
	/// Checks that the last program ending finishes the job with its exit code, or 128 plus the signal that ended it.
	/// </summary>
	/// <param name="status">The status <c>waitpid</c> returned.</param>
	/// <param name="expected">The exit code the job should report.</param>
	[Theory]
	[InlineData(0x0000, 0)]
	[InlineData(0x0300, 3)]
	[InlineData(0x0009, 137)]
	public void Record_LastProgramEnds_IsDoneWithExitCode(int status, int expected)
	{
		// Arrange
		this.sut.Add(101);

		// Act
		this.sut.Record(101, status);

		// Assert
		_ = (this.sut.State, this.sut.ExitCode, this.sut.Members.Count).Should().Be((JobState.Done, expected, 0));
	}

	/// <summary>
	/// Checks that a stop status marks the job stopped and keeps the program as a member.
	/// </summary>
	[Fact]
	public void Record_Stopped_IsStoppedAndKeepsMember()
	{
		// Arrange
		this.sut.Add(101);

		// Act
		this.sut.Record(101, 0x7F | (NativeMethods.SignalStop << 8));

		// Assert
		_ = (this.sut.State, this.sut.Members.Count).Should().Be((JobState.Stopped, 1));
	}

	/// <summary>
	/// Gets the two ways <c>waitpid</c> reports a continue: Linux's <c>0xFFFF</c>, and the stop-style status carrying
	/// SIGCONT.
	/// </summary>
	/// <value>
	/// The statuses, built at run time because SIGCONT's number differs between Linux and macOS.
	/// </value>
	public static TheoryData<int> ContinueStatuses => [0xFFFF, 0x7F | (NativeMethods.SignalContinue << 8)];

	/// <summary>
	/// Checks that both ways a continue is reported put a stopped job back to running.
	/// </summary>
	/// <param name="status">The status <c>waitpid</c> returned.</param>
	[Theory]
	[MemberData(nameof(ContinueStatuses))]
	public void Record_Continued_IsRunning(int status)
	{
		// Arrange
		this.sut.Add(101);
		this.sut.State = JobState.Stopped;

		// Act
		this.sut.Record(101, status);

		// Assert
		_ = (this.sut.State, this.sut.Members.Count).Should().Be((JobState.Running, 1));
	}

	/// <summary>
	/// Checks that an earlier stage of a pipeline ending leaves the job running and its exit code alone.
	/// </summary>
	[Fact]
	public void Record_FirstOfTwoEnds_StaysRunningWithoutExitCode()
	{
		// Arrange
		this.sut.Add(101);
		this.sut.Add(102);

		// Act
		this.sut.Record(101, 0x0500);

		// Assert
		_ = (this.sut.State, this.sut.ExitCode).Should().Be((JobState.Running, 0));
		_ = this.sut.Members.Should().Equal(102);
	}

	/// <summary>
	/// Checks that the exit code comes from the last stage of a pipeline even when it ends before an earlier one.
	/// </summary>
	[Fact]
	public void Record_LastStageEndsFirst_KeepsItsExitCode()
	{
		// Arrange
		this.sut.Add(101);
		this.sut.Add(102);

		// Act
		this.sut.Record(102, 0x0200);
		this.sut.Record(101, 0x0500);

		// Assert
		_ = (this.sut.State, this.sut.ExitCode).Should().Be((JobState.Done, 2));
	}
}
