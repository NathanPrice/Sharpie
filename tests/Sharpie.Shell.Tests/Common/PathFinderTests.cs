using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="PathFinder" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class PathFinderTests : IDisposable
{
	private readonly ShellEnvironment environment = new();

	/// <summary>
	/// Checks that nothing is found when <c>PATH</c> is unset or empty.
	/// </summary>
	/// <param name="pathValue">The value to give <c>PATH</c>; <see langword="null" /> unsets it.</param>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void FindExecutable_WhenPathUnsetOrEmpty_ReturnsNull(string? pathValue)
	{
		// Arrange
		Environment.SetEnvironmentVariable("PATH", pathValue);

		// Act
		var result = PathFinder.FindExecutable("ls");

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks that an executable file in a <c>PATH</c> folder is returned by its full path.
	/// </summary>
	[Fact]
	public void FindExecutable_WhenExecutableInPathFolder_ReturnsFullPath()
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		var fullPath = ShellEnvironment.CreateFile(folder, "mytool", executable: true);
		ShellEnvironment.SetPath(folder);

		// Act
		var result = PathFinder.FindExecutable("mytool");

		// Assert
		_ = result.Should().Be(fullPath);
	}

	/// <summary>
	/// Checks that a matching file without an execute bit is ignored.
	/// </summary>
	[Fact]
	public void FindExecutable_WhenFileNotExecutable_ReturnsNull()
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(folder, "mytool", executable: false);
		ShellEnvironment.SetPath(folder);

		// Act
		var result = PathFinder.FindExecutable("mytool");

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks that the earliest <c>PATH</c> folder wins when two hold an executable with the same name.
	/// </summary>
	[Fact]
	public void FindExecutable_WhenExecutableInTwoFolders_ReturnsFirst()
	{
		// Arrange
		var first = this.environment.CreateFolder();
		var second = this.environment.CreateFolder();
		var expected = ShellEnvironment.CreateFile(first, "mytool", executable: true);
		_ = ShellEnvironment.CreateFile(second, "mytool", executable: true);
		ShellEnvironment.SetPath(first, second);

		// Act
		var result = PathFinder.FindExecutable("mytool");

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a non-executable match in an earlier folder doesn't stop the search.
	/// </summary>
	[Fact]
	public void FindExecutable_WhenFirstMatchNotExecutable_ReturnsLaterExecutable()
	{
		// Arrange
		var first = this.environment.CreateFolder();
		var second = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(first, "mytool", executable: false);
		var expected = ShellEnvironment.CreateFile(second, "mytool", executable: true);
		ShellEnvironment.SetPath(first, second);

		// Act
		var result = PathFinder.FindExecutable("mytool");

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that empty <c>PATH</c> entries are skipped.
	/// </summary>
	[Fact]
	public void FindExecutable_WhenPathHasEmptyEntries_SkipsThem()
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		var expected = ShellEnvironment.CreateFile(folder, "mytool", executable: true);
		ShellEnvironment.SetPath("", "", folder);

		// Act
		var result = PathFinder.FindExecutable("mytool");

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a name missing from every <c>PATH</c> folder isn't found.
	/// </summary>
	[Fact]
	public void FindExecutable_WhenNotInAnyFolder_ReturnsNull()
	{
		// Arrange
		ShellEnvironment.SetPath(this.environment.CreateFolder(), this.environment.CreateFolder());

		// Act
		var result = PathFinder.FindExecutable("mytool");

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks that <see cref="PathFinder.FindExecutableNames" /> returns only executables starting with the prefix,
	/// and skips a <c>PATH</c> folder that doesn't exist.
	/// </summary>
	[Fact]
	public void FindExecutableNames_WithMixedFiles_ReturnsOnlyMatchingExecutables()
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(folder, "mytool", executable: true);
		_ = ShellEnvironment.CreateFile(folder, "mydoc", executable: false);
		_ = ShellEnvironment.CreateFile(folder, "other", executable: true);
		ShellEnvironment.SetPath(Path.Combine(folder, "missing"), folder);

		// Act
		var result = PathFinder.FindExecutableNames("my");

		// Assert
		_ = result.Should().Equal("mytool");
	}

	/// <summary>
	/// Checks that <see cref="PathFinder.FindFileNames" /> lists every file on <c>PATH</c>, including one that can't
	/// run, which <see cref="PathFinder.FindExecutableNames" /> leaves out.
	/// </summary>
	[Fact]
	public void FindFileNames_WithMixedFiles_ListsEveryFile()
	{
		// Arrange
		var folder = this.environment.CreateFolder();

		_ = ShellEnvironment.CreateFile(folder, "sampletool", executable: true);
		_ = ShellEnvironment.CreateFile(folder, "samplenotes", executable: false);
		ShellEnvironment.SetPath(folder);

		// Act
		var all = PathFinder.FindFileNames();
		var executables = PathFinder.FindExecutableNames(string.Empty);

		// Assert
		_ = all.Should().BeEquivalentTo("sampletool", "samplenotes");
		_ = executables.Should().Equal(OperatingSystem.IsWindows() ? ["sampletool", "samplenotes"] : ["sampletool"]);
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
