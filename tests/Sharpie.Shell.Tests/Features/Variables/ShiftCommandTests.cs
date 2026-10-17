using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for <see cref="ShiftCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ShiftCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new();
	private readonly ShiftCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="ShiftCommandTests" /> class with the parameters <c>a b c</c>.
	/// </summary>
	public ShiftCommandTests()
	{
		this.sut = new ShiftCommand(this.declare);
		this.declare.SetPositional(["a", "b", "c"]);
	}

	/// <summary>
	/// Checks that <c>shift</c> drops one parameter, or as many as the count, up to all of them.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The parameters left.</param>
	[Theory]
	[InlineData(new string[0], new[] { "b", "c" })]
	[InlineData(new[] { "2" }, new[] { "c" })]
	[InlineData(new[] { "3" }, new string[0])]
	[InlineData(new[] { "0" }, new[] { "a", "b", "c" })]
	public void Run_WithCount_DropsThatMany(string[] args, string[] expected)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.declare.Positional.Should().Equal(expected);
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a count past <c>$#</c> drops nothing and gives 1 quietly, as bash does.
	/// </summary>
	[Fact]
	public void Run_WithCountPastEnd_GivesOneAndKeepsParameters()
	{
		// Act
		var status = this.sut.Run(["4"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.declare.Positional.Should().Equal("a", "b", "c");
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a bad count is reported with bash's status and drops nothing.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="code">The error code.</param>
	/// <param name="message">The message.</param>
	/// <param name="help">The hint, or <see langword="null" /> for none.</param>
	/// <param name="expected">The status.</param>
	[Theory]
	[InlineData(new[] { "x" }, "sharpie::shift::not_a_number", "`x` isn't a number", "shift takes a whole number of parameters to drop", 2)]
	[InlineData(new[] { "-1" }, "sharpie::shift::out_of_range", "Can't shift by -1", "the count can't be negative", 1)]
	[InlineData(new[] { "1", "2" }, "sharpie::shift::too_many_arguments", "`shift` takes at most one count", null, 1)]
	public void Run_WithBadCount_ReportsIt(string[] args, string code, string message, string? help, int expected)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(expected);
		_ = this.declare.Positional.Should().Equal("a", "b", "c");
		_ = this.environment.Error.Should().Be(new ShellError(code, message) { Help = help }.Render(color: false));
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
