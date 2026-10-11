using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="ShellOptions" />.
/// </summary>
public sealed class ShellOptionsTests
{
	/// <summary>
	/// Checks that each way of starting the shell is read the way <c>sh</c> and bash read it.
	/// </summary>
	/// <param name="programName">The shell's <c>argv[0]</c>.</param>
	/// <param name="args">The arguments.</param>
	/// <param name="login">Whether a login shell is expected.</param>
	/// <param name="interactive">Whether <c>-i</c> is expected.</param>
	/// <param name="noRc">Whether <c>--norc</c> is expected.</param>
	/// <param name="command">The expected <c>-c</c> command.</param>
	/// <param name="scriptFile">The expected script file.</param>
	[Theory]
	[InlineData("Sharpie.Shell", new string[0], false, false, false, null, null)]
	[InlineData("-Sharpie.Shell", new string[0], true, false, false, null, null)]
	[InlineData(null, new string[0], false, false, false, null, null)]
	[InlineData("Sharpie.Shell", new[] { "-c", "echo hi" }, false, false, false, "echo hi", null)]
	[InlineData("-Sharpie.Shell", new[] { "-c", "echo hi" }, true, false, false, "echo hi", null)]
	[InlineData("Sharpie.Shell", new[] { "-lc", "ls" }, true, false, false, "ls", null)]
	[InlineData("Sharpie.Shell", new[] { "-lic", "ls" }, true, true, false, "ls", null)]
	[InlineData("Sharpie.Shell", new[] { "-c", "-l", "ls" }, true, false, false, "ls", null)]
	[InlineData("Sharpie.Shell", new[] { "-c", "ls", "name", "one" }, false, false, false, "ls", null)]
	[InlineData("Sharpie.Shell", new[] { "-i" }, false, true, false, null, null)]
	[InlineData("Sharpie.Shell", new[] { "--login" }, true, false, false, null, null)]
	[InlineData("Sharpie.Shell", new[] { "--norc" }, false, false, true, null, null)]
	[InlineData("Sharpie.Shell", new[] { "--norc", "-i", "-c", "x" }, false, true, true, "x", null)]
	[InlineData("Sharpie.Shell", new[] { "script.sh" }, false, false, false, null, "script.sh")]
	[InlineData("Sharpie.Shell", new[] { "script.sh", "-c" }, false, false, false, null, "script.sh")]
	[InlineData("Sharpie.Shell", new[] { "--", "-c" }, false, false, false, null, "-c")]
	[InlineData("Sharpie.Shell", new[] { "-", "-x" }, false, false, false, null, "-x")]
	[InlineData("Sharpie.Shell", new[] { "-c", "--", "-x" }, false, false, false, "-x", null)]
	public void Parse_WithArguments_ReadsOptions(string? programName, string[] args, bool login, bool interactive, bool noRc, string? command, string? scriptFile)
	{
		// Act
		var options = ShellOptions.Parse(programName, args, out var error);

		// Assert
		_ = options.Should().Be(new ShellOptions(login, interactive, noRc, command, scriptFile));
		_ = error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that an unknown option or a <c>-c</c> with nothing after it is refused with the reason.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The reason.</param>
	[Theory]
	[InlineData(new[] { "-x" }, "-x: invalid option")]
	[InlineData(new[] { "-lx", "ls" }, "-x: invalid option")]
	[InlineData(new[] { "--verbose" }, "--verbose: invalid option")]
	[InlineData(new[] { "-c" }, "-c: option requires an argument")]
	[InlineData(new[] { "-c", "--" }, "-c: option requires an argument")]
	public void Parse_WithBadArguments_ReturnsNullAndReason(string[] args, string expected)
	{
		// Act
		var options = ShellOptions.Parse("Sharpie.Shell", args, out var error);

		// Assert
		_ = options.Should().BeNull();
		_ = error.Should().Be(expected);
	}

	/// <summary>
	/// Checks the POSIX rule: interactive with <c>-i</c>, or with nothing to run while on a terminal.
	/// </summary>
	/// <param name="interactiveOption">Whether <c>-i</c> was given.</param>
	/// <param name="command">The <c>-c</c> command.</param>
	/// <param name="scriptFile">The script file.</param>
	/// <param name="terminal">Whether standard input and error are terminals.</param>
	/// <param name="expected">Whether the shell is interactive.</param>
	[Theory]
	[InlineData(false, null, null, true, true)]
	[InlineData(false, null, null, false, false)]
	[InlineData(false, "ls", null, true, false)]
	[InlineData(false, null, "script.sh", true, false)]
	[InlineData(true, "ls", null, false, true)]
	[InlineData(true, null, null, false, true)]
	public void IsInteractive_WithOptions_FollowsPosix(bool interactiveOption, string? command, string? scriptFile, bool terminal, bool expected)
	{
		// Arrange
		var options = new ShellOptions(false, interactiveOption, false, command, scriptFile);

		// Act
		var interactive = options.IsInteractive(terminal);

		// Assert
		_ = interactive.Should().Be(expected);
	}
}
