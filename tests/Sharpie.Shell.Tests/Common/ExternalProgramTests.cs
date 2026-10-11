using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Jobs;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="ExternalProgram" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed partial class ExternalProgramTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly string folder;

	/// <summary>
	/// Initializes a new instance of the <see cref="ExternalProgramTests" /> class with an empty folder as the only
	/// <c>PATH</c> entry.
	/// </summary>
	public ExternalProgramTests()
	{
		this.folder = this.environment.CreateFolder();
		ShellEnvironment.SetPath(this.folder);
	}

	/// <summary>
	/// Checks that a program missing from <c>PATH</c> returns <see langword="null" /> and prints nothing.
	/// </summary>
	[Fact]
	public void Run_WhenNotOnPath_ReturnsNullAndPrintsNothing()
	{
		// Act
		var result = ExternalProgram.Run("nosuchcommand", []);

		// Assert
		_ = result.Should().BeNull();
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a runnable program on <c>PATH</c> returns its exit status.
	/// </summary>
	/// <param name="code">The status the program exits with.</param>
	[Theory]
	[InlineData(0)]
	[InlineData(3)]
	public void Run_WhenExecutableOnPath_ReturnsExitStatus(int code)
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "mytool", executable: true, $"#!/bin/sh\nexit {code}\n");

		// Act
		var result = ExternalProgram.Run("mytool", ["one", "two"]);

		// Assert
		_ = result.Should().Be(code);
	}

	/// <summary>
	/// Checks that a program ended by a signal returns 128 plus the signal number, as POSIX shells report it.
	/// </summary>
	[Fact]
	public void Run_WhenProgramIsKilled_ReturnsSignalStatus()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "victim", executable: true, "#!/bin/sh\nkill -9 $$\n");

		// Act
		var result = ExternalProgram.Run("victim", []);

		// Assert
		_ = result.Should().Be(128 + 9);
	}

	/// <summary>
	/// Checks that a program found on <c>PATH</c> that fails to start returns <see cref="ExternalProgram.CannotRun" />
	/// and prints the error after its name.
	/// </summary>
	[Fact]
	public void Run_WhenProgramFailsToStart_ReturnsCannotRunAndPrintsError()
	{
		// Arrange
		// No shebang and not a binary, so the OS refuses to run it and Process.Start throws Win32Exception.
		_ = ShellEnvironment.CreateFile(this.folder, "broken", executable: true, "not a program");

		// Act
		var result = ExternalProgram.Run("broken", []);

		// Assert
		_ = result.Should().Be(ExternalProgram.CannotRun);
		_ = this.environment.Output.Should().StartWith("broken: ");
	}

	/// <summary>
	/// Checks that a program's standard error goes to the stream passed as the error target.
	/// </summary>
	[Fact]
	public void Run_WithErrorStream_WritesStderrToStream()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "complain", executable: true, "#!/bin/sh\necho oops >&2\n");
		using var error = new MemoryStream();

		// Act
		_ = ExternalProgram.Run("complain", [], error: error);

		// Assert
		_ = System.Text.Encoding.UTF8.GetString(error.ToArray()).Should().Be("oops\n");
	}

	/// <summary>
	/// Checks that an executable in the current folder with the same name as a program on <c>PATH</c> does not run in
	/// its place.
	/// </summary>
	[Fact]
	public void Run_WhenCurrentFolderHasSameName_RunsThePathProgram()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "mytool", executable: true, "#!/bin/sh\necho real\n");
		var planted = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(planted, "mytool", executable: true, "#!/bin/sh\necho planted\n");
		using var output = new MemoryStream();
		var previous = Directory.GetCurrentDirectory();
		Directory.SetCurrentDirectory(planted);

		// Act
		try
		{
			_ = ExternalProgram.Run("mytool", [], output);
		}
		finally
		{
			Directory.SetCurrentDirectory(previous);
		}

		// Assert
		_ = System.Text.Encoding.UTF8.GetString(output.ToArray()).Should().Be("real\n");
	}

	/// <summary>
	/// Checks that a background program prints its job number and process id and returns while the program is still
	/// running.
	/// </summary>
	[Fact]
	public void Run_InBackground_PrintsJobAndPidWithoutWaiting()
	{
		// Arrange
		// sleep is called by full path because PATH holds only the test folder. A bare name made the script exit
		// at once, so the job could be gone before the test looked it up.
		_ = ShellEnvironment.CreateFile(this.folder, "napper", executable: true, "#!/bin/sh\nexec /bin/sleep 10\n");
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();

		// Act
		var result = ExternalProgram.Run("napper", [], background: true);
		stopwatch.Stop();

		// Assert
		// The job number isn't always 1, since earlier tests may have started background jobs too.
		var match = JobLine().Match(this.environment.Output);
		_ = match.Success.Should().BeTrue();
		System.Diagnostics.Process.GetProcessById(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Kill();
		_ = stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
		_ = result.Should().Be(0);
	}

	/// <summary>
	/// Checks that when a later program in a pipeline exits early, the earlier one gets SIGPIPE and ends too, as the
	/// real pipes on Linux and macOS give it, so the pipeline returns at once.
	/// </summary>
	[Fact]
	public void RunPipeline_WhenLastStageExitsEarly_EndsEarlierStage()
	{
		// Arrange
		// Windows pipelines copy between programs instead of sharing a pipe, so there's no SIGPIPE to check.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		_ = ShellEnvironment.CreateFile(this.folder, "spew", executable: true, "#!/bin/sh\nexec /usr/bin/yes\n");
		_ = ShellEnvironment.CreateFile(this.folder, "firstline", executable: true, "#!/bin/sh\nexec /usr/bin/head -n 1 > /dev/null\n");
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();

		// Act
		_ = ExternalProgram.RunPipeline([["spew"], ["firstline"]]);

		// Assert
		_ = stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
	}

	/// <summary>
	/// Checks that a pipeline with programs that aren't on <c>PATH</c> names each of them and runs none of the others.
	/// </summary>
	[Fact]
	public void RunPipeline_WithMissingPrograms_ReportsEachAndRunsNothing()
	{
		// Arrange
		var marker = Path.Combine(this.folder, "ran");
		_ = ShellEnvironment.CreateFile(this.folder, "toucher", executable: true, $"#!/bin/sh\n/usr/bin/touch {marker}\n");

		// Act
		var result = ExternalProgram.RunPipeline([["nope1"], ["toucher"], ["nope2"]]);

		// Assert
		_ = result.Should().Be(ExternalProgram.NotFound);
		_ = this.environment.Output.Should().Be($"nope1: command not found{Environment.NewLine}nope2: command not found{Environment.NewLine}");
		_ = File.Exists(marker).Should().BeFalse();
	}

	/// <summary>
	/// Checks that a pipeline returns its last program's status, not the first one's.
	/// </summary>
	/// <param name="first">The status the first program exits with.</param>
	/// <param name="last">The status the last program exits with.</param>
	[Theory]
	[InlineData(0, 4)]
	[InlineData(5, 0)]
	public void RunPipeline_WhenStagesFinish_ReturnsLastStatus(int first, int last)
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "first", executable: true, $"#!/bin/sh\nexit {first}\n");
		_ = ShellEnvironment.CreateFile(this.folder, "last", executable: true, $"#!/bin/sh\nexit {last}\n");

		// Act
		var result = ExternalProgram.RunPipeline([["first"], ["last"]]);

		// Assert
		_ = result.Should().Be(last);
	}

	/// <summary>
	/// Checks that a pipeline whose later program can't start returns <see cref="ExternalProgram.CannotRun" />.
	/// </summary>
	[Fact]
	public void RunPipeline_WhenLaterStageFailsToStart_ReturnsCannotRun()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "fine", executable: true, "#!/bin/sh\nexit 0\n");
		_ = ShellEnvironment.CreateFile(this.folder, "broken", executable: true, "not a program");

		// Act
		var result = ExternalProgram.RunPipeline([["fine"], ["broken"]]);

		// Assert
		_ = result.Should().Be(ExternalProgram.CannotRun);
	}

	/// <summary>
	/// Checks that a new job is numbered one past the newest job still listed, like bash, even after older ones ended.
	/// </summary>
	[Fact]
	public void AddJob_AfterOlderJobEnded_NumbersPastNewest()
	{
		// Arrange
		ExternalProgram.Jobs.Clear();
		ExternalProgram.AddJob(new Job(0, 101, "a"));
		ExternalProgram.AddJob(new Job(0, 102, "b"));
		ExternalProgram.Jobs.RemoveAt(0);

		// Act
		ExternalProgram.AddJob(new Job(0, 103, "c"));

		// Assert
		_ = ExternalProgram.Jobs.Select(job => job.Number).Should().Equal(2, 3);
		ExternalProgram.Jobs.Clear();
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Matches the <c>[job] pid</c> line a background program prints, capturing the process id.
	/// </summary>
	/// <returns>
	/// The compiled pattern.
	/// </returns>
	[GeneratedRegex(@"^\[\d+\] (\d+)\n$")]
	private static partial Regex JobLine();
}
