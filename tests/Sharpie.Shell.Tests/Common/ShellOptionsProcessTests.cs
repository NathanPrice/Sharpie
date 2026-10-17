using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Runs the shell as a real process the ways other programs start a login shell: <c>-c</c>, <c>-l</c>, a script file,
/// and a <c>-</c> in front of its name.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class ShellOptionsProcessTests : IDisposable
{
	/// <summary>
	/// A made-up variable the test profiles export.
	/// </summary>
	private const string ProfileVariable = "SHARPIE_PROFILE_SAMPLE";

	private readonly string home = Directory.CreateTempSubdirectory("shell-tests-").FullName;

	/// <summary>
	/// Checks that <c>-c</c> runs its list with no prompt and exits with the status <c>exit</c> gives.
	/// </summary>
	[Fact]
	public async Task Shell_WithCommandOption_RunsListAndExitsWithStatus()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "echo a && false || echo b; exit 7"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("a\nb\n");
		_ = run.ExitCode.Should().Be(7);
	}

	/// <summary>
	/// Checks that <c>-c</c> ends with the last command's status: 127 when it isn't found, as POSIX says.
	/// </summary>
	/// <param name="command">The command string.</param>
	/// <param name="expected">The expected exit code.</param>
	[Theory]
	[InlineData("echo hi", 0)]
	[InlineData("false", 1)]
	[InlineData("sampletool_not_installed", 127)]
	[InlineData("cd /nonexistent-sharpie-folder", 1)]
	public async Task Shell_WithCommandOption_ExitsWithLastStatus(string command, int expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", command]).ConfigureAwait(true);

		// Assert
		_ = run.ExitCode.Should().Be(expected);
	}

	/// <summary>
	/// Checks that the shell writes UTF-8 when the locale names another charset, so the prompt's <c>❯</c> doesn't
	/// turn into <c>?</c>.
	/// </summary>
	/// <remarks>
	/// A Latin-1 locale fails the same way a Windows console code page does, so the test can run on Linux.
	/// </remarks>
	[Fact]
	public async Task Shell_WithNonUtf8Locale_WritesUtf8()
	{
		// Arrange
		var environment = new Dictionary<string, string> { ["LANG"] = "en_US.ISO-8859-1", ["LC_ALL"] = "en_US.ISO-8859-1" };

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, environment: environment, arguments: ["-c", "echo ❯"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("❯\n");
	}

	/// <summary>
	/// Checks that an unclosed substitution in <c>-c</c> runs nothing, says why, and exits with 2.
	/// </summary>
	[Fact]
	public async Task Shell_WithUnclosedSubstitution_RunsNothingAndExitsTwo()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "echo before; echo $(date"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().BeEmpty();
		_ = run.Error.Should().Contain("sharpie::parser::unclosed_substitution");
		_ = run.ExitCode.Should().Be(CommandList.UnsupportedStatus);
	}

	/// <summary>
	/// Checks that an unknown option or a bare <c>-c</c> is refused with 2 before anything runs.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The error line.</param>
	[Theory]
	[InlineData(new[] { "-x" }, "-x: invalid option")]
	[InlineData(new[] { "-c" }, "-c: option requires an argument")]
	public async Task Shell_WithBadArguments_ExitsTwo(string[] args, string expected)
	{
		// Act
		var run = await ShellProcess.RunAsync("echo ran\n", arguments: args).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().BeEmpty();
		_ = run.Error.Should().Be(new ShellError("sharpie::options::invalid", expected).Render(color: false));
		_ = run.ExitCode.Should().Be(2);
	}

	/// <summary>
	/// Checks that <c>-i -c</c> runs the startup file first and plain <c>-c</c> doesn't, as bash treats
	/// <c>~/.bashrc</c>.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expectedOutput">What the shell should print.</param>
	/// <param name="expectedCode">The expected exit code.</param>
	[Theory]
	[InlineData(new[] { "-i", "-c", "samplegreet" }, "hello\n", 0)]
	[InlineData(new[] { "-c", "samplegreet" }, "", 127)]
	public async Task Shell_WithCommandOption_RunsStartupFileOnlyWhenInteractive(string[] args, string expectedOutput, int expectedCode)
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.home, ".sharpierc", executable: false, "alias samplegreet='echo hello'\n");

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, environment: this.HomeEnvironment(), arguments: args).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expectedOutput);
		_ = run.ExitCode.Should().Be(expectedCode);
	}

	/// <summary>
	/// Checks that a script file runs line by line with no prompt and exits with its last line's status.
	/// </summary>
	[Fact]
	public async Task Shell_WithScriptFile_RunsItAndExitsWithLastStatus()
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.home, "sample.sh", executable: false, "echo one; echo two\nfalse\n");

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: [script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("one\ntwo\n");
		_ = run.ExitCode.Should().Be(1);
	}

	/// <summary>
	/// Checks that <c>-c</c> inside the window, which a program started from a tab inherits, prints only the command's
	/// output: no banner, prompt or working-directory report.
	/// </summary>
	[Fact]
	public async Task Shell_WithCommandOptionInWindow_PrintsOnlyOutput()
	{
		// Arrange
		var environment = this.HomeEnvironment();
		environment[Program.WindowVariable] = "1";
		environment[Program.HistoryVariable] = "0";

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, environment: environment, arguments: ["-c", "echo hi"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("hi\n");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that a login shell, marked by <c>-</c> in front of its name or by <c>-l</c>, gets what
	/// <c>~/.profile</c> exports, and a plain one doesn't.
	/// </summary>
	/// <param name="programName">The shell's <c>argv[0]</c>, or <see langword="null" /> for its path.</param>
	/// <param name="option">An option before <c>-c</c>, or an empty string for none.</param>
	/// <param name="expected">What the shell should print.</param>
	[Theory]
	[InlineData("-Sharpie", "", "from-profile\n")]
	[InlineData(null, "-l", "from-profile\n")]
	[InlineData(null, "--login", "from-profile\n")]
	[InlineData("Sharpie", "", "\n")]
	public async Task Shell_AsLoginShell_ReadsProfile(string? programName, string option, string expected)
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.home, ".profile", executable: false, $"export {ProfileVariable}=from-profile\n");
		string[] args = option.Length > 0 ? [option, "-c", $"echo ${ProfileVariable}"] : ["-c", $"echo ${ProfileVariable}"];

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, environment: this.HomeEnvironment(), arguments: args, programName: programName).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that typed lines run their lists, and <c>$?</c> holds the status of the command before it.
	/// </summary>
	[Fact]
	public async Task Shell_WithListsOnPrompt_RunsThemAndTracksStatus()
	{
		// Act
		var run = await ShellProcess.RunAsync("false; echo $?\ncd /nonexistent-sharpie-folder && echo no || echo yes\necho $?\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ 1\n$ yes\n$ 0\n$ ");
		_ = run.Error.Should().Be(new ShellError("sharpie::cd::not_found", "No such folder: `/nonexistent-sharpie-folder`").Render(color: false));
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Deletes the test's home folder.
	/// </summary>
	public void Dispose() => Directory.Delete(this.home, recursive: true);

	/// <summary>
	/// Gets an environment that points <c>HOME</c> at the test's own folder, so the user's startup files stay out.
	/// </summary>
	/// <returns>
	/// The variables to set.
	/// </returns>
	private Dictionary<string, string> HomeEnvironment() => new() { ["HOME"] = this.home, [ProfileVariable] = string.Empty };
}
