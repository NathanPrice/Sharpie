using System.Diagnostics.CodeAnalysis;
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
	[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The process-wide writer, borrowed only to put it back.")]
	private readonly TextWriter originalError = Console.Error;
	private readonly StringWriter error = new();
	private readonly DeclareCommand sut = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="DeclareCommandTests" /> class.
	/// </summary>
	public DeclareCommandTests() => Console.SetError(this.error);

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
		_ = this.error.ToString().Should().StartWith($"declare: `{argument}': not a valid identifier{Environment.NewLine}");
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose()
	{
		Console.SetError(this.originalError);
		this.error.Dispose();
		this.environment.Dispose();
	}
}
