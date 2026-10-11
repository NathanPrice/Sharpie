using System.Collections;
using System.Text;
using Avalonia.Threading;
using DotNetTips.Spargine.Core;
using Porta.Pty;

namespace Sharpie.Features.Terminal;

/// <summary>
/// Runs the shell in a pseudo-terminal and carries bytes both ways, so the shell and every program it starts see a
/// real terminal.
/// </summary>
/// <remarks>
/// Porta.Pty does the per-OS work: <c>forkpty</c> through a small native helper on Linux and macOS, which makes the
/// shell a session leader with the terminal as its controlling terminal, and ConPTY on Windows.
/// </remarks>
internal sealed class ShellConnection : IDisposable
{
	private readonly IPtyConnection pty;
	private readonly Action<string> onOutput;
	private bool disposed;

	/// <summary>
	/// Initializes a new instance of the <see cref="ShellConnection" /> class around a started terminal.
	/// </summary>
	/// <param name="pty">The terminal the shell runs in.</param>
	/// <param name="onOutput">Called on the UI thread with each chunk the terminal prints.</param>
	private ShellConnection(IPtyConnection pty, Action<string> onOutput)
	{
		this.pty = pty;
		this.onOutput = onOutput;
		this.pty.ProcessExited += this.OnProcessExited;
		_ = this.PumpAsync();
	}

	/// <summary>
	/// Occurs when the shell process ends, for example after <c>exit</c>. Raised on a background thread.
	/// </summary>
	public event EventHandler? Exited;

	/// <summary>
	/// Starts the shell that was built next to this app in a new terminal.
	/// </summary>
	/// <param name="onOutput">Called on the UI thread with each chunk the terminal prints.</param>
	/// <param name="useSavedHistory">
	/// <see langword="false" /> to keep the shell away from the saved history file and its key, from the user's own
	/// shell settings and from <c>~/.sharpierc</c>, so tests never read or write the user's history or depend on their
	/// aliases.
	/// </param>
	/// <param name="columns">The terminal's width in columns.</param>
	/// <param name="rows">The terminal's height in rows.</param>
	/// <param name="workingDirectory">
	/// The folder the shell starts in, or <see langword="null" /> for the user's home folder. A folder that no longer
	/// exists falls back to the home folder too.
	/// </param>
	/// <returns>
	/// The connection to the running shell.
	/// </returns>
	public static ShellConnection Start(Action<string> onOutput, bool useSavedHistory, int columns, int rows, string? workingDirectory)
	{
		_ = onOutput.ArgumentNotNull();

		var fileName = OperatingSystem.IsWindows() ? "Sharpie.Shell.exe" : "Sharpie.Shell";
		var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
			.ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? string.Empty);

		environment[Shell.Program.WindowVariable] = "1";
		environment["TERM"] = "xterm-256color";
		environment["COLORTERM"] = "truecolor";

		if (!useSavedHistory)
		{
			environment[Shell.Program.HistoryVariable] = "0";
			environment[Shell.Program.ImportVariable] = "0";
		}

		var options = new PtyOptions
		{
			Name = "Sharpie",
			App = Path.Combine(AppContext.BaseDirectory, fileName),
			CommandLine = useSavedHistory ? [] : [Shell.Program.NoRcOption],
			Cwd = Directory.Exists(workingDirectory) ? workingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			Cols = columns,
			Rows = rows,
			Environment = environment,
		};

		// Run on the thread pool, so blocking on it here can't wait on the UI thread it was called from.
		var pty = Task.Run(() => PtyProvider.SpawnAsync(options, CancellationToken.None)).GetAwaiter().GetResult();

		return new ShellConnection(pty, onOutput);
	}

	/// <summary>
	/// Sends text to the terminal as UTF-8, as if typed.
	/// </summary>
	/// <param name="text">The text, such as a line ending in <c>\r</c>.</param>
	public void Write(string text) => this.Write(Encoding.UTF8.GetBytes(text.ArgumentNotNull()));

	/// <summary>
	/// Sends bytes to the terminal, as if typed.
	/// </summary>
	/// <param name="bytes">The bytes.</param>
	/// <remarks>
	/// Every write to the terminal goes through here and stays synchronous, so writes from keys and from replies to
	/// the program can't overlap.
	/// </remarks>
	public void Write(byte[] bytes)
	{
		if (this.disposed)
		{
			return;
		}

		try
		{
			this.pty.WriterStream.Write(bytes.ArgumentNotNull());
			this.pty.WriterStream.Flush();
		}
		// The shell is exiting, and the window closes when it has.
		catch (IOException)
		{
		}
	}

	/// <summary>
	/// Tells the terminal its new size, which sends SIGWINCH to the program in front.
	/// </summary>
	/// <param name="columns">The width in columns.</param>
	/// <param name="rows">The height in rows.</param>
	public void Resize(int columns, int rows)
	{
		if (!this.disposed)
		{
			this.pty.Resize(columns, rows);
		}
	}

	/// <summary>
	/// Ends the shell and everything it started, then releases the terminal. Calling it again does nothing.
	/// </summary>
	public void Dispose()
	{
		if (this.disposed)
		{
			return;
		}

		this.disposed = true;

		// Detached first, so killing the shell here doesn't raise Exited for a window that's already closing.
		this.pty.ProcessExited -= this.OnProcessExited;

		try
		{
			this.pty.Kill();
		}
		// Already gone.
		catch (InvalidOperationException)
		{
		}

		this.pty.Dispose();
	}

	/// <summary>
	/// Hands everything the terminal prints to the output callback until it closes.
	/// </summary>
	/// <returns>
	/// A task that completes when the terminal closes.
	/// </returns>
	private async Task PumpAsync()
	{
		// The decoder keeps a character split across two reads whole.
		using var reader = new StreamReader(this.pty.ReaderStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
		var buffer = new char[16384];

		try
		{
			int count;

			while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
			{
				var text = new string(buffer, 0, count);

				Dispatcher.UIThread.Post(() => this.onOutput(text));
			}
		}
		// Linux reports the end of a terminal whose program exited as an I/O error rather than end of file.
		catch (IOException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
	}

	/// <summary>
	/// Passes the shell's exit on as <see cref="Exited" />.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnProcessExited(object? sender, PtyExitedEventArgs e) => this.Exited?.Invoke(this, EventArgs.Empty);
}
