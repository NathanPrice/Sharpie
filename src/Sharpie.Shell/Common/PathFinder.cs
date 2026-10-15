using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Common;

/// <summary>
/// Resolves command names to executable files by searching the <c>PATH</c> environment variable.
/// </summary>
internal static class PathFinder
{
	/// <summary>
	/// Searches each <c>PATH</c> folder in order for an executable file named <paramref name="commandName" />.
	/// </summary>
	/// <param name="commandName">The bare command name to look for, such as <c>ls</c>.</param>
	/// <returns>
	/// The full path of the first match, or <see langword="null" /> when <c>PATH</c> is unset or no folder holds an
	/// executable file with that name.
	/// </returns>
	public static string? FindExecutable(string commandName)
	{
		_ = commandName.ArgumentNotNull();

		var pathVariable = Environment.GetEnvironmentVariable("PATH");

		if (string.IsNullOrEmpty(pathVariable))
		{
			return null;
		}

		var folders = pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

		return folders.Select(folder => Path.Combine(folder, commandName)).FirstOrDefault(fullPath => File.Exists(fullPath) && IsExecutable(fullPath));
	}

	/// <summary>
	/// Lists the executable files in the <c>PATH</c> folders whose names start with <paramref name="prefix" />.
	/// </summary>
	/// <param name="prefix">The start of the command name, such as <c>gi</c>.</param>
	/// <returns>
	/// The matching file names without their folders, in <c>PATH</c> order. A name found in more than one folder
	/// appears more than once. Empty when <c>PATH</c> is unset.
	/// </returns>
	public static IEnumerable<string> FindExecutableNames(string prefix) =>
		FindFiles(prefix.ArgumentNotNull()).Where(IsExecutable).Select(file => Path.GetFileName(file));

	/// <summary>
	/// Lists the names of every file in the <c>PATH</c> folders, without checking whether each can run.
	/// </summary>
	/// <returns>
	/// The file names without their folders, in <c>PATH</c> order. A name found in more than one folder appears more
	/// than once. Empty when <c>PATH</c> is unset.
	/// </returns>
	/// <remarks>
	/// Checking the execute bit reads each file's details, and <c>PATH</c> on Linux holds thousands of files. The
	/// command-not-found suggestion only needs the few names close to what was typed, and checks just those with
	/// <see cref="FindExecutable" />.
	/// </remarks>
	public static IEnumerable<string> FindFileNames() => FindFiles(string.Empty).Select(file => Path.GetFileName(file));

	/// <summary>
	/// Lists the files in the <c>PATH</c> folders whose names start with <paramref name="prefix" />.
	/// </summary>
	/// <param name="prefix">The start of the file name, or empty for every file.</param>
	/// <returns>
	/// The full paths, in <c>PATH</c> order. Empty when <c>PATH</c> is unset.
	/// </returns>
	private static IEnumerable<string> FindFiles(string prefix)
	{
		var pathVariable = Environment.GetEnvironmentVariable("PATH");

		if (string.IsNullOrEmpty(pathVariable))
		{
			yield break;
		}

		foreach (var folder in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			IEnumerable<string> files;

			// PATH often names folders that don't exist or can't be read, and one bad entry shouldn't stop the rest.
			try
			{
				files = Directory.GetFiles(folder, prefix + "*");
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				continue;
			}

			foreach (var file in files)
			{
				// A * or ? typed in prefix acts as a wildcard in the search pattern, so check the real prefix too.
				if (Path.GetFileName(file.AsSpan()).StartsWith(prefix, StringComparison.Ordinal))
				{
					yield return file;
				}
			}
		}
	}

	/// <summary>
	/// Determines whether the file at <paramref name="filePath" /> has any execute permission bit set.
	/// </summary>
	/// <param name="filePath">The full path of an existing file.</param>
	/// <returns>
	/// <see langword="true" /> if the user, group, or other execute bit is set, or always on Windows; otherwise,
	/// <see langword="false" />, including when the file mode can't be read.
	/// </returns>
	private static bool IsExecutable(string filePath)
	{
		// Windows has no execute bit, and File.GetUnixFileMode throws there.
		if (OperatingSystem.IsWindows())
		{
			return true;
		}

		try
		{
			var mode = File.GetUnixFileMode(filePath);

			return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}
}
