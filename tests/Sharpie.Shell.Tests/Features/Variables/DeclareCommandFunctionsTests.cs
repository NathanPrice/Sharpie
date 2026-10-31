using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for <c>declare -f</c>, <c>declare -F</c> and the option words <see cref="DeclareCommand" /> reads.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class DeclareCommandFunctionsTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly Dictionary<string, ICommand> commands = new() { ["echo"] = new EchoCommand() };
	private readonly DeclareCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="DeclareCommandFunctionsTests" /> class, with the functions
	/// <c>beta</c> and <c>alpha</c> defined in that order and the builtin <c>echo</c>.
	/// </summary>
	public DeclareCommandFunctionsTests()
	{
		this.sut = new DeclareCommand(new ShellSettings(), this.commands);
		this.commands["beta"] = new FunctionCommand("beta", "{ :; }", null, this.sut, _ => 0);
		this.commands["alpha"] = new FunctionCommand("alpha", "{ echo hi; } > log", null, this.sut, _ => 0);
	}

	/// <summary>
	/// Checks what <c>declare -f</c> and <c>-F</c> print and give, as bash 5.3 does: <c>-F</c> wins over <c>-f</c>,
	/// either wins over <c>-p</c>, a name that isn't a function prints nothing and gives 1, and with no names every
	/// function is listed in name order.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="lines">The lines expected on standard output.</param>
	/// <param name="expected">The status.</param>
	[Theory]
	[InlineData(new[] { "-F" }, new[] { "declare -f alpha", "declare -f beta" }, 0)]
	[InlineData(new[] { "-F", "alpha", "nope" }, new[] { "alpha" }, 1)]
	[InlineData(new[] { "-F", "--", "beta" }, new[] { "beta" }, 0)]
	[InlineData(new[] { "-fF", "beta" }, new[] { "beta" }, 0)]
	[InlineData(new[] { "-F", "echo" }, new string[0], 1)]
	[InlineData(new[] { "-f", "alpha" }, new[] { "alpha () \n{ echo hi; } > log" }, 0)]
	[InlineData(new[] { "-pf", "beta" }, new[] { "beta () \n{ :; }" }, 0)]
	[InlineData(new[] { "-f" }, new[] { "alpha () \n{ echo hi; } > log", "beta () \n{ :; }" }, 0)]
	[InlineData(new[] { "-f", "nope" }, new string[0], 1)]
	public void Run_WithFunctionOption_PrintsFunctions(string[] args, string[] lines, int expected)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(expected);
		_ = this.environment.Output.Should().Be(string.Concat(lines.Select(line => line + Environment.NewLine)));
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a <c>declare</c> made without the shell's commands knows no functions.
	/// </summary>
	[Fact]
	public void Run_WithoutCommands_FindsNoFunctions()
	{
		// Arrange
		var bare = new DeclareCommand(new ShellSettings());

		// Act
		var listed = bare.Run(["-F"]);
		var found = bare.Run(["-f", "alpha"]);

		// Assert
		_ = (listed, found).Should().Be((0, 1));
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>--</c> ends the options and that a word with any letter <c>declare</c> doesn't take is a name,
	/// reported as not valid while the words after it are still carried out.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="invalid">The word reported as not a valid name, or <see langword="null" /> for none.</param>
	[Theory]
	[InlineData(new[] { "--", "x=1" }, null)]
	[InlineData(new[] { "-z", "x=1" }, "-z")]
	[InlineData(new[] { "-az", "x=1" }, "-az")]
	public void Run_WithOptionWords_SetsAfterThem(string[] args, string? invalid)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(invalid is null ? 0 : 1);
		_ = this.sut.Variables["x"].Should().Be("1");
		_ = this.environment.Error.Should().Be(invalid is null ? string.Empty : new ShellError("sharpie::declare::invalid_identifier", $"`{invalid}` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Render(color: false));
	}

	/// <summary>
	/// Checks that joined options are read letter by letter, so <c>-ap</c> prints rather than assigns.
	/// </summary>
	[Fact]
	public void Run_WithJoinedArrayAndPrint_Prints()
	{
		// Arrange
		_ = this.sut.Run(["x=1"]);

		// Act
		var status = this.sut.Run(["-ap", "x"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be($"declare -- x=\"1\"{Environment.NewLine}");
	}

	/// <summary>
	/// Puts the console back after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
