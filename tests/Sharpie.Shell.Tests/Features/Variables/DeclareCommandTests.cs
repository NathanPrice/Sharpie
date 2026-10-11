using FluentAssertions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for <see cref="DeclareCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class DeclareCommandTests : IDisposable
{
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
		this.sut.Run([$"{name}={value}"]);

		// Act
		this.sut.Run(["-p", name]);

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
		this.sut.Run([argument]);
		this.sut.Run(["-p", name]);

		// Assert
		_ = this.environment.Error.Should().StartWith($"declare: `{argument}': not a valid identifier{Environment.NewLine}");
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>-p</c> with a name that isn't set reports it each time and doesn't create it.
	/// </summary>
	[Fact]
	public void Run_PrintUnknownName_ReportsNotFoundAndCreatesNothing()
	{
		// Act
		this.sut.Run(["-p", "missing"]);
		this.sut.Run(["-p", "missing"]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Contain($"declare: missing: not found{Environment.NewLine}declare: missing: not found{Environment.NewLine}");
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
