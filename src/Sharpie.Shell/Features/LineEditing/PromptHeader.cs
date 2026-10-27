using System.Globalization;
using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Features.Git;

namespace Sharpie.Shell.Features.LineEditing;

/// <summary>
/// Builds the header line printed above each interactive prompt: the folder, its git branch, and how long the last
/// command took.
/// </summary>
/// <remarks>
/// Colors are the standard ANSI ones, which Sharpie's window maps to its theme: black is its muted gray, blue its
/// accent, magenta its branch purple and yellow its yellow. In another terminal the muted parts can come out dark.
/// </remarks>
internal static class PromptHeader
{
	/// <summary>
	/// The prompt symbol in bold blue, drawn in front of the line being typed.
	/// </summary>
	/// <remarks>
	/// The color codes sit between <c>\x01</c> and <c>\x02</c>, the marks <c>\[</c> and <c>\]</c> leave in a
	/// <c>PS1</c>, so they aren't counted as columns.
	/// </remarks>
	public const string Symbol = "\u0001\e[1;34m\u0002❯\u0001\e[0m\u0002 ";

	// Commands that finish faster than this show no time, so quick ones like ls and cd don't add clutter after every
	// line.
	private static readonly TimeSpan SlowCommand = TimeSpan.FromSeconds(2);

	/// <summary>
	/// Builds the text that goes before <see cref="Symbol" />.
	/// </summary>
	/// <param name="directory">The shell's working directory.</param>
	/// <param name="took">How long the last command ran.</param>
	/// <param name="columns">The terminal's width in columns.</param>
	/// <param name="blankLine">
	/// <see langword="true" /> to leave a blank line between the last command's output and the header; otherwise,
	/// <see langword="false" />, as on an empty screen.
	/// </param>
	/// <returns>
	/// What <see cref="Start" /> gives, the header line, and the line break after it.
	/// </returns>
	public static string Build(string directory, TimeSpan took, int columns, bool blankLine)
	{
		_ = directory.ArgumentNotNullOrEmpty();

		var (parent, name) = SplitForDisplay(directory);
		var header = new StringBuilder(Start(directory, columns));

		if (blankLine)
		{
			_ = header.Append("\r\n");
		}

		_ = header.Append(CultureInfo.InvariantCulture, $"\e[30m{parent}\e[1;34m{name}\e[0m");

		if (GitRepository.FindBranch(directory) is { } branch)
		{
			_ = header.Append(CultureInfo.InvariantCulture, $"\e[30m on \e[35m{branch}\e[0m");
		}

		if (took >= SlowCommand)
		{
			_ = header.Append(CultureInfo.InvariantCulture, $"\e[30m took \e[33m{FormatDuration(took)}\e[0m");
		}

		return header.Append("\r\n").ToString();
	}

	/// <summary>
	/// Builds what every prompt starts with, the default header and a <c>PS1</c> alike.
	/// </summary>
	/// <param name="directory">The shell's working directory.</param>
	/// <param name="columns">The terminal's width in columns.</param>
	/// <returns>
	/// The OSC 7 working directory report, then a row of spaces, a carriage return and an erase.
	/// </returns>
	/// <remarks>
	/// The window reads the working directory from the report for its title and man pages, and other terminals use it
	/// to open new tabs in the same folder. The row is the trick zsh calls <c>PROMPT_SP</c>. When the cursor is already
	/// at the start of a line nothing moves. When the last output didn't end with a newline, the spaces wrap onto a
	/// fresh line first, so the prompt never lands after that output.
	/// </remarks>
	internal static string Start(string directory, int columns) =>
		$"\e]7;{new Uri(directory).AbsoluteUri}\a{new string(' ', Math.Max(1, columns))}\r\e[K";

	/// <summary>
	/// Shortens <paramref name="directory" /> with <c>~</c> for the home folder and splits off its last folder name,
	/// which the header shows in bold.
	/// </summary>
	/// <param name="directory">The full path of the shell's working directory.</param>
	/// <returns>
	/// The path up to and including the last separator, and the last folder name. For the root or the home folder
	/// itself the first part is empty.
	/// </returns>
	internal static (string Parent, string Name) SplitForDisplay(string directory)
	{
		directory = ShortenHome(directory);

		var lastSeparator = directory.LastIndexOf(Path.DirectorySeparatorChar);

		return lastSeparator < 0 || lastSeparator == directory.Length - 1
			? (string.Empty, directory)
			: (directory[..(lastSeparator + 1)], directory[(lastSeparator + 1)..]);
	}

	/// <summary>
	/// Replaces the home folder at the start of <paramref name="directory" /> with <c>~</c>.
	/// </summary>
	/// <param name="directory">A full path.</param>
	/// <returns>
	/// <c>~</c> for the home folder itself, <c>~</c> followed by the rest of the path for a folder under it, and
	/// <paramref name="directory" /> unchanged otherwise.
	/// </returns>
	internal static string ShortenHome(string directory)
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

		return home.Length > 0 &&
			(directory == home || directory.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				? "~" + directory[home.Length..]
				: directory;
	}

	/// <summary>
	/// Writes <paramref name="elapsed" /> the short way, like <c>4s</c>, <c>2m 5s</c>, or <c>1h 3m</c>.
	/// </summary>
	/// <param name="elapsed">How long the command ran.</param>
	/// <returns>
	/// The two largest units only, since seconds stop mattering once a command runs for an hour.
	/// </returns>
	internal static string FormatDuration(TimeSpan elapsed) =>
		elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
		: elapsed.TotalMinutes >= 1 ? $"{elapsed.Minutes}m {elapsed.Seconds}s"
		: $"{elapsed.Seconds}s";
}
