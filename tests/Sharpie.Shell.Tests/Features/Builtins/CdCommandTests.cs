using FluentAssertions;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="CdCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class CdCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new();
	private readonly CdCommand sut;
	private readonly string home;
	private readonly string start;

	/// <summary>
	/// Initializes a new instance of the <see cref="CdCommandTests" /> class with <c>HOME</c> and the working
	/// directory set to two separate empty folders.
	/// </summary>
	public CdCommandTests()
	{
		this.sut = new(this.declare);
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
	/// Checks that an empty word, such as <c>cd ""</c> gives, goes to the home folder.
	/// </summary>
	[Fact]
	public void Run_WithEmptyWord_GoesHome()
	{
		// Act
		_ = this.sut.Run([string.Empty]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(this.home);
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
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::cd::too_many_arguments", "Too many arguments")
		{
			Help = "quote a folder name that has spaces, like cd 'My Files'",
		}.Render(color: false));
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
	/// Checks that a one-letter folder name, too short to hold a drive letter and colon, goes there.
	/// </summary>
	[Fact]
	public void Run_WithOneLetterFolder_GoesThere()
	{
		// Arrange
		var folder = Directory.CreateDirectory(Path.Combine(this.start, "a")).FullName;

		// Act
		_ = this.sut.Run(["a"]);

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
		_ = this.environment.Error.Should().Contain("sharpie::cd::invalid_path").And.Contain("× Invalid path: ");
		_ = Directory.GetCurrentDirectory().Should().Be(this.start);
	}

	/// <summary>
	/// Checks that a change exports the new folder as <c>PWD</c> and the old one as <c>OLDPWD</c>, both in the
	/// environment and as shell variables.
	/// </summary>
	[Fact]
	public void Run_WithAbsolutePath_ExportsPwdAndOldPwd()
	{
		// Arrange
		var target = this.environment.CreateFolder();

		// Act
		var status = this.sut.Run([target]);

		// Assert
		_ = status.Should().Be(0);
		_ = Environment.GetEnvironmentVariable("PWD").Should().Be(target);
		_ = Environment.GetEnvironmentVariable("OLDPWD").Should().Be(this.start);
		_ = this.declare.Variables.Should().Contain("PWD", target).And.Contain("OLDPWD", this.start);
	}

	/// <summary>
	/// Checks that a failed change leaves <c>PWD</c> and <c>OLDPWD</c> as they were.
	/// </summary>
	[Fact]
	public void Run_WithMissingFolder_LeavesPwdAndOldPwdAlone()
	{
		// Arrange
		Environment.SetEnvironmentVariable("PWD", "/before");

		// Act
		var status = this.sut.Run(["nosuchfolder"]);

		// Assert
		_ = status.Should().Be(1);
		_ = Environment.GetEnvironmentVariable("PWD").Should().Be("/before");
		_ = Environment.GetEnvironmentVariable("OLDPWD").Should().BeNull();
		_ = this.declare.Variables.Should().NotContainKey("PWD").And.NotContainKey("OLDPWD");
	}

	/// <summary>
	/// Checks that <c>cd -</c> goes back to the previous folder and prints it, and a second <c>cd -</c> swaps back.
	/// </summary>
	[Fact]
	public void Run_WithDash_GoesBackAndPrintsFolder()
	{
		// Arrange
		var target = this.environment.CreateFolder();
		_ = this.sut.Run([target]);

		// Act
		var first = this.sut.Run(["-"]);
		var afterFirst = Directory.GetCurrentDirectory();
		var second = this.sut.Run(["-"]);

		// Assert
		_ = first.Should().Be(0);
		_ = second.Should().Be(0);
		_ = afterFirst.Should().Be(this.start);
		_ = Directory.GetCurrentDirectory().Should().Be(target);
		_ = this.environment.Output.Should().Be($"{this.start}{Environment.NewLine}{target}{Environment.NewLine}");
		_ = Environment.GetEnvironmentVariable("OLDPWD").Should().Be(this.start);
		_ = this.declare.Variables.Should().Contain("PWD", target).And.Contain("OLDPWD", this.start);
	}

	/// <summary>
	/// Checks that <c>cd -</c> before any change reports that <c>OLDPWD</c> isn't set and stays put.
	/// </summary>
	[Fact]
	public void Run_WithDashBeforeAnyChange_PrintsOldPwdNotSet()
	{
		// Act
		var status = this.sut.Run(["-"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::cd::no_oldpwd", "OLDPWD isn't set") { Help = "cd somewhere first" }.Render(color: false));
		_ = this.environment.Output.Should().BeEmpty();
		_ = Directory.GetCurrentDirectory().Should().Be(this.start);
	}

	/// <summary>
	/// Checks that a <c>PWD</c> shell variable set earlier is updated too, since shell variables win over the
	/// environment when <c>$PWD</c> expands.
	/// </summary>
	[Fact]
	public void Run_WithStaleShellVariable_UpdatesIt()
	{
		// Arrange
		var target = this.environment.CreateFolder();
		_ = this.declare.Run(["PWD=/stale"]);

		// Act
		_ = this.sut.Run([target]);

		// Assert
		_ = CommandLine.Lookup(this.declare.Variables, "PWD").Should().Be(target);
		_ = Environment.GetEnvironmentVariable("PWD").Should().Be(target);
	}

	/// <summary>
	/// Checks that a relative name is found in the first <c>CDPATH</c> folder that holds it, skipping one that doesn't,
	/// and the folder reached is printed.
	/// </summary>
	[Fact]
	public void Run_WithCdPath_FindsFolderAndPrintsIt()
	{
		// Arrange
		var empty = this.environment.CreateFolder();
		var projects = this.environment.CreateFolder();
		var target = Directory.CreateDirectory(Path.Combine(projects, "sampletool")).FullName;
		var later = this.environment.CreateFolder();
		_ = Directory.CreateDirectory(Path.Combine(later, "sampletool"));
		Environment.SetEnvironmentVariable("CDPATH", string.Join(Path.PathSeparator, empty, projects, later));

		// Act
		var status = this.sut.Run(["sampletool"]);

		// Assert
		_ = status.Should().Be(0);
		_ = Directory.GetCurrentDirectory().Should().Be(target);
		_ = this.environment.Output.Should().Be($"{target}{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that an empty <c>CDPATH</c> entry stands for the current folder, wins over later entries, and prints
	/// nothing.
	/// </summary>
	[Fact]
	public void Run_WithEmptyCdPathEntry_PrefersCurrentFolderQuietly()
	{
		// Arrange
		var projects = this.environment.CreateFolder();
		_ = Directory.CreateDirectory(Path.Combine(projects, "sampletool"));
		var target = Directory.CreateDirectory(Path.Combine(this.start, "sampletool")).FullName;
		Environment.SetEnvironmentVariable("CDPATH", string.Join(Path.PathSeparator, string.Empty, projects));

		// Act
		_ = this.sut.Run(["sampletool"]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(target);
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a name no <c>CDPATH</c> entry holds is still found in the current folder, quietly.
	/// </summary>
	[Fact]
	public void Run_WithCdPathMiss_FallsBackToCurrentFolder()
	{
		// Arrange
		var projects = this.environment.CreateFolder();
		var target = Directory.CreateDirectory(Path.Combine(this.start, "sampletool")).FullName;
		Environment.SetEnvironmentVariable("CDPATH", projects);

		// Act
		_ = this.sut.Run(["sampletool"]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(target);
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a path starting with <c>./</c> or <c>../</c> skips <c>CDPATH</c>, even when an entry holds the
	/// same name.
	/// </summary>
	/// <param name="prefix">The leading path part that turns off the <c>CDPATH</c> search.</param>
	[Theory]
	[InlineData(".")]
	[InlineData("..")]
	public void Run_WithDotSlash_IgnoresCdPath(string prefix)
	{
		// Arrange
		var projects = this.environment.CreateFolder();
		_ = Directory.CreateDirectory(Path.Combine(projects, "sampletool"));
		var target = Path.GetFullPath(Path.Combine(this.start, prefix, "sampletool"));
		_ = Directory.CreateDirectory(target);
		Environment.SetEnvironmentVariable("CDPATH", projects);

		// Act
		_ = this.sut.Run([$"{prefix}/sampletool"]);

		// Assert
		_ = Directory.GetCurrentDirectory().Should().Be(target);
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Restores the console, <c>PATH</c>, <c>HOME</c>, <c>PWD</c>, <c>OLDPWD</c>, <c>CDPATH</c>, and working directory
	/// after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
