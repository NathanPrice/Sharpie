using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Runs the builtins through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class BuiltinProcessTests
{
	/// <summary>
	/// Checks that <c>clear</c> runs as a builtin with piped output, printing its escape sequence instead of failing
	/// the way <see cref="Console.Clear" /> would.
	/// </summary>
	[Fact]
	public async Task Shell_WithClearPiped_PrintsClearSequenceThenExits()
	{
		// Act
		var run = await ShellProcess.RunAsync("clear\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ \e[H\e[2J\e[3J$ ");
		_ = run.Exited.Should().BeTrue();
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>complete</c> is registered with the running shell, so <c>type</c> reports it as a builtin.
	/// </summary>
	[Fact]
	public async Task Shell_WithTypeComplete_ReportsShellBuiltin()
	{
		// Act
		var run = await ShellProcess.RunAsync("type complete\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ complete is a shell builtin\n$ ");
		_ = run.Exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that after <c>cd</c>, external programs and <c>pwd</c> on later lines run in the new folder.
	/// </summary>
	[Fact]
	public async Task Shell_WithCd_RunsLaterProgramsInNewDirectory()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-cd-").FullName;
		await File.WriteAllTextAsync(Path.Combine(folder, "note.txt"), "hello\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"cd {folder}\ncat note.txt\npwd\nexit\n").ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be($"$ $ hello\n$ {folder}\n$ ");
			_ = run.Exited.Should().BeTrue();
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>cd</c> with no arguments goes to the folder named by <c>HOME</c>.
	/// </summary>
	[Fact]
	public async Task Shell_WithCdNoArguments_GoesToHome()
	{
		// Arrange
		var home = Directory.CreateTempSubdirectory("shell-home-").FullName;

		try
		{
			// Act
			var run = await ShellProcess.RunAsync("cd /\ncd\npwd\nexit\n", environment: new Dictionary<string, string> { ["HOME"] = home })
				.ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be($"$ $ $ {home}\n$ ");
			_ = run.Exited.Should().BeTrue();
		}
		finally
		{
			Directory.Delete(home, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>$PWD</c> names the folder the shell started in, even when the parent passed a stale value.
	/// </summary>
	[Fact]
	public async Task Shell_WithStalePwd_ExpandsPwdToStartFolder()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-pwd-").FullName;

		try
		{
			// Act
			var run = await ShellProcess.RunAsync("echo $PWD\nexit\n", workingDirectory: folder, environment: new Dictionary<string, string> { ["PWD"] = "/stale" })
				.ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be($"$ {folder}\n$ ");
			_ = run.Exited.Should().BeTrue();
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}
}
