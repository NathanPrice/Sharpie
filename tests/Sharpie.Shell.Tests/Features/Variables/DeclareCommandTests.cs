using FluentAssertions;
using Sharpie.Shell.Features.Variables;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for <see cref="DeclareCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class DeclareCommandTests : IDisposable
{
	/// <summary>
	/// A made-up environment variable name the tests set and remove.
	/// </summary>
	private const string SampleVariable = "SHARPIE_SAMPLE_DECLARED";

	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand sut = new();

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Run" /> stores a variable whose name is a valid identifier.
	/// </summary>
	/// <param name="name">The variable name.</param>
	/// <param name="value">The value assigned to it.</param>
	[Theory]
	[InlineData("foo", "bar")]
	[InlineData("_FOO", "BAR")]
	[InlineData("a1_b", "x")]
	public void Run_WithValidName_StoresVariable(string name, string value)
	{
		// Arrange
		_ = this.sut.Run([$"{name}={value}"]);

		// Act
		_ = this.sut.Run(["-p", name]);

		// Assert
		_ = this.environment.Output.Should().Be($"declare -- {name}=\"{value}\"{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Run" /> reports an invalid name and does not store the variable.
	/// </summary>
	/// <param name="argument">The <c>NAME=VALUE</c> argument with an invalid name.</param>
	/// <param name="name">The name part of <paramref name="argument" />.</param>
	[Theory]
	[InlineData("23=x", "23")]
	[InlineData("1abc=x", "1abc")]
	[InlineData("a-b=x", "a-b")]
	[InlineData("=x", "")]
	public void Run_WithInvalidName_PrintsErrorAndStoresNothing(string argument, string name)
	{
		// Act
		_ = this.sut.Run([argument]);
		_ = this.sut.Run(["-p", name]);

		// Assert
		_ = this.environment.Error.Should().StartWith(new ShellError("sharpie::declare::invalid_identifier", $"`{argument}` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Render(color: false));
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>-p</c> with a name that isn't set reports it each time and doesn't create it.
	/// </summary>
	[Fact]
	public void Run_PrintUnknownName_ReportsNotFoundAndCreatesNothing()
	{
		// Act
		_ = this.sut.Run(["-p", "missing"]);
		_ = this.sut.Run(["-p", "missing"]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Contain(string.Concat(Enumerable.Repeat(new ShellError("sharpie::declare::not_found", "No variable named `missing`").Render(color: false), 2)));
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.IsAssignment" /> accepts a valid name followed by <c>=</c> and rejects
	/// everything else.
	/// </summary>
	/// <param name="word">The word to check.</param>
	/// <param name="expected">Whether it is an assignment.</param>
	[Theory]
	[InlineData("A=1", true)]
	[InlineData("_a1=", true)]
	[InlineData("A=b=c", true)]
	[InlineData("A", false)]
	[InlineData("=1", false)]
	[InlineData("1A=1", false)]
	[InlineData("a-b=1", false)]
	public void IsAssignment_WithWord_ReturnsExpected(string word, bool expected)
	{
		// Act
		var result = DeclareCommand.IsAssignment(word);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Set" /> also updates the environment when the name is already exported.
	/// </summary>
	[Fact]
	public void Set_WithExportedName_UpdatesEnvironment()
	{
		// Arrange
		Environment.SetEnvironmentVariable(SampleVariable, "old");

		// Act
		this.sut.Set(SampleVariable, "new");

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("new");
		_ = this.sut.Variables.Should().Contain(SampleVariable, "new");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Set" /> keeps a name that isn't exported out of the environment.
	/// </summary>
	[Fact]
	public void Set_WithNameNotExported_LeavesEnvironmentAlone()
	{
		// Act
		this.sut.Set(SampleVariable, "value");

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().BeNull();
		_ = this.sut.Variables.Should().Contain(SampleVariable, "value");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Remove" /> takes the name out of both the shell and the environment.
	/// </summary>
	[Fact]
	public void Remove_WithExportedName_RemovesBoth()
	{
		// Arrange
		Environment.SetEnvironmentVariable(SampleVariable, "value");
		this.sut.Set(SampleVariable, "value");

		// Act
		this.sut.Remove(SampleVariable);

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().BeNull();
		_ = this.sut.Variables.Should().NotContainKey(SampleVariable);
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test, and removes the sample variable.
	/// </summary>
	public void Dispose()
	{
		Environment.SetEnvironmentVariable(SampleVariable, null);
		this.environment.Dispose();
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Status" /> starts at 0 and is kept as the variable <c>?</c>.
	/// </summary>
	[Fact]
	public void Status_WhenSet_IsKeptAsQuestionMarkVariable()
	{
		// Arrange
		var before = this.sut.Status;

		// Act
		this.sut.Status = 42;

		// Assert
		_ = before.Should().Be(0);
		_ = this.sut.Status.Should().Be(42);
		_ = this.sut.Variables.Should().Contain("?", "42");
	}

	/// <summary>
	/// Checks that <c>declare -p</c> gives 1 when a name isn't set and 0 when every name is.
	/// </summary>
	/// <param name="name">The name to print.</param>
	/// <param name="expected">The expected status.</param>
	[Theory]
	[InlineData("set_one", 0)]
	[InlineData("missing_one", 1)]
	public void Run_WithPrintOption_GivesStatus(string name, int expected)
	{
		// Arrange
		_ = this.sut.Run(["set_one=1"]);

		// Act
		var status = this.sut.Run(["-p", name]);

		// Assert
		_ = status.Should().Be(expected);
	}

	/// <summary>
	/// Checks that an assignment gives 0, and a bad name gives 1 while the good names around it are still set.
	/// </summary>
	[Fact]
	public void Run_WithBadName_GivesOneAndSetsTheRest()
	{
		// Act
		var good = this.sut.Run(["a=1"]);
		var bad = this.sut.Run(["1x=2", "b=3"]);

		// Assert
		_ = good.Should().Be(0);
		_ = bad.Should().Be(1);
		_ = this.sut.Variables.Should().Contain("b", "3");
	}
}
