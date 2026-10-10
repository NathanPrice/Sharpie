using FluentAssertions;
using Sharpie.Shell.Features.Git;

namespace Sharpie.Shell.Tests.Features.Git;

/// <summary>
/// Tests for <see cref="GitRepository" />.
/// </summary>
public sealed class GitRepositoryTests : IDisposable
{
	private readonly string root = Directory.CreateTempSubdirectory("git-tests-").FullName;

	/// <summary>
	/// Checks that a checked-out branch is reported by name, including names with a slash in them.
	/// </summary>
	/// <param name="head">The contents of <c>HEAD</c>.</param>
	/// <param name="expected">The branch the prompt should show.</param>
	[Theory]
	[InlineData("ref: refs/heads/master\n", "master")]
	[InlineData("ref: refs/heads/feature/prompt\n", "feature/prompt")]
	[InlineData("0123456789abcdef0123456789abcdef01234567\n", "0123456")]
	public void FindBranch_InRepositoryRoot_ReadsHead(string head, string expected)
	{
		// Arrange
		CreateGitFolder(this.root, head);

		// Act
		var branch = GitRepository.FindBranch(this.root);

		// Assert
		_ = branch.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a folder deep inside a repository finds the repository's <c>.git</c> folder above it.
	/// </summary>
	[Fact]
	public void FindBranch_InNestedFolder_FindsParentRepository()
	{
		// Arrange
		CreateGitFolder(this.root, "ref: refs/heads/main\n");
		var nested = Directory.CreateDirectory(Path.Combine(this.root, "src", "deep")).FullName;

		// Act
		var branch = GitRepository.FindBranch(nested);

		// Assert
		_ = branch.Should().Be("main");
	}

	/// <summary>
	/// Checks that a worktree's <c>.git</c> file is followed to the git folder it names.
	/// </summary>
	[Fact]
	public void FindBranch_WithGitFile_FollowsGitdir()
	{
		// Arrange
		var realGit = Directory.CreateDirectory(Path.Combine(this.root, "real")).FullName;
		File.WriteAllText(Path.Combine(realGit, "HEAD"), "ref: refs/heads/worktree\n");
		var worktree = Directory.CreateDirectory(Path.Combine(this.root, "tree")).FullName;
		File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../real\n");

		// Act
		var branch = GitRepository.FindBranch(worktree);

		// Assert
		_ = branch.Should().Be("worktree");
	}

	/// <summary>
	/// Checks that a folder outside any repository has no branch.
	/// </summary>
	/// <remarks>
	/// Uses the file system root, since the temporary folder could itself sit inside a repository on some machines.
	/// </remarks>
	[Fact]
	public void FindBranch_OutsideRepository_ReturnsNull()
	{
		// Act
		var branch = GitRepository.FindBranch(Path.GetPathRoot(this.root)!);

		// Assert
		_ = branch.Should().BeNull();
	}

	/// <summary>
	/// Deletes the temporary folder.
	/// </summary>
	public void Dispose()
	{
		Directory.Delete(this.root, recursive: true);
	}

	/// <summary>
	/// Creates a <c>.git</c> folder in <paramref name="folder" /> holding only a <c>HEAD</c> file.
	/// </summary>
	/// <param name="folder">The repository's top folder.</param>
	/// <param name="head">The contents of <c>HEAD</c>.</param>
	private static void CreateGitFolder(string folder, string head)
	{
		var git = Directory.CreateDirectory(Path.Combine(folder, ".git")).FullName;
		File.WriteAllText(Path.Combine(git, "HEAD"), head);
	}
}
