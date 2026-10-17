using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="PushdCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class PushdCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DirsCommand dirs = new();
	private readonly PushdCommand sut;
	private readonly string home;
	private readonly string other;

	/// <summary>
	/// Initializes a new instance of the <see cref="PushdCommandTests" /> class in a temporary home folder, with an
	/// empty stack.
	/// </summary>
	public PushdCommandTests()
	{
		this.sut = new PushdCommand(this.dirs, new CdCommand());
		this.home = this.environment.CreateFolder();
		this.other = this.environment.CreateFolder();
		ShellEnvironment.SetHome(this.home);
		Directory.SetCurrentDirectory(this.home);
	}

	/// <summary>
	/// Checks that <c>pushd folder</c> changes to it, saves the folder it left and prints the stack.
	/// </summary>
	/// <param name="separator">Nothing, or the <c>--</c> that ends the options.</param>
	[Theory]
	[InlineData("")]
	[InlineData("--")]
	public void Run_WithFolder_ChangesAndSavesOldFolder(string separator)
	{
		// Act
		var status = this.sut.Run(separator.Length == 0 ? [this.other] : [separator, this.other]);

		// Assert
		_ = status.Should().Be(0);
		_ = Directory.GetCurrentDirectory().Should().Be(this.other);
		_ = this.dirs.Stack.Should().Equal(this.home);
		_ = this.environment.Output.Should().Be($"{this.other} ~{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <c>pushd</c> alone swaps the current folder with the top of the stack.
	/// </summary>
	[Fact]
	public void Run_NoArguments_SwapsWithTop()
	{
		// Arrange
		this.dirs.Stack.AddRange([this.other, "/below"]);

		// Act
		var status = this.sut.Run([]);

		// Assert
		_ = status.Should().Be(0);
		_ = Directory.GetCurrentDirectory().Should().Be(this.other);
		_ = this.dirs.Stack.Should().Equal(this.home, "/below");
		_ = this.environment.Output.Should().Be($"{this.other} ~ /below{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that a folder <c>cd</c> can't change to gives 1 and leaves the folder and the stack as they were, both
	/// when it's named and when it's the top of the stack.
	/// </summary>
	/// <param name="named">Whether the missing folder is named on the line instead of being on top of the stack.</param>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Run_MissingFolder_KeepsStack(bool named)
	{
		// Arrange
		var missing = Path.Combine(this.other, "missing");
		this.dirs.Stack.Add(named ? this.other : missing);

		// Act
		var status = this.sut.Run(named ? [missing] : []);

		// Assert
		_ = status.Should().Be(1);
		_ = Directory.GetCurrentDirectory().Should().Be(this.home);
		_ = this.dirs.Stack.Should().Equal(named ? this.other : missing);
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::cd::not_found", $"No such folder: `{missing}`").Render(color: false));
	}

	/// <summary>
	/// Checks that bad arguments are reported with bash's status, without changing folder or touching the stack.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="code">The error code.</param>
	/// <param name="message">The message.</param>
	/// <param name="help">The hint, or <see langword="null" /> for none.</param>
	/// <param name="expected">The status.</param>
	[Theory]
	[InlineData(new string[0], "sharpie::pushd::stack_empty", "No other folder to swap with", "pushd a folder first", 1)]
	[InlineData(new[] { "--" }, "sharpie::pushd::stack_empty", "No other folder to swap with", "pushd a folder first", 1)]
	[InlineData(new[] { "/", "/" }, "sharpie::pushd::too_many_arguments", "`pushd` takes at most one folder", null, 1)]
	[InlineData(new[] { "" }, "sharpie::pushd::empty_folder", "The folder name is empty", null, 1)]
	[InlineData(new[] { "-n" }, "sharpie::pushd::unsupported_option", "Option `-n` isn't supported", "to change to a folder whose name starts with - or +, write pushd -- name", 2)]
	[InlineData(new[] { "+1" }, "sharpie::pushd::unsupported_option", "Option `+1` isn't supported", "to change to a folder whose name starts with - or +, write pushd -- name", 2)]
	public void Run_WithBadArguments_ReportsThem(string[] args, string code, string message, string? help, int expected)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(expected);
		_ = Directory.GetCurrentDirectory().Should().Be(this.home);
		_ = this.dirs.Stack.Should().BeEmpty();
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Be(new ShellError(code, message) { Help = help }.Render(color: false));
	}

	/// <summary>
	/// Checks that <c>--</c> lets <c>pushd</c> change to a folder whose name starts with <c>-</c>.
	/// </summary>
	[Fact]
	public void Run_WithDashFolderAfterSeparator_ChangesToIt()
	{
		// Arrange
		var dashed = Directory.CreateDirectory(Path.Combine(this.other, "-n")).FullName;
		Directory.SetCurrentDirectory(this.other);

		// Act
		var status = this.sut.Run(["--", "-n"]);

		// Assert
		_ = status.Should().Be(0);
		_ = Directory.GetCurrentDirectory().Should().Be(dashed);
		_ = this.dirs.Stack.Should().Equal(this.other);
	}

	/// <summary>
	/// Restores the console, <c>HOME</c> and working directory after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
