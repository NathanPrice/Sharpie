using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="PopdCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class PopdCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DirsCommand dirs = new();
	private readonly PopdCommand sut;
	private readonly string home;
	private readonly string other;

	/// <summary>
	/// Initializes a new instance of the <see cref="PopdCommandTests" /> class in a folder outside a temporary home
	/// folder, with an empty stack.
	/// </summary>
	public PopdCommandTests()
	{
		this.sut = new PopdCommand(this.dirs, new CdCommand(new DeclareCommand()));
		this.home = this.environment.CreateFolder();
		this.other = this.environment.CreateFolder();
		ShellEnvironment.SetHome(this.home);
		Directory.SetCurrentDirectory(this.other);
	}

	/// <summary>
	/// Checks that <c>popd</c> changes to the top of the stack, drops it and prints what's left.
	/// </summary>
	/// <param name="separator">Nothing, or the <c>--</c> that ends the options.</param>
	[Theory]
	[InlineData("")]
	[InlineData("--")]
	public void Run_WithStack_ChangesToTopAndDropsIt(string separator)
	{
		// Arrange
		this.dirs.Stack.AddRange([this.home, "/below"]);

		// Act
		var status = this.sut.Run(separator.Length == 0 ? [] : [separator]);

		// Assert
		_ = status.Should().Be(0);
		_ = Directory.GetCurrentDirectory().Should().Be(this.home);
		_ = this.dirs.Stack.Should().Equal("/below");
		_ = this.environment.Output.Should().Be($"~ /below{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that an empty stack gives 1, as in bash.
	/// </summary>
	[Fact]
	public void Run_EmptyStack_GivesOne()
	{
		// Act
		var status = this.sut.Run([]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Be(
			new ShellError("sharpie::popd::stack_empty", "The folder stack is empty") { Help = "pushd a folder first" }.Render(color: false));
	}

	/// <summary>
	/// Checks that a top folder that no longer exists gives 1 and stays on the stack.
	/// </summary>
	[Fact]
	public void Run_MissingTop_KeepsStack()
	{
		// Arrange
		var missing = Path.Combine(this.home, "missing");
		this.dirs.Stack.Add(missing);

		// Act
		var status = this.sut.Run([]);

		// Assert
		_ = status.Should().Be(1);
		_ = Directory.GetCurrentDirectory().Should().Be(this.other);
		_ = this.dirs.Stack.Should().Equal(missing);
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that any argument gives 2, as in bash, and leaves the stack alone.
	/// </summary>
	/// <param name="argument">The argument.</param>
	[Theory]
	[InlineData("x")]
	[InlineData("-n")]
	public void Run_WithArgument_GivesTwo(string argument)
	{
		// Arrange
		this.dirs.Stack.Add(this.home);

		// Act
		var status = this.sut.Run(["--", argument]);

		// Assert
		_ = status.Should().Be(2);
		_ = Directory.GetCurrentDirectory().Should().Be(this.other);
		_ = this.dirs.Stack.Should().Equal(this.home);
		_ = this.environment.Error.Should().Be(
			new ShellError("sharpie::popd::unsupported_argument", $"`popd` doesn't take `{argument}`") { Help = "popd takes no arguments" }.Render(color: false));
	}

	/// <summary>
	/// Restores the console, <c>HOME</c> and working directory after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
