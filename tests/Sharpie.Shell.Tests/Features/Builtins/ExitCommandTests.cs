using FluentAssertions;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="ExitCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ExitCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();

	/// <summary>
	/// Checks that <c>exit</c> with no argument keeps the last command's status and a number keeps its low eight bits.
	/// </summary>
	/// <param name="args">The words after <c>exit</c>.</param>
	/// <param name="status">The last command's status.</param>
	/// <param name="expected">The status the shell should exit with.</param>
	[Theory]
	[InlineData(new string[0], 5, 5)]
	[InlineData(new string[0], 0, 0)]
	[InlineData(new[] { "3" }, 5, 3)]
	[InlineData(new[] { "0" }, 5, 0)]
	[InlineData(new[] { "255" }, 0, 255)]
	[InlineData(new[] { "256" }, 0, 0)]
	[InlineData(new[] { "-1" }, 0, 255)]
	[InlineData(new[] { "+2" }, 0, 2)]
	[InlineData(new[] { "--", "3" }, 5, 3)]
	[InlineData(new[] { "--" }, 5, 5)]
	public void Code_WithArguments_GivesPosixStatus(string[] args, int status, int expected)
	{
		// Act
		var code = ExitCommand.Code(args, status);

		// Assert
		_ = code.Should().Be(expected);
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a first argument that isn't a number prints an error and gives no status, even with more after it,
	/// as bash checks it first.
	/// </summary>
	/// <param name="argument">The argument.</param>
	[Theory]
	[InlineData("x")]
	[InlineData("1.5")]
	[InlineData("")]
	public void Code_WithNonNumber_ReportsAndGivesNull(string argument)
	{
		// Act
		var code = ExitCommand.Code([argument, "more"], 0);

		// Assert
		_ = code.Should().BeNull();
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::exit::not_a_number", $"`{argument}` isn't a number") { Help = "exit takes a whole number as its status" }.Render(color: false));
	}

	/// <summary>
	/// Checks that more than one status, after any leading <c>--</c>, throws the error that skips the rest of the
	/// line, as bash does.
	/// </summary>
	/// <param name="args">The words after <c>exit</c>.</param>
	[Theory]
	[InlineData([new[] { "1", "2" }])]
	[InlineData([new[] { "--", "1", "2" }])]
	[InlineData([new[] { "--", "-1", "x" }])]
	public void Code_WithTooManyArguments_SkipsTheLine(string[] args)
	{
		// Act
		var act = () => ExitCommand.Code(args, 0, "samplename");

		// Assert
		var failure = act.Should().Throw<CommandLine.ExpansionError>().Which;

		_ = failure.Fatal.Should().BeFalse();
		_ = failure.Error.Render(color: false).Should().Be(new ShellError("sharpie::samplename::too_many_arguments", "`samplename` takes at most one status").Render(color: false));
	}

	/// <summary>
	/// Checks that <c>exit</c> with more than one status skips the line instead of ending the shell, even in a
	/// command substitution.
	/// </summary>
	[Fact]
	public void Run_WithTooManyArguments_SkipsTheLine()
	{
		// Arrange
		var sut = new ExitCommand(new DeclareCommand(new ShellSettings()), () => true);

		// Act
		var act = () => sut.Run(["1", "2"]);

		// Assert
		_ = act.Should().Throw<CommandLine.ExpansionError>().Which.Error.Code.Should().Be("sharpie::exit::too_many_arguments");
	}

	/// <summary>
	/// Checks that <c>exit</c> with a status that isn't a number gives 2 and leaves the shell running, as bash 5.3
	/// does.
	/// </summary>
	[Fact]
	public void Run_WithNonNumber_GivesTwoWithoutExiting()
	{
		// Arrange
		var sut = new ExitCommand(new DeclareCommand(new ShellSettings()), () => true);

		// Act
		var status = sut.Run(["abc"]);

		// Assert
		_ = status.Should().Be(2);
		_ = this.environment.Error.Should().Contain("sharpie::exit::not_a_number");
	}

	/// <summary>
	/// Checks that the error for an argument that isn't a number names the builtin it's for.
	/// </summary>
	[Fact]
	public void Code_WithNameAndNonNumber_NamesIt()
	{
		// Act
		var code = ExitCommand.Code(["x"], 0, "samplename");

		// Assert
		_ = code.Should().BeNull();
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::samplename::not_a_number", "`x` isn't a number") { Help = "samplename takes a whole number as its status" }.Render(color: false));
	}

	/// <summary>
	/// Checks that inside a command substitution <c>exit</c> ends only the substitution, with its status.
	/// </summary>
	[Fact]
	public void Run_InSubshell_ThrowsSubshellExitWithCode()
	{
		// Arrange
		var sut = new ExitCommand(new DeclareCommand(new ShellSettings()), () => true);

		// Act
		var act = () => sut.Run(["259"]);

		// Assert
		_ = act.Should().Throw<ExitCommand.SubshellExit>().Which.Status.Should().Be(3);
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
