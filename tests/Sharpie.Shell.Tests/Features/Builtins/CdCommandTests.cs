using FluentAssertions;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="CdCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class CdCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly CdCommand sut = new();
	private readonly string home;
	private readonly string start;

	/// <summary>
	/// Initializes a new instance of the <see cref="CdCommandTests" /> class with <c>HOME</c> and the working
	/// directory set to two separate empty folders.
	/// </summary>
	public CdCommandTests()
	{
		this.home = this.environment.CreateFolder();
		this.start = this.environment.CreateFolder();
		ShellEnvironment.SetHome(this.home);
		Directory.SetCurrentDirectory(this.start);
	}

	/// <summary>
	/// Checks that running with no arguments goes to the home folder.
	/// </summary>
	[Fact]
	public void Run_WithNoArguments_GoesHome()
	{
		// Act
		this.sut.Run([]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(this.home);
	}

	/// <summary>
	/// Checks that a bare <c>~</c>, or an empty word such as <c>cd ""</c> gives, goes to the home folder.
	/// </summary>
	/// <param name="argument">The single argument passed to the command.</param>
	[Theory]
	[InlineData("~")]
	[InlineData("")]
	public void Run_WithHomeShorthand_GoesHome(string argument)
	{
		// Act
		this.sut.Run([argument]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(this.home);
	}

	/// <summary>
	/// Checks that <c>~/</c> at the start of the path expands to the home folder.
	/// </summary>
	[Fact]
	public void Run_WithTildePrefix_GoesToFolderUnderHome()
	{
		// Arrange
		var target = Directory.CreateDirectory(Path.Combine(this.home, "sub")).FullName;

		// Act
		this.sut.Run(["~/sub"]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(target);
	}

	/// <summary>
	/// Checks that an absolute path to an existing folder becomes the working directory and nothing is printed.
	/// </summary>
	[Fact]
	public void Run_WithAbsolutePath_ChangesDirectoryQuietly()
	{
		// Arrange
		var target = this.environment.CreateFolder();

		// Act
		this.sut.Run([target]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(target);
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a relative path is resolved against the current working directory.
	/// </summary>
	[Fact]
	public void Run_WithRelativePath_GoesToChildFolder()
	{
		// Arrange
		var target = Directory.CreateDirectory(Path.Combine(this.start, "child")).FullName;

		// Act
		this.sut.Run(["child"]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(target);
	}

	/// <summary>
	/// Checks that <c>..</c> goes to the parent of the current working directory.
	/// </summary>
	[Fact]
	public void Run_WithDotDot_GoesToParentFolder()
	{
		// Act
		this.sut.Run([".."]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(Path.GetDirectoryName(this.start));
	}

	/// <summary>
	/// Checks that a path with a space, which the shell passes as one word once it's quoted, goes to that folder.
	/// </summary>
	[Fact]
	public void Run_WithPathContainingSpace_GoesToFolderWithSpace()
	{
		// Arrange
		var target = Directory.CreateDirectory(Path.Combine(this.start, "my dir")).FullName;

		// Act
		this.sut.Run(["my dir"]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(target);
	}

	/// <summary>
	/// Checks that two words print bash's "too many arguments" error and leave the folder unchanged.
	/// </summary>
	[Fact]
	public void Run_WithTwoArguments_PrintsTooManyArgumentsAndStays()
	{
		// Arrange
		_ = Directory.CreateDirectory(Path.Combine(this.start, "my dir"));

		// Act
		this.sut.Run(["my", "dir"]);

		// Assert
		_ = this.environment.Output.Should().Be("cd: too many arguments\n");
		_ = Directory.GetCurrentDirectory().Should().Be(this.start);
	}

	/// <summary>
	/// Checks that a backslash is treated as a folder separator on Unix.
	/// </summary>
	[Fact]
	public void Run_WithBackslashSeparator_GoesToNestedFolder()
	{
		// Arrange
		var target = Directory.CreateDirectory(Path.Combine(this.start, "a", "b")).FullName;

		// Act
		this.sut.Run([@"a\b"]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(target);
	}

	/// <summary>
	/// Checks that a folder that doesn't exist prints the standard error with the full path and leaves the working
	/// directory alone.
	/// </summary>
	[Fact]
	public void Run_WithMissingFolder_PrintsErrorAndStays()
	{
		// Arrange
		var missing = Path.Combine(this.start, "nosuchfolder");

		// Act
		this.sut.Run(["nosuchfolder"]);

		// Assert
		_ = this.environment.Output.Should().Be($"cd: {missing}: No such file or directory{Environment.NewLine}");
		_ = Directory.GetCurrentDirectory().Should().Be(this.start);
	}

	/// <summary>
	/// Checks that a Windows-only path is rejected on Unix and the working directory is left alone.
	/// </summary>
	/// <param name="argument">A path with a drive letter or a UNC prefix.</param>
	[Theory]
	[InlineData(@"C:\x")]
	[InlineData(@"\\server\share")]
	public void Run_WithWindowsPath_PrintsErrorAndStays(string argument)
	{
		// Act
		this.sut.Run([argument]);

		// Assert
		_ = this.environment.Output.Should().Be($"Windows path not valid on this OS: {argument}{Environment.NewLine}");
		_ = Directory.GetCurrentDirectory().Should().Be(this.start);
	}

	/// <summary>
	/// Restores the console, <c>PATH</c>, <c>HOME</c>, and working directory after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
