using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="LoopControlCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class LoopControlCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new(new ShellSettings());

	/// <summary>
	/// Checks that <c>break</c> and <c>continue</c> inside loops leave as many as they're given, at most all of them.
	/// </summary>
	/// <param name="name">The builtin.</param>
	/// <param name="arguments">Its arguments.</param>
	/// <param name="expectedLevels">How many loops it leaves.</param>
	[Theory]
	[InlineData("break", new string[0], 1)]
	[InlineData("break", new[] { "2" }, 2)]
	[InlineData("continue", new[] { "1" }, 1)]
	[InlineData("continue", new[] { "9" }, 3)]
	public void Run_InsideLoops_LeavesThem(string name, string[] arguments, int expectedLevels)
	{
		// Arrange
		this.declare.LoopDepth = 3;

		// Act
		var act = () => new LoopControlCommand(name, this.declare).Run(arguments);

		// Assert
		var control = act.Should().Throw<LoopControl>().Which;

		_ = control.Levels.Should().Be(expectedLevels);
		_ = control.Next.Should().Be(name == "continue");
	}

	/// <summary>
	/// Checks the descriptions <c>help</c>-style listings show.
	/// </summary>
	[Fact]
	public void Description_NamesWhatEachDoes()
	{
		// Assert
		_ = new LoopControlCommand("break", this.declare).Description.Should().Be("Leaves the loops around it");
		_ = new LoopControlCommand("continue", this.declare).Description.Should().Be("Starts the next round of a loop");
	}

	/// <summary>
	/// Checks that outside a loop it warns and succeeds, as bash does.
	/// </summary>
	[Fact]
	public void Run_OutsideLoop_WarnsAndSucceeds()
	{
		// Act
		var status = new LoopControlCommand("continue", this.declare).Run([]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::continue::outside_loop", "`continue` only works inside a loop")
		{
			Help = "use it between do and done of a for, while or until loop",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that a count that isn't a whole number from 1 up is refused with status 1.
	/// </summary>
	/// <param name="count">The count.</param>
	[Theory]
	[InlineData("0")]
	[InlineData("-1")]
	[InlineData("x")]
	public void Run_WithBadCount_Refuses(string count)
	{
		// Arrange
		this.declare.LoopDepth = 1;

		// Act
		var status = new LoopControlCommand("break", this.declare).Run([count]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::break::bad_count", $"`{count}` isn't a loop count") { Help = "break takes a whole number from 1 up" }.Render(color: false));
	}

	/// <summary>
	/// Checks that a function call starts the loop count again and gives the caller's back, so a <c>break</c> in a
	/// function can't leave a loop around the call.
	/// </summary>
	[Fact]
	public void Run_InsideFunctionInLoop_WarnsAsOutsideLoop()
	{
		// Arrange
		this.declare.LoopDepth = 2;

		// Act
		var status = this.declare.RunFunction([], () => new LoopControlCommand("break", this.declare).Run([]));

		// Assert
		_ = status.Should().Be(0);
		_ = this.declare.LoopDepth.Should().Be(2);
		_ = this.environment.Error.Should().Contain("sharpie::break::outside_loop");
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
