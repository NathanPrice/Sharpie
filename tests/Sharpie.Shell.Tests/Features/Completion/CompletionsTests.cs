using FluentAssertions;
using Sharpie.Shell.Features.Completion;

namespace Sharpie.Shell.Tests.Features.Completion;

/// <summary>
/// Tests for <see cref="Completions" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class CompletionsTests : IDisposable
{
	private static readonly string[] Names = ["echo", "exit", "pwd", "cd", "clear", "type"];

	private readonly ShellEnvironment environment = new();
	private readonly string pathFolder;

	/// <summary>
	/// Initializes a new instance of the <see cref="CompletionsTests" /> class with <c>PATH</c> pointed at an empty
	/// folder, so programs installed on this machine can't change the results.
	/// </summary>
	public CompletionsTests()
	{
		this.pathFolder = this.environment.CreateFolder();
		ShellEnvironment.SetPath(this.pathFolder);
	}

	/// <summary>
	/// Checks that <see cref="Completions.Complete" /> finishes a unique command name and adds a trailing space.
	/// </summary>
	/// <param name="partial">The text typed before Tab.</param>
	/// <param name="expected">The text Tab should add.</param>
	[Theory]
	[InlineData("ech", "o ")]
	[InlineData("exi", "t ")]
	[InlineData("echo", " ")]
	public void Complete_WithUniqueMatch_ReturnsRestAndSpace(string partial, string expected)
	{
		// Act
		var result = Completions.Complete(partial, Names);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <see cref="Completions.Complete" /> adds nothing when the text matches no name or matches several.
	/// </summary>
	/// <param name="partial">The text typed before Tab.</param>
	[Theory]
	[InlineData("e")]
	[InlineData("xyz")]
	public void Complete_WithoutUniqueMatch_ReturnsNull(string partial)
	{
		// Act
		var result = Completions.Complete(partial, Names);

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks that <see cref="Completions.Complete" /> finishes the name of an executable in a <c>PATH</c> folder.
	/// </summary>
	[Fact]
	public void Complete_WithExecutableOnPath_ReturnsRestAndSpace()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.pathFolder, "mytool", executable: true);

		// Act
		var result = Completions.Complete("myt", Names);

		// Assert
		_ = result.Should().Be("ool ");
	}

	/// <summary>
	/// Checks that a file on <c>PATH</c> without an execute bit isn't offered.
	/// </summary>
	[Fact]
	public void Complete_WithNonExecutableOnPath_ReturnsNull()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.pathFolder, "mytool", executable: false);

		// Act
		var result = Completions.Complete("myt", Names);

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks that a builtin which is also an executable on <c>PATH</c> counts as one match, not two.
	/// </summary>
	[Fact]
	public void Complete_WithBuiltinAlsoOnPath_StillCompletes()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.pathFolder, "echo", executable: true);

		// Act
		var result = Completions.Complete("ech", Names);

		// Assert
		_ = result.Should().Be("o ");
	}

	/// <summary>
	/// Checks that when several programs match, <see cref="Completions.Complete" /> adds the part they all share, with
	/// no trailing space, and adds nothing once there's nothing more to share.
	/// </summary>
	/// <param name="partial">The text typed before Tab.</param>
	/// <param name="expected">The text Tab should add, or <see langword="null" /> for nothing.</param>
	[Theory]
	[InlineData("my", "tool")]
	[InlineData("mytool", null)]
	public void Complete_WithSeveralMatches_ReturnsSharedPart(string partial, string? expected)
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.pathFolder, "mytool1", executable: true);
		_ = ShellEnvironment.CreateFile(this.pathFolder, "mytool2", executable: true);

		// Act
		var result = Completions.Complete(partial, Names);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a builtin and a program sharing its start, like <c>exit</c> and <c>exiv2</c>, leave Tab with
	/// nothing to add.
	/// </summary>
	[Fact]
	public void Complete_WithBuiltinAndProgramSharingPrefix_ReturnsNull()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.pathFolder, "exiv2", executable: true);

		// Act
		var result = Completions.Complete("exi", Names);

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks that <see cref="Completions.FindMatches" /> returns the matches sorted, and a builtin that is also on
	/// <c>PATH</c> only once.
	/// </summary>
	/// <param name="partial">The text typed before Tab.</param>
	/// <param name="expected">The matches, in order.</param>
	[Theory]
	[InlineData("my", new[] { "mytool1", "mytool2" })]
	[InlineData("ech", new[] { "echo" })]
	public void FindMatches_WithProgramsOnPath_ReturnsSortedDistinctNames(string partial, string[] expected)
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.pathFolder, "mytool2", executable: true);
		_ = ShellEnvironment.CreateFile(this.pathFolder, "mytool1", executable: true);
		_ = ShellEnvironment.CreateFile(this.pathFolder, "echo", executable: true);

		// Act
		var result = Completions.FindMatches(partial, Names);

		// Assert
		_ = result.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a word after the command completes to a file in the working directory with a trailing space, to a
	/// directory with a trailing slash, and that a name that doesn't match isn't offered.
	/// </summary>
	/// <param name="partial">The text typed before Tab.</param>
	/// <param name="expected">The text Tab should add, or <see langword="null" /> for nothing.</param>
	[Theory]
	[InlineData("cat re", "adme.txt ")]
	[InlineData("cat a.txt re", "adme.txt ")]
	[InlineData("cat no", null)]
	[InlineData("cat sr", "c/")]
	[InlineData(" re", "adme.txt ")]
	public void Complete_WithArgument_CompletesFileName(string partial, string? expected)
	{
		// Arrange
		var folder = this.environment.CreateFolder();

		_ = ShellEnvironment.CreateFile(folder, "readme.txt", executable: false);
		_ = ShellEnvironment.CreateFile(folder, "a.txt", executable: false);
		_ = Directory.CreateDirectory(Path.Combine(folder, "src"));
		Directory.SetCurrentDirectory(folder);

		// Act
		var result = Completions.Complete(partial, Names);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a word holding a <c>/</c> completes to a file in that folder, keeping the folder part, and that a
	/// missing folder or file adds nothing.
	/// </summary>
	/// <param name="partial">The text typed before Tab.</param>
	/// <param name="expected">The text Tab should add, or <see langword="null" /> for nothing.</param>
	[Theory]
	[InlineData("cat path/to/f", "ile.txt ")]
	[InlineData("cat path/to/x", null)]
	[InlineData("cat nope/f", null)]
	public void Complete_WithPathInArgument_CompletesFileInThatFolder(string partial, string? expected)
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		var nested = Directory.CreateDirectory(Path.Combine(folder, "path", "to")).FullName;

		_ = ShellEnvironment.CreateFile(nested, "file.txt", executable: false);
		Directory.SetCurrentDirectory(folder);

		// Act
		var result = Completions.Complete(partial, Names);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a directory completes with a slash and no space, both at the top level and one folder down.
	/// </summary>
	/// <param name="partial">The text typed before Tab.</param>
	/// <param name="expected">The text Tab should add.</param>
	[Theory]
	[InlineData("ls ", "pig/")]
	[InlineData("ls p", "ig/")]
	[InlineData("ls pig/", "dog/")]
	public void Complete_WithDirectory_AddsSlashWithoutSpace(string partial, string expected)
	{
		// Arrange
		var folder = this.environment.CreateFolder();

		_ = Directory.CreateDirectory(Path.Combine(folder, "pig", "dog"));
		Directory.SetCurrentDirectory(folder);

		// Act
		var result = Completions.Complete(partial, Names);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks the start every word shares, whichever word is shorter.
	/// </summary>
	/// <param name="words">The words, separated by <c>|</c>.</param>
	/// <param name="expected">The shared start.</param>
	[Theory]
	[InlineData("abc|ab", "ab")]
	[InlineData("ab|abc", "ab")]
	[InlineData("checkout|cherry-pick|chmod", "ch")]
	[InlineData("ab|cd", "")]
	[InlineData("only", "only")]
	public void SharedPrefix_Words_ReturnsCommonStart(string words, string expected)
	{
		// Act
		var result = Completions.SharedPrefix(words.Split('|'));

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
