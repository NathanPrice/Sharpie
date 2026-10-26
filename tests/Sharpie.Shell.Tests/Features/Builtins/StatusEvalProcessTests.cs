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
	/// Checks that <c>eval</c> and <c>-c</c> with a quote left open run nothing and give 2, as bash's unexpected end
	/// of file does, and that <c>exit</c> with two statuses skips the rest of the <c>-c</c> line with 1, as in bash.
	/// </summary>
	[Fact]
	public async Task Shell_WithOpenQuoteOrTwoStatuses_StopsTheLine()
	{
		// Act
		var evaluated = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "eval 'echo \"x'; echo $?; exit 7 2; echo still"]).ConfigureAwait(true);
		var command = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "echo \"x"]).ConfigureAwait(true);

		// Assert
		_ = evaluated.Output.Should().Be("2\n");
		_ = evaluated.ExitCode.Should().Be(1);
		_ = evaluated.Error.Should().Contain("sharpie::exit::too_many_arguments");
		_ = command.Output.Should().BeEmpty();
		_ = command.ExitCode.Should().Be(2);
	}

	/// <summary>
	/// Checks that <c>return</c> with two statuses in a function skips the rest of its line but the shell reads on,
	/// and that <c>exit</c> with a status that isn't a number leaves it running, as bash 5.3 does.
	/// </summary>
	[Fact]
	public async Task Shell_WithTwoReturnStatuses_SkipsOnlyThatLine()
	{
		// Act
		var run = await ShellProcess.RunAsync("f() { return 1 2; }; f; echo skipped\necho next\nexit abc; echo alive $?\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Contain("next\n").And.Contain("alive 2\n").And.NotContain("skipped");
		_ = run.Error.Should().Contain("sharpie::return::too_many_arguments");
	}

	/// <summary>
	/// Checks that piped lines inside an open quote are read on and run as one command, as bash joins them.
	/// </summary>
	[Fact]
	public async Task Shell_WithQuoteAcrossPipedLines_JoinsThem()
	{
		// Act
		var run = await ShellProcess.RunAsync("echo \"x\ny\"\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Replace("$ ", string.Empty, StringComparison.Ordinal).Replace("> ", string.Empty, StringComparison.Ordinal).Should().Be("x\ny\n");
		_ = run.Error.Should().BeEmpty();
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
