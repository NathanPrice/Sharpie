using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="CommandCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class CommandCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly Dictionary<string, ICommand> builtins = new() { ["echo"] = new EchoCommand() };
	private readonly Dictionary<string, string> aliases = [];
	private readonly string folder;
	private readonly CommandCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="CommandCommandTests" /> class with an empty folder as the only
	/// <c>PATH</c> entry.
	/// </summary>
	public CommandCommandTests()
	{
		this.folder = this.environment.CreateFolder();
		ShellEnvironment.SetPath(this.folder);
		this.sut = new CommandCommand(new TypeCommand(this.builtins, this.aliases));
	}

	/// <summary>
	/// Checks which <c>command</c> words are dropped so the name after them runs directly.
	/// </summary>
	/// <param name="line">The command as typed.</param>
	/// <param name="expected">The words left, joined by spaces.</param>
	[Theory]
	[InlineData("command ls -l", "ls -l")]
	[InlineData("command command ls", "ls")]
	[InlineData("echo hi | command cat", "echo hi | cat")]
	[InlineData("SHARPIE_SAMPLE_A=1 command env", "SHARPIE_SAMPLE_A=1 env")]
	[InlineData("command -v ls", "command -v ls")]
	[InlineData("command", "command")]
	[InlineData("command | cat", "command | cat")]
	[InlineData("echo command ls", "echo command ls")]
	[InlineData("echo SHARPIE_SAMPLE_A=1 command ls", "echo SHARPIE_SAMPLE_A=1 command ls")]
	public void Unwrap_WithLine_DropsCommandWordsInCommandPosition(string line, string expected)
	{
		// Act
		var words = CommandCommand.Unwrap(CommandLine.Split(line));

		// Assert
		_ = string.Join(' ', words.Select(word => word.Text)).Should().Be(expected);
	}

	/// <summary>
	/// Checks that <c>-v</c> prints a builtin's name, a program's path and an alias's definition, one per line.
	/// </summary>
	[Fact]
	public void Run_WithVAndFoundNames_PrintsWhatEachRuns()
	{
		// Arrange
		var fullPath = ShellEnvironment.CreateFile(this.folder, "sampletool", executable: true);

		this.aliases["ll"] = "ls -l";

		// Act
		var status = this.sut.Run(["-v", "echo", "sampletool", "ll"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be($"echo{Environment.NewLine}{fullPath}{Environment.NewLine}alias ll='ls -l'{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <c>-v</c> prints nothing and gives 1 for a name that isn't found.
	/// </summary>
	[Fact]
	public void Run_WithVAndMissingName_PrintsNothingAndReturnsOne()
	{
		// Act
		var status = this.sut.Run(["-v", "sampletool_missing"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>-v</c> gives 0 when any name is found, as bash does, printing only the found ones.
	/// </summary>
	[Fact]
	public void Run_WithVAndMissingThenFoundName_PrintsFoundAndReturnsZero()
	{
		// Act
		var status = this.sut.Run(["-v", "sampletool_missing", "echo"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be($"echo{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <c>-V</c> prints what <c>type</c> does, with its status.
	/// </summary>
	[Fact]
	public void Run_WithBigVAndNames_PrintsTypeText()
	{
		// Act
		var status = this.sut.Run(["-V", "echo", "sampletool_missing"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Output.Should().Be($"echo is a shell builtin{Environment.NewLine}sampletool_missing: not found{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <c>-V</c> gives 0 when every name is found.
	/// </summary>
	[Fact]
	public void Run_WithBigVAndFoundName_ReturnsZero()
	{
		// Act
		var status = this.sut.Run(["-V", "echo"]);

		// Assert
		_ = status.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>-V</c> wins over <c>-v</c> when both are given.
	/// </summary>
	[Fact]
	public void Run_WithVThenBigV_PrintsTypeText()
	{
		// Act
		_ = this.sut.Run(["-v", "-V", "echo"]);

		// Assert
		_ = this.environment.Output.Should().Be($"echo is a shell builtin{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that no arguments and <c>-v</c> with no names both do nothing and give 0, as in bash.
	/// </summary>
	/// <param name="option">The option, or empty for none.</param>
	/// <param name="expectedStatus">The status, 0 in both cases.</param>
	[Theory]
	[InlineData("", 0)]
	[InlineData("-v", 0)]
	public void Run_WithNoNames_PrintsNothingAndReturnsZero(string option, int expectedStatus)
	{
		// Act
		var status = this.sut.Run(option.Length == 0 ? [] : [option]);

		// Assert
		_ = status.Should().Be(expectedStatus);
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that an unknown option is an error with status 2, as in bash.
	/// </summary>
	[Fact]
	public void Run_WithUnknownOption_ReportsErrorAndReturnsTwo()
	{
		// Act
		var status = this.sut.Run(["-x", "echo"]);

		// Assert
		_ = status.Should().Be(2);
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Contain("sharpie::command::invalid_option").And.Contain("`-x`");
	}

	/// <summary>
	/// Checks that the Spargine guard rejects a missing <c>type</c> builtin when the command is created.
	/// </summary>
	[Fact]
	public void Constructor_WithNullType_ThrowsArgumentNullException()
	{
		// Act
		var act = () => new CommandCommand(null!);

		// Assert
		_ = act.Should().Throw<ArgumentNullException>();
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Checks that a function is found by its name, and that <c>command</c> right before the name skips it for the
	/// builtin it hides, or for a program when it hides none.
	/// </summary>
	/// <param name="line">The command as typed.</param>
	/// <param name="nameIndex">Which word is the command name.</param>
	/// <param name="expected">What is found: function, builtin or none.</param>
	[Theory]
	[InlineData("echo hi", 0, "function")]
	[InlineData("command echo hi", 1, "builtin")]
	[InlineData("command command echo", 2, "builtin")]
	[InlineData("x | command echo", 3, "builtin")]
	[InlineData("samplefn", 0, "function")]
	[InlineData("command samplefn", 1, "none")]
	[InlineData("cd", 0, "builtin")]
	[InlineData("command cd", 1, "builtin")]
	[InlineData("ls", 0, "none")]
	public void Find_WithName_SkipsFunctionAfterCommand(string line, int nameIndex, string expected)
	{
		// Arrange
		var cd = new EchoCommand();

		this.builtins["cd"] = cd;
		this.builtins["echo"] = new FunctionCommand("echo", string.Empty, this.builtins["echo"], new DeclareCommand(new ShellSettings()), _ => 0);
		this.builtins["samplefn"] = new FunctionCommand("samplefn", string.Empty, null, new DeclareCommand(new ShellSettings()), _ => 0);
		var words = CommandLine.Split(line);

		// Act
		var found = CommandCommand.Find(this.builtins, words, words[nameIndex]);

		// Assert
		_ = (found switch
		{
			FunctionCommand => "function",
			null => "none",
			_ => "builtin",
		}).Should().Be(expected);
	}
}
