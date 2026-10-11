using DotNetTips.Spargine.Core;

namespace Sharpie.Features.Terminal;

/// <summary>
/// Reads the command line the window was started with, the way desktops start a terminal for a program.
/// </summary>
internal static class LaunchArguments
{
	/// <summary>
	/// The option that runs a program in the window instead of an interactive shell.
	/// </summary>
	/// <remarks>
	/// xterm's <c>-e</c>, which KDE, GNOME and <c>xdg-terminal-exec</c> pass to a terminal other than their own for
	/// an app whose desktop file says <c>Terminal=true</c>.
	/// </remarks>
	internal const string ExecuteOption = "-e";

	/// <summary>
	/// Gets the program and arguments to run, as the words after <see cref="ExecuteOption" />.
	/// </summary>
	/// <param name="args">The window's command-line arguments.</param>
	/// <returns>
	/// The program name followed by its arguments, or <see langword="null" /> when <see cref="ExecuteOption" /> is
	/// missing or nothing follows it.
	/// </returns>
	internal static string[]? Command(IReadOnlyList<string> args)
	{
		var index = args.ArgumentNotNull().ToList().IndexOf(ExecuteOption);

		return index >= 0 && index < args.Count - 1 ? [.. args.Skip(index + 1)] : null;
	}
}
