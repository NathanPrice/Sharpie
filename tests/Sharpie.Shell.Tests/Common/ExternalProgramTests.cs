using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Sharpie.Shell.Common;

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
	/// Checks that a program missing from <c>PATH</c> returns <see langword="false" /> and prints nothing.
	/// </summary>
	[Fact]
	public void TryRun_WhenNotOnPath_ReturnsFalseAndPrintsNothing()
	{
		// Act
		var result = ExternalProgram.TryRun("nosuchcommand", []);

		// Assert
		_ = result.Should().BeFalse();
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a runnable program on <c>PATH</c> returns <see langword="true" />.
	/// </summary>
	[Fact]
	public void TryRun_WhenExecutableOnPath_ReturnsTrue()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "mytool", executable: true, "#!/bin/sh\nexit 0\n");

		// Act
		var result = ExternalProgram.TryRun("mytool", ["one", "two"]);

		// Assert
		_ = result.Should().BeTrue();
	}

	/// <summary>
	/// Checks that a program found on <c>PATH</c> that fails to start still returns <see langword="true" /> and
	/// prints the error after its name.
	/// </summary>
	[Fact]
	public void TryRun_WhenProgramFailsToStart_ReturnsTrueAndPrintsError()
	{
		// Arrange
		// No shebang and not a binary, so the OS refuses to run it and Process.Start throws Win32Exception.
		_ = ShellEnvironment.CreateFile(this.folder, "broken", executable: true, "not a program");

		// Act
		var result = ExternalProgram.TryRun("broken", []);

		// Assert
		_ = result.Should().BeTrue();
		_ = this.environment.Output.Should().StartWith("broken: ");
	}

	/// <summary>
	/// Checks that a program's standard error goes to the stream passed as the error target.
	/// </summary>
	[Fact]
	public void TryRun_WithErrorStream_WritesStderrToStream()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "complain", executable: true, "#!/bin/sh\necho oops >&2\n");
		using var error = new MemoryStream();

		// Act
		_ = ExternalProgram.TryRun("complain", [], error: error);

		// Assert
		_ = System.Text.Encoding.UTF8.GetString(error.ToArray()).Should().Be("oops\n");
	}

	/// <summary>
	/// Checks that an executable in the current folder with the same name as a program on <c>PATH</c> does not run in
	/// its place.
	/// </summary>
	[Fact]
	public void TryRun_WhenCurrentFolderHasSameName_RunsThePathProgram()
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
			_ = ExternalProgram.TryRun("mytool", [], output);
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
	public void TryRun_InBackground_PrintsJobAndPidWithoutWaiting()
	{
		// Arrange
		// sleep is called by full path because PATH holds only the test folder. A bare name made the script exit
		// at once, so the job could be gone before the test looked it up.
		_ = ShellEnvironment.CreateFile(this.folder, "napper", executable: true, "#!/bin/sh\nexec /bin/sleep 10\n");
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();

		// Act
		_ = ExternalProgram.TryRun("napper", [], background: true);
		stopwatch.Stop();

		// Assert
		// The job number isn't always 1, since earlier tests may have started background jobs too.
		var match = JobLine().Match(this.environment.Output);
		_ = match.Success.Should().BeTrue();
		System.Diagnostics.Process.GetProcessById(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Kill();
		_ = stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
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
		ExternalProgram.RunPipeline([["spew"], ["firstline"]]);

		// Assert
		_ = stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
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
