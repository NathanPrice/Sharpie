using FluentAssertions;
using Sharpie.Shell.Features.History;

namespace Sharpie.Shell.Tests.Features.History;

/// <summary>
/// Runs <see cref="HistoryCommand" /> and the <c>HISTFILE</c> file through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class HistoryProcessTests
{
	/// <summary>
	/// Checks that <c>history</c> is a builtin and that a run outside the window lists only its own lines, never history saved
	/// by an earlier session.
	/// </summary>
	[Fact]
	public async Task Shell_WithTypeHistoryAndHistory_ReportsBuiltinAndListsSessionLines()
	{
		// Act
		var run = await ShellProcess.RunAsync("type history\nhistory\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ history is a shell builtin\n$     1  type history\n    2  history\n$ ");
		_ = run.Exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that the file named by <c>HISTFILE</c> is loaded at startup, before the session's own lines, with its
	/// trailing blank line skipped.
	/// </summary>
	[Fact]
	public async Task Shell_WithHistFile_LoadsFileLinesBeforeSessionLines()
	{
		// Arrange
		var path = Path.GetTempFileName();
		await File.WriteAllTextAsync(path, "echo hello\necho world\n\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync("history\nexit\n", environment: HistFile(path)).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$     1  echo hello\n    2  echo world\n    3  history\n$ ");
			_ = run.Exited.Should().BeTrue();
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
	public async Task Shell_WithHistFile_WritesHistoryOnExit(string? existing, string input, string expected)
	{
		// Arrange
		var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
		if (existing is not null)
		{
			await File.WriteAllTextAsync(path, existing).ConfigureAwait(true);
		}

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(input, environment: HistFile(path)).ConfigureAwait(true);

			// Assert
			_ = run.Exited.Should().BeTrue();
			_ = (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be(expected);
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
	public async Task Shell_WithHistoryWrite_WritesSessionLinesToFile()
	{
		// Arrange
		var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"echo hello\necho world\nhistory -w {path}\nexit\n").ConfigureAwait(true);

			// Assert
			_ = (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be($"echo hello\necho world\nhistory -w {path}\n");
			_ = run.Exited.Should().BeTrue();
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
	public async Task Shell_WithHistoryAppendTwice_AppendsOnlyNewLines()
	{
		// Arrange
		var path = Path.GetTempFileName();
		await File.WriteAllTextAsync(path, "echo initial_command_1\necho initial_command_2\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"echo new_command\nhistory -a {path}\necho second\nhistory -a {path}\nexit\n")
				.ConfigureAwait(true);

			// Assert
			_ = (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be(
				$"echo initial_command_1\necho initial_command_2\necho new_command\nhistory -a {path}\n" +
				$"echo second\nhistory -a {path}\n");
			_ = run.Exited.Should().BeTrue();
		}
		finally
		{
			File.Delete(path);
		}
	}

	/// <summary>
	/// Checks that an interactive shell runs <c>!!</c> and <c>!$</c> as the lines they name, prints each expanded line on
	/// standard error, and saves the expanded lines to history, as bash does.
	/// </summary>
	[Fact]
	public async Task Shell_InteractiveWithHistoryReferences_RunsAndSavesExpandedLines()
	{
		// Act
		var run = await ShellProcess.RunAsync("echo one two\n!!\necho !$\nhistory\n", arguments: ["-i", "--norc"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(
			"$ one two\n$ one two\n$ two\n$     1  echo one two\n    2  echo one two\n    3  echo two\n    4  history\n$ ");
		_ = run.Error.Should().Be("echo one two\necho two\n");
	}

	/// <summary>
	/// Checks that a reference naming no earlier line reports the error, runs nothing, keeps <c>$?</c> and isn't
	/// saved to history.
	/// </summary>
	[Fact]
	public async Task Shell_InteractiveWithMissingEvent_ReportsErrorAndSkipsLine()
	{
		// Act
		var run = await ShellProcess.RunAsync("false\necho !zzz\necho $?\nhistory\n", arguments: ["-i", "--norc"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ $ 1\n$     1  false\n    2  echo $?\n    3  history\n$ ");
		_ = run.Error.Should().Contain("sharpie::history::event_not_found");
	}

	/// <summary>
	/// Checks that <c>!!</c> stays as typed after <c>set +H</c>, and in a shell that isn't interactive.
	/// </summary>
	/// <param name="input">The lines sent to the shell.</param>
	/// <param name="arguments">The shell's arguments.</param>
	/// <param name="expected">The expected output.</param>
	[Theory]
	[InlineData("set +H\necho hi\necho !!\n", new[] { "-i", "--norc" }, "$ $ hi\n$ !!\n$ ")]
	[InlineData("echo hi\necho !!\n", new string[0], "$ hi\n$ !!\n$ ")]
	public async Task Shell_WithHistExpandOff_LeavesReferencesAsTyped(string input, string[] arguments, string expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(input, arguments: arguments).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Builds an environment that points <c>HISTFILE</c> at <paramref name="path" />.
	/// </summary>
	/// <param name="path">The history file the shell should load and save.</param>
	/// <returns>
	/// The variables to set on the shell process.
	/// </returns>
	private static Dictionary<string, string> HistFile(string path) => new() { ["HISTFILE"] = path };
}
