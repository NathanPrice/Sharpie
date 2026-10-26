using FluentAssertions;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Runs <see cref="SourceCommand" /> and the startup file through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class SourceProcessTests
{
	/// <summary>
	/// Checks that <c>.</c> and <c>source</c> run a file's comments, aliases, exports and continued lines in the current
	/// shell, so later lines and the programs they start see what it set.
	/// </summary>
	/// <param name="command">The name the file is run with.</param>
	[Theory]
	[InlineData(".")]
	[InlineData("source")]
	public async Task Shell_WithSourcedFile_KeepsWhatItSet(string command)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-source-").FullName;
		var path = Path.Combine(folder, "sample.rc");

		await File.WriteAllTextAsync(path, "# settings\nalias greet='echo hello'\nexport SHARPIE_SAMPLE_RC=\"on \\\nand-on\"\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync($"{command} {path}\ngreet world\nprintenv SHARPIE_SAMPLE_RC\nexit\n").ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ $ hello world\n$ on and-on\n$ ");
			_ = run.Error.Should().BeEmpty();
			_ = run.Exited.Should().BeTrue();
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a file run with arguments keeps the parameters its own <c>set</c> made, but not ones a subshell or
	/// command substitution made, as bash's forked subshell can't change the file's.
	/// </summary>
	/// <param name="body">The file.</param>
	/// <param name="expected">The parameters after the file.</param>
	[Theory]
	[InlineData("set -- z\n", "z")]
	[InlineData("(set -- z)\n", "p")]
	[InlineData("x=$(set -- z)\n", "p")]
	public async Task Shell_WithSetInSourcedFile_KeepsOnlyItsOwn(string body, string expected)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-source-").FullName;
		var path = Path.Combine(folder, "sample.rc");

		await File.WriteAllTextAsync(path, body).ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", $"set -- p; . -- {path} a b; echo \"$@\""]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be(expected + "\n");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a shell whose input isn't a terminal skips <c>~/.sharpierc</c>, as bash skips <c>~/.bashrc</c>.
	/// </summary>
	[Fact]
	public async Task Shell_NotInteractive_SkipsRcFile()
	{
		// Arrange
		var home = Directory.CreateTempSubdirectory("shell-home-").FullName;

		await File.WriteAllTextAsync(Path.Combine(home, Program.RcFileName), "alias samplegreet='echo hello'\n").ConfigureAwait(true);

		try
		{
			// Act
			var run = await ShellProcess.RunAsync("samplegreet\nexit\n", environment: new Dictionary<string, string> { ["HOME"] = home }).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("$ $ ");
			_ = run.Error.Should().Contain("Command `samplegreet` not found");
		}
		finally
		{
			Directory.Delete(home, recursive: true);
		}
	}
}
