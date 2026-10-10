using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Changes the shell's working directory.
/// </summary>
internal class CdCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "cd";
	/// <inheritdoc />
	public string Description => "Changes the directory you're in";

	/// <inheritdoc />
	public void Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var targetPath = ResolvePath(args);

		if (targetPath is null)
		{
			return;
		}

		if (!Directory.Exists(targetPath))
		{
			Console.WriteLine($@"{this.Name}: {targetPath}: No such file or directory");

			return;
		}

		try
		{
			Directory.SetCurrentDirectory(targetPath);
		}
		catch (UnauthorizedAccessException)
		{
			Console.WriteLine($@"Access denied: {targetPath}");
		}
	}

	/// <summary>
	/// Turns the arguments to <c>cd</c> into an absolute path for the current OS.
	/// </summary>
	/// <param name="args">The words that followed <c>cd</c> on the input line.</param>
	/// <returns>
	/// The full path to change to, or the user's home folder when <paramref name="args" /> is empty or holds one empty
	/// word. Returns <see langword="null" /> when the path can't be used or there's more than one word; the reason has
	/// already been printed.
	/// </returns>
	/// <remarks>
	/// The returned path is not checked for existence. <see cref="Run" /> does that so it can print the
	/// standard "No such file or directory" message.
	/// </remarks>
	private static string? ResolvePath(string[]? args)
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

		// No argument: go home, like Unix "cd"
		if (args is null || args.Length == 0)
		{
			return home;
		}

		// CommandLine.Split has already removed the quotes and kept a quoted path in one word, so more than one word is
		// a mistake, as in bash.
		if (args.Length > 1)
		{
			Console.WriteLine(Properties.Resources.CdTooManyArguments);

			return null;
		}

		var input = args[0];

		if (input.Length == 0)
		{
			return home;
		}

		if (!OperatingSystem.IsWindows())
		{
			// Windows-only path forms have no meaning on Unix
			if (HasDriveLetter(input) || input.StartsWith(@"\\", StringComparison.Ordinal))
			{
				Console.WriteLine($@"Windows path not valid on this OS: {input}");

				return null;
			}

			// Unix only treats '/' as a separator; accept '\' too
			input = input.Replace('\\', '/');
		}

		// Expand ~. Both "~/" and "~\" are checked because Windows keeps its backslashes;
		// only the Unix branch above rewrites them.
		if (input == "~" || input.StartsWith("~/", StringComparison.Ordinal) || input.StartsWith(@"~\", StringComparison.Ordinal))
		{
			input = input.Length > 1 ? Path.Combine(home, input[2..]) : home;
		}

		try
		{
			// Absolute input replaces the current directory; relative input joins it.
			// GetFullPath normalizes "..", ".", and separators for the current OS.
			return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), input));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			Console.WriteLine($@"Invalid path: {ex.Message}");

			return null;
		}
	}

	/// <summary>
	/// Checks whether <paramref name="path" /> starts with a Windows drive letter such as <c>C:</c>.
	/// </summary>
	/// <param name="path">The path to check.</param>
	/// <returns>
	/// <see langword="true" /> if the first two characters are an ASCII letter and a colon; otherwise,
	/// <see langword="false" />.
	/// </returns>
	private static bool HasDriveLetter(string path) => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
}
