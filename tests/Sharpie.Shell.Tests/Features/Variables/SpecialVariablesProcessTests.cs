using System.Globalization;
using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Runs <c>$$</c>, <c>$!</c>, <c>$-</c>, <c>LINENO</c>, <c>SHLVL</c> and <c>PPID</c> through the built shell as a real
/// process, checked against what bash gives.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class SpecialVariablesProcessTests : IDisposable
{
	private readonly string folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

	/// <summary>
	/// Checks that <c>$$</c> is the shell's process id, which a program it starts sees as its parent, and that it stays
	/// the shell's inside <c>$(...)</c>.
	/// </summary>
	[Fact]
	public async Task Shell_WithDollarDollar_GivesShellProcessId()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "echo $$; sh -c 'echo $PPID'; echo $(echo $$)"]).ConfigureAwait(true);

		// Assert
		var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		_ = lines.Should().HaveCount(3);
		_ = lines.Should().AllBe(lines[0]);
	}

	/// <summary>
	/// Checks that <c>$!</c> is the last background program's process id.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundProgram_GivesItsIdAsBang()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "sleep 0 & echo $!"]).ConfigureAwait(true);

		// Assert
		var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		// A shell that isn't interactive doesn't print the job's number and id, as in bash.
		_ = lines.Should().ContainSingle().Which.Should().MatchRegex("^[1-9][0-9]*$");
	}

	/// <summary>
	/// Checks that an interactive shell prints a background program's job number and process id, which is <c>$!</c>.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundProgramInteractive_PrintsJob()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-i", "--norc", "-c", "sleep 0 & echo $!"]).ConfigureAwait(true);

		// Assert
		var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		_ = lines.Should().HaveCount(2);
		_ = lines[0].Should().Be($"[1] {lines[1]}");
	}

	/// <summary>
	/// Checks that <c>$!</c> set inside <c>$(...)</c> doesn't reach the shell, and that it's unset before any
	/// background program, so <c>set -u</c> stops on it.
	/// </summary>
	[Fact]
	public async Task Shell_WithBangUnset_StopsUnderNounset()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "x=$(sleep 0 &); set -u; echo $!; echo after"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().BeEmpty();
		_ = run.ExitCode.Should().NotBe(0);
	}

	/// <summary>
	/// Checks that <c>LINENO</c> in a script gives each command's line: commands joined by <c>;</c> share it, one
	/// joined by a backslash gets the line it starts on, a sourced file counts its own lines, <c>eval</c> keeps its
	/// line, and a here-document's body gets its command's.
	/// </summary>
	[Fact]
	public async Task Shell_WithScript_GivesLineNumbers()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "sub.sh", executable: false, "\necho sub $LINENO\n");
		var script = ShellEnvironment.CreateFile(
			this.folder,
			"main.sh",
			executable: false,
			"echo $LINENO; echo $LINENO\necho a \\\n  $LINENO\n. ./sub.sh\neval 'echo $LINENO'\ncat <<EOF\n$LINENO\nEOF\necho $LINENO\n");

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: [script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("1\n1\na 2\nsub 2\n5\n6\n9\n");
	}

	/// <summary>
	/// Checks that <c>LINENO</c> counts the lines of <c>-c</c> text from 1.
	/// </summary>
	[Fact]
	public async Task Shell_WithMultiLineCommand_GivesLineNumbers()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "echo $LINENO\necho $LINENO; echo $LINENO\n\necho $LINENO"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("1\n2\n2\n4\n");
	}

	/// <summary>
	/// Checks that <c>LINENO</c> counts the lines typed at the prompt, a here-document's body included.
	/// </summary>
	[Fact]
	public async Task Shell_WithTypedLines_GivesLineNumbers()
	{
		// Act
		var run = await ShellProcess.RunAsync("echo $LINENO\ncat <<EOF\nx\nEOF\necho $LINENO\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Contain("1\n").And.Contain("x\n").And.Contain("5\n");
	}

	/// <summary>
	/// Checks that <c>$-</c> lists the options that are on, with no <c>i</c> under <c>-c</c>.
	/// </summary>
	[Fact]
	public async Task Shell_WithSetE_ListsItInDash()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "echo \"[$-]\"; set -e; echo $-"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("[]\ne\n");
	}

	/// <summary>
	/// Checks that <c>SHLVL</c> goes up by one for each nested shell and stays the same through <c>exec</c>.
	/// </summary>
	[Fact]
	public async Task Shell_WithNestedShells_RaisesLevel()
	{
		// Arrange
		var environment = new Dictionary<string, string> { ["SHLVL"] = "3" };
		var shell = Path.Combine(AppContext.BaseDirectory, "Sharpie");

		// Act
		var run = await ShellProcess.RunAsync(
			string.Empty,
			environment: environment,
			arguments: ["-c", "echo $SHLVL; \"$1\" -c 'echo $SHLVL'; SHLVL=8 \"$1\" -c 'echo $SHLVL'; exec \"$1\" -c 'echo $SHLVL'", "samplename", shell]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("4\n5\n9\n4\n");
	}

	/// <summary>
	/// Checks that a builtin run in the background stays on the shell's level, as bash's background subshell does,
	/// though a copy of the shell runs it.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundBuiltin_KeepsLevel()
	{
		// Arrange
		var environment = new Dictionary<string, string> { ["SHLVL"] = "3" };

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, environment: environment, arguments: ["-c", "echo $SHLVL &\nwait"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("4\n");
	}

	/// <summary>
	/// Checks that a <c>SHLVL</c> typed in front of <c>exec</c> wins over the level the shell hands down.
	/// </summary>
	[Fact]
	public async Task Shell_WithTypedLevelOnExec_KeepsIt()
	{
		// Arrange
		var shell = Path.Combine(AppContext.BaseDirectory, "Sharpie");

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "SHLVL=20 exec \"$1\" -c 'echo $SHLVL'", "samplename", shell]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("21\n");
	}

	/// <summary>
	/// Checks that <c>$PPID</c> is the process that started the shell.
	/// </summary>
	[Fact]
	public async Task Shell_WithPpid_GivesParentId()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "echo $PPID"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be($"{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}\n");
	}

	/// <summary>
	/// Removes the test folder.
	/// </summary>
	public void Dispose() => Directory.Delete(this.folder, recursive: true);
}
