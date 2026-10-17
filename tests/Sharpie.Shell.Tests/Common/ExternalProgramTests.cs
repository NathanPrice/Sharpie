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
		_ = this.environment.Error.Should().Contain("× Can't start `broken`: ");
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
		_ = ExternalProgram.RunPipeline([["spew"], ["firstline"]], _ => { });

		// Assert
		_ = stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
	}

	/// <summary>
	/// Checks that a pipeline with programs that aren't on <c>PATH</c> reports each of them by index and runs none of the
	/// others.
	/// </summary>
	[Fact]
	public void RunPipeline_WithMissingPrograms_ReportsEachAndRunsNothing()
	{
		// Arrange
		var marker = Path.Combine(this.folder, "ran");
		_ = ShellEnvironment.CreateFile(this.folder, "toucher", executable: true, $"#!/bin/sh\n/usr/bin/touch {marker}\n");

		var missing = new List<int>();

		// Act
		var result = ExternalProgram.RunPipeline([["nope1"], ["toucher"], ["nope2"]], missing.Add);

		// Assert
		_ = result.Should().Be(ExternalProgram.NotFound);
		_ = missing.Should().Equal(0, 2);
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
		var result = ExternalProgram.RunPipeline([["first"], ["last"]], _ => { });

		// Assert
		_ = result.Should().Be(last);
	}

	/// <summary>
	/// Checks that with pipefail a pipeline gives the status of its rightmost failing program, and 0 when none fails.
	/// </summary>
	/// <param name="first">The status the first program exits with.</param>
	/// <param name="middle">The status the middle program exits with.</param>
	/// <param name="last">The status the last program exits with.</param>
	/// <param name="expected">The pipeline's status.</param>
	[Theory]
	[InlineData(3, 4, 0, 4)]
	[InlineData(3, 0, 0, 3)]
	[InlineData(0, 0, 5, 5)]
	[InlineData(0, 0, 0, 0)]
	public void RunPipeline_WithPipeFail_ReturnsRightmostFailure(int first, int middle, int last, int expected)
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "first", executable: true, $"#!/bin/sh\nexit {first}\n");
		_ = ShellEnvironment.CreateFile(this.folder, "middle", executable: true, $"#!/bin/sh\nexit {middle}\n");
		_ = ShellEnvironment.CreateFile(this.folder, "last", executable: true, $"#!/bin/sh\nexit {last}\n");

		// Act
		var result = ExternalProgram.RunPipeline([["first"], ["middle"], ["last"]], _ => { }, pipeFail: true);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks the status rule on its own: the last status, or with pipefail the rightmost one that isn't 0.
	/// </summary>
	/// <param name="codes">The statuses in pipeline order.</param>
	/// <param name="pipeFail">Whether pipefail is on.</param>
	/// <param name="expected">The pipeline's status.</param>
	[Theory]
	[InlineData(new[] { 1, 0 }, false, 0)]
	[InlineData(new[] { 1, 0 }, true, 1)]
	[InlineData(new[] { 1, 2 }, true, 2)]
	[InlineData(new[] { 0, 0 }, true, 0)]
	public void PipelineStatus_WithCodes_FollowsPipeFail(int[] codes, bool pipeFail, int expected)
	{
		// Act
		var status = ExternalProgram.PipelineStatus(codes, pipeFail);

		// Assert
		_ = status.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a pipeline given an output stream writes its last program's output there instead of the terminal.
	/// </summary>
	[Fact]
	public void RunPipeline_WithOutput_CapturesLastStage()
	{
		// Arrange
		// The fake programs are shell scripts.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		_ = ShellEnvironment.CreateFile(this.folder, "twolines", executable: true, "#!/bin/sh\n/usr/bin/printf 'a\\nb\\n'\n");
		_ = ShellEnvironment.CreateFile(this.folder, "count", executable: true, "#!/bin/sh\nexec /usr/bin/wc -l\n");
		using var output = new MemoryStream();

		// Act
		var result = ExternalProgram.RunPipeline([["twolines"], ["count"]], _ => { }, output: output);

		// Assert
		_ = result.Should().Be(0);
		_ = System.Text.Encoding.UTF8.GetString(output.ToArray()).Trim().Should().Be("2");
	}

	/// <summary>
	/// Checks that an unlisted background program, as in a command substitution, isn't added to the jobs or
	/// announced, and that its output reaches the stream once <see cref="ExternalProgram.WaitForDetached" /> returns.
	/// </summary>
	[Fact]
	public void Run_DetachedInBackground_CopiesOutputWithoutJob()
	{
		// Arrange
		// The fake program is a shell script.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		_ = ShellEnvironment.CreateFile(this.folder, "late", executable: true, "#!/bin/sh\n/usr/bin/sleep 0.2\necho late\n");
		using var output = new MemoryStream();
		var jobs = ExternalProgram.Jobs.Count;

		// Act
		var status = ExternalProgram.Run("late", [], output, background: true, listJob: false);
		var before = output.Length;

		ExternalProgram.WaitForDetached(output);

		// Assert
		_ = status.Should().Be(0);
		_ = before.Should().Be(0);
		_ = System.Text.Encoding.UTF8.GetString(output.ToArray()).Should().Be("late\n");
		_ = ExternalProgram.Jobs.Should().HaveCount(jobs);
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that waiting on one substitution's stream leaves the background programs of another to its own wait.
	/// </summary>
	[Fact]
	public void WaitForDetached_WithTwoStreams_WaitsForEachOwn()
	{
		// Arrange
		// The fake programs are shell scripts.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		_ = ShellEnvironment.CreateFile(this.folder, "quick", executable: true, "#!/bin/sh\necho quick\n");
		_ = ShellEnvironment.CreateFile(this.folder, "late", executable: true, "#!/bin/sh\n/usr/bin/sleep 0.3\necho late\n");
		using var first = new MemoryStream();
		using var second = new MemoryStream();

		_ = ExternalProgram.Run("quick", [], first, background: true, listJob: false);
		_ = ExternalProgram.Run("late", [], second, background: true, listJob: false);

		// Act
		ExternalProgram.WaitForDetached(first);
		ExternalProgram.WaitForDetached(second);

		// Assert
		_ = System.Text.Encoding.UTF8.GetString(first.ToArray()).Should().Be("quick\n");
		_ = System.Text.Encoding.UTF8.GetString(second.ToArray()).Should().Be("late\n");
	}

	/// <summary>
	/// Checks that <see cref="ExternalProgram.StopWaiting" /> ends a wait on a background program that's still
	/// writing.
	/// </summary>
	[Fact]
	public void WaitForDetached_WhenStopped_ReturnsEarly()
	{
		// Arrange
		// The fake program is a shell script.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		_ = ShellEnvironment.CreateFile(this.folder, "slow", executable: true, "#!/bin/sh\nexec /usr/bin/sleep 3\n");
		using var output = new MemoryStream();

		_ = ExternalProgram.Run("slow", [], output, background: true, listJob: false);
		using var stop = Task.Run(async () =>
		{
			await Task.Delay(300).ConfigureAwait(false);
			ExternalProgram.StopWaiting();
		});
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();

		// Act
		ExternalProgram.WaitForDetached(output);

		// Assert
		_ = stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
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
		var result = ExternalProgram.RunPipeline([["fine"], ["broken"]], _ => { });

		// Assert
		_ = result.Should().Be(ExternalProgram.CannotRun);
	}

	/// <summary>
	/// Checks that <c>NAME=VALUE</c> words reach the program's environment, an empty value included, a repeated name
	/// takes its last value, and none of them are left in the shell's environment.
	/// </summary>
	/// <param name="assignments">The words typed in front of the program.</param>
	/// <param name="expected">What the program prints for <c>SAMPLE_A</c> and <c>SAMPLE_B</c>.</param>
	[Theory]
	[InlineData(new[] { "SAMPLE_A=1" }, "1|unset\n")]
	[InlineData(new[] { "SAMPLE_A=1", "SAMPLE_B=" }, "1|\n")]
	[InlineData(new[] { "SAMPLE_A=1", "SAMPLE_A=2" }, "2|unset\n")]
	public void Run_WithAssignments_GivesOnlyThatProgramTheValues(string[] assignments, string expected)
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "showenv", executable: true, "#!/bin/sh\necho \"${SAMPLE_A-unset}|${SAMPLE_B-unset}\"\n");
		using var output = new MemoryStream();

		// Act
		_ = ExternalProgram.Run("showenv", [], output, assignments: assignments);

		// Assert
		_ = System.Text.Encoding.UTF8.GetString(output.ToArray()).Should().Be(expected);
		_ = Environment.GetEnvironmentVariable("SAMPLE_A").Should().BeNull();
	}

	/// <summary>
	/// Checks that an assigned <c>PATH</c> is where the program is looked for, as in bash, so an empty one finds nothing.
	/// </summary>
	[Fact]
	public void Run_WithAssignedPath_LooksThere()
	{
		// Arrange
		var other = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(other, "elsewhere", executable: true, "#!/bin/sh\nexit 6\n");
		_ = ShellEnvironment.CreateFile(this.folder, "here", executable: true, "#!/bin/sh\nexit 0\n");

		// Act
		var found = ExternalProgram.Run("elsewhere", [], assignments: [$"PATH={other}"]);
		var missing = ExternalProgram.Run("here", [], assignments: ["PATH="]);

		// Assert
		_ = found.Should().Be(6);
		_ = missing.Should().BeNull();
	}

	/// <summary>
	/// Checks that each pipeline stage gets only its own <c>NAME=VALUE</c> words.
	/// </summary>
	[Fact]
	public void RunPipeline_WithAssignments_GivesEachStageOnlyItsOwn()
	{
		// Arrange
		var first = Path.Combine(this.folder, "first.txt");
		var second = Path.Combine(this.folder, "second.txt");
		_ = ShellEnvironment.CreateFile(this.folder, "recorder", executable: true, "#!/bin/sh\necho \"${SAMPLE_A-unset}\" > \"$1\"\n");

		// Act
		_ = ExternalProgram.RunPipeline([["recorder", first], ["recorder", second]], _ => { }, [["SAMPLE_A=1"], []]);

		// Assert
		_ = File.ReadAllText(first).Should().Be("1\n");
		_ = File.ReadAllText(second).Should().Be("unset\n");
	}

	/// <summary>
	/// Checks that <c>3&gt;&amp;1 1&gt;&amp;2 2&gt;&amp;3</c> swaps a program's output and errors, which needs every
	/// stream read before any is replaced.
	/// </summary>
	[Fact]
	public void Run_WithSwappedStreams_SwapsOutputAndErrors()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "sampletool", executable: true, "#!/bin/sh\necho out\necho err >&2\n");
		using var output = new MemoryStream();
		using var error = new MemoryStream();
		using var redirection = Open("sampletool 3>&1 1>&2 2>&3");

		// Act
		_ = ExternalProgram.Run("sampletool", [], output, error, redirection: redirection);

		// Assert
		_ = System.Text.Encoding.UTF8.GetString(output.ToArray()).Should().Be("err\n");
		_ = System.Text.Encoding.UTF8.GetString(error.ToArray()).Should().Be("out\n");
	}

	/// <summary>
	/// Checks that a file opened on a stream past 2 reaches the program under that number, and that a closed stream
	/// is closed in the program.
	/// </summary>
	[Fact]
	public void Run_WithHigherStream_GivesProgramThatNumber()
	{
		// Arrange
		var file = Path.Combine(this.folder, "three.txt");
		_ = ShellEnvironment.CreateFile(this.folder, "sampletool", executable: true, "#!/bin/sh\necho x >&3\necho y >&4 || echo closed >&3\n");
		using var redirection = Open($"sampletool 3> {file} 4>&1 4>&-");

		// Act
		var status = ExternalProgram.Run("sampletool", [], redirection: redirection);

		// Assert
		_ = status.Should().Be(0);
		_ = File.ReadAllText(file).Should().Be("x\nclosed\n");
	}

	/// <summary>
	/// Checks that <c>2&gt;&amp;1</c> into one stream copies the program's output and errors into it once each, in the
	/// order they were written.
	/// </summary>
	[Fact]
	public void Run_WithErrorsCopiedToOutput_WritesBothIntoOneStream()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "sampletool", executable: true, "#!/bin/sh\necho one\necho two >&2\necho three\n");
		using var output = new MemoryStream();
		using var redirection = Open("sampletool 2>&1");

		// Act
		_ = ExternalProgram.Run("sampletool", [], output, redirection: redirection);

		// Assert
		_ = System.Text.Encoding.UTF8.GetString(output.ToArray()).Should().Be("one\ntwo\nthree\n");
	}

	/// <summary>
	/// Checks that a here-document on a pipeline's first stage is what that stage reads, and that a redirect on the
	/// last stage takes its output and errors.
	/// </summary>
	[Fact]
	public void RunPipeline_WithHereDocumentOnFirstStage_FeedsIt()
	{
		// Arrange
		var file = Path.Combine(this.folder, "out.txt");
		_ = ShellEnvironment.CreateFile(this.folder, "reader", executable: true, "#!/bin/sh\nread line\necho \"got $line\"\n");
		_ = ShellEnvironment.CreateFile(this.folder, "brackets", executable: true, "#!/bin/sh\nread line\necho \"[$line]\"\necho done >&2\n");
		using var first = Open("reader <<EOF\nhello\nEOF");
		using var last = Open($"brackets > {file} 2>&1");

		// Act
		var status = ExternalProgram.RunPipeline([["reader"], ["brackets"]], _ => { }, redirections: [first, last]);

		// Assert
		_ = status.Should().Be(0);
		_ = File.ReadAllText(file).Should().Be("[got hello]\ndone\n");
	}

	/// <summary>
	/// Checks that <c>2&gt;&amp;1 |</c> sends a stage's errors down the pipe with its output.
	/// </summary>
	[Fact]
	public void RunPipeline_WithErrorsCopiedToPipe_SendsBothToNextStage()
	{
		// Arrange
		var file = Path.Combine(this.folder, "out.txt");
		_ = ShellEnvironment.CreateFile(this.folder, "complain", executable: true, "#!/bin/sh\necho oops >&2\n");
		_ = ShellEnvironment.CreateFile(this.folder, "reader", executable: true, "#!/bin/sh\nread line\necho \"got $line\"\n");
		using var first = Open("complain 2>&1");
		using var last = Open($"reader > {file}");

		// Act
		_ = ExternalProgram.RunPipeline([["complain"], ["reader"]], _ => { }, redirections: [first, last]);

		// Assert
		_ = File.ReadAllText(file).Should().Be("got oops\n");
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
	/// Opens the redirects of <paramref name="line" />.
	/// </summary>
	/// <param name="line">A command with redirects that all open.</param>
	/// <returns>
	/// The opened redirects.
	/// </returns>
	private static Redirection Open(string line) => Redirection.Open(CommandLine.SplitRedirect(CommandLine.Split(line)).Redirects, line, noClobber: false)!;

	/// <summary>
	/// Matches the <c>[job] pid</c> line a background program prints, capturing the process id.
	/// </summary>
	/// <returns>
	/// The compiled pattern.
	/// </returns>
	[GeneratedRegex(@"^\[\d+\] (\d+)\n$")]
	private static partial Regex JobLine();
}
