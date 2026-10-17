using System.Globalization;
using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Runs quoting, redirects, and pipelines from <see cref="CommandLine" /> through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class CommandLineProcessTests
{
	/// <summary>
	/// Checks that single-quoted text reaches <c>echo</c> as one argument with its spaces kept.
	/// </summary>
	[Fact]
	public async Task Shell_WithSingleQuotedEcho_KeepsSpaces()
	{
		// Act
		var run = await ShellProcess.RunAsync("echo 'world     test'\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ world     test\n$ ");
		_ = run.Exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>~</c> reaches a command as the <c>HOME</c> folder.
	/// </summary>
	[Fact]
	public async Task Shell_WithTilde_PassesHomeFolder()
	{
		// Arrange
		var home = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		try
		{
			// Act
			var run = await ShellProcess.RunAsync("echo ~/x\nexit\n", environment: new Dictionary<string, string> { ["HOME"] = home }).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be($"$ {home}/x\n$ ");
		}
		finally
		{
			Directory.Delete(home, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a wildcard reaches a command as the sorted names it matches in the working folder, and that a
	/// quoted one reaches it as typed.
	/// </summary>
	[Fact]
	public async Task Shell_WithWildcard_PassesMatchingNames()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		await File.WriteAllTextAsync(Path.Combine(folder, "b.tmp"), string.Empty).ConfigureAwait(true);
		await File.WriteAllTextAsync(Path.Combine(folder, "a.tmp"), string.Empty).ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync("echo *.tmp '*.tmp'\nexit\n", folder).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ a.tmp b.tmp *.tmp\n$ ");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>&gt;</c> and <c>1&gt;</c> send a builtin's output to a file, replacing what was there, and
	/// that only the prompts reach the terminal.
	/// </summary>
	/// <param name="operatorText">The redirect operator to use.</param>
	[Theory]
	[InlineData(">")]
	[InlineData("1>")]
	public async Task Shell_WithEchoRedirected_WritesFileAndOverwrites(string operatorText)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var file = Path.Combine(folder, "foo.md");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"echo old {operatorText} {file}\necho Hello James {operatorText} {file}\nexit\n")
				.ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ $ $ ");
			_ = (await File.ReadAllTextAsync(file).ConfigureAwait(true)).Should().Be("Hello James\n");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>&gt;&gt;</c> and <c>1&gt;&gt;</c> add a builtin's output to the end of an existing file,
	/// create the file when it's missing, and send only the prompts to the terminal.
	/// </summary>
	/// <param name="operatorText">The append operator to use.</param>
	[Theory]
	[InlineData(">>")]
	[InlineData("1>>")]
	public async Task Shell_WithEchoAppended_KeepsExistingContent(string operatorText)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var existing = Path.Combine(folder, "qux.md");
		var created = Path.Combine(folder, "new.md");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(
				$"echo first > {existing}\necho second {operatorText} {existing}\necho third {operatorText} {created}\nexit\n")
				.ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ $ $ $ ");
			_ = (await File.ReadAllTextAsync(existing).ConfigureAwait(true)).Should().Be("first\nsecond\n");
			_ = (await File.ReadAllTextAsync(created).ConfigureAwait(true)).Should().Be("third\n");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that an external program's standard output goes to the file while its errors still reach the
	/// terminal.
	/// </summary>
	[Fact]
	public async Task Shell_WithExternalRedirected_WritesOnlyStdoutToFile()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var source = Path.Combine(folder, "blueberry");
		var file = Path.Combine(folder, "quz.md");

		await File.WriteAllTextAsync(source, "blueberry\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"cat {source} nonexistent 1> {file}\nexit\n", folder).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ $ ");
			_ = run.Error.Should().Contain("nonexistent: No such file or directory");
			_ = (await File.ReadAllTextAsync(file).ConfigureAwait(true)).Should().Be("blueberry\n");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>2&gt;&gt;</c> adds each external program's errors to the end of the file, keeping the errors
	/// already there, and that nothing reaches the terminal but the prompts.
	/// </summary>
	[Fact]
	public async Task Shell_WithExternalStderrAppended_KeepsEarlierErrors()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var file = Path.Combine(folder, "err.md");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"cat first 2>> {file}\ncat second 2>> {file}\nexit\n", folder).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ $ $ ");
			_ = run.Error.Should().BeEmpty();
			_ = (await File.ReadAllLinesAsync(file).ConfigureAwait(true)).Should().SatisfyRespectively(
				line => line.Should().Contain("first: No such file or directory"),
				line => line.Should().Contain("second: No such file or directory"));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>2&gt;</c> sends an external program's errors to the file while its standard output still
	/// reaches the terminal.
	/// </summary>
	[Fact]
	public async Task Shell_WithExternalStderrRedirected_WritesOnlyStderrToFile()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var source = Path.Combine(folder, "pear");
		var file = Path.Combine(folder, "quz.md");

		await File.WriteAllTextAsync(source, "pear\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"cat {source} nonexistent 2> {file}\nexit\n", folder).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ pear\n$ ");
			_ = run.Error.Should().BeEmpty();
			_ = (await File.ReadAllTextAsync(file).ConfigureAwait(true)).Should().Contain("nonexistent: No such file or directory");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>2&gt;</c> on a builtin that prints no errors still creates an empty file and leaves its
	/// output on the terminal.
	/// </summary>
	[Fact]
	public async Task Shell_WithEchoStderrRedirected_PrintsOutputAndCreatesEmptyFile()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var file = Path.Combine(folder, "foo.md");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"echo Maria file cannot be found 2> {file}\nexit\n").ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ Maria file cannot be found\n$ ");
			_ = (await File.ReadAllTextAsync(file).ConfigureAwait(true)).Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>|</c> feeds the first program's output to the second program's input.
	/// </summary>
	[Fact]
	public async Task Shell_WithPipeline_FeedsFirstOutputToSecond()
	{
		// Arrange
		var file = Path.GetTempFileName();
		await File.WriteAllTextAsync(file, "one\ntwo\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"cat {file} | wc -l\nexit\n").ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ 2\n$ ");
		}
		finally
		{
			File.Delete(file);
		}
	}

	/// <summary>
	/// Checks that a pipeline whose first program never ends on its own, like <c>tail -f</c>, still returns to the
	/// prompt once the second program exits.
	/// </summary>
	[Fact]
	public async Task Shell_WithTailFollowIntoHead_ReturnsToPrompt()
	{
		// Arrange
		var file = Path.GetTempFileName();
		await File.WriteAllTextAsync(file, "first\nsecond\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"tail -f {file} | head -n 2\nexit\n").ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ first\nsecond\n$ ");
			_ = run.Exited.Should().BeTrue();
		}
		finally
		{
			File.Delete(file);
		}
	}

	/// <summary>
	/// Checks that a builtin's output reaches the program after <c>|</c>, and that a builtin after <c>|</c> prints its
	/// own output while the program before it is discarded.
	/// </summary>
	/// <param name="line">The pipeline to run.</param>
	/// <param name="expected">What the builtin or program should print.</param>
	[Theory]
	[InlineData("echo apple-orange | wc", "      1       1      13\n")]
	[InlineData("ls | type exit", "exit is a shell builtin\n")]
	[InlineData("echo hi | type echo", "echo is a shell builtin\n")]
	[InlineData("echo hi|type echo", "echo is a shell builtin\n")]
	public async Task Shell_WithBuiltinInPipeline_PrintsPipelineOutput(string line, string expected)
	{
		// Act
		var run = await ShellProcess.RunAsync($"{line}\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be($"$ {expected}$ ");
	}

	/// <summary>
	/// Checks that a pipeline of three or more programs passes each program's output on to the next.
	/// </summary>
	/// <param name="pipeline">The pipeline to run, with <c>{0}</c> standing for a file holding five lines.</param>
	/// <param name="expected">What the last program should print.</param>
	[Theory]
	[InlineData("cat {0} | head -n 3 | wc -l", "3\n")]
	[InlineData("cat {0} | tail -n 4 | head -n 2 | grep x", "two-x\n")]
	public async Task Shell_WithMultiStagePipeline_ChainsOutput(string pipeline, string expected)
	{
		// Arrange
		var file = Path.GetTempFileName();
		await File.WriteAllTextAsync(file, "one-x\ntwo-x\nthree\nfour-x\nfive\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"{string.Format(CultureInfo.InvariantCulture, pipeline, file)}\nexit\n")
				.ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be($"$ {expected}$ ");
		}
		finally
		{
			File.Delete(file);
		}
	}

	/// <summary>
	/// Checks that a redirect typed with no spaces around it still writes the file.
	/// </summary>
	[Fact]
	public async Task Shell_WithUnspacedRedirect_WritesFile()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var file = Path.Combine(folder, "foo.md");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"echo hi>{file}\nexit\n").ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ $ ");
			_ = (await File.ReadAllTextAsync(file).ConfigureAwait(true)).Should().Be("hi\n");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that quoted or escaped operators reach the command as plain arguments.
	/// </summary>
	[Fact]
	public async Task Shell_WithQuotedOperators_PassesThemAsArguments()
	{
		// Act
		var run = await ShellProcess.RunAsync("echo '>' \"|\" \\& x\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ > | & x\n$ ");
	}

	/// <summary>
	/// Checks that an operator the shell can't run yet, or a redirect or pipe with nothing after it, is refused with
	/// status 2 instead of being passed to the command.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="operatorText">The operator the error should name.</param>
	/// <param name="start">Where the operator starts in <paramref name="line" />.</param>
	/// <param name="label">The note the error puts under the operator.</param>
	[Theory]
	[InlineData("echo a <x", "<", 7, "Sharpie can't run this operator yet")]
	[InlineData("echo a 2>&1", "2>&", 7, "Sharpie can't run this operator yet")]
	[InlineData("echo a >", ">", 7, "needs a file name after it")]
	[InlineData("echo a |", "|", 7, "needs a command on both sides")]
	[InlineData("| echo a", "|", 0, "needs a command on both sides")]
	[InlineData("echo a | | wc", "|", 7, "needs a command on both sides")]
	public async Task Shell_WithUnusableOperator_PrintsErrorAndStatus2(string line, string operatorText, int start, string label)
	{
		// Act
		var run = await ShellProcess.RunAsync($"{line}\necho $?\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ 2\n$ ");
		_ = run.Error.Should().Be(new ShellError("sharpie::parser::unsupported_operator", $"`{operatorText}` can't be used here")
		{
			Source = line,
			Start = start,
			End = start + operatorText.Length,
			Label = label,
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that a mistyped command is reported on standard error, pointing at the name, with the program on
	/// <c>PATH</c> it was probably meant to be.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithMistypedCommand_SuggestsCloseProgram()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		_ = ShellEnvironment.CreateFile(folder, "sampletool", executable: true, "#!/bin/sh\n");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync("echo hi; sampletoll --flag\nexit\n", environment: new Dictionary<string, string> { ["PATH"] = folder }).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ hi\n$ ");
			_ = run.Error.Should().Be(new ShellError("sharpie::shell::command_not_found", "Command `sampletoll` not found")
			{
				Source = "sampletoll --flag",
				End = 10,
				Label = "not a builtin, alias or program on PATH",
				Help = "did you mean `sampletool`?",
			}.Render(color: false));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a command that isn't found reports into its <c>2&gt;</c> file, as bash does, so
	/// <c>2&gt;/dev/null</c> hides it.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithMissingCommandAndErrorRedirect_ReportsIntoFile()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var file = Path.Combine(folder, "err.txt");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"nosuchsample 2> {file}\nexit\n", folder).ConfigureAwait(true);

			// Assert
			_ = run.Error.Should().BeEmpty();
			_ = (await File.ReadAllTextAsync(file).ConfigureAwait(true)).Should().StartWith("Error: sharpie::shell::command_not_found\n");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a redirect file that can't be opened is reported with the file name underlined.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithRedirectIntoMissingFolder_PointsAtFileName()
	{
		// Act
		var run = await ShellProcess.RunAsync("echo hi > missing-sample-folder/out.txt\necho $?\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ 1\n$ ");
		_ = run.Error.Should().StartWith("Error: sharpie::shell::redirect_failed\n\n  × Can't open `missing-sample-folder/out.txt` for writing\n   ╭─[1:11]\n 1 │ echo hi > missing-sample-folder/out.txt\n   ·           ──────────────┬──────────────\n");
	}

	/// <summary>
	/// Checks that a missing program in the middle of a pipeline is reported at its own place in the line.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithMissingProgramInPipeline_PointsAtThatStage()
	{
		// Act
		var run = await ShellProcess.RunAsync("ls | nosuchsample | wc\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Error.Should().StartWith("Error: sharpie::shell::command_not_found\n\n  × Command `nosuchsample` not found\n   ╭─[1:6]\n 1 │ ls | nosuchsample | wc\n   ·      ──────┬─────\n");
	}

	/// <summary>
	/// Checks that a file on <c>PATH</c> that can't run is never suggested, even when its name is closer.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithCloserFileThatCantRun_SuggestsTheProgram()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		_ = ShellEnvironment.CreateFile(folder, "sampletool", executable: true, "#!/bin/sh\n");
		_ = ShellEnvironment.CreateFile(folder, "samplexyolz", executable: false);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync("samplexyol\nexit\n", environment: new Dictionary<string, string> { ["PATH"] = folder }).ConfigureAwait(true);

			// Assert
			_ = run.Error.Should().EndWith("  help: did you mean `sampletool`?\n");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}
}
