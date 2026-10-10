using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests;

/// <summary>
/// Runs the built shell as a real process with piped standard streams, the way the terminal window drives it.
/// </summary>
public sealed class ShellProcessTests
{
	/// <summary>
	/// The shell's launcher, which the build copies into this test's output folder because the shell is an Exe
	/// project reference.
	/// </summary>
	private static readonly string ShellPath = Path.Combine(AppContext.BaseDirectory, "Sharpie.Shell");

	/// <summary>
	/// Checks that commands piped to standard input run in order, each after a <c>$ </c> prompt, and that <c>exit</c>
	/// ends the process with code 0.
	/// </summary>
	[Fact]
	public void Shell_WithPipedCommands_PrintsPromptsAndOutputThenExits()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write("echo hi\nnosuchcommand\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

		// Assert
		_ = output.Should().Be("$ hi\n$ nosuchcommand: command not found\n$ ");
		_ = exited.Should().BeTrue();
		_ = shell.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that an empty file name after a redirect or a history flag prints an error and the shell keeps running,
	/// instead of ending on the exception .NET throws for an empty path.
	/// </summary>
	/// <param name="line">A command line with an empty file name in it.</param>
	[Theory]
	[InlineData("echo hi > \"\"")]
	[InlineData("history -r \"\"")]
	[InlineData("history -w \"\"")]
	public async Task Shell_WithEmptyFileName_KeepsRunning(string line)
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		await shell.StandardInput.WriteAsync($"{line}\necho after\nexit\n").ConfigureAwait(true);

		var errors = shell.StandardError.ReadToEndAsync();
		var output = await shell.StandardOutput.ReadToEndAsync().ConfigureAwait(true);
		var exited = await ExitsWithinAsync(shell, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

		// Assert
		_ = output.Should().EndWith("$ after\n$ ");
		_ = (await errors.ConfigureAwait(true)).Should().NotBeEmpty();
		_ = exited.Should().BeTrue();
		_ = shell.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that a program started with a trailing <c>&amp;</c> writes to the shell's own standard output instead
	/// of a pipe the shell never reads.
	/// </summary>
	[Fact]
	public void Shell_WithBackgroundJob_JobOutputReachesShellOutput()
	{
		// Arrange
		var file = Path.GetTempFileName();
		File.WriteAllText(file, "Hello from background\n");
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write($"cat {file} &\nexit\n");

		// ReadToEnd waits for every writer of the pipe to close, the background cat included, so the job has
		// finished by the time it returns.
		var output = shell.StandardOutput.ReadToEnd();
		_ = shell.WaitForExit(TimeSpan.FromSeconds(10));
		File.Delete(file);

		// Assert
		_ = output.Should().Contain("Hello from background\n");
	}

	/// <summary>
	/// Checks that <c>jobs</c> lists a program started with <c>&amp;</c> as job 1, running, with its command line.
	/// </summary>
	[Fact]
	public void Shell_WithBackgroundSleep_JobsListsIt()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		// Short enough that ReadToEnd, which also waits for sleep to let go of the pipe, doesn't hold up the run.
		shell.StandardInput.Write("sleep 1 &\njobs\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		_ = shell.WaitForExit(TimeSpan.FromSeconds(10));

		// Assert
		_ = output.Should().MatchRegex(@"^\$ \[1\] \d+\n\$ \[1\]\+  Running                 sleep 1 &\n\$ $");
	}

	/// <summary>
	/// Checks that with the window's environment variable set, each prompt is preceded by the OSC 7 sequence carrying
	/// the working directory, so the window can show it.
	/// </summary>
	[Fact]
	public void Shell_WithWindowVariable_ReportsDirectoryBeforePrompt()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-cwd-").FullName;
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			WorkingDirectory = folder,

			// An empty folder as PATH keeps an installed fastfetch from printing its banner first.
			// The window variable turns on saved history, so the data folder moves into the temporary folder and
			// the session bus is dropped. That way the test never writes to the user's real history or keyring.
			Environment =
			{
				[Program.WindowVariable] = "1", ["PATH"] = folder, ["XDG_DATA_HOME"] = folder,
                // Removing the variable isn't enough, because D-Bus then falls back to $XDG_RUNTIME_DIR/bus.
                ["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={folder}/no-bus",
			},
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write("exit\n");

		var output = shell.StandardOutput.ReadToEnd();
		_ = shell.WaitForExit(TimeSpan.FromSeconds(10));
		Directory.Delete(folder, recursive: true);

		// Assert
		_ = output.Should().Be($"\e]7;{new Uri(folder).AbsoluteUri}\a$ ");
	}

	/// <summary>
	/// Checks that with the window's environment variable set and a <c>fastfetch</c> on <c>PATH</c>, the shell runs it
	/// once before the first prompt.
	/// </summary>
	[Fact]
	public void Shell_WithWindowVariableAndFastfetchOnPath_PrintsBannerBeforePrompt()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-fastfetch-").FullName;
		var fastfetch = Path.Combine(folder, "fastfetch");
		File.WriteAllText(fastfetch, "#!/bin/sh\necho banner\n");
		File.SetUnixFileMode(fastfetch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			WorkingDirectory = folder,
			// The window variable turns on saved history, so the data folder moves into the temporary folder and
			// the session bus is dropped. That way the test never writes to the user's real history or keyring.
			Environment =
			{
				[Program.WindowVariable] = "1", ["PATH"] = folder, ["XDG_DATA_HOME"] = folder,
                // Removing the variable isn't enough, because D-Bus then falls back to $XDG_RUNTIME_DIR/bus.
                ["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={folder}/no-bus",
			},
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write("exit\n");

		var output = shell.StandardOutput.ReadToEnd();
		_ = shell.WaitForExit(TimeSpan.FromSeconds(10));
		Directory.Delete(folder, recursive: true);

		// Assert
		_ = output.Should().Be($"banner\n\e]7;{new Uri(folder).AbsoluteUri}\a$ ");
	}

	/// <summary>
	/// Checks that <c>clear</c> runs as a builtin with piped output, printing its escape sequence instead of failing
	/// the way <see cref="Console.Clear" /> would.
	/// </summary>
	[Fact]
	public void Shell_WithClearPiped_PrintsClearSequenceThenExits()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write("clear\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

		// Assert
		_ = output.Should().Be("$ \e[H\e[2J\e[3J$ ");
		_ = exited.Should().BeTrue();
		_ = shell.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>complete</c> is registered with the running shell, so <c>type</c> reports it as a builtin.
	/// </summary>
	[Fact]
	public void Shell_WithTypeComplete_ReportsShellBuiltin()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write("type complete\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

		// Assert
		_ = output.Should().Be("$ complete is a shell builtin\n$ ");
		_ = exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>jobs</c> is registered with the running shell and prints nothing when no jobs are running.
	/// </summary>
	[Fact]
	public void Shell_WithTypeJobsAndJobs_ReportsBuiltinAndPrintsNothing()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write("type jobs\njobs\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

		// Assert
		_ = output.Should().Be("$ jobs is a shell builtin\n$ $ ");
		_ = exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>declare</c> is registered with the running shell.
	/// </summary>
	[Fact]
	public void Shell_WithTypeDeclare_ReportsBuiltin()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write("type declare\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

		// Assert
		_ = output.Should().Be("$ declare is a shell builtin\n$ ");
		_ = exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>declare -p</c> on a name that was never set prints a not found error to standard error.
	/// </summary>
	[Fact]
	public async Task Shell_WithDeclarePMissing_ReportsNotFound()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		await shell.StandardInput.WriteAsync("declare -p missing_variable\nexit\n").ConfigureAwait(true);

		var errorTask = shell.StandardError.ReadToEndAsync();
		var output = await shell.StandardOutput.ReadToEndAsync().ConfigureAwait(true);
		var error = await errorTask.ConfigureAwait(true);
		var exited = await ExitsWithinAsync(shell, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

		// Assert
		_ = error.Should().Be("declare: missing_variable: not found\n");
		_ = output.Should().Be("$ $ ");
		_ = exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>declare NAME=VALUE</c> stores a variable that <c>declare -p</c> prints, and that declaring it
	/// again replaces the value.
	/// </summary>
	[Fact]
	public async Task Shell_WithDeclaredVariable_PrintsAndOverwrites()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		await shell.StandardInput.WriteAsync(
			"declare foo=bar\ndeclare -p foo\ndeclare -p missing_variable\ndeclare foo=bar2\ndeclare -p foo\nexit\n").ConfigureAwait(true);

		var errorTask = shell.StandardError.ReadToEndAsync();
		var output = await shell.StandardOutput.ReadToEndAsync().ConfigureAwait(true);
		var error = await errorTask.ConfigureAwait(true);
		var exited = await ExitsWithinAsync(shell, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

		// Assert
		_ = output.Should().Be("$ $ declare -- foo=\"bar\"\n$ $ $ declare -- foo=\"bar2\"\n$ ");
		_ = error.Should().Be("declare: missing_variable: not found\n");
		_ = exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>history</c> is a builtin and that a run outside the window lists only its own lines, never history saved
	/// by an earlier session.
	/// </summary>
	[Fact]
	public void Shell_WithTypeHistoryAndHistory_ReportsBuiltinAndListsSessionLines()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write("type history\nhistory\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

		// Assert
		_ = output.Should().Be("$ history is a shell builtin\n$     1  type history\n    2  history\n$ ");
		_ = exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that the file named by <c>HISTFILE</c> is loaded at startup, before the session's own lines, with its
	/// trailing blank line skipped.
	/// </summary>
	[Fact]
	public void Shell_WithHistFile_LoadsFileLinesBeforeSessionLines()
	{
		// Arrange
		var path = Path.GetTempFileName();
		File.WriteAllText(path, "echo hello\necho world\n\n");
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			Environment = { ["HISTFILE"] = path },
		};

		try
		{
			// Act
			using var shell = Process.Start(startInfo)!;

			shell.StandardInput.Write("history\nexit\n");

			var output = shell.StandardOutput.ReadToEnd();
			var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

			// Assert
			_ = output.Should().Be("$     1  echo hello\n    2  echo world\n    3  history\n$ ");
			_ = exited.Should().BeTrue();
		}
		finally
		{
			File.Delete(path);
		}
	}

	/// <summary>
	/// Checks that the shell writes its history to the file named by <c>HISTFILE</c> when it exits, after the file's
	/// own lines, with a trailing newline.
	/// </summary>
	/// <param name="existing">The file's contents before the shell starts, or <see langword="null" /> for no file.</param>
	/// <param name="input">The lines sent to the shell. Input without <c>exit</c> ends when standard input closes.</param>
	/// <param name="expected">The file's contents after the shell exits.</param>
	[Theory]
	[InlineData(null, "echo hello\necho world\nexit\n", "echo hello\necho world\nexit\n")]
	[InlineData("echo old\n", "echo new\nexit\n", "echo old\necho new\nexit\n")]
	[InlineData(null, "echo hi\n", "echo hi\n")]
	public void Shell_WithHistFile_WritesHistoryOnExit(string? existing, string input, string expected)
	{
		// Arrange
		var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
		if (existing is not null)
		{
			File.WriteAllText(path, existing);
		}

		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			Environment = { ["HISTFILE"] = path },
		};

		try
		{
			// Act
			using var shell = Process.Start(startInfo)!;

			shell.StandardInput.Write(input);
			shell.StandardInput.Close();

			_ = shell.StandardOutput.ReadToEnd();
			var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

			// Assert
			_ = exited.Should().BeTrue();
			_ = File.ReadAllText(path).Should().Be(expected);
		}
		finally
		{
			File.Delete(path);
		}
	}

	/// <summary>
	/// Checks that <c>history -w</c> creates the file and writes every session line to it, including its own, with a
	/// trailing newline.
	/// </summary>
	[Fact]
	public void Shell_WithHistoryWrite_WritesSessionLinesToFile()
	{
		// Arrange
		var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		try
		{
			// Act
			using var shell = Process.Start(startInfo)!;

			shell.StandardInput.Write($"echo hello\necho world\nhistory -w {path}\nexit\n");

			_ = shell.StandardOutput.ReadToEnd();
			var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

			// Assert
			_ = File.ReadAllText(path).Should().Be($"echo hello\necho world\nhistory -w {path}\n");
			_ = exited.Should().BeTrue();
		}
		finally
		{
			File.Delete(path);
		}
	}

	/// <summary>
	/// Checks that <c>history -a</c> keeps the file's existing lines and that a second <c>-a</c> appends only the
	/// lines run since the first.
	/// </summary>
	[Fact]
	public void Shell_WithHistoryAppendTwice_AppendsOnlyNewLines()
	{
		// Arrange
		var path = Path.GetTempFileName();
		File.WriteAllText(path, "echo initial_command_1\necho initial_command_2\n");
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		try
		{
			// Act
			using var shell = Process.Start(startInfo)!;

			shell.StandardInput.Write($"echo new_command\nhistory -a {path}\necho second\nhistory -a {path}\nexit\n");

			_ = shell.StandardOutput.ReadToEnd();
			var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

			// Assert
			_ = File.ReadAllText(path).Should().Be(
				$"echo initial_command_1\necho initial_command_2\necho new_command\nhistory -a {path}\n" +
				$"echo second\nhistory -a {path}\n");
			_ = exited.Should().BeTrue();
		}
		finally
		{
			File.Delete(path);
		}
	}

	/// <summary>
	/// Checks that single-quoted text reaches <c>echo</c> as one argument with its spaces kept.
	/// </summary>
	[Fact]
	public void Shell_WithSingleQuotedEcho_KeepsSpaces()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write("echo 'world     test'\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

		// Assert
		_ = output.Should().Be("$ world     test\n$ ");
		_ = exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that SIGINT, what Ctrl+C sends, stops the running program but leaves the shell running the next
	/// command.
	/// </summary>
	[Fact]
	public async Task Shell_WhenInterruptedDuringProgram_StopsProgramAndKeepsRunning()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		await shell.StandardInput.WriteAsync("sleep 30\necho done\nexit\n").ConfigureAwait(true);

		// Gives the shell time to start sleep before the signal arrives.
		await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
		ExternalProgram.SendInterrupt(shell.Id);

		var output = shell.StandardOutput.ReadToEndAsync();
		var exited = await ExitsWithinAsync(shell, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

		// Assert
		_ = exited.Should().BeTrue();
		_ = shell.ExitCode.Should().Be(0);
		_ = (await output.ConfigureAwait(true)).Should().Contain("done");
	}

	/// <summary>
	/// Checks that the shell ends with code 0 when standard input closes without an <c>exit</c> command.
	/// </summary>
	[Fact]
	public void Shell_WhenInputClosesWithoutExit_Exits()
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write("echo hi\n");
		shell.StandardInput.Close();

		var output = shell.StandardOutput.ReadToEnd();
		var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));

		// Assert
		_ = output.Should().Be("$ hi\n$ ");
		_ = exited.Should().BeTrue();
		_ = shell.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>&gt;</c> and <c>1&gt;</c> send a builtin's output to a file, replacing what was there, and
	/// that only the prompts reach the terminal.
	/// </summary>
	/// <param name="operatorText">The redirect operator to use.</param>
	[Theory]
	[InlineData(">")]
	[InlineData("1>")]
	public void Shell_WithEchoRedirected_WritesFileAndOverwrites(string operatorText)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var file = Path.Combine(folder, "foo.md");
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		try
		{
			// Act
			using var shell = Process.Start(startInfo)!;

			shell.StandardInput.Write($"echo old {operatorText} {file}\necho Hello James {operatorText} {file}\nexit\n");

			var output = shell.StandardOutput.ReadToEnd();

			_ = shell.WaitForExit(TimeSpan.FromSeconds(10));

			// Assert
			_ = output.Should().Be("$ $ $ ");
			_ = File.ReadAllText(file).Should().Be("Hello James\n");
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
	public void Shell_WithEchoAppended_KeepsExistingContent(string operatorText)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var existing = Path.Combine(folder, "qux.md");
		var created = Path.Combine(folder, "new.md");
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		try
		{
			// Act
			using var shell = Process.Start(startInfo)!;

			shell.StandardInput.Write(
				$"echo first > {existing}\necho second {operatorText} {existing}\necho third {operatorText} {created}\nexit\n");

			var output = shell.StandardOutput.ReadToEnd();

			_ = shell.WaitForExit(TimeSpan.FromSeconds(10));

			// Assert
			_ = output.Should().Be("$ $ $ $ ");
			_ = File.ReadAllText(existing).Should().Be("first\nsecond\n");
			_ = File.ReadAllText(created).Should().Be("third\n");
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

		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = folder,
		};

		try
		{
			// Act
			using var shell = Process.Start(startInfo)!;

			await shell.StandardInput.WriteAsync($"cat {source} nonexistent 1> {file}\nexit\n").ConfigureAwait(true);

			var errors = shell.StandardError.ReadToEndAsync();
			var output = await shell.StandardOutput.ReadToEndAsync().ConfigureAwait(true);

			_ = await ExitsWithinAsync(shell, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

			// Assert
			_ = output.Should().Be("$ $ ");
			_ = (await errors.ConfigureAwait(true)).Should().Contain("nonexistent: No such file or directory");
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
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = folder,
		};

		try
		{
			// Act
			using var shell = Process.Start(startInfo)!;

			await shell.StandardInput.WriteAsync($"cat first 2>> {file}\ncat second 2>> {file}\nexit\n").ConfigureAwait(true);

			var errors = shell.StandardError.ReadToEndAsync();
			var output = await shell.StandardOutput.ReadToEndAsync().ConfigureAwait(true);

			_ = await ExitsWithinAsync(shell, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

			// Assert
			_ = output.Should().Be("$ $ $ ");
			_ = (await errors.ConfigureAwait(true)).Should().BeEmpty();
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

		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = folder,
		};

		try
		{
			// Act
			using var shell = Process.Start(startInfo)!;

			await shell.StandardInput.WriteAsync($"cat {source} nonexistent 2> {file}\nexit\n").ConfigureAwait(true);

			var errors = shell.StandardError.ReadToEndAsync();
			var output = await shell.StandardOutput.ReadToEndAsync().ConfigureAwait(true);

			_ = await ExitsWithinAsync(shell, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

			// Assert
			_ = output.Should().Be("$ pear\n$ ");
			_ = (await errors.ConfigureAwait(true)).Should().BeEmpty();
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
	public void Shell_WithEchoStderrRedirected_PrintsOutputAndCreatesEmptyFile()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var file = Path.Combine(folder, "foo.md");
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		try
		{
			// Act
			using var shell = Process.Start(startInfo)!;

			shell.StandardInput.Write($"echo Maria file cannot be found 2> {file}\nexit\n");

			var output = shell.StandardOutput.ReadToEnd();

			_ = shell.WaitForExit(TimeSpan.FromSeconds(10));

			// Assert
			_ = output.Should().Be("$ Maria file cannot be found\n$ ");
			_ = File.ReadAllText(file).Should().BeEmpty();
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
	public void Shell_WithPipeline_FeedsFirstOutputToSecond()
	{
		// Arrange
		var file = Path.GetTempFileName();
		File.WriteAllText(file, "one\ntwo\n");
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write($"cat {file} | wc -l\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		_ = shell.WaitForExit(TimeSpan.FromSeconds(10));
		File.Delete(file);

		// Assert
		_ = output.Should().Be("$ 2\n$ ");
	}

	/// <summary>
	/// Checks that a pipeline whose first program never ends on its own, like <c>tail -f</c>, still returns to the
	/// prompt once the second program exits.
	/// </summary>
	[Fact]
	public void Shell_WithTailFollowIntoHead_ReturnsToPrompt()
	{
		// Arrange
		var file = Path.GetTempFileName();
		File.WriteAllText(file, "first\nsecond\n");
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write($"tail -f {file} | head -n 2\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		var exited = shell.WaitForExit(TimeSpan.FromSeconds(10));
		File.Delete(file);

		// Assert
		_ = output.Should().Be("$ first\nsecond\n$ ");
		_ = exited.Should().BeTrue();
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
	public void Shell_WithBuiltinInPipeline_PrintsPipelineOutput(string line, string expected)
	{
		// Arrange
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write($"{line}\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		_ = shell.WaitForExit(TimeSpan.FromSeconds(10));

		// Assert
		_ = output.Should().Be($"$ {expected}$ ");
	}

	/// <summary>
	/// Checks that a pipeline of three or more programs passes each program's output on to the next.
	/// </summary>
	/// <param name="pipeline">The pipeline to run, with <c>{0}</c> standing for a file holding five lines.</param>
	/// <param name="expected">What the last program should print.</param>
	[Theory]
	[InlineData("cat {0} | head -n 3 | wc -l", "3\n")]
	[InlineData("cat {0} | tail -n 4 | head -n 2 | grep x", "two-x\n")]
	public void Shell_WithMultiStagePipeline_ChainsOutput(string pipeline, string expected)
	{
		// Arrange
		var file = Path.GetTempFileName();
		File.WriteAllText(file, "one-x\ntwo-x\nthree\nfour-x\nfive\n");
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
		};

		// Act
		using var shell = Process.Start(startInfo)!;

		shell.StandardInput.Write($"{string.Format(CultureInfo.InvariantCulture, pipeline, file)}\nexit\n");

		var output = shell.StandardOutput.ReadToEnd();
		_ = shell.WaitForExit(TimeSpan.FromSeconds(10));
		File.Delete(file);

		// Assert
		_ = output.Should().Be($"$ {expected}$ ");
	}

	/// <summary>
	/// Waits for <paramref name="process" /> to end, but no longer than <paramref name="timeout" />.
	/// </summary>
	/// <param name="process">The shell process.</param>
	/// <param name="timeout">How long to wait.</param>
	/// <returns>
	/// <see langword="true" /> when the process ended in time.
	/// </returns>
	private static async Task<bool> ExitsWithinAsync(Process process, TimeSpan timeout)
	{
		using var cancellation = new CancellationTokenSource(timeout);

		try
		{
			await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(true);
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
	}
}
