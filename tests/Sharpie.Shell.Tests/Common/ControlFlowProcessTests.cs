using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for compound commands and functions, run by the real shell.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class ControlFlowProcessTests : IDisposable
{
	private readonly string folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

	/// <summary>
	/// Checks that a compound command typed over several lines asks for each line after the first with <c>&gt; </c>
	/// and runs once it's whole.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithIfOverSeveralLines_PromptsForEachLine()
	{
		// Act
		var run = await ShellProcess.RunAsync("if true\nthen\n  echo yes\nfi\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ > > > yes\n$ ");
	}

	/// <summary>
	/// Checks that a line ending in <c>|</c> asks for the rest of the pipeline.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithLineEndingInPipe_PromptsForRest()
	{
		// Act
		var run = await ShellProcess.RunAsync("echo piped |\ncat\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ > piped\n$ ");
	}

	/// <summary>
	/// Checks the status and output of each compound command, run from <c>-c</c>.
	/// </summary>
	/// <param name="command">The command.</param>
	/// <param name="expected">What it prints.</param>
	/// <returns>A task that completes when the test is done.</returns>
	[Theory]
	[InlineData("if false; then echo a; elif true; then echo b; else echo c; fi", "b\n")]
	[InlineData("if false; then echo a; fi; echo $?", "0\n")]
	[InlineData("for i in 1 2 3; do echo $i; done", "1\n2\n3\n")]
	[InlineData("set -- p 'q r'; for a; do echo \"[$a]\"; done", "[p]\n[q r]\n")]
	[InlineData("for i in 1; do false; done; echo $?", "1\n")]
	[InlineData("n=; while [ \"$n\" != xxx ]; do n=x$n; done; echo $n", "xxx\n")]
	[InlineData("until true; do echo never; done; echo $?", "0\n")]
	[InlineData("case '*' in \"*\") echo star;; esac; case ab in \"*\") echo wrong;; a*) echo right;; esac", "star\nright\n")]
	[InlineData("w=hello; case $w in h*) echo h;; *) echo other;; esac", "h\n")]
	[InlineData("case x in y) echo no;; esac; echo $?", "0\n")]
	[InlineData("! false && echo flipped; ! true; echo $?", "flipped\n1\n")]
	[InlineData("x=1; ( x=2; echo $x ); echo $x", "2\n1\n")]
	[InlineData("{ x=3; }; echo $x", "3\n")]
	[InlineData("echo $(case x in x) echo inside;; esac)", "inside\n")]
	[InlineData("for i in 1 2 3; do for j in a b; do [ $j = b ] && continue 2; echo $i$j; done; done", "1a\n2a\n3a\n")]
	[InlineData("for i in 1 2 3; do [ $i = 2 ] && break; echo $i; done; echo $?", "1\n0\n")]
	[InlineData("for i in 1 2; do ( break ); echo $i; done", "1\n2\n")]
	[InlineData("while break; do :; done; echo after", "after\n")]
	[InlineData("for i in 1 2; do until break; do :; done; echo $i; done", "1\n2\n")]
	[InlineData("echo() { command echo \"wrapped $1\"; }; echo hi; echo x | cat; command echo plain | cat", "wrapped hi\nwrapped x\nplain\n")]
	[InlineData("set -e; if echo a | while read l; do false; echo \"got $l\"; done; then echo yes; fi", "got a\nyes\n")]
	[InlineData("if true; then cat <<EOF\nbody\nEOF\nfi", "body\n")]
	[InlineData("type if fi", "if is a shell keyword\nfi is a shell keyword\n")]
	[InlineData("command -v while", "while\n")]
	public async Task Shell_WithCompoundCommand_RunsIt(string command, string expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", command]).ConfigureAwait(true);

		// Assert
		_ = run.Error.Should().BeEmpty();
		_ = run.Output.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a subshell puts back the folder it changed and ends with its own <c>exit</c> status.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithSubshellExit_KeepsFolderAndGivesStatus()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "( cd /; exit 3 ); echo $?; pwd"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be($"3\n{this.folder}\n");
	}

	/// <summary>
	/// Checks that redirects after a compound command reach the builtins and programs inside, in the order they
	/// write, and that a <c>&lt;</c> after a loop feeds every <c>read</c> in it.
	/// </summary>
	/// <param name="command">The command.</param>
	/// <param name="expected">What it prints.</param>
	/// <returns>A task that completes when the test is done.</returns>
	[Theory]
	[InlineData("{ echo a; printf 'b\\n'; echo c; } > f; cat f", "a\nb\nc\n")]
	[InlineData("{ printf 'a\\n'; printf 'b\\n'; } > f; { echo c; printf 'd\\n'; } >> f; cat f", "a\nb\nc\nd\n")]
	[InlineData("printf 'one\\ntwo\\n' > f; while read l; do echo \"<$l>\"; done < f", "<one>\n<two>\n")]
	[InlineData("printf 'v\\n' > f; read x < f; echo $x", "v\n")]
	[InlineData("if true; then ls /nope; fi 2>/dev/null; echo $?", "2\n")]
	[InlineData("while read l; do echo \"[$l]\"; done <<EOF\nx\ny\nEOF", "[x]\n[y]\n")]
	[InlineData("printf '1\\n2\\n3\\n' > f; while read l; do echo $l; echo x | read y; done < f", "1\n2\n3\n")]
	public async Task Shell_WithRedirectedCompound_AppliesToEverythingInside(string command, string expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", command]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
	}

	/// <summary>
	/// Checks that the shell's own report inside a redirected compound command reaches the file before
	/// <c>set -u</c> ends the shell.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithReportInsideRedirectedGroup_WritesItToTheFile()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "set -u; { echo $nope; } 2>err; echo never"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().BeEmpty();
		_ = (await File.ReadAllTextAsync(Path.Combine(this.folder, "err")).ConfigureAwait(true)).Should().Contain("sharpie::shell::unset_variable");
	}

	/// <summary>
	/// Checks that a pipeline with a compound stage runs each stage with real pipes, with the shell's variables and
	/// aliases in the copy that runs a compound stage.
	/// </summary>
	/// <param name="command">The command.</param>
	/// <param name="expected">What it prints.</param>
	/// <returns>A task that completes when the test is done.</returns>
	[Theory]
	[InlineData("printf 'a\\nb\\n' | while read l; do echo \"[$l]\"; done", "[a]\n[b]\n")]
	[InlineData("for i in 1 2; do echo $i; done | tr 12 ab", "a\nb\n")]
	[InlineData("x=inner; alias hey='echo hey'; { echo $x; hey; } | cat", "inner\nhey\n")]
	[InlineData("up() { tr a-z A-Z; }; echo hi | up", "HI\n")]
	[InlineData("say() { echo \"$1-$2\"; }; say a b | cat", "a-b\n")]
	public async Task Shell_WithCompoundStage_RunsPipeline(string command, string expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", command]).ConfigureAwait(true);

		// Assert
		_ = run.Error.Should().BeEmpty();
		_ = run.Output.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a function gets its arguments as positional parameters, ends with <c>return</c>'s status, takes
	/// the redirects of its call, and gives the caller's parameters back.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithFunction_RunsBodyWithArguments()
	{
		// Arrange
		const string Command = "set -- outer; sampletool() { echo \"got $1 $2\"; printf 'prog\\n'; return 4; echo never; }; sampletool a b; echo $?; sampletool x y > out; echo $?; cat out; echo $1; type sampletool";

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", Command]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("got a b\nprog\n4\n4\ngot x y\nprog\nouter\nsampletool is a function\n");
	}

	/// <summary>
	/// Checks that <c>unset -f</c> removes a function, so its name is looked up as a command again.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithUnsetFunction_ForgetsIt()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "sampletool() { echo x; }; unset -f sampletool; sampletool; echo $?"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("127\n");
	}

	/// <summary>
	/// Checks that <c>set -e</c> doesn't end the shell at a failed <c>if</c> condition or a failure inside a tested
	/// group, but does at a failure that isn't tested.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithErrExit_IgnoresTestedFailures()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "set -e; if false; then echo no; fi; { false && true; }; while false; do :; done; echo still; false; echo never"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("still\n");
		_ = run.ExitCode.Should().Be(1);
	}

	/// <summary>
	/// Checks that a compound command in the background runs as a job of its own, with the shell's variables.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithBackgroundSubshell_RunsAsJob()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "y=5; ( sleep 0.1; echo \"bg $y\" ) & wait; echo done"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().MatchRegex(@"^\[1\] \d+\nbg 5\ndone\n$");
	}

	/// <summary>
	/// Checks that <c>break</c> outside a loop warns and succeeds, and <c>return</c> outside a function is an error
	/// with status 2, as in bash.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithLoopControlOutsideLoop_Warns()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "break; echo $?; return; echo $?"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("0\n2\n");
		_ = run.Error.Should().Contain("sharpie::break::outside_loop").And.Contain("sharpie::return::outside_function");
	}

	/// <summary>
	/// Checks that a <c>-c</c> command that ends inside a compound command is a syntax error with status 2.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithUnfinishedCompound_FailsWithStatus2()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "echo before; if true; then echo a"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().BeEmpty();
		_ = run.ExitCode.Should().Be(CommandList.UnsupportedStatus);
		_ = run.Error.Should().Be(new ShellError("sharpie::parser::syntax", "The command isn't finished") { Help = "end it with `fi`" }.Render(color: false));
	}

	/// <summary>
	/// Checks that a script holding compound commands and functions over many lines runs the same whether it's run
	/// as a file, with <c>.</c>, or piped in.
	/// </summary>
	/// <param name="how">How the script runs: <c>file</c>, <c>source</c> or <c>stdin</c>.</param>
	/// <returns>A task that completes when the test is done.</returns>
	[Theory]
	[InlineData("file")]
	[InlineData("source")]
	[InlineData("stdin")]
	public async Task Shell_WithScript_RunsEveryCompound(string how)
	{
		// Arrange
		const string Script = """
			# a comment
			greet() {
			  for name in "$@"; do
			    case $name in
			      b*) echo "hi $name" ;;
			      *)
			        echo "hello $name"
			        ;;
			    esac
			  done
			}

			count=0
			while [ $count != 2 ]; do
			  count=$(expr $count + 1)
			done

			if [ $count = 2 ]; then
			  greet al bo
			else
			  echo wrong
			fi
			early() {
			  return 3
			}
			early
			echo "status $?"
			""";
		var script = Path.Combine(this.folder, "script.sh");

		await File.WriteAllTextAsync(script, Script + "\n").ConfigureAwait(true);

		// Act
		var run = how switch
		{
			"file" => await ShellProcess.RunAsync(string.Empty, this.folder, arguments: [script]).ConfigureAwait(true),
			"source" => await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", ". ./script.sh"]).ConfigureAwait(true),
			_ => await ShellProcess.RunAsync(Script + "\nexit\n", this.folder).ConfigureAwait(true),
		};

		// Assert
		_ = run.Error.Should().BeEmpty();
		_ = run.Output.Replace("$ ", string.Empty, StringComparison.Ordinal).Replace("> ", string.Empty, StringComparison.Ordinal).Should().Be("hello al\nhi bo\nstatus 3\n");
	}

	/// <summary>
	/// Checks that <c>return</c> at the top of a file run with <c>.</c> ends the file with its status.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithReturnInSourcedFile_EndsFile()
	{
		// Arrange
		await File.WriteAllTextAsync(Path.Combine(this.folder, "part.sh"), "echo first\nreturn 5\necho never\n").ConfigureAwait(true);

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", ". ./part.sh; echo $?"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("first\n5\n");
	}

	/// <summary>
	/// Removes the test's folder.
	/// </summary>
	public void Dispose() => Directory.Delete(this.folder, recursive: true);
}
