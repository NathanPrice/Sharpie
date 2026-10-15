using FluentAssertions;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Common;

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
		_ = this.sut.Run([]);

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
		_ = this.sut.Run([argument]);

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
		_ = this.sut.Run(["~/sub"]);

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
		_ = this.sut.Run([target]);

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
		_ = this.sut.Run(["child"]);

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
		_ = this.sut.Run([".."]);

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
		_ = this.sut.Run(["my dir"]);

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
		_ = this.sut.Run(["my", "dir"]);

		// Assert
		_ = this.environment.Error.Should().Contain("sharpie::cd::too_many_arguments");
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
		_ = this.sut.Run([@"a\b"]);

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
		_ = this.sut.Run(["nosuchfolder"]);

		// Assert
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::cd::not_found", $"No such folder: `{missing}`").Render(color: false));
		_ = Directory.GetCurrentDirectory().Should().Be(this.start);
	}

	/// <summary>
	/// Checks that a Windows-only path is rejected on Unix and the working directory is left alone.
	/// </summary>
	/// <param name="argument">A path with a drive letter or a UNC prefix.</param>
	[Theory]
	[InlineData(@"C:\x")]
	[InlineData("C:")]
	[InlineData(@"\\server\share")]
	public void Run_WithWindowsPath_PrintsErrorAndStays(string argument)
	{
		// Act
		_ = this.sut.Run([argument]);

		// Assert
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::cd::windows_path", $"Windows path not valid on this OS: `{argument}`").Render(color: false));
		_ = Directory.GetCurrentDirectory().Should().Be(this.start);
	}

	/// <summary>
	/// Checks that a two-letter folder name isn't mistaken for a Windows drive.
	/// </summary>
	[Fact]
	public void Run_WithTwoLetterFolder_GoesThere()
	{
		// Arrange
		var folder = Directory.CreateDirectory(Path.Combine(this.start, "ab")).FullName;

		// Act
		_ = this.sut.Run(["ab"]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(folder);
	}

	/// <summary>
	/// Checks that a folder this account can't enter is reported and the working directory is left alone.
	/// </summary>
	[Fact]
	public void Run_WithFolderWithoutAccess_PrintsAccessDenied()
	{
		// Arrange
		var folder = Directory.CreateDirectory(Path.Combine(this.start, "locked")).FullName;
		File.SetUnixFileMode(folder, UnixFileMode.None);

		try
		{
			// Act
			_ = this.sut.Run([folder]);

			// Assert
			_ = this.environment.Error.Should().Be(new ShellError("sharpie::cd::access_denied", $"Access denied: `{folder}`").Render(color: false));
			_ = Directory.GetCurrentDirectory().Should().Be(this.start);
		}
		finally
		{
			// The temporary folder can't be deleted while this one can't be listed.
			File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	/// <summary>
	/// Checks that a path the file system can't hold, like one with a null character, is reported instead of
	/// ending the shell.
	/// </summary>
	[Fact]
	public void Run_WithNullCharacter_PrintsInvalidPath()
	{
		// Act
		_ = this.sut.Run(["a\0b"]);

		// Assert
		_ = this.environment.Error.Should().Contain("× Invalid path: ");
		_ = Directory.GetCurrentDirectory().Should().Be(this.start);
	}

	/// <summary>
	/// Restores the console, <c>PATH</c>, <c>HOME</c>, and working directory after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
