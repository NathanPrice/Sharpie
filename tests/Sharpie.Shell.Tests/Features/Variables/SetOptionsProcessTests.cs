using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Runs <c>set -e</c>, <c>-u</c>, <c>-x</c> and <c>-o pipefail</c> through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class SetOptionsProcessTests
{
	/// <summary>
	/// Checks that <c>set -e</c> ends the shell with the status of a failed command that ends its chain, and keeps
	/// going after one followed by <c>&amp;&amp;</c> or <c>||</c>.
	/// </summary>
	/// <param name="command">The command string.</param>
	/// <param name="output">What it should print.</param>
	/// <param name="expected">The shell's exit code.</param>
	[Theory]
	[InlineData("set -e; echo a; sh -c 'exit 3'; echo b", "a\n", 3)]
	[InlineData("set -e; false && true; echo after", "after\n", 0)]
	[InlineData("set -e; false || true; echo after", "after\n", 0)]
	[InlineData("set -e; true && false; echo after", "", 1)]
	[InlineData("set -e; set +e; false; echo after", "after\n", 0)]
	public async Task Shell_WithErrExit_StopsAtChainEndFailure(string command, string output, int expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", command]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(output);
		_ = run.ExitCode.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <c>set -u</c> reports an unset variable, pointing at it, and ends a shell that isn't interactive
	/// with 127, as bash does, while <c>$@</c>, <c>$#</c> and <c>$?</c> stay usable.
	/// </summary>
	[Fact]
	public async Task Shell_WithNoUnset_ReportsAndExits()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "set -u; echo \"$@\" $# $?; echo $samplenope; echo after"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("0 0\n");
		_ = run.Error.Should().Be(new ShellError("sharpie::shell::unset_variable", "`samplenope` isn't set")
		{
			Source = "echo $samplenope",
			Start = 5,
			End = 16,
			Label = "set -u makes this an error",
			Help = "set it first, or turn the check off with set +u",
		}.Render(color: false));
		_ = run.ExitCode.Should().Be(127);
	}

	/// <summary>
	/// Checks that an interactive shell under <c>set -u</c> gives the command status 1 and keeps reading.
	/// </summary>
	[Fact]
	public async Task Shell_WithNoUnsetInteractive_KeepsGoing()
	{
		// Act
		var run = await ShellProcess.RunAsync("set -u\necho $samplenope\necho status $?\n", arguments: ["-i", "--norc"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ $ status 1\n$ ");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>set -x</c> prints each stage to standard error before it runs, with each <c>NAME=VALUE</c> on a
	/// line of its own and words quoted only where needed, and leaves redirects out.
	/// </summary>
	[Fact]
	public async Task Shell_WithXTrace_PrintsCommands()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "set -x; echo 'a b' '' plain > /dev/null; A='x y' B= echo hi | cat; C=1; set +x; echo quiet"]).ConfigureAwait(true);

		// Assert
		_ = run.Error.Should().Be("+ echo 'a b' '' plain\n+ A='x y'\n+ B=\n+ echo hi\n+ cat\n+ C=1\n+ set +x\n");
		_ = run.Output.Should().Be("hi\nquiet\n");
	}

	/// <summary>
	/// Checks that <c>set -o pipefail</c> gives a pipeline the status of its rightmost failing stage, builtins
	/// included, and that without it the last stage's status counts.
	/// </summary>
	/// <param name="command">The command string.</param>
	/// <param name="output">What it should print.</param>
	[Theory]
	[InlineData("false | true; echo $?", "0\n")]
	[InlineData("set -o pipefail; false | true; echo $?", "1\n")]
	[InlineData("set -o pipefail; sh -c 'exit 3' | sh -c 'exit 4' | true; echo $?", "4\n")]
	[InlineData("set -o pipefail; cd /nonexistent-sharpie-folder 2>/dev/null | cat; echo $?", "1\n")]
	[InlineData("set -o pipefail; sh -c 'exit 5' | cd .; echo $?", "5\n")]
	public async Task Shell_WithPipeFail_GivesRightmostFailure(string command, string output)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", command]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(output);
	}
}
