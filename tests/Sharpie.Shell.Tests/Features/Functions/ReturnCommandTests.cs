using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Functions;

/// <summary>
/// Tests for <see cref="ReturnCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ReturnCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new(new ShellSettings());

	/// <summary>
	/// Checks that inside a function <c>return</c> ends it with the number's low eight bits, or the last status with
	/// no argument.
	/// </summary>
	/// <param name="args">The words after <c>return</c>.</param>
	/// <param name="expected">The status the function ends with.</param>
	[Theory]
	[InlineData(new string[0], 5)]
	[InlineData(new[] { "3" }, 3)]
	[InlineData(new[] { "256" }, 0)]
	[InlineData(new[] { "-1" }, 255)]
	public void Run_InFunction_EndsItWithStatus(string[] args, int expected)
	{
		// Arrange
		var sut = new ReturnCommand(this.declare, () => false);

		this.declare.Status = 5;

		// Act
		var status = this.declare.RunFunction([], () => ReturnCommand.Catch(() => sut.Run(args)));

		// Assert
		_ = status.Should().Be(expected);
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>return</c> ends a sourced file outside any function.
	/// </summary>
	[Fact]
	public void Run_InSourcedFile_EndsIt()
	{
		// Arrange
		var sut = new ReturnCommand(this.declare, () => true);

		// Act
		var act = () => sut.Run(["2"]);

		// Assert
		_ = act.Should().Throw<ReturnCommand.Signal>().Which.Status.Should().Be(2);
	}

	/// <summary>
	/// Checks that an argument that isn't a number ends the function with 2 after an error that names <c>return</c>.
	/// </summary>
	[Fact]
	public void Run_WithNonNumber_EndsWithTwo()
	{
		// Arrange
		var sut = new ReturnCommand(this.declare, () => true);

		// Act
		var status = ReturnCommand.Catch(() => sut.Run(["abc"]));

		// Assert
		_ = status.Should().Be(2);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::return::not_a_number", "`abc` isn't a number") { Help = "return takes a whole number as its status" }.Render(color: false));
	}

	/// <summary>
	/// Checks that outside a function and a sourced file <c>return</c> prints an error and gives 2, as bash does.
	/// </summary>
	[Fact]
	public void Run_AtTopLevel_ReportsAndGivesTwo()
	{
		// Arrange
		var sut = new ReturnCommand(this.declare, () => false);

		// Act
		var status = sut.Run(["3"]);

		// Assert
		_ = status.Should().Be(2);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::return::outside_function", "`return` only works in a function or a sourced file")
		{
			Help = "use exit to end the shell",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that the signal is one a command substitution catches like <c>exit</c>.
	/// </summary>
	[Fact]
	public void Signal_IsSubshellExit()
	{
		// Act
		ExitCommand.SubshellExit signal = new ReturnCommand.Signal(4);

		// Assert
		_ = signal.Status.Should().Be(4);
	}

	/// <summary>
	/// Checks that <see cref="ReturnCommand.Catch" /> gives the status of a run that ends normally.
	/// </summary>
	[Fact]
	public void Catch_WithoutReturn_GivesRunStatus()
	{
		// Act
		var status = ReturnCommand.Catch(() => 6);

		// Assert
		_ = status.Should().Be(6);
	}

	/// <summary>
	/// Checks the name and description the builtin is listed with.
	/// </summary>
	[Fact]
	public void NameAndDescription_AreReturns()
	{
		// Arrange
		var sut = new ReturnCommand(this.declare, () => false);

		// Act
		var listed = $"{sut.Name}: {sut.Description}";

		// Assert
		_ = listed.Should().Be("return: Ends a function or a sourced file");
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
