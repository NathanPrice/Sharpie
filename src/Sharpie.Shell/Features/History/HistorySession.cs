using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Features.History;

/// <summary>
/// Loads the saved history when the shell starts and saves each new line, to the encrypted file and to
/// <c>HISTFILE</c>.
/// </summary>
internal sealed class HistorySession
{
	/// <summary>
	/// Gets the folder that holds the encrypted history file and, on Windows, its key.
	/// </summary>
	/// <value>
	/// <c>sharpie</c> under the user's local application data folder.
	/// </value>
	public static string Folder { get; } =
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sharpie");

	/// <summary>
	/// Gets the encrypted history file.
	/// </summary>
	/// <value>
	/// The file <c>history</c> in <see cref="Folder" />.
	/// </value>
	public static string FilePath { get; } = Path.Combine(Folder, "history");

	private readonly HistoryCommand history;
	private readonly string path;

	// HISTFILE's own lines plus this session's. Never the ones decrypted from the encrypted file, so those don't end up
	// in plain text.
	private readonly List<string> plainLines;

	// Null when nothing should be saved: outside the window, no secret store, or a file that couldn't be read and
	// mustn't be overwritten.
	private byte[]? key;

	/// <summary>
	/// Initializes a new instance of the <see cref="HistorySession" /> class.
	/// </summary>
	/// <param name="history">The <c>history</c> builtin whose lines are loaded and saved.</param>
	/// <param name="path">The encrypted history file.</param>
	/// <param name="key">The history file's key, or <see langword="null" /> when nothing should be saved.</param>
	/// <param name="plainLines">The lines read from <c>HISTFILE</c>, or an empty list.</param>
	private HistorySession(HistoryCommand history, string path, byte[]? key, List<string> plainLines)
	{
		this.history = history;
		this.path = path;
		this.key = key;
		this.plainLines = plainLines;
	}

	/// <summary>
	/// Loads the encrypted history and <c>HISTFILE</c> into <paramref name="history" />, and arranges for
	/// <c>HISTFILE</c> to be written when the shell exits.
	/// </summary>
	/// <param name="history">The <c>history</c> builtin to fill.</param>
	/// <param name="useEncryptedFile">
	/// <see langword="true" /> to load and save the encrypted file. Only the window turns this on, so the shell's
	/// process tests never touch the keyring.
	/// </param>
	/// <returns>
	/// The session that <see cref="Record" /> saves new lines through.
	/// </returns>
	public static HistorySession Start(HistoryCommand history, bool useEncryptedFile)
	{
		_ = history.ArgumentNotNull();

		var path = FilePath;
		byte[]? key = null;

		if (useEncryptedFile && HistoryFile.PrepareFolder(Folder))
		{
			key = HistoryKey.LoadOrCreate(Folder, allowCreate: !File.Exists(path));

			if (key is not null && HistoryFile.Load(path, key) is { } saved)
			{
				history.Lines.AddRange(saved);
				// Lines from earlier sessions aren't new, so history -a doesn't copy them into a plain-text file.
				history.AppendedCount = history.Lines.Count;
			}
			else
			{
				key = null;
			}
		}

		var histFile = Environment.GetEnvironmentVariable("HISTFILE");
		var fileLines = ImportHistFile(history, histFile, encrypted: key is not null);

		// Without encryption HISTFILE gets the whole list on exit, as in bash. Decided once here, because a failed
		// save later nulls the key and must not switch the exit write to the decrypted lines.
		var encrypted = key is not null;
		var session = new HistorySession(history, path, key, [.. fileLines ?? []]);

		if (!string.IsNullOrEmpty(histFile))
		{
			// Runs for both the exit builtin, which calls Environment.Exit, and Main returning when input closes.
			AppDomain.CurrentDomain.ProcessExit += (_, _) => HistoryCommand.WriteFile(histFile, encrypted ? session.plainLines : history.Lines, append: false);
		}

		return session;
	}

	/// <summary>
	/// Adds a line the user entered to the history and saves the encrypted file.
	/// </summary>
	/// <param name="line">The line as typed. Blank lines are skipped.</param>
	/// <remarks>
	/// Called before the line runs, because the exit builtin ends the process without coming back. A failed save
	/// stops saving for the rest of the session.
	/// </remarks>
	public void Record(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return;
		}

		this.history.Add(line);
		this.plainLines.Add(line);

		HistoryCommand.Trim(this.plainLines);

		if (this.key is not null && !HistoryFile.Save(this.path, this.key, this.history.Lines))
		{
			this.key = null;
		}
	}

	/// <summary>
	/// Adds the lines of the plain-text <c>HISTFILE</c> that aren't in <paramref name="history" /> yet.
	/// </summary>
	/// <param name="history">The <c>history</c> builtin to add the lines to.</param>
	/// <param name="histFile">The value of <c>HISTFILE</c>, or <see langword="null" /> when it isn't set.</param>
	/// <param name="encrypted"><see langword="true" /> when the encrypted history file is in use.</param>
	/// <returns>
	/// Every line read from the file, or <see langword="null" /> when it's unset or missing, which is skipped
	/// quietly, as bash does.
	/// </returns>
	/// <remarks>
	/// HISTFILE is plain text, as in bash. Its lines go after the encrypted ones, and the next save copies them into
	/// the encrypted file.
	/// </remarks>
	private static List<string>? ImportHistFile(HistoryCommand history, string? histFile, bool encrypted)
	{
		if (string.IsNullOrEmpty(histFile) || !File.Exists(histFile) || HistoryCommand.ReadFile(histFile) is not { } fileLines)
		{
			return null;
		}

		var imported = fileLines.Skip(HistoryCommand.Overlap(history.Lines, fileLines)).ToList();

		imported.ForEach(history.Add);
		history.AppendedCount = history.Lines.Count;

		// The user chose to keep HISTFILE rather than delete it after import, so at least hide it from other accounts
		// and say it's still readable.
		if (!encrypted || imported.Count <= 0)
		{
			return fileLines;
		}

		try
		{
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(histFile, HistoryFile.OwnerOnly);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"history: {exception.Message}");
		}

		Console.Error.WriteLine($"history: imported {imported.Count} lines from {histFile} into encrypted history; {histFile} is still plain text");

		return fileLines;
	}
}
