using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Runs <c>:</c>, <c>true</c>, <c>false</c>, <c>eval</c> and <c>unalias</c> through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class StatusEvalProcessTests : IDisposable
{
	private readonly string folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

	/// <summary>
	/// Checks that <c>: &gt; file</c> empties the file through the shell's redirect.
	/// </summary>
	[Fact]
	public async Task Shell_WithColonRedirect_EmptiesFile()
	{
		// Arrange
		var file = ShellEnvironment.CreateFile(this.folder, "sample.txt", executable: false, "some text\n");

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", $": > {file}"]).ConfigureAwait(true);

		// Assert
		_ = run.ExitCode.Should().Be(0);
		_ = new FileInfo(file).Length.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>true</c> and <c>false</c> give 0 and 1 whatever their arguments, and <c>:</c> gives 0.
	/// </summary>
	[Fact]
	public async Task Shell_WithTrueFalseColon_GivesTheirStatuses()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "true --help; echo $?; false x; echo $?; : a b; echo $?"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("0\n1\n0\n");
	}

	/// <summary>
	/// Checks that <c>eval</c> splits and expands its arguments again, so a quoted <c>$1</c> and an assignment
	/// followed by <c>;</c> work as in bash, and that its status is the line's.
	/// </summary>
	[Fact]
	public async Task Shell_WithEval_RunsArgumentsAsLine()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "set -- x; eval echo '$1'; eval 'a=1; echo $a'; eval false; echo $?"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("x\n1\n1\n");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that after <c>unalias</c> the name no longer runs the alias value.
	/// </summary>
	[Fact]
	public async Task Shell_WithUnalias_StopsExpandingName()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "alias sampletool='echo hello'; sampletool; unalias sampletool; sampletool"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().StartWith("hello\n");
		_ = run.ExitCode.Should().Be(127);
	}

	/// <summary>
	/// Removes the temporary folder.
	/// </summary>
	public void Dispose() => Directory.Delete(this.folder, recursive: true);
}
