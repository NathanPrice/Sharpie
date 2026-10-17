using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Changes the shell's working directory.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which holds <c>PWD</c>, <c>OLDPWD</c> and <c>CDPATH</c>.</param>
/// <remarks>
/// After each change <c>PWD</c> and <c>OLDPWD</c> are set and exported, as bash does, so <c>cd -</c> and the programs
/// the shell starts see the new and previous folders.
/// </remarks>
internal class CdCommand(DeclareCommand declare) : ICommand
{
	/// <inheritdoc />
	public string Name => "cd";
	/// <inheritdoc />
	public string Description => "Changes the directory you're in";

	/// <inheritdoc />
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var before = Directory.GetCurrentDirectory();
		var targetPath = this.ResolvePath(args, out var print);

		if (targetPath is null)
		{
			return 1;
		}

		if (!Directory.Exists(targetPath))
		{
			new ShellError("sharpie::cd::not_found", $"No such folder: `{targetPath}`").Write();

			return 1;
		}

		try
		{
			Directory.SetCurrentDirectory(targetPath);

			var after = Directory.GetCurrentDirectory();

			declare.Export("OLDPWD", before);
			declare.Export("PWD", after);

			if (print)
			{
				Console.WriteLine(after);
			}

			return 0;
		}
		catch (UnauthorizedAccessException)
		{
			new ShellError("sharpie::cd::access_denied", $"Access denied: `{targetPath}`").Write();

			return 1;
		}
	}

	/// <summary>
	/// Turns the arguments to <c>cd</c> into an absolute path for the current OS.
	/// </summary>
	/// <param name="args">The words that followed <c>cd</c> on the input line.</param>
	/// <param name="print">
	/// Set to <see langword="true" /> when the new folder should be printed after the change: for <c>cd -</c> and for a
	/// folder found through a non-empty <c>CDPATH</c> entry, as bash does.
	/// </param>
	/// <returns>
	/// The path to change to: <c>OLDPWD</c> for <c>-</c>, the user's home folder when <paramref name="args" /> is empty
	/// or holds one empty word, or else a full path. Returns <see langword="null" /> when the path can't be used, there's
	/// more than one word, or <c>-</c> is given before <c>OLDPWD</c> is set; the reason has already been printed.
	/// </returns>
	/// <remarks>
	/// The returned path is not checked for existence. <see cref="Run" /> does that so it can print the
	/// standard "No such file or directory" message. A relative path is looked up in each <c>CDPATH</c> entry in order,
	/// where an empty entry means the current folder, unless it starts with <c>.</c> or <c>..</c>. When no entry holds
	/// it, it joins the current folder.
	/// </remarks>
	private string? ResolvePath(string[]? args, out bool print)
	{
		print = false;

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
			new ShellError("sharpie::cd::too_many_arguments", Properties.Resources.CdTooManyArguments)
			{
				Help = "quote a folder name that has spaces, like cd 'My Files'",
			}.Write();

			return null;
		}

		var input = args[0];

		if (input.Length == 0)
		{
			return home;
		}

		if (input == "-")
		{
			var previous = CommandLine.Lookup(declare.Variables, "OLDPWD");

			if (previous.Length == 0)
			{
				new ShellError("sharpie::cd::no_oldpwd", "OLDPWD isn't set") { Help = "cd somewhere first" }.Write();

				return null;
			}

			print = true;

			return previous;
		}

		if (!OperatingSystem.IsWindows())
		{
			// Windows-only path forms have no meaning on Unix
			if (HasDriveLetter(input) || input.StartsWith(@"\\", StringComparison.Ordinal))
			{
				new ShellError("sharpie::cd::windows_path", $"Windows path not valid on this OS: `{input}`").Write();

				return null;
			}

			// Unix only treats '/' as a separator; accept '\' too
			input = input.Replace('\\', '/');
		}

		try
		{
			var current = Directory.GetCurrentDirectory();

			if (!Path.IsPathRooted(input) && input.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] is not ("." or ".."))
			{
				foreach (var entry in CommandLine.Lookup(declare.Variables, "CDPATH").Split(Path.PathSeparator))
				{
					var candidate = Path.GetFullPath(Path.Combine(current, entry, input));

					if (Directory.Exists(candidate))
					{
						print = entry.Length > 0;

						return candidate;
					}
				}
			}

			// Absolute input replaces the current directory; relative input joins it.
			// GetFullPath normalizes "..", ".", and separators for the current OS.
			return Path.GetFullPath(Path.Combine(current, input));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			new ShellError("sharpie::cd::invalid_path", $"Invalid path: {ex.Message}").Write();

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
