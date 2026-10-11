using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.History;

/// <summary>
/// Lists the command lines the shell has read, oldest first and numbered from 1.
/// </summary>
internal class HistoryCommand : ICommand
{
	/// <summary>
	/// The most lines kept in memory and on disk. Bash defaults to 500, and Ubuntu's stock <c>.bashrc</c> raises it
	/// to 1000. The cap keeps the saved file from growing without bound.
	/// </summary>
	public const int MaxLines = 1000;

	/// <inheritdoc />
	public string Name => "history";
	/// <inheritdoc />
	public string Description => "Lists previously run commands";

	/// <summary>
	/// Gets the remembered lines, oldest first.
	/// </summary>
	/// <value>
	/// At most <see cref="MaxLines" /> lines once they're added through <see cref="Add" />.
	/// </value>
	public List<string> Lines { get; } = [];

	/// <summary>
	/// Gets or sets how many of the oldest <see cref="Lines" /> <c>history -a</c> skips because they're already in a
	/// file.
	/// </summary>
	/// <value>
	/// An index into <see cref="Lines" />; 0 means <c>-a</c> appends every line.
	/// </value>
	internal int AppendedCount { get; set; }

	/// <summary>
	/// Remembers <paramref name="line" />, then drops the oldest lines past <see cref="MaxLines" />.
	/// </summary>
	/// <param name="line">The command line as the user typed it.</param>
	public void Add(string line)
	{
		this.Lines.Add(line);
		var countBeforeTrim = this.Lines.Count;
		Trim(this.Lines);
		this.AppendedCount = Math.Max(0, this.AppendedCount - (countBeforeTrim - this.Lines.Count));
	}

	/// <summary>
	/// Removes the oldest entries of <paramref name="lines" /> until at most <see cref="MaxLines" /> remain.
	/// </summary>
	/// <param name="lines">The lines to trim in place, oldest first.</param>
	internal static void Trim(List<string> lines)
	{
		_ = lines.ArgumentNotNull();

		lines.RemoveRange(0, Math.Max(0, lines.Count - MaxLines));
	}

	/// <summary>
	/// Reads a plain-text history file with one command per line.
	/// </summary>
	/// <param name="path">The file to read.</param>
	/// <returns>
	/// The file's non-blank lines in order, or <see langword="null" />, after printing the reason, when it can't be
	/// read.
	/// </returns>
	internal static List<string>? ReadFile(string path)
	{
		_ = path.ArgumentNotNull();

		try
		{
			// Same rule as the prompt loop, which never records a blank line.
			return [.. File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))];
		}
		catch (Exception e) when (e is FileNotFoundException or System.IO.DirectoryNotFoundException)
		{
			Console.Error.WriteLine($"history: {path}: No such file or directory");
		}
		// ArgumentException is an empty path, like history -r "", which would otherwise end the whole shell.
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			Console.Error.WriteLine($"history: {path}: {e.Message}");
		}

		return null;
	}

	/// <summary>
	/// Writes <paramref name="lines" /> to a plain-text history file, one command per line.
	/// </summary>
	/// <param name="path">The file to write.</param>
	/// <param name="lines">The lines to write, oldest first.</param>
	/// <param name="append"><see langword="true" /> to add to the end of the file; <see langword="false" /> to replace it.</param>
	/// <returns>
	/// <see langword="true" /> once the file is written; otherwise <see langword="false" />, after printing the reason.
	/// </returns>
	internal static bool WriteFile(string path, IEnumerable<string> lines, bool append)
	{
		_ = path.ArgumentNotNull();
		_ = lines.ArgumentNotNull();

		try
		{
			// Both end every line with a newline, which gives the trailing empty line bash writes.
			if (append)
			{
				File.AppendAllLines(path, lines);
			}
			else
			{
				File.WriteAllLines(path, lines);
			}

			return true;
		}
		catch (System.IO.DirectoryNotFoundException)
		{
			Console.Error.WriteLine($"history: {path}: No such file or directory");
		}
		// ArgumentException is an empty path, like history -r "", which would otherwise end the whole shell.
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			Console.Error.WriteLine($"history: {path}: {e.Message}");
		}

		return false;
	}

	/// <summary>
	/// Finds how many of the first lines of <paramref name="file" /> already appear, in a row, somewhere in
	/// <paramref name="saved" />.
	/// </summary>
	/// <param name="saved">The history already loaded, oldest first.</param>
	/// <param name="file">The lines of a history file about to be imported, oldest first.</param>
	/// <returns>
	/// The number of leading lines to skip; 0 means none of the file was imported before, and
	/// <c>file.Count</c> means all of it was.
	/// </returns>
	/// <remarks>
	/// The <c>HISTFILE</c> stays on disk after its lines go into the encrypted history, so the next start sees the
	/// same lines again. Matching the file's start anywhere in <paramref name="saved" /> skips what was imported
	/// before, even with newer commands after it, and still imports lines another shell appended to the file since.
	/// </remarks>
	internal static int Overlap(IReadOnlyList<string> saved, IReadOnlyList<string> file)
	{
		_ = saved.ArgumentNotNull();
		_ = file.ArgumentNotNull();

		var best = 0;

		for (var start = 0; start < saved.Count && best < file.Count; start++)
		{
			var length = 0;
			while (length < file.Count && start + length < saved.Count && saved[start + length] == file[length])
			{
				length++;
			}

			best = Math.Max(best, length);
		}

		return best;
	}

	/// <inheritdoc />
	/// <remarks>
	/// Numbers are right-aligned in five columns followed by two spaces, as bash prints them. They restart at 1 once
	/// old lines are trimmed, where bash keeps counting up. A count in the first argument prints only that many of the
	/// newest lines, keeping their numbers, as bash does. zsh instead reads it as the line to start from.
	/// <c>-r</c> followed by a path appends that file's lines to the list. The file is plain text with one command per
	/// line, unlike the encrypted file the shell saves on its own, and blank lines are skipped. <c>-w</c> followed by a
	/// path writes the list to that file in the same plain format, replacing what was there. <c>-a</c> followed by a
	/// path appends only the lines added since the last <c>-a</c> or <c>-w</c>, as bash does.
	/// </remarks>
	public void Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args is [("-w" or "-a") and var flag, var writePath])
		{
			var append = flag == "-a";

			if (WriteFile(writePath, append ? this.Lines.Skip(this.AppendedCount) : this.Lines, append))
			{
				this.AppendedCount = this.Lines.Count;
			}

			return;
		}

		if (args is ["-r", var path])
		{
			foreach (var line in ReadFile(path) ?? [])
			{
				this.Add(line);
			}

			return;
		}

		var count = this.Lines.Count;
		if (args.Length > 0 && (!int.TryParse(args[0], out count) || !count.CheckIsInRange(0, int.MaxValue)))
		{
			Console.Error.WriteLine($"history: {args[0]}: numeric argument required");

			return;
		}

		for (var i = Math.Max(0, this.Lines.Count - count); i < this.Lines.Count; i++)
		{
			Console.WriteLine($@"{i + 1,5}  {this.Lines[i]}");
		}
	}
}
