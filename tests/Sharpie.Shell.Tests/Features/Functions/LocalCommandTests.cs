using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Functions;

/// <summary>
/// Tests for <see cref="LocalCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class LocalCommandTests : IDisposable
{
	/// <summary>
	/// A made-up environment variable name the tests export and remove.
	/// </summary>
	private const string SampleVariable = "SHARPIE_SAMPLE_LOCAL";

	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new(new ShellSettings());
	private readonly LocalCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="LocalCommandTests" /> class.
	/// </summary>
	public LocalCommandTests() => this.sut = new LocalCommand(this.declare);

	/// <summary>
	/// Checks that <c>local NAME=VALUE</c> sets the value for the function's run, and the old one comes back.
	/// </summary>
	[Fact]
	public void Run_WithValue_SetsItUntilFunctionReturns()
	{
		// Arrange
		this.declare.Set("samplevar", "outer");
		string? inside = null;

		// Act
		var status = this.declare.RunFunction([], () =>
		{
			var local = this.sut.Run(["samplevar=inner"]);

			inside = this.declare.Variables["samplevar"];

			return local;
		});

		// Assert
		_ = status.Should().Be(0);
		_ = inside.Should().Be("inner");
		_ = this.declare.Variables["samplevar"].Should().Be("outer");
	}

	/// <summary>
	/// Checks that <c>local NAME</c> hides an exported value from the shell and the environment, and a second
	/// <c>local NAME</c> keeps the value set since.
	/// </summary>
	[Fact]
	public void Run_WithoutValue_HidesOuterAndKeepsLocalOnSecondCall()
	{
		// Arrange
		this.declare.Export(SampleVariable, "outer");
		var seen = new List<string?>();

		// Act
		_ = this.declare.RunFunction([], () =>
		{
			_ = this.sut.Run([SampleVariable]);
			seen.Add(this.declare.Variables.GetValueOrDefault(SampleVariable));
			seen.Add(Environment.GetEnvironmentVariable(SampleVariable));
			this.declare.Set(SampleVariable, "inner");
			_ = this.sut.Run([SampleVariable]);
			seen.Add(this.declare.Variables.GetValueOrDefault(SampleVariable));

			return 0;
		});

		// Assert
		_ = seen.Should().Equal(null, null, "inner");
		_ = this.declare.Variables[SampleVariable].Should().Be("outer");
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("outer");
	}

	/// <summary>
	/// Checks that an invalid name is reported with status 1 and the valid names around it still become local.
	/// </summary>
	[Fact]
	public void Run_WithInvalidName_ReportsAndGoesOn()
	{
		// Arrange
		string? inside = null;
		var badSet = true;

		// Act
		var status = this.declare.RunFunction([], () =>
		{
			var local = this.sut.Run(["1bad=x", "samplevar=ok"]);

			inside = this.declare.Variables.GetValueOrDefault("samplevar");
			badSet = this.declare.Variables.ContainsKey("1bad");

			return local;
		});

		// Assert
		_ = status.Should().Be(1);
		_ = inside.Should().Be("ok");
		_ = badSet.Should().BeFalse();
		_ = this.declare.Variables.Should().NotContainKey("samplevar");
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::local::invalid_identifier", "`1bad=x` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Render(color: false));
	}

	/// <summary>
	/// Checks that <c>local</c> outside a function prints an error, gives 1 and sets nothing, as bash does.
	/// </summary>
	[Fact]
	public void Run_OutsideFunction_ReportsAndGivesOne()
	{
		// Act
		var status = this.sut.Run(["samplevar=x"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.declare.Variables.Should().NotContainKey("samplevar");
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::local::outside_function", "`local` only works in a function")
		{
			Help = "set the variable without local outside a function",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks the name and description the builtin is listed with.
	/// </summary>
	[Fact]
	public void NameAndDescription_AreLocals()
	{
		// Act
		var listed = $"{this.sut.Name}: {this.sut.Description}";

		// Assert
		_ = listed.Should().Be("local: Makes variables local to a function");
	}

	/// <summary>
	/// Restores the console and removes the sample variable after each test.
	/// </summary>
	public void Dispose()
	{
		Environment.SetEnvironmentVariable(SampleVariable, null);
		this.environment.Dispose();
	}
}
