using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Runs <c>exec</c> and <c>command</c> through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class ExecProcessTests : IDisposable
{
	private readonly string folder = Directory.CreateTempSubdirectory("shell-exec-").FullName;

	/// <summary>
	/// Checks that after <c>exec &gt; file</c> builtins and programs on later commands write to the file, though the
	/// shell wrote to its standard output before.
	/// </summary>
	[Fact]
	public async Task Shell_WithExecOutputRedirect_WritesLaterOutputToFile()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "echo before; exec > out.txt; echo hi; sh -c 'echo child'"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("before\n");
		_ = run.ExitCode.Should().Be(0);
		_ = (await File.ReadAllTextAsync(Path.Combine(this.folder, "out.txt")).ConfigureAwait(true)).Should().Be("hi\nchild\n");
	}

	/// <summary>
	/// Checks that after <c>exec 2&gt; file</c> the shell's own errors go to the file, without color codes, though the
	/// shell wrote to its standard error before.
	/// </summary>
	[Fact]
	public async Task Shell_WithExecErrorRedirect_WritesLaterErrorsToFile()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "sampletool_before; exec 2> err.txt; sampletool_missing; echo still"]).ConfigureAwait(true);

		// Assert
		var errors = await File.ReadAllTextAsync(Path.Combine(this.folder, "err.txt")).ConfigureAwait(true);

		_ = run.Output.Should().Be("still\n");
		_ = run.Error.Should().Contain("`sampletool_before` not found").And.NotContain("sampletool_missing");
		_ = errors.Should().Contain("`sampletool_missing` not found").And.NotContain("sampletool_before").And.NotContain("\e[");
	}

	/// <summary>
	/// Checks that <c>exec program</c> replaces the shell, so the program runs under the shell's own process id.
	/// </summary>
	[Fact]
	public async Task Shell_WithExecProgram_KeepsShellProcessId()
	{
		// Arrange
		using var shell = ShellProcess.Start(arguments: ["-c", "exec sh -c 'echo $$'"]);

		// Act
		var output = await shell.StandardOutput.ReadToEndAsync().ConfigureAwait(true);

		// Assert
		_ = output.Should().Be($"{shell.Id}\n");
	}

	/// <summary>
	/// Checks that the program gets the <c>NAME=VALUE</c> words in front of <c>exec</c>, and its redirect.
	/// </summary>
	[Fact]
	public async Task Shell_WithExecAssignmentAndRedirect_PassesBothToProgram()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "SHARPIE_SAMPLE_VALUE=one exec sh -c 'echo $SHARPIE_SAMPLE_VALUE' > out.txt"]).ConfigureAwait(true);

		// Assert
		_ = run.ExitCode.Should().Be(0);
		_ = (await File.ReadAllTextAsync(Path.Combine(this.folder, "out.txt")).ConfigureAwait(true)).Should().Be("one\n");
	}

	/// <summary>
	/// Checks that the program gets SIGPIPE back at its default, though the shell ignores it, so a writer whose reader
	/// went away ends quietly instead of reporting a broken pipe.
	/// </summary>
	[Fact]
	public async Task Shell_WithExecProgram_ResetsIgnoredSignals()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "exec sh -c 'yes | head -n 1'"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("y\n");
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that the copy of standard output the shell keeps while redirecting for the program doesn't leak into
	/// it, so the program only has the descriptors it opened itself.
	/// </summary>
	/// <remarks>
	/// <c>ls</c> opens the folder it lists as descriptor 3.
	/// </remarks>
	[Fact]
	public async Task Shell_WithExecProgramAndRedirect_PassesOnlyStandardDescriptors()
	{
		// Act
		_ = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "exec ls /dev/fd > out.txt"]).ConfigureAwait(true);

		// Assert
		_ = (await File.ReadAllTextAsync(Path.Combine(this.folder, "out.txt")).ConfigureAwait(true)).Should().Be("0\n1\n2\n3\n");
	}

	/// <summary>
	/// Checks that a shell running <c>-c</c> exits with 127 when the program isn't found, as bash does.
	/// </summary>
	[Fact]
	public async Task Shell_WithExecMissingProgram_ExitsWith127()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "exec sampletool_missing; echo after"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().BeEmpty();
		_ = run.ExitCode.Should().Be(127);
	}

	/// <summary>
	/// Checks that a shell running <c>-c</c> exits with 126 when the program can't start, as bash does.
	/// </summary>
	[Fact]
	public async Task Shell_WithExecProgramThatCantStart_ExitsWith126()
	{
		// Arrange
		var program = ShellEnvironment.CreateFile(this.folder, "sampletool", executable: true, "echo hi\n");

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", $"exec {program}; echo after"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().BeEmpty();
		_ = run.ExitCode.Should().Be(126);
	}

	/// <summary>
	/// Checks that an interactive shell keeps going after <c>exec</c> of a missing program.
	/// </summary>
	[Fact]
	public async Task Shell_InteractiveWithExecMissingProgram_KeepsGoing()
	{
		// Act
		var run = await ShellProcess.RunAsync("exec sampletool_missing\necho after\n", arguments: ["-i", "--norc"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ after\n$ ");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that the history is saved before <c>exec</c> replaces the shell, since no exit handler runs then.
	/// </summary>
	[Fact]
	public async Task Shell_InteractiveWithExecProgram_SavesHistoryFirst()
	{
		// Arrange
		var histFile = Path.Combine(this.folder, "history");

		// Act
		var run = await ShellProcess.RunAsync("echo one\nexec true\n", arguments: ["-i", "--norc"], environment: new Dictionary<string, string> { ["HISTFILE"] = histFile })
			.ConfigureAwait(true);

		// Assert
		_ = run.ExitCode.Should().Be(0);
		_ = (await File.ReadAllTextAsync(histFile).ConfigureAwait(true)).Should().Be("echo one\nexec true\n");
	}

	/// <summary>
	/// Checks that <c>command name</c> runs the program past an alias of the same name, with a redirect, and that
	/// <c>command -v</c> shows the alias.
	/// </summary>
	[Fact]
	public async Task Shell_WithCommandBeforeAliasedName_RunsProgram()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, this.folder, arguments: ["-c", "alias cat='echo aliased'; command -v cat; echo hi > in.txt; command cat in.txt > out.txt; echo hi | command cat"])
			.ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("alias cat='echo aliased'\nhi\n");
		_ = (await File.ReadAllTextAsync(Path.Combine(this.folder, "out.txt")).ConfigureAwait(true)).Should().Be("hi\n");
	}

	/// <summary>
	/// Deletes the test's folder.
	/// </summary>
	public void Dispose() => Directory.Delete(this.folder, recursive: true);
}
