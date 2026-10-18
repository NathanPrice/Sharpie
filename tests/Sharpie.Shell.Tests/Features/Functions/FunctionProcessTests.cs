using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Functions;

/// <summary>
/// Runs functions, <c>local</c> and <c>return</c> through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class FunctionProcessTests : IDisposable
{
	private readonly string folder = Directory.CreateTempSubdirectory("shell-functions-").FullName;

	/// <summary>
	/// Checks that a call gets its arguments as <c>$1</c>, <c>$#</c> and <c>"$@"</c>, keeps <c>$0</c>, and that the
	/// caller's come back afterwards.
	/// </summary>
	[Fact]
	public async Task Shell_CallingFunction_PassesArguments()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "samplefn() { echo \"$0 $# $1\"; printf '[%s]' \"$@\"; echo; }; set -- outer; samplefn a 'b c'; echo \"$# $1\"", "sampleshell"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("sampleshell 2 a\n[a][b c]\n1 outer\n");
		_ = run.Error.Should().BeEmpty();
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks every header form bash takes, a body over several lines, and a header with its <c>{</c> on the next line.
	/// </summary>
	[Fact]
	public async Task Shell_WithEachHeaderForm_DefinesFunction()
	{
		// Arrange
		const string script = "one() { echo 1; }\ntwo () {\n  echo 2\n  echo 2b\n}\nfunction three { echo 3; }\nfunction four() { echo 4; }\nfive()\n{\n  echo 5\n}\none; two; three; four; five";

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("1\n2\n2b\n3\n4\n5\n");
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>return</c> ends the function with its status, so the commands after it don't run.
	/// </summary>
	[Fact]
	public async Task Shell_WithReturn_EndsFunctionWithStatus()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "samplefn() { return 3; echo no; }; samplefn; echo $?; other() { false; return; }; other; echo $?"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("3\n1\n");
	}

	/// <summary>
	/// Checks that <c>return</c> outside a function prints bash's error and gives 2, and inside <c>$(...)</c> in a
	/// function ends only the substitution.
	/// </summary>
	[Fact]
	public async Task Shell_WithReturnOutsideOrInSubstitution_EndsOnlyWhatItCan()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "return 1; echo \"top=$?\"; samplefn() { x=$(echo a; return 3; echo b); echo \"s=$? x=$x\"; }; samplefn"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("top=2\ns=3 x=a\n");
		_ = run.Error.Should().Contain("`return` only works in a function or a sourced file");
	}

	/// <summary>
	/// Checks that <c>local</c> keeps a value inside the function, and the outer value, an exported one included,
	/// comes back after.
	/// </summary>
	[Fact]
	public async Task Shell_WithLocal_PutsOuterValueBack()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "x=out; export SHARPIE_SAMPLE_FN=env; samplefn() { local x=in SHARPIE_SAMPLE_FN=inner; echo \"$x\"; printenv SHARPIE_SAMPLE_FN; }; samplefn; echo \"$x\"; printenv SHARPIE_SAMPLE_FN; local y=1; echo $?"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("in\ninner\nout\nenv\n1\n");
		_ = run.Error.Should().Contain("`local` only works in a function");
	}

	/// <summary>
	/// Checks that a function hides the builtin of its name, and <c>command</c> inside it still runs the builtin
	/// instead of calling the function forever.
	/// </summary>
	[Fact]
	public async Task Shell_WithFunctionOverBuiltin_CommandRunsBuiltin()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", $"cd() {{ command cd \"$@\" && echo moved; }}; cd {this.folder}; pwd"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be($"moved\n{this.folder}\n");
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>command</c> skips a function on either side of a pipe, and that the function still runs there
	/// without it.
	/// </summary>
	[Fact]
	public async Task Shell_WithCommandInPipeline_SkipsFunction()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "echo() { printf 'fn\\n'; }; command echo left | cat; printf x | command echo right; echo | cat"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("left\nright\nfn\n");
	}

	/// <summary>
	/// Checks that a function's output can go to a file or down a pipe, like a builtin's.
	/// </summary>
	[Fact]
	public async Task Shell_WithRedirectOrPipe_SendsFunctionOutputThere()
	{
		// Arrange
		var file = Path.Combine(this.folder, "out.txt");

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", $"samplefn() {{ echo hi; echo there; }}; samplefn > {file}; cat {file}; samplefn | grep there"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("hi\nthere\nthere\n");
	}

	/// <summary>
	/// Checks that under <c>set -e</c> a failure inside a function ends the shell, but not when the call is tested by
	/// <c>||</c>.
	/// </summary>
	[Fact]
	public async Task Shell_WithErrExit_EndsOnlyOnUntestedFailure()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "set -e; samplefn() { false; echo inside; }; samplefn || echo tested; samplefn; echo never"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("inside\n");
		_ = run.ExitCode.Should().Be(1);
	}

	/// <summary>
	/// Checks that a <c>[[ ... ]]</c> with <c>&amp;&amp;</c> and <c>||</c> inside works in a body, on the header's line
	/// and on a line of its own.
	/// </summary>
	[Fact]
	public async Task Shell_WithConditionInBody_TestsIt()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "samplefn() { [[ $1 == a* && -n $1 || -z $1 ]] && echo \"yes $1\"\n[[ $1 == b || $1 == c ]] || echo \"no $1\"\n}\nsamplefn abc; samplefn b"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("yes abc\nno abc\n");
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>type</c> and <c>command -v</c> report a function.
	/// </summary>
	[Fact]
	public async Task Shell_WithType_ReportsFunction()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "samplefn() { :; }; type samplefn; command -v samplefn"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("samplefn is a function\nsamplefn\n");
	}

	/// <summary>
	/// Checks that a body that's never closed is reported with status 2 at the end of <c>-c</c>, and nothing runs.
	/// </summary>
	[Fact]
	public async Task Shell_WithUnclosedBody_ReportsAndRunsNothing()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "echo a; samplefn() { echo b"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().BeEmpty();
		_ = run.Error.Should().Contain("The function's body is never closed");
		_ = run.ExitCode.Should().Be(2);
	}

	/// <summary>
	/// Checks that a function that calls itself forever is stopped with an error rather than ending the shell.
	/// </summary>
	[Fact]
	public async Task Shell_WithRunawayFunction_StopsIt()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "samplefn() { echo \"x\" > /dev/null; samplefn; samplefn; }; samplefn; echo \"s=$?\""]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("s=1\n");
		_ = run.Error.Should().Contain("`samplefn` is nested more than 100 calls deep");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that a sourced file can define a function over several lines, and that <c>return</c> in a sourced file
	/// ends only the file.
	/// </summary>
	[Fact]
	public async Task Shell_WithSourcedFile_DefinesFunctionAndReturns()
	{
		// Arrange
		var path = Path.Combine(this.folder, "sample.rc");

		await File.WriteAllTextAsync(path, "samplefn() {\n  local word=\"$1\"\n  echo \"got $word\"\n}\necho loaded\nreturn 4\necho never\n").ConfigureAwait(true);

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", $". {path}; echo \"s=$?\"; samplefn one"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("loaded\ns=4\ngot one\n");
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that at the prompt a definition's header keeps reading lines after <c>&gt; </c> until the body closes.
	/// </summary>
	[Fact]
	public async Task Shell_AtPrompt_ReadsBodyLines()
	{
		// Act
		var run = await ShellProcess.RunAsync("samplefn() {\necho hi\n}\nsamplefn\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ > > $ hi\n$ ");
		_ = run.Exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that at the prompt a quote that runs over several lines inside a body doesn't end the body early, so no
	/// body line runs before the definition is done.
	/// </summary>
	[Fact]
	public async Task Shell_AtPromptWithQuoteInBody_ReadsToBodyEnd()
	{
		// Act
		var run = await ShellProcess.RunAsync("samplefn() {\necho \"a\necho early\n\"\n}\nsamplefn\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ > > > > $ a\necho early\n\n$ ");
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a function defined inside <c>$(...)</c>, or a <c>local</c> made there, doesn't reach the shell
	/// around it, as with bash's subshell.
	/// </summary>
	[Fact]
	public async Task Shell_WithDefinitionOrLocalInSubstitution_KeepsThemInside()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "y=$(samplefn() { echo inner; }; samplefn); echo \"$y\"; x=global; other() { : $(local x=inner); x=changed; }; other; echo \"x=$x\"; samplefn"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("inner\nx=changed\n");
		_ = run.Error.Should().Contain("Command `samplefn` not found");
		_ = run.ExitCode.Should().Be(127);
	}

	/// <summary>
	/// Checks that <c>return</c> at a script file's top level is an error the script goes on past, as in bash, while
	/// one in a file the script sources still ends that file.
	/// </summary>
	[Fact]
	public async Task Shell_WithReturnInScriptFile_ReportsAndGoesOn()
	{
		// Arrange
		var library = Path.Combine(this.folder, "sample.lib");
		var script = Path.Combine(this.folder, "sample.sh");

		await File.WriteAllTextAsync(library, "return 4\necho never\n").ConfigureAwait(true);
		await File.WriteAllTextAsync(script, $"echo before\nreturn 3\necho \"after $?\"\n. {library}\necho \"sourced $?\"\n").ConfigureAwait(true);

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: [script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("before\nafter 2\nsourced 4\n");
		_ = run.Error.Should().Contain("`return` only works in a function or a sourced file");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Removes the temporary folder.
	/// </summary>
	public void Dispose() => Directory.Delete(this.folder, recursive: true);
}
