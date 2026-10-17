using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Runs the positional parameters, <c>set</c> and <c>shift</c> through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class PositionalProcessTests : IDisposable
{
	private readonly string folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

	/// <summary>
	/// Checks that the word after <c>-c</c>'s command sets <c>$0</c> and the rest are <c>$1</c> onwards.
	/// </summary>
	[Fact]
	public async Task Shell_WithCommandAndWords_SetsZeroAndParameters()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "echo $0 $# $1 \"$2\"; printf '[%s]' \"$@\"", "samplename", "a", "b c"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("samplename 2 a b c\n[a][b c]");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that a script file's name is <c>$0</c> and the words after it are its positional parameters.
	/// </summary>
	[Fact]
	public async Task Shell_WithScriptAndWords_SetsZeroAndParameters()
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.folder, "sample.sh", executable: false, "echo $0 $#\necho \"$*\"\n");

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: [script, "one", "two"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be($"{script} 2\none two\n");
	}

	/// <summary>
	/// Checks that <c>set --</c> and <c>shift</c> change what <c>$1</c> and <c>$#</c> give on later lines, and that a
	/// shift past the end gives 1.
	/// </summary>
	[Fact]
	public async Task Shell_WithSetAndShift_ChangesParameters()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "set -- a b c; shift; echo $1 $#; shift 5; echo $? $#; set --; echo $#"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("b 2\n1 2\n0\n");
	}

	/// <summary>
	/// Checks that <c>"$@"</c> with no parameters passes no argument at all, not an empty one.
	/// </summary>
	[Fact]
	public async Task Shell_WithQuotedAtAndNoParameters_PassesNothing()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "printf '[%s]' x\"$@\"y \"$@\"; echo"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("[xy]\n");
	}

	/// <summary>
	/// Checks that <c>set</c> and <c>shift</c> are registered with the running shell.
	/// </summary>
	[Fact]
	public async Task Shell_WithTypeSetAndShift_ReportsBuiltins()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "type set shift"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("set is a shell builtin\nshift is a shell builtin\n");
	}

	/// <summary>
	/// Removes the test folder.
	/// </summary>
	public void Dispose() => Directory.Delete(this.folder, recursive: true);
}
