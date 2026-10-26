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
	/// Checks that <c>set -e</c> ignores a failure inside a file that <c>.</c> runs on the left of <c>||</c>, and
	/// still exits for one run as the last command of its chain, as bash does.
	/// </summary>
	/// <param name="command">The command string, where FILE is a file holding <c>false</c>.</param>
	/// <param name="output">What it should print.</param>
	/// <param name="expected">The shell's exit code.</param>
	[Theory]
	[InlineData("set -e; . FILE || echo ok; echo after", "ok\nafter\n", 0)]
	[InlineData("set -e; . FILE && echo no; echo after", "after\n", 0)]
	[InlineData("set -e; true && . FILE; echo after", "", 1)]
	[InlineData("set -e; . FILE || true; false; echo after", "", 1)]
	public async Task Shell_WithErrExitInsideTestedSource_IgnoresFailure(string command, string output, int expected)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var file = ShellEnvironment.CreateFile(folder, "fails.sh", executable: false, "false\n");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", command.Replace("FILE", file, StringComparison.Ordinal)]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be(output);
			_ = run.ExitCode.Should().Be(expected);
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
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
	/// Checks that <c>set -x</c> shows <c>for</c> and <c>case</c> headers as typed, puts <c>PS4</c> in front of each
	/// line, and repeats its first character once for each <c>$(...)</c>, backquotes, <c>eval</c> or <c>.</c> around
	/// the line but not for a subshell or function, as bash does.
	/// </summary>
	/// <param name="command">The command string, where FILE is a file holding <c>echo f</c>.</param>
	/// <param name="error">What it should print to standard error.</param>
	[Theory]
	[InlineData("set -x; x='a b'; for i in $x; do :; done", "+ x='a b'\n+ for i in $x\n+ :\n+ for i in $x\n+ :\n")]
	[InlineData("set -- p; set -x; for i; do :; done; for j in; do :; done", "+ for i in \"$@\"\n+ :\n")]
	[InlineData("set -x; x=a; case $x in a) :;; esac", "+ x=a\n+ case $x in\n+ :\n")]
	[InlineData("PS4='>> '; set -x; :", ">> :\n")]
	[InlineData("PS4='$n> '; n=7; set -x; :", "7> :\n")]
	[InlineData("PS4=; set -x; x=$(:)", ":\nx=\n")]
	[InlineData("set -x; x=$(echo s); y=`:`", "++ echo s\n+ x=s\n++ :\n+ y=\n")]
	[InlineData("set -x; eval 'echo $(:)'", "+ eval 'echo $(:)'\n+++ :\n++ echo\n")]
	[InlineData("set -x; . FILE", "+ . FILE\n++ echo f\n")]
	[InlineData("set -x; (:); g() { :; }; g", "+ :\n+ g\n+ :\n")]
	public async Task Shell_WithXTrace_ShowsHeadersAndLevels(string command, string error)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var file = ShellEnvironment.CreateFile(folder, "sourced.sh", executable: false, "echo f\n");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", command.Replace("FILE", file, StringComparison.Ordinal)]).ConfigureAwait(true);

			// Assert
			_ = run.Error.Should().Be(error.Replace("FILE", file, StringComparison.Ordinal));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <c>set -x</c> shows a function stage of a pipeline once, from the copy of the shell that runs it.
	/// </summary>
	[Fact]
	public async Task Shell_WithXTraceFunctionStage_ShowsItOnce()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "f() { echo hi; }; set -x; f | cat"]).ConfigureAwait(true);

		// Assert
		_ = run.Error.Split('\n').Should().ContainSingle(line => line == "+ f");
		_ = run.Output.Should().Be("hi\n");
	}

	/// <summary>
	/// Checks that every stage of a pipeline, a builtin or <c>NAME=VALUE</c> words included, runs as a subshell, so
	/// it leaves the shell's folder, variables, options and function calls alone, with any number of stages.
	/// </summary>
	/// <param name="command">The command string.</param>
	/// <param name="output">What it should print.</param>
	/// <param name="expected">The shell's exit code.</param>
	[Theory]
	[InlineData("true | exit 3; echo $?", "3\n", 0)]
	[InlineData("p=$(pwd); cd / | true; [ \"$(pwd)\" = \"$p\" ] && echo same", "same\n", 0)]
	[InlineData("v=x; echo a | read v; echo $v", "x\n", 0)]
	[InlineData("set -e | true; false; echo still", "still\n", 0)]
	[InlineData("f() { true | return 2; echo after; }; f", "after\n", 0)]
	[InlineData("echo $(exit 3 | true; echo z)", "z\n", 0)]
	[InlineData("p=$(pwd); cd / | cat | cat; [ \"$(pwd)\" = \"$p\" ] && echo same", "same\n", 0)]
	[InlineData("echo a | tr a b | cat", "b\n", 0)]
	[InlineData("echo a | echo b | cat", "b\n", 0)]
	[InlineData("set -o pipefail; true | false | cat; echo $?", "1\n", 0)]
	[InlineData("set -o pipefail; false | true | cat; echo $?", "1\n", 0)]
	[InlineData("set -e; true | false | true; echo reached", "reached\n", 0)]
	[InlineData("set -e; true | true | false; echo no", "", 1)]
	[InlineData("x=1 | cat; echo \"[$x]\"", "[]\n", 0)]
	public async Task Shell_WithBuiltinInPipeline_RunsItAsSubshell(string command, string output, int expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", command]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(output);
		_ = run.Error.Should().BeEmpty();
		_ = run.ExitCode.Should().Be(expected);
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
