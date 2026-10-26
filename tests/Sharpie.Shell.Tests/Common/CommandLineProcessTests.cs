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
			_ = run.Output.Should().StartWith("$ $ ");
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
			_ = run.Output.Should().StartWith("$ $ ");
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
	/// Checks the redirects bash scripts lean on, run through the shell: reading a file, sending errors down a pipe,
	/// a redirect on a pipeline's last stage, both streams into one file, and a command of nothing but a redirect.
	/// </summary>
	/// <param name="line">The command, with <c>{0}</c> for a folder holding <c>in.txt</c>.</param>
	/// <param name="expectedOutput">What the terminal shows after the prompt.</param>
	/// <param name="expectedFile">What <c>out.txt</c> in the folder holds afterwards, or <see langword="null" /> when it's never made.</param>
	[Theory]
	[InlineData("wc -l < {0}/in.txt", "2\n", null)]
	[InlineData("ls {0}/nope 2>&1 | wc -l", "1\n", null)]
	[InlineData("ls {0}/nope 2>/dev/null | wc -l", "0\n", null)]
	[InlineData("cat {0}/in.txt | head -n 1 > {0}/out.txt", "", "one\n")]
	[InlineData("echo early > {0}/out.txt; sh -c 'echo out; echo err >&2' &> {0}/out.txt", "", "out\nerr\n")]
	[InlineData("echo early > {0}/out.txt; echo late &>> {0}/out.txt", "", "early\nlate\n")]
	[InlineData("echo early > {0}/out.txt; > {0}/out.txt", "", "")]
	[InlineData("> {0}/out.txt", "", "")]
	[InlineData("echo both >&{0}/out.txt", "", "both\n")]
	[InlineData("echo hi 2>&1 | wc -c", "3\n", null)]
	[InlineData("cat <<< 'some text' | wc -c", "10\n", null)]
	[InlineData("echo to-file 2>{0}/out.txt 1>&2", "", "to-file\n")]
	public async Task Shell_WithRedirects_RoutesStreams(string line, string expectedOutput, string? expectedFile)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		await File.WriteAllTextAsync(Path.Combine(folder, "in.txt"), "one\ntwo\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"{string.Format(CultureInfo.InvariantCulture, line, folder)}\nexit\n", folder).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be($"$ {expectedOutput}$ ");
			_ = File.Exists(Path.Combine(folder, "out.txt")).Should().Be(expectedFile is not null);

			if (expectedFile is not null)
			{
				_ = (await File.ReadAllTextAsync(Path.Combine(folder, "out.txt")).ConfigureAwait(true)).Should().Be(string.Format(CultureInfo.InvariantCulture, expectedFile, folder));
			}
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a here-document typed at the plain prompt asks for its body after <c>&gt; </c> and feeds it to
	/// the command, expanding variables only when the delimiter isn't quoted.
	/// </summary>
	[Fact]
	public async Task Shell_WithHereDocumentAtPrompt_ReadsBody()
	{
		// Act
		var run = await ShellProcess.RunAsync("SAMPLE=val\ncat <<EOF | tr a-z A-Z\nhi $SAMPLE\nEOF\ncat <<'EOF'; echo next\nhi $SAMPLE\nEOF\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ > > HI VAL\n$ > > hi $SAMPLE\nnext\n$ ");
	}

	/// <summary>
	/// Checks that a here-document whose delimiter line never comes runs with the lines it had once input ends, after
	/// a warning.
	/// </summary>
	[Fact]
	public async Task Shell_WithUnclosedHereDocumentAtEndOfInput_RunsWhatItHas()
	{
		// Act
		var run = await ShellProcess.RunAsync("cat <<EOF\nlast line\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ > > last line\n$ ");
		_ = run.Error.Should().StartWith("Error: sharpie::parser::unclosed_here_document\n");
	}

	/// <summary>
	/// Checks that a here-document in a <c>-c</c> command reads the lines after it, with <c>&lt;&lt;-</c> dropping
	/// the leading tabs.
	/// </summary>
	[Fact]
	public async Task Shell_WithHereDocumentInCommandOption_ReadsBody()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "cat <<-EOF && echo after\n\tone\n\t\ttwo\n\tEOF\necho last"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("one\ntwo\nafter\nlast\n");
	}

	/// <summary>
	/// Checks that <c>$(...)</c> and backquotes in a here-document's body run when the delimiter isn't quoted, with
	/// their output kept whole, and stay as typed when it is, as in bash.
	/// </summary>
	[Fact]
	public async Task Shell_WithSubstitutionInHereDocument_RunsItOnlyWhenUnquoted()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "cat <<EOF\n$(echo sub) `printf 'a  b'`\nEOF\ncat <<'EOF'\n$(echo sub) `echo a`\nEOF"]).ConfigureAwait(true);

		// Assert
		_ = run.Error.Should().BeEmpty();
		_ = run.Output.Should().Be("sub a  b\n$(echo sub) `echo a`\n");
	}

	/// <summary>
	/// Checks that a here-document in a sourced file reads its body, keeping a body line's trailing backslash for
	/// the here-document's own rules.
	/// </summary>
	[Fact]
	public async Task Shell_WithHereDocumentInSourcedFile_ReadsBody()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		await File.WriteAllTextAsync(Path.Combine(folder, "script.sh"), "cat <<EOF\njoined \\\nline\nkept\\\\\nEOF\ncat <<'EOF'\nraw \\\nEOF\necho done\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(". ./script.sh\nexit\n", folder).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ joined line\nkept\\\nraw \\\ndone\n$ ");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that after <c>set -C</c>, or with the shell started as <c>sharpie -C</c>, <c>&gt;</c> won't replace a
	/// file while <c>&gt;|</c> and <c>&gt;&gt;</c> still write to it, and that <c>set +C</c> turns it off again.
	/// </summary>
	/// <param name="startOption">Whether the shell is started with <c>-C</c> instead of running <c>set -C</c>.</param>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Shell_WithNoClobber_KeepsFiles(bool startOption)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var setup = startOption ? string.Empty : "set -C\n";

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(
				$"{setup}echo a > f\necho b > f\necho $?\ncat f\necho c >| f\necho d >> f\necho e > /dev/null\ncat f\nset +C\necho g > f\ncat f\nexit\n",
				folder,
				arguments: startOption ? ["-C"] : null).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be($"{(startOption ? string.Empty : "$ ")}$ $ $ 1\n$ a\n$ $ $ $ c\nd\n$ $ $ g\n$ ");
			_ = run.Error.Should().StartWith("Error: sharpie::shell::redirect_failed\n\n  × Can't overwrite `f`\n");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>set -C</c> still holds for a command run in the background, both a builtin, which runs in a copy
	/// of the shell, and a program, which the shell starts itself.
	/// </summary>
	/// <param name="command">The background command, without its redirect.</param>
	[Theory]
	[InlineData("echo b")]
	[InlineData("env echo b")]
	public async Task Shell_WithNoClobberAndBackgroundCommand_KeepsFile(string command)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"set -C\necho a > f\n{command} > f &\nsleep 1\ncat f\nexit\n", folder).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().MatchRegex(@"(?m)^(\$ )*a$");
			_ = run.Error.Should().Contain("Can't overwrite `f`");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>3&gt;&amp;1 1&gt;&amp;2 2&gt;&amp;3</c> swaps a program's output and errors when they're the
	/// shell's own, which needs both read before either is replaced.
	/// </summary>
	[Fact]
	public async Task Shell_WithSwappedStreams_SwapsShellOutputAndErrors()
	{
		// Act
		var run = await ShellProcess.RunAsync("sh -c 'echo out; echo err >&2' 3>&1 1>&2 2>&3\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ err\n$ ");
		_ = run.Error.Should().Be("out\n");
	}

	/// <summary>
	/// Checks that a command of only a substitution and a redirect gives the substitution's status, as in bash.
	/// </summary>
	[Fact]
	public async Task Shell_WithOnlySubstitutionAndRedirect_GivesItsStatus()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, folder, arguments: ["-c", "$(exit 3) > f; echo $?"]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("3\n");
			_ = File.Exists(Path.Combine(folder, "f")).Should().BeTrue();
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a background program's redirect still gets its output after the prompt comes back.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundRedirect_WritesFile()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var file = Path.Combine(folder, "out.txt");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"sh -c 'sleep 0.2; echo later' > {file} &\nsleep 1\nexit\n").ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().StartWith("$ $ ");
			_ = (await File.ReadAllTextAsync(file).ConfigureAwait(true)).Should().Be("later\n");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a redirect that fails runs nothing, not even the stages whose own redirects opened.
	/// </summary>
	[Fact]
	public async Task Shell_WithFailedRedirectInPipeline_RunsNothing()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		try
		{
			// Act
			var run = await ShellProcess.RunAsync("echo ran > made.txt | cat < missing.txt\necho $?\nexit\n", folder).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ $ 1\n$ ");
			_ = run.Error.Should().StartWith("Error: sharpie::shell::redirect_failed\n\n  × Can't open `missing.txt` for reading\n");
			_ = (await File.ReadAllTextAsync(Path.Combine(folder, "made.txt")).ConfigureAwait(true)).Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
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
	/// Checks that a second shell can redirect into a file, and into <c>/dev/null</c>, while another shell still has
	/// them open through its own redirects, as bash allows.
	/// </summary>
	[Fact]
	public async Task Shell_WithFileOpenInAnotherShell_StillRedirects()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var file = Path.Combine(folder, "shared.txt");
		using var first = ShellProcess.Start(arguments: ["-c", $"sleep 5 > {file} 2>/dev/null"]);

		try
		{
			for (var tries = 0; tries < 100 && !File.Exists(file); tries++)
			{
				await Task.Delay(50).ConfigureAwait(true);
			}

			// Act
			var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", $"echo second >> {file} 2>/dev/null"]).ConfigureAwait(true);

			// Assert
			_ = run.Error.Should().BeEmpty();
			_ = run.ExitCode.Should().Be(0);
			_ = (await File.ReadAllTextAsync(file).ConfigureAwait(true)).Should().Be("second\n");
		}
		finally
		{
			first.Kill(entireProcessTree: true);
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
	[InlineData("echo a <>x", "<>", 7, "Sharpie can't run this operator yet")]
	[InlineData("echo a |& cat", "|&", 7, "Sharpie can't run this operator yet")]
	[InlineData("echo a >", ">", 7, "needs a file name after it")]
	[InlineData("echo a 2>&", "2>&", 7, "needs a stream number after it")]
	[InlineData("cat <<", "<<", 4, "needs a delimiter after it")]
	[InlineData("echo a 256>x", "256>", 7, "stream numbers go up to 255")]
	[InlineData("echo a | > f", "|", 7, "needs a command on both sides")]
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
	/// Checks that a <c>|</c> with no command on one side is a syntax error with status 2, pointing at the token after
	/// the gap, as bash's is.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="start">Where the token the error names starts.</param>
	/// <returns>A task that completes when the test is done.</returns>
	[Theory]
	[InlineData("echo a > f | | wc", 13)]
	[InlineData("| echo a", 0)]
	[InlineData("echo a | | wc", 9)]
	public async Task Shell_WithPipeMissingCommand_PrintsSyntaxErrorAndStatus2(string line, int start)
	{
		// Act
		var run = await ShellProcess.RunAsync($"{line}\necho $?\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ 2\n$ ");
		_ = run.Error.Should().Be(new ShellError("sharpie::parser::syntax", "Unexpected `|`")
		{
			Source = line,
			Start = start,
			End = start + 1,
			Label = "expected a command",
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

	/// <summary>
	/// Checks that <c>$(...)</c> and backquotes pass a command list's output as arguments, split outside quotes and
	/// with trailing newlines cut, whether the list runs a builtin, a program or a pipeline.
	/// </summary>
	/// <param name="command">The <c>-c</c> command.</param>
	/// <param name="expected">What it prints.</param>
	[Theory]
	[InlineData("echo $(echo hi)", "hi\n")]
	[InlineData("echo `echo hi`", "hi\n")]
	[InlineData("echo $(echo $(echo deep))", "deep\n")]
	[InlineData("printf '%s|' $(echo a b)", "a|b|")]
	[InlineData("echo \"$(printf 'a\\n\\n')\"x", "ax\n")]
	[InlineData("echo $(printf 'a\\nb\\n' | wc -l)", "2\n")]
	[InlineData("cd $(dirname /tmp/x) && pwd", "/tmp\n")]
	[InlineData("A=$(false); echo $?", "1\n")]
	[InlineData("A=$(exit 3); echo $? ok", "3 ok\n")]
	[InlineData("out=$(false) || echo failed", "failed\n")]
	[InlineData("false; echo $(exit 3) $?", "3\n")]
	[InlineData("echo $(true)#x", "#x\n")]
	[InlineData("echo $(echo a # )\n)", "a\n")]
	[InlineData("X=$(sh -c 'sleep 0.2; echo late' &); echo \"[$X]\"; jobs", "[late]\n")]
	[InlineData("X=$(echo hi &); echo \"[$X]\"", "[hi]\n")]
	[InlineData("set -e; X=$(false; echo hi); echo \"[$X]\"", "[hi]\n")]
	[InlineData("X=$(set -e; false; echo no); echo \"[$X] $?\"", "[] 1\n")]
	[InlineData("set -e; X=$(false); echo unreachable", "")]
	[InlineData("X=$(exec echo hi; echo no); echo \"[$X] $?\"", "[hi] 0\n")]
	[InlineData("X=$(set -u); echo $SHARPIE_SAMPLE_UNSET ok", "ok\n")]
	[InlineData("set -- p; X=$(set -- a b; shift); echo \"$# $1\"", "1 p\n")]
	[InlineData("umask; X=$(umask 077); umask", null)]
	[InlineData("dirs; X=$(pushd / > /dev/null); dirs", null)]
	public async Task Shell_WithCommandSubstitution_PassesOutput(string command, string? expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", command]).ConfigureAwait(true);

		// Assert
		// With no expected output, the two lines printed before and after the substitution must match.
		_ = run.Output.Should().Be(expected ?? string.Concat(Enumerable.Repeat(run.Output.Split('\n')[0] + "\n", 2)));
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that under <c>set -u</c> an unset variable ends only the substitution, with 1 as in bash, and that
	/// <c>exec</c> with only a redirect is refused there rather than moving the whole shell's output.
	/// </summary>
	/// <param name="command">The <c>-c</c> command.</param>
	/// <param name="expected">What it prints.</param>
	/// <param name="code">The error the substitution reports.</param>
	[Theory]
	[InlineData("set -u; X=$(echo $SHARPIE_SAMPLE_UNSET; echo after); echo \"[$X] $?\"", "[] 1\n", "sharpie::shell::unset_variable")]
	[InlineData("X=$(exec > /dev/null; echo a); echo \"[$X] $?\"", "[a] 0\n", "sharpie::exec::in_substitution")]
	public async Task Shell_WithErrorInSubstitution_EndsOnlyIt(string command, string expected, string code)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", command]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
		_ = run.Error.Should().Contain(code);
	}

	/// <summary>
	/// Checks that a builtin's output inside a substitution is captured too, not printed.
	/// </summary>
	[Fact]
	public async Task Shell_WithBuiltinInSubstitution_CapturesIt()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, folder, arguments: ["-c", "echo [$(pwd)]"]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be($"[{folder}]\n");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that the folder, variables, environment and aliases a substitution changes are put back after it, as
	/// a subshell's would be.
	/// </summary>
	[Fact]
	public async Task Shell_WithStateChangedInSubstitution_PutsItBack()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, folder, arguments: ["-c", "X=$(cd /; B=1; export C=2; alias q=x; pwd); pwd; echo \"$X[$B][$C]\"; type q"]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be($"{folder}\n/[][]\nq: not found\n");
			_ = run.ExitCode.Should().Be(1);
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that an exported variable a substitution changes gets its value back, and one with an empty value it
	/// doesn't touch is still there.
	/// </summary>
	[Fact]
	public async Task Shell_WithExportedVariables_KeepsThemAfterSubstitution()
	{
		// Arrange
		var environment = new Dictionary<string, string> { ["SHARPIE_SAMPLE_EMPTY"] = string.Empty, ["SHARPIE_SAMPLE_SET"] = "old" };

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, environment: environment, arguments: ["-c", "X=$(export SHARPIE_SAMPLE_SET=new); env | grep SHARPIE_SAMPLE_ | sort"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("SHARPIE_SAMPLE_EMPTY=\nSHARPIE_SAMPLE_SET=old\n");
	}

	/// <summary>
	/// Checks that a program run by <c>. file</c> inside a substitution writes into the substitution, not the terminal.
	/// </summary>
	[Fact]
	public async Task Shell_WithSourcedProgramInSubstitution_CapturesIt()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		await File.WriteAllTextAsync(Path.Combine(folder, "file"), "printf hi\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, folder, arguments: ["-c", "X=$(. ./file); echo \"[$X]\""]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("[hi]\n");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a program run by <c>. file</c> writes into the file that <c>&gt;</c> sends the builtin's output to.
	/// </summary>
	[Fact]
	public async Task Shell_WithSourcedProgramRedirected_WritesFile()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		await File.WriteAllTextAsync(Path.Combine(folder, "file"), "printf hi\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, folder, arguments: ["-c", ". ./file > out"]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().BeEmpty();
			_ = (await File.ReadAllTextAsync(Path.Combine(folder, "out")).ConfigureAwait(true)).Should().Be("hi");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a background program inside a substitution that sends its output to a file isn't waited for.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundToFileInSubstitution_DoesNotWait()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, folder, arguments: ["-c", "X=$(sleep 5 > out 2> err &); echo done"]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("done\n");
			_ = stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a background program a substitution started is collected once it ends, not left as a zombie.
	/// </summary>
	[Fact]
	public async Task Shell_WithFinishedBackgroundInSubstitution_LeavesNoZombie()
	{
		// Arrange
		// ps --ppid is the Linux ps.
		if (!OperatingSystem.IsLinux())
		{
			return;
		}

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "X=$(true &); sleep 0.5; sh -c 'ps -o stat= --ppid $PPID'"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().NotContain("Z");
		_ = run.Output.Should().NotBeEmpty();
	}

	/// <summary>
	/// Checks that a sourced file's substitution and quote can each span several lines.
	/// </summary>
	[Fact]
	public async Task Shell_WithMultiLineSubstitutionInFile_RunsIt()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		await File.WriteAllTextAsync(Path.Combine(folder, "file"), "X=$(echo a\necho b)\necho \"[$X]\" 'c\nd'\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, folder, arguments: ["-c", ". ./file"]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("[a\nb] c\nd\n");
			_ = run.Error.Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a substitution running a missing command reports it on standard error and gives 127.
	/// </summary>
	[Fact]
	public async Task Shell_WithMissingCommandInSubstitution_ReportsNotFound()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "$(sampletool)"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().BeEmpty();
		_ = run.Error.Should().Contain("sharpie::shell::command_not_found").And.Contain("sampletool");
		_ = run.ExitCode.Should().Be(127);
	}

	/// <summary>
	/// Checks that an unquoted variable passes each of its words as its own argument, as bash does.
	/// </summary>
	[Fact]
	public async Task Shell_WithUnquotedVariable_SplitsIntoArguments()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "F=\"-l -a\"; printf '[%s]' $F"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("[-l][-a]");
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a redirect to a variable that splits into two words is refused as ambiguous, failing only that
	/// command with 1, as bash does, and creating no file.
	/// </summary>
	[Fact]
	public async Task Shell_WithAmbiguousRedirect_FailsOnlyThatCommand()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", $"cd {CommandLine.Quote(folder)}; F=\"a b\"; echo hi > $F; echo \"status $?\""]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("status 1\n");
			_ = run.Error.Should().Contain("ambiguous redirect").And.Contain("$F");
			_ = Directory.GetFileSystemEntries(folder).Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>${X:?message}</c> with <c>X</c> unset reports the message and ends a script with 127, as bash
	/// does, running nothing after it.
	/// </summary>
	[Fact]
	public async Task Shell_WithRequiredParameterUnset_ExitsWith127()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "echo ${SHARPIE_SAMPLE_UNSET:?gone}; echo after"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().BeEmpty();
		_ = run.Error.Should().Contain("sharpie::shell::parameter_unset").And.Contain("SHARPIE_SAMPLE_UNSET: gone");
		_ = run.ExitCode.Should().Be(127);
	}

	/// <summary>
	/// Checks that <c>${X:=value}</c> leaves the value in <c>X</c> for the commands after it.
	/// </summary>
	[Fact]
	public async Task Shell_WithAssigningDefault_KeepsValue()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", ": ${SHARPIE_SAMPLE_UNSET:=v}; echo $SHARPIE_SAMPLE_UNSET"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("v\n");
		_ = run.Error.Should().BeEmpty();
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that in a script an expansion error other than <c>${X:?}</c> skips only the rest of its line, even from
	/// inside a function, and that <c>${X:?}</c> in a subshell ends only the subshell, as in bash.
	/// </summary>
	[Fact]
	public async Task Shell_WithExpansionErrorsInScript_SkipsRestOfLine()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var script = ShellEnvironment.CreateFile(folder, "sample.sh", executable: false, "X=abc\necho ${X:1:-9}; echo same\necho next $?\n"
			+ "f() { echo ${X:1:-9}; echo inf; }; f; echo afterf\n( : ${SHARPIE_SAMPLE_UNSET:?boom}; echo in ); echo after $?\n");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, arguments: [script]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("next 1\nafter 1\n");
			_ = run.Error.Should().Contain("-9: substring expression < 0").And.Contain("SHARPIE_SAMPLE_UNSET: boom");
			_ = run.ExitCode.Should().Be(0);
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}
}
