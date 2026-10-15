using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests;

/// <summary>
/// Runs the built shell as a real process with piped standard streams, the way the terminal window drives it.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class ShellProcessTests
{
	/// <summary>
	/// Checks that commands piped to standard input run in order, each after a <c>$ </c> prompt, and that <c>exit</c>
	/// ends the process with the last command's status, which is 127 for one that wasn't found.
	/// </summary>
	[Fact]
	public async Task Shell_WithPipedCommands_PrintsPromptsAndOutputThenExits()
	{
		// Act
		var run = await ShellProcess.RunAsync("echo hi\nnosuchcommand\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ hi\n$ $ ");
		_ = run.Error.Should().Be(new ShellError("sharpie::shell::command_not_found", "Command `nosuchcommand` not found")
		{
			Source = "nosuchcommand",
			End = "nosuchcommand".Length,
			Label = "not a builtin, alias or program on PATH",
		}.Render(color: false));
		_ = run.Exited.Should().BeTrue();
		_ = run.ExitCode.Should().Be(127);
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
		// Act
		var run = await ShellProcess.RunAsync($"{line}\necho after\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().EndWith("$ after\n$ ");
		_ = run.Error.Should().NotBeEmpty();
		_ = run.Exited.Should().BeTrue();
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that with the window's environment variable set, each prompt is preceded by the OSC 7 sequence carrying
	/// the working directory, so the window can show it.
	/// </summary>
	[Fact]
	public async Task Shell_WithWindowVariable_ReportsDirectoryBeforePrompt()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-cwd-").FullName;

		try
		{
			// Act
			var run = await ShellProcess.RunAsync("exit\n", folder, WindowEnvironment(folder)).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be($"\e]7;{new Uri(folder).AbsoluteUri}\a$ ");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that with the window's environment variable set and a <c>fastfetch</c> on <c>PATH</c>, the shell runs it
	/// once before the first prompt.
	/// </summary>
	[Fact]
	public async Task Shell_WithWindowVariableAndFastfetchOnPath_PrintsBannerBeforePrompt()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-fastfetch-").FullName;
		var fastfetch = Path.Combine(folder, "fastfetch");
		await File.WriteAllTextAsync(fastfetch, "#!/bin/sh\necho banner\n").ConfigureAwait(true);
		File.SetUnixFileMode(fastfetch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync("exit\n", folder, WindowEnvironment(folder)).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be($"banner\n\e]7;{new Uri(folder).AbsoluteUri}\a$ ");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that SIGINT, what Ctrl+C sends, stops the running program but leaves the shell running the next
	/// command.
	/// </summary>
	[Fact]
	public async Task Shell_WhenInterruptedDuringProgram_StopsProgramAndKeepsRunning()
	{
		// Act
		using var shell = ShellProcess.Start();

		await shell.StandardInput.WriteAsync("sleep 30\necho done\nexit\n").ConfigureAwait(true);

		// Gives the shell time to start sleep before the signal arrives.
		await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
		ExternalProgram.SendInterrupt(shell.Id);

		var output = shell.StandardOutput.ReadToEndAsync();
		var exited = await ShellProcess.ExitsWithinAsync(shell, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

		// Assert
		_ = exited.Should().BeTrue();
		_ = shell.ExitCode.Should().Be(0);
		_ = (await output.ConfigureAwait(true)).Should().Contain("done");
	}

	/// <summary>
	/// Checks that the shell ends with code 0 when standard input closes without an <c>exit</c> command.
	/// </summary>
	[Fact]
	public async Task Shell_WhenInputClosesWithoutExit_Exits()
	{
		// Act
		var run = await ShellProcess.RunAsync("echo hi\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ hi\n$ ");
		_ = run.Exited.Should().BeTrue();
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Builds the environment the window gives the shell, with <paramref name="folder" /> standing in for the user's
	/// data folder and <c>PATH</c>.
	/// </summary>
	/// <param name="folder">The test's temporary folder.</param>
	/// <returns>
	/// The variables to set on the shell process.
	/// </returns>
	private static Dictionary<string, string> WindowEnvironment(string folder) => new()
	{
		// The window variable turns on saved history, so the data folder moves into the temporary folder and
		// the session bus is dropped. That way the test never writes to the user's real history or keyring.
		[Program.WindowVariable] = "1",
		["XDG_DATA_HOME"] = folder,

		// Removing the variable isn't enough, because D-Bus then falls back to $XDG_RUNTIME_DIR/bus.
		["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={folder}/no-bus",

		// Only the test's own folder, so an installed fastfetch can't print its banner first.
		["PATH"] = folder,
	};
}
