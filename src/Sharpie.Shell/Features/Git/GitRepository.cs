using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Features.Git;

/// <summary>
/// Reads what the prompt header shows about the git repository a folder belongs to.
/// </summary>
/// <remarks>
/// Reads the files under <c>.git</c> directly instead of running <c>git</c>, because the shell asks before every
/// prompt and starting a process each time would slow it down. The cost is that it can't tell whether there are
/// uncommitted changes, which needs <c>git status</c>.
/// </remarks>
internal static class GitRepository
{
	/// <summary>
	/// Finds the branch checked out in the repository that holds <paramref name="directory" />.
	/// </summary>
	/// <param name="directory">The folder to start from. Its parent folders are searched too.</param>
	/// <returns>
	/// The branch name, the first 7 characters of the commit when no branch is checked out, or
	/// <see langword="null" /> when the folder isn't in a repository or its files can't be read.
	/// </returns>
	/// <remarks>
	/// A worktree or submodule has a <c>.git</c> file instead of a folder, holding <c>gitdir: </c> and the path of the
	/// real git folder.
	/// </remarks>
	public static string? FindBranch(string directory)
	{
		_ = directory.ArgumentNotNullOrEmpty();

		try
		{
			for (var folder = new DirectoryInfo(directory); folder is not null; folder = folder.Parent)
			{
				var dotGit = Path.Combine(folder.FullName, ".git");
				string gitFolder;

				if (Directory.Exists(dotGit))
				{
					gitFolder = dotGit;
				}
				else if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is var link && link.StartsWith("gitdir: ", StringComparison.Ordinal))
				{
					gitFolder = Path.GetFullPath(link["gitdir: ".Length..], folder.FullName);
				}
				else
				{
					continue;
				}

				var head = File.ReadAllText(Path.Combine(gitFolder, "HEAD")).Trim();

				return head.Length == 0
					? null
					: head.StartsWith("ref: refs/heads/", StringComparison.Ordinal)
						? head["ref: refs/heads/".Length..]
						: head[..Math.Min(7, head.Length)];
			}
		}
		// A repository that can't be read shows no branch rather than breaking the prompt.
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}

		return null;
	}
}
