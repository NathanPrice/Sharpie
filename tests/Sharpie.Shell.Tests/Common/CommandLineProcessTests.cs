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
	[Theory]
	[InlineData("echo a <x", "<")]
	[InlineData("echo a 2>&1", "2>&")]
	[InlineData("echo a & echo b", "&")]
	[InlineData("echo a >", ">")]
	[InlineData("echo a |", "|")]
	public async Task Shell_WithUnusableOperator_PrintsErrorAndStatus2(string line, string operatorText)
	{
		// Act
		var run = await ShellProcess.RunAsync($"{line}\necho $?\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ 2\n$ ");
		_ = run.Error.Should().Be($"sharpie: can't use '{operatorText}' here\n");
	}
}
