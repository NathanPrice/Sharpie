using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for <see cref="SetCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class SetCommandTests : IDisposable
{
	/// <summary>
	/// A made-up environment variable name the tests set and remove.
	/// </summary>
	private const string SampleVariable = "SHARPIE_SAMPLE_SET";

	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new();
	private readonly SetCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="SetCommandTests" /> class.
	/// </summary>
	public SetCommandTests() => this.sut = new SetCommand(this.declare);

	/// <summary>
	/// Checks that words after <c>--</c> or <c>-</c>, or with neither, become the positional parameters, and that
	/// <c>set --</c> alone clears them.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The parameters after.</param>
	[Theory]
	[InlineData(new[] { "--", "a", "-x" }, new[] { "a", "-x" })]
	[InlineData(new[] { "-", "a" }, new[] { "a" })]
	[InlineData(new[] { "+", "a" }, new[] { "a" })]
	[InlineData(new[] { "-" }, new[] { "old1", "old2", "old3" })]
	[InlineData(new[] { "+" }, new[] { "old1", "old2", "old3" })]
	[InlineData(new[] { "a", "b c" }, new[] { "a", "b c" })]
	[InlineData(new[] { "ab" }, new[] { "ab" })]
	[InlineData(new[] { "", "a" }, new[] { "", "a" })]
	[InlineData(new[] { "--" }, new string[0])]
	public void Run_WithWords_SetsPositionalParameters(string[] args, string[] expected)
	{
		// Arrange
		this.declare.SetPositional(["old1", "old2", "old3"]);

		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.declare.Positional.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that an option is refused with status 2 and the parameters are left alone.
	/// </summary>
	[Fact]
	public void Run_WithOption_ReportsItAndGivesTwo()
	{
		// Arrange
		this.declare.SetPositional(["a"]);

		// Act
		var status = this.sut.Run(["-q", "b"]);

		// Assert
		_ = status.Should().Be(2);
		_ = this.declare.Positional.Should().Equal("a");
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::set::invalid_option", "`-q` isn't an option of set").Render(color: false));
	}

	/// <summary>
	/// Checks that <c>set</c> alone lists shell and environment variables sorted by name, with values quoted only when
	/// needed and the shell variable winning over the environment, and leaves out names like <c>?</c> and <c>1</c>.
	/// </summary>
	[Fact]
	public void Run_WithNoArguments_ListsVariables()
	{
		// Arrange
		Environment.SetEnvironmentVariable(SampleVariable, "from environment");
		this.declare.Set(SampleVariable + "_A", "a b");
		this.declare.Set(SampleVariable + "_B", string.Empty);
		this.declare.Set(SampleVariable + "_C", "plain");
		this.declare.Set(SampleVariable, "shell");
		this.declare.Status = 3;
		this.declare.SetPositional(["one"]);

		// Act
		var status = this.sut.Run([]);

		// Assert
		_ = status.Should().Be(0);
		var lines = this.environment.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

		_ = lines.Where(line => line.StartsWith(SampleVariable, StringComparison.Ordinal)).Should().Equal(
			$"{SampleVariable}=shell",
			$"{SampleVariable}_A='a b'",
			$"{SampleVariable}_B=",
			$"{SampleVariable}_C=plain");
		_ = lines.Should().BeInAscendingOrder(StringComparer.Ordinal);
		_ = lines.Should().NotContain(line => line.StartsWith("?=", StringComparison.Ordinal) || line.StartsWith("1=", StringComparison.Ordinal) || line.StartsWith("#=", StringComparison.Ordinal));
		_ = lines.Should().Contain(line => line.StartsWith("PATH=", StringComparison.Ordinal));
	}

	/// <summary>
	/// Restores the console after each test and removes the sample variable.
	/// </summary>
	public void Dispose()
	{
		Environment.SetEnvironmentVariable(SampleVariable, null);
		this.environment.Dispose();
	}
}
