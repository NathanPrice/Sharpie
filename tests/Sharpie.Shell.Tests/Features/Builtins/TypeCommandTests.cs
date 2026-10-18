using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="TypeCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class TypeCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly Dictionary<string, ICommand> builtins = new() { ["echo"] = new EchoCommand() };
	private readonly Dictionary<string, string> aliases = [];
	private readonly string folder;
	private readonly TypeCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="TypeCommandTests" /> class with an empty folder as the only
	/// <c>PATH</c> entry.
	/// </summary>
	public TypeCommandTests()
	{
		this.folder = this.environment.CreateFolder();
		ShellEnvironment.SetPath(this.folder);
		this.sut = new TypeCommand(this.builtins, this.aliases);
	}

	/// <summary>
	/// Checks that a registered builtin is reported as one.
	/// </summary>
	[Fact]
	public void Run_WithBuiltinName_ReportsShellBuiltin()
	{
		// Act
		_ = this.sut.Run(["echo"]);

		// Assert
		_ = this.environment.Output.Should().Be($"echo is a shell builtin{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that a builtin wins over an executable of the same name on <c>PATH</c>.
	/// </summary>
	[Fact]
	public void Run_WithBuiltinNameAlsoOnPath_ReportsShellBuiltin()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "echo", executable: true);

		// Act
		_ = this.sut.Run(["echo"]);

		// Assert
		_ = this.environment.Output.Should().Be($"echo is a shell builtin{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that an executable found on <c>PATH</c> is reported with its full path.
	/// </summary>
	[Fact]
	public void Run_WithExecutableOnPath_ReportsFullPath()
	{
		// Arrange
		var fullPath = ShellEnvironment.CreateFile(this.folder, "mytool", executable: true);

		// Act
		_ = this.sut.Run(["mytool"]);

		// Assert
		_ = this.environment.Output.Should().Be($"mytool is {fullPath}{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that a name that is neither a builtin nor on <c>PATH</c> is reported as not found.
	/// </summary>
	[Fact]
	public void Run_WithUnknownName_ReportsNotFound()
	{
		// Act
		_ = this.sut.Run(["nosuchcommand"]);

		// Assert
		_ = this.environment.Output.Should().Be($"nosuchcommand: not found{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that each argument gets its own line, in the order given.
	/// </summary>
	[Fact]
	public void Run_WithSeveralNames_ReportsEachInOrder()
	{
		// Arrange
		var fullPath = ShellEnvironment.CreateFile(this.folder, "mytool", executable: true);

		// Act
		_ = this.sut.Run(["nosuchcommand", "echo", "mytool"]);

		// Assert
		_ = this.environment.Output.Should().Be(
			$"nosuchcommand: not found{Environment.NewLine}" +
			$"echo is a shell builtin{Environment.NewLine}" +
			$"mytool is {fullPath}{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that running with no arguments prints nothing.
	/// </summary>
	[Fact]
	public void Run_WithNoArguments_PrintsNothing()
	{
		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a builtin added to the dictionary after construction is still found.
	/// </summary>
	/// <remarks>
	/// <c>Program.Main</c> creates <see cref="TypeCommand" /> before it fills the dictionary, so the lookup has to
	/// happen on each run.
	/// </remarks>
	[Fact]
	public void Run_WithBuiltinAddedAfterConstruction_ReportsShellBuiltin()
	{
		// Arrange
		this.builtins.Add(this.sut.Name, this.sut);

		// Act
		_ = this.sut.Run(["type"]);

		// Assert
		_ = this.environment.Output.Should().Be($"type is a shell builtin{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that an alias is reported with its value, ahead of a builtin of the same name.
	/// </summary>
	[Fact]
	public void Run_WithAliasName_ReportsAliasValue()
	{
		// Arrange
		this.aliases["echo"] = "echo -n";

		// Act
		_ = this.sut.Run(["echo"]);

		// Assert
		_ = this.environment.Output.Should().Be($"echo is an alias for echo -n{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that the Spargine guard rejects a missing alias table when the command is created.
	/// </summary>
	[Fact]
	public void Constructor_WithNullAliases_ThrowsArgumentNullException()
	{
		// Act
		var act = () => new TypeCommand(this.builtins, null!);

		// Assert
		_ = act.Should().Throw<ArgumentNullException>();
	}

	/// <summary>
	/// Checks that the Spargine guard rejects a missing builtin table when the command is created.
	/// </summary>
	[Fact]
	public void Constructor_WithNullBuiltins_ThrowsArgumentNullException()
	{
		// Act
		var act = () => new TypeCommand(null!, this.aliases);

		// Assert
		_ = act.Should().Throw<ArgumentNullException>();
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Checks that a function is reported as one, over a builtin or program of the same name, and that
	/// <c>command -v</c> prints its name.
	/// </summary>
	[Fact]
	public void Run_WithFunctionName_ReportsFunction()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "samplefn", executable: true);
		this.builtins["samplefn"] = new FunctionCommand("samplefn", string.Empty, null, new DeclareCommand(new ShellSettings()), _ => 0);
		this.builtins["echo"] = new FunctionCommand("echo", string.Empty, this.builtins["echo"], new DeclareCommand(new ShellSettings()), _ => 0);

		// Act
		var status = this.sut.Run(["samplefn", "echo"]);
		var commandStatus = new CommandCommand(this.sut).Run(["-v", "samplefn"]);

		// Assert
		_ = status.Should().Be(0);
		_ = commandStatus.Should().Be(0);
		_ = this.environment.Output.Should().Be($"samplefn is a function{Environment.NewLine}echo is a function{Environment.NewLine}samplefn{Environment.NewLine}");
	}
}
