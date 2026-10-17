using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Jobs;

namespace Sharpie.Shell.Tests.Features.Jobs;

/// <summary>
/// Tests for <see cref="DisownCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class DisownCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DisownCommand sut = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="DisownCommandTests" /> class with jobs 1 to 3 in the table. Job 3
	/// is a pipeline whose programs are 300 and 301.
	/// </summary>
	public DisownCommandTests()
	{
		ExternalProgram.Jobs.Clear();
		ExternalProgram.Jobs.Add(new Job(1, 100, "sampletool one"));
		ExternalProgram.Jobs.Add(new Job(2, 200, "sampletool two"));

		var pipeline = new Job(3, 300, "sampletool three | sampletool four");
		pipeline.Add(300);
		pipeline.Add(301);
		ExternalProgram.Jobs.Add(pipeline);
	}

	/// <summary>
	/// Checks which jobs each form of <c>disown</c> takes out of the table: the newest with no arguments, others by
	/// <c>%n</c> or by any of their process ids, and all of them with <c>-a</c>.
	/// </summary>
	/// <param name="args">The arguments, split on spaces.</param>
	/// <param name="left">The numbers of the jobs left.</param>
	[Theory]
	[InlineData("", new[] { 1, 2 })]
	[InlineData("%1", new[] { 2, 3 })]
	[InlineData("%1 %2", new[] { 3 })]
	[InlineData("200", new[] { 1, 3 })]
	[InlineData("301", new[] { 1, 2 })]
	[InlineData("-- %2", new[] { 1, 3 })]
	[InlineData("-a", new int[0])]
	public void Run_WithTargets_RemovesThoseJobs(string args, int[] left)
	{
		// Act
		var status = this.sut.Run(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

		// Assert
		_ = (status, this.environment.Error).Should().Be((0, string.Empty));
		_ = ExternalProgram.Jobs.Select(job => job.Number).Should().Equal(left);
	}

	/// <summary>
	/// Checks that <c>-h</c> is taken alone or with <c>-a</c> and leaves every job in the table, as bash does.
	/// </summary>
	/// <param name="args">The arguments, split on spaces.</param>
	[Theory]
	[InlineData("-h")]
	[InlineData("-h %1")]
	[InlineData("-ah")]
	[InlineData("-a -h")]
	public void Run_WithKeepOption_LeavesJobs(string args)
	{
		// Act
		var status = this.sut.Run(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

		// Assert
		_ = status.Should().Be(0);
		_ = ExternalProgram.Jobs.Select(job => job.Number).Should().Equal(1, 2, 3);
	}

	/// <summary>
	/// Checks that a job that isn't found gives 1 while the ones that are still leave the table, as in bash.
	/// </summary>
	/// <param name="args">The arguments, split on spaces.</param>
	/// <param name="message">The error printed.</param>
	/// <param name="left">The numbers of the jobs left.</param>
	[Theory]
	[InlineData("%1 %5", "Error: sharpie::disown::no_such_job\n\n  × No job `%5`\n  help: jobs lists the running and stopped jobs\n", new[] { 2, 3 })]
	[InlineData("999", "Error: sharpie::disown::no_such_job\n\n  × No job `999`\n  help: jobs lists the running and stopped jobs\n", new[] { 1, 2, 3 })]
	[InlineData("abc %2", "Error: sharpie::disown::invalid_target\n\n  × `abc` isn't a job or a process id\n  help: use %n for job n, or a process id\n", new[] { 1, 3 })]
	public void Run_WithUnknownJob_GivesOne(string args, string message, int[] left)
	{
		// Act
		var status = this.sut.Run(args.Split(' '));

		// Assert
		_ = (status, this.environment.Error).Should().Be((1, message));
		_ = ExternalProgram.Jobs.Select(job => job.Number).Should().Equal(left);
	}

	/// <summary>
	/// Checks that <c>disown</c> with no arguments and no jobs says there's no current job and gives 1.
	/// </summary>
	[Fact]
	public void Run_WithNoJobs_ReportsNoCurrentJob()
	{
		// Arrange
		ExternalProgram.Jobs.Clear();

		// Act
		var status = this.sut.Run([]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::disown::no_such_job", "There is no current job").Render(color: false));
	}

	/// <summary>
	/// Checks that an unknown option gives 2 and leaves every job, as bash does.
	/// </summary>
	[Fact]
	public void Run_WithUnknownOption_GivesTwo()
	{
		// Act
		var status = this.sut.Run(["-x", "%1"]);

		// Assert
		_ = status.Should().Be(2);
		_ = this.environment.Error.Should().Be("Error: sharpie::disown::invalid_option\n\n  × Unknown option `-x`\n  help: disown takes -a, -h and %n job numbers or process ids\n");
		_ = ExternalProgram.Jobs.Should().HaveCount(3);
	}

	/// <summary>
	/// Empties the job table and restores the console.
	/// </summary>
	public void Dispose()
	{
		ExternalProgram.Jobs.Clear();
		this.environment.Dispose();
	}
}
