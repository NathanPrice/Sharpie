using FluentAssertions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for <see cref="UnsetCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class UnsetCommandTests : IDisposable
{
	/// <summary>
	/// A made-up environment variable name the tests set and remove.
	/// </summary>
	private const string SampleVariable = "SHARPIE_SAMPLE_UNSET";

	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new();
	private readonly UnsetCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="UnsetCommandTests" /> class.
	/// </summary>
	public UnsetCommandTests() => this.sut = new UnsetCommand(this.declare);

	/// <summary>
	/// Checks that <c>unset</c>, with or without <c>-v</c>, removes a variable from the shell and the environment.
	/// </summary>
	/// <param name="arguments">The arguments before the name.</param>
	[Theory]
	[InlineData]
	[InlineData("-v")]
	public void Run_WithSetName_RemovesIt(params string[] arguments)
	{
		// Arrange
		Environment.SetEnvironmentVariable(SampleVariable, "value");
		this.declare.Run([$"{SampleVariable}=value"]);

		// Act
		this.sut.Run([.. arguments, SampleVariable]);

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().BeNull();
		_ = this.declare.Variables.Should().NotContainKey(SampleVariable);
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a name that was never set is skipped quietly.
	/// </summary>
	[Fact]
	public void Run_WithUnknownName_PrintsNothing()
	{
		// Act
		this.sut.Run([SampleVariable]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that an invalid name is reported, and that <c>-v</c> anywhere but first counts as a name.
	/// </summary>
	/// <param name="name">The invalid name.</param>
	[Theory]
	[InlineData("a-b")]
	[InlineData("-v")]
	public void Run_WithInvalidName_ReportsIt(string name)
	{
		// Act
		this.sut.Run([SampleVariable, name]);

		// Assert
		_ = this.environment.Error.Should().Be($"unset: `{name}': not a valid identifier{Environment.NewLine}");
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
