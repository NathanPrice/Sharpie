using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="DirsCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class DirsCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DirsCommand sut = new();
	private readonly string home;
	private readonly string other;

	/// <summary>
	/// Initializes a new instance of the <see cref="DirsCommandTests" /> class in a <c>sub</c> folder of a temporary
	/// home folder, with a folder outside home on the stack.
	/// </summary>
	public DirsCommandTests()
	{
		this.home = this.environment.CreateFolder();
		this.other = this.environment.CreateFolder();
		ShellEnvironment.SetHome(this.home);
		Directory.SetCurrentDirectory(Directory.CreateDirectory(Path.Combine(this.home, "sub")).FullName);
		this.sut.Stack.AddRange([this.other, this.home]);
	}

	/// <summary>
	/// Checks that <c>dirs</c> prints the current folder then the stack on one line, with the home folder as <c>~</c>.
	/// </summary>
	/// <param name="separator">Nothing, or the <c>--</c> that ends the options.</param>
	[Theory]
	[InlineData("")]
	[InlineData("--")]
	public void Run_NoOptions_PrintsFoldersOnOneLine(string separator)
	{
		// Act
		var status = this.sut.Run(separator.Length == 0 ? [] : [separator]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be($"~{Path.DirectorySeparatorChar}sub {this.other} ~{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <c>-v</c>, <c>-p</c> and <c>-l</c> change the layout the way bash's do.
	/// </summary>
	/// <param name="option">The option.</param>
	/// <param name="expected">The output, with <c>{S}</c> for the separator, <c>{O}</c> for the folder outside home,
	/// <c>{H}</c> for the home folder and <c>{N}</c> for a line break.</param>
	[Theory]
	[InlineData("-v", " 0  ~{S}sub{N} 1  {O}{N} 2  ~{N}")]
	[InlineData("-p", "~{S}sub{N}{O}{N}~{N}")]
	[InlineData("-l", "{H}{S}sub {O} {H}{N}")]
	public void Run_WithLayoutOption_PrintsThatLayout(string option, string expected)
	{
		// Act
		var status = this.sut.Run([option]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be(expected
			.Replace("{S}", Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
			.Replace("{O}", this.other, StringComparison.Ordinal)
			.Replace("{H}", this.home, StringComparison.Ordinal)
			.Replace("{N}", Environment.NewLine, StringComparison.Ordinal));
	}

	/// <summary>
	/// Checks that <c>-c</c> empties the stack quietly, so a later <c>dirs</c> shows only the current folder.
	/// </summary>
	[Fact]
	public void Run_WithClear_EmptiesStackQuietly()
	{
		// Act
		var status = this.sut.Run(["-c", "-v"]);
		_ = this.sut.Run([]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.sut.Stack.Should().BeEmpty();
		_ = this.environment.Output.Should().Be($"~{Path.DirectorySeparatorChar}sub{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that an option <c>dirs</c> doesn't know, joined options or an operand give status 2 and keep the stack.
	/// </summary>
	/// <param name="argument">The argument.</param>
	[Theory]
	[InlineData("-x")]
	[InlineData("-vp")]
	[InlineData("+1")]
	[InlineData("a")]
	public void Run_WithUnknownOption_GivesTwo(string argument)
	{
		// Act
		var status = this.sut.Run(["-c", argument]);

		// Assert
		_ = status.Should().Be(2);
		_ = this.sut.Stack.Should().HaveCount(2);
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Be(
			new ShellError("sharpie::dirs::unknown_option", $"Unknown option `{argument}`") { Help = "dirs takes -c, -l, -p and -v" }.Render(color: false));
	}

	/// <summary>
	/// Restores the console, <c>HOME</c> and working directory after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
