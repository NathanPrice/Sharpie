using FluentAssertions;
using Sharpie.Shell.Features.Variables;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for <see cref="ExportCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ExportCommandTests : IDisposable
{
	/// <summary>
	/// A made-up environment variable name the tests set and remove.
	/// </summary>
	private const string SampleVariable = "SHARPIE_SAMPLE_EXPORTED";

	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new(new ShellSettings());
	private readonly ExportCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="ExportCommandTests" /> class.
	/// </summary>
	public ExportCommandTests() => this.sut = new ExportCommand(this.declare);

	/// <summary>
	/// Checks that <c>export NAME=VALUE</c> sets both the environment and the shell variable.
	/// </summary>
	/// <param name="value">The value, which may be empty or hold another <c>=</c>.</param>
	[Theory]
	[InlineData("value")]
	[InlineData("")]
	[InlineData("a=b")]
	public void Run_WithAssignment_SetsEnvironmentAndShellVariable(string value)
	{
		// Act
		_ = this.sut.Run([$"{SampleVariable}={value}"]);

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be(value);
		_ = this.declare.Variables.Should().Contain(SampleVariable, value);
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>export NAME</c> copies a variable set earlier with <c>declare</c> into the environment.
	/// </summary>
	[Fact]
	public void Run_WithDeclaredName_ExportsIt()
	{
		// Arrange
		_ = this.declare.Run([$"{SampleVariable}=declared"]);

		// Act
		_ = this.sut.Run([SampleVariable]);

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("declared");
	}

	/// <summary>
	/// Checks that <c>export NAME</c> for a name that was never set leaves the environment alone and prints nothing.
	/// </summary>
	[Fact]
	public void Run_WithUnknownName_DoesNothing()
	{
		// Act
		_ = this.sut.Run([SampleVariable]);

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().BeNull();
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that an invalid name is reported and nothing is exported, while the valid names around it still are.
	/// </summary>
	/// <param name="argument">The argument with an invalid name.</param>
	[Theory]
	[InlineData("a-b=1")]
	[InlineData("1a")]
	[InlineData("=x")]
	public void Run_WithInvalidName_ReportsItAndExportsTheRest(string argument)
	{
		// Act
		_ = this.sut.Run([argument, $"{SampleVariable}=kept"]);

		// Assert
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::export::invalid_identifier", $"`{argument}` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Render(color: false));
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("kept");
	}

	/// <summary>
	/// Checks that <c>export</c> and <c>export -p</c> list exported variables quoted so they can be typed back in.
	/// </summary>
	/// <param name="arguments">The arguments, which ask for the list either way.</param>
	[Theory]
	[InlineData]
	[InlineData("-p")]
	public void Run_WithNoNames_ListsExportedVariables(params string[] arguments)
	{
		// Arrange
		Environment.SetEnvironmentVariable(SampleVariable, "it's");

		// Act
		_ = this.sut.Run(arguments);

		// Assert
		_ = this.environment.Output.Should().Contain($"export {SampleVariable}='it'\\''s'{Environment.NewLine}");
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that the list is sorted by name.
	/// </summary>
	[Fact]
	public void Run_WithNoNames_ListsInNameOrder()
	{
		// Arrange
		Environment.SetEnvironmentVariable(SampleVariable + "_B", "2");
		Environment.SetEnvironmentVariable(SampleVariable + "_A", "1");

		try
		{
			// Act
			_ = this.sut.Run([]);

			// Assert
			_ = this.environment.Output.Should().Contain(
				$"export {SampleVariable}_A='1'{Environment.NewLine}export {SampleVariable}_B='2'{Environment.NewLine}");
		}
		finally
		{
			Environment.SetEnvironmentVariable(SampleVariable + "_A", null);
			Environment.SetEnvironmentVariable(SampleVariable + "_B", null);
		}
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
