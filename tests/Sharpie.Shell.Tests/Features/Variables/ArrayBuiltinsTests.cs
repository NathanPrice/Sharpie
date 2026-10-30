using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Conditions;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for what the builtins and <c>[[ =~ ]]</c> do with arrays. Each result was checked against bash 5.3.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ArrayBuiltinsTests : IDisposable
{
	/// <summary>
	/// A made-up environment variable name the tests set and remove.
	/// </summary>
	private const string SampleVariable = "SHARPIE_SAMPLE_ARRAYS";

	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new(new ShellSettings());

	/// <summary>
	/// Checks that <c>local -a</c> makes each name an empty array, <c>local NAME=(...)</c> sets one, and both go back
	/// when the function returns.
	/// </summary>
	[Fact]
	public void Local_WithArrays_KeepsThemInsideFunction()
	{
		// Arrange
		var local = new LocalCommand(this.declare);
		this.declare.Set("w", "outer");
		var inside = new List<string>();

		// Act
		var status = this.declare.RunFunction([], () =>
		{
			var made = local.Run(["-a", "w", "v"]) + local.Run(["l=('a b' c)"]);

			inside.Add($"{this.declare.IsArray("w")} {DeclareCommand.Elements(this.declare.Variables, "w").Count} {this.declare.IsArray("v")}");
			inside.Add(string.Join('|', DeclareCommand.Elements(this.declare.Variables, "l").Select(element => element.Value)));

			return made;
		});

		// Assert
		_ = status.Should().Be(0);
		_ = inside.Should().Equal("True 0 True", "a b|c");
		_ = this.declare.Variables["w"].Should().Be("outer");
		_ = this.declare.IsArray("w").Should().BeFalse();
		_ = this.declare.IsArray("l").Should().BeFalse();
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>export NAME+=VALUE</c> adds to the end of the value before exporting it.
	/// </summary>
	[Fact]
	public void Export_WithAppend_AddsToValue()
	{
		// Arrange
		this.declare.Set(SampleVariable, "/bin");
		this.declare.Set($"{SampleVariable}_B", "old");

		// Act
		var status = new ExportCommand(this.declare).Run([$"{SampleVariable}+=:/opt/bin", $"{SampleVariable}_B=new"]);

		// Assert
		_ = status.Should().Be(0);
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("/bin:/opt/bin");
		_ = Environment.GetEnvironmentVariable($"{SampleVariable}_B").Should().Be("new");
	}

	/// <summary>
	/// Checks that <c>set</c> lists an array as bash does, an empty one included.
	/// </summary>
	[Fact]
	public void Set_WithArrays_ListsThemAsBashDoes()
	{
		// Arrange
		this.declare.SetArray($"{SampleVariable}_A", "1 'b c'", append: false);
		this.declare.SetArray($"{SampleVariable}_E", string.Empty, append: false);

		// Act
		_ = new SetCommand(this.declare, new ShellSettings()).Run([]);

		// Assert
		var lines = this.environment.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

		_ = lines.Where(line => line.StartsWith(SampleVariable, StringComparison.Ordinal)).Should().Equal(
			$"{SampleVariable}_A=([0]=\"1\" [1]=\"b c\")",
			$"{SampleVariable}_E=()");
	}

	/// <summary>
	/// Checks that <c>unset</c> reports a subscript that counts back past the first element, gives 1 and goes on.
	/// </summary>
	[Fact]
	public void Unset_WithSubscriptBeforeFirst_ReportsIt()
	{
		// Arrange
		this.declare.SetArray("a", "x y", append: false);

		// Act
		var status = new UnsetCommand(this.declare).Run(["a[-5]", "a[0]"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Error.Should().Contain("a[-5]: bad array subscript");
		_ = DeclareCommand.Elements(this.declare.Variables, "a").Select(element => element.Value).Should().Equal("y");
	}

	/// <summary>
	/// Checks that <c>=~</c> sets <c>BASH_REMATCH</c> to the match and its groups, and to no elements when it fails.
	/// </summary>
	[Fact]
	public void Condition_WithRegexMatch_SetsBashRematch()
	{
		// Act
		var matched = Run("[[ abc =~ (b)(x)?c ]]");
		var groups = DeclareCommand.Elements(this.declare.Variables, "BASH_REMATCH").Select(element => element.Value).ToArray();
		var failed = Run("[[ abc =~ z ]]");

		// Assert
		_ = (matched, failed).Should().Be((0, 1));
		_ = groups.Should().Equal("bc", "b", string.Empty);
		_ = this.declare.IsArray("BASH_REMATCH").Should().BeTrue();
		_ = DeclareCommand.Elements(this.declare.Variables, "BASH_REMATCH").Should().BeEmpty();

		int Run(string line) => ConditionalCommand.Run(CommandLine.Split(line, this.declare.Variables), line, this.declare.Variables);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		Environment.SetEnvironmentVariable(SampleVariable, null);
		Environment.SetEnvironmentVariable($"{SampleVariable}_B", null);
		this.environment.Dispose();
	}
}
