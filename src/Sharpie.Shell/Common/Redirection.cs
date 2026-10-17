using System.Globalization;
using System.Text;
using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Common;

/// <summary>
/// Where one of a command's streams goes once its redirects are applied.
/// </summary>
/// <param name="Opened">The file the shell opened for it, or <see langword="null" />.</param>
/// <param name="Inherited">
/// When <paramref name="Opened" /> is <see langword="null" /> and the stream isn't <paramref name="Closed" />, the
/// command's own stream 0, 1 or 2 as it would be with no redirects: the terminal, a pipe end, or a builtin's buffer.
/// </param>
/// <param name="Closed"><see langword="true" /> when the stream was closed, as by <c>2&gt;&amp;-</c>.</param>
internal readonly record struct StreamTarget(FileStream? Opened, int Inherited, bool Closed)
{
	/// <summary>
	/// Gets a closed stream.
	/// </summary>
	/// <value>
	/// A target with <see cref="Closed" /> set.
	/// </value>
	internal static StreamTarget None { get; } = new(null, 0, Closed: true);

	/// <summary>
	/// Gets the command's own stream <paramref name="stream" />, with no redirect applied.
	/// </summary>
	/// <param name="stream">0, 1 or 2.</param>
	/// <returns>
	/// The inherited target.
	/// </returns>
	internal static StreamTarget Own(int stream) => new(null, stream, Closed: false);
}

/// <summary>
/// A command's redirects applied in the order they were typed, as bash's <c>do_redirections</c> applies them, with
/// the files they opened.
/// </summary>
internal sealed class Redirection : IDisposable
{
	/// <summary>
	/// The path bash scripts write to throw output away.
	/// </summary>
	internal const string NullDevice = "/dev/null";

	// Shared, because .NET turns the share mode into a file lock on Linux and macOS. An exclusive lock made a second
	// shell's > /dev/null fail while the first one's was open; bash takes no lock.
	private const FileShare Shared = FileShare.ReadWrite | FileShare.Delete;

	// Each opened file once, however many streams point at it.
	private readonly List<FileStream> opened = [];

	private Redirection()
	{
	}

	/// <summary>
	/// Gets the streams of a command with no redirects.
	/// </summary>
	/// <value>
	/// Streams 0, 1 and 2, each its own.
	/// </value>
	internal static StreamTarget[] Standard => [StreamTarget.Own(0), StreamTarget.Own(1), StreamTarget.Own(2)];

	/// <summary>
	/// Gets where each stream of the command goes, by stream number.
	/// </summary>
	/// <value>
	/// At least streams 0, 1 and 2. A number between those and the highest one redirected that no redirect named is
	/// closed.
	/// </value>
	public StreamTarget[] Targets { get; private set; } = Standard;

	/// <summary>
	/// Opens the files and applies the stream copies of <paramref name="redirects" />, in order.
	/// </summary>
	/// <param name="redirects">The redirects from <see cref="CommandLine.SplitRedirect" />.</param>
	/// <param name="line">The command as typed, which an error report shows and points into.</param>
	/// <param name="noClobber">
	/// <see langword="true" /> while <c>set -C</c> is on, so <c>&gt;</c> and <c>&amp;&gt;</c> won't replace an
	/// existing file.
	/// </param>
	/// <returns>
	/// The streams, or <see langword="null" /> after reporting the first redirect that failed, with every file opened
	/// so far closed again.
	/// </returns>
	/// <remarks>
	/// Like bash, <c>&gt;f 2&gt;&amp;1</c> sends both streams to <c>f</c>, and <c>2&gt;&amp;1 &gt;f</c> sends errors
	/// where output went before and output to <c>f</c>. A copy from a stream that isn't open is an error, so stream
	/// numbers the shell itself uses never reach a program.
	/// </remarks>
	public static Redirection? Open(IReadOnlyList<Redirect> redirects, string line, bool noClobber)
	{
		_ = redirects.ArgumentNotNull();

		Redirection? redirection = new();

		try
		{
			foreach (var redirect in redirects)
			{
				if (redirection.Apply(redirect, line, noClobber) is { } error)
				{
					error.Write();

					return null;
				}
			}

			(var applied, redirection) = (redirection, null);

			return applied;
		}
		finally
		{
			redirection?.Dispose();
		}
	}

	/// <summary>
	/// Hands the opened files to a new <see cref="Redirection" />, so this one no longer closes them.
	/// </summary>
	/// <returns>
	/// The new owner, with the same <see cref="Targets" />.
	/// </returns>
	/// <remarks>
	/// For a background job, whose copies outlive the command that started it.
	/// </remarks>
	public Redirection Detach()
	{
		var owner = new Redirection { Targets = this.Targets };

		owner.opened.AddRange(this.opened);
		this.opened.Clear();

		return owner;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		foreach (var file in this.opened)
		{
			file.Dispose();
		}

		this.opened.Clear();
	}

	/// <summary>
	/// Checks whether <paramref name="file" /> is a regular file, which <c>set -C</c> keeps <c>&gt;</c> from replacing.
	/// </summary>
	/// <param name="file">The open file.</param>
	/// <returns>
	/// <see langword="false" /> for a device, a pipe or a socket, like <c>/dev/null</c>.
	/// </returns>
	// Stryker disable once all : each check only runs on its own OS, so a test run on one OS can't tell them apart.
	private static bool IsRegularFile(FileStream file) =>
		OperatingSystem.IsWindows() ? NativeMethods.IsDiskFile(file.SafeFileHandle) : NativeMethods.IsRegularFile(file.SafeFileHandle);

	/// <summary>
	/// Gives the name the system knows <paramref name="path" /> by.
	/// </summary>
	/// <param name="path">The path as typed.</param>
	/// <returns>
	/// <c>NUL</c> for <see cref="NullDevice" /> on Windows; otherwise, <paramref name="path" />.
	/// </returns>
	// Stryker disable once all : NUL only exists on Windows, so a test run elsewhere can't see it.
	private static string SystemPath(string path) => OperatingSystem.IsWindows() && path == NullDevice ? "NUL" : path;

	/// <summary>
	/// Applies one redirect.
	/// </summary>
	/// <param name="redirect">The redirect.</param>
	/// <param name="line">The command as typed.</param>
	/// <param name="noClobber"><see langword="true" /> while <c>set -C</c> is on.</param>
	/// <returns>
	/// The error to report, or <see langword="null" /> when the redirect was applied.
	/// </returns>
	private ShellError? Apply(Redirect redirect, string line, bool noClobber)
	{
		var (stream, operatorText, target) = redirect;

		try
		{
			switch (operatorText)
			{
				case "<":
					this.Set(stream, this.Keep(new FileStream(SystemPath(target.Text), FileMode.Open, FileAccess.Read, Shared)));

					break;
				case ">" or "&>" when noClobber:
					if (this.CreateNew(target.Text) is not { } file)
					{
						return Report(line, target, $"Can't overwrite `{target.Text}`", "it already exists, and `set -C` is on", "use `>|` to replace it anyway");
					}

					this.Set(stream, file, both: operatorText == "&>");

					break;
				case ">" or ">|" or "&>":
					this.Set(stream, this.Keep(new FileStream(SystemPath(target.Text), FileMode.Create, FileAccess.Write, Shared)), both: operatorText == "&>");

					break;
				case ">>" or "&>>":
					this.Set(stream, this.Keep(OpenAppend(SystemPath(target.Text))), both: operatorText == "&>>");

					break;
				case ">&" or "<&" when target.Text == "-":
					this.Set(stream, StreamTarget.None);

					break;
				case ">&" or "<&":
					if (!CommandLine.IsNumber(target.Text) || !int.TryParse(target.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var from)
						|| from >= this.Targets.Length || this.Targets[from].Closed)
					{
						return Report(line, target, $"`{target.Text}`: bad stream number", "not a stream this command has open", null);
					}

					this.Set(stream, this.Targets[from]);

					break;
				default:
					this.Set(stream, this.Keep(TextFile(operatorText == "<<<" ? target.Text + "\n" : target.Text)));

					break;
			}
		}
		// A missing folder, a read-only file, or an empty name like > "" would otherwise end the whole shell.
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return Report(line, target, $"Can't open `{target.Text}` for {(operatorText == "<" ? "reading" : "writing")}", exception.Message, null);
		}

		return null;

		static ShellError Report(string line, Word target, string summary, string label, string? help) =>
			new("sharpie::shell::redirect_failed", summary)
			{
				Source = line,
				Start = target.Start,
				End = target.End,
				Label = label,
				Help = help,
			};
	}

	/// <summary>
	/// Opens <paramref name="path" /> for <c>&gt;</c> under <c>set -C</c>, as bash does.
	/// </summary>
	/// <param name="path">The path as typed.</param>
	/// <returns>
	/// The new file, or an existing one that isn't a regular file, like <c>/dev/null</c>, opened without emptying it.
	/// <see langword="null" /> when a regular file is already there.
	/// </returns>
	private FileStream? CreateNew(string path)
	{
		try
		{
			return this.Keep(new FileStream(SystemPath(path), FileMode.CreateNew, FileAccess.Write, Shared));
		}
		// Stryker disable once all : on Unix /dev/null exists as a file, so only Windows needs the second check.
		catch (IOException) when (File.Exists(path) || path == NullDevice)
		{
		}

		var existing = new FileStream(SystemPath(path), FileMode.Open, FileAccess.Write, Shared);

		if (IsRegularFile(existing))
		{
			existing.Dispose();

			return null;
		}

		return this.Keep(existing);
	}

	/// <summary>
	/// Opens <paramref name="path" /> to add to its end, creating it when it's missing.
	/// </summary>
	/// <param name="path">The system's name for the file.</param>
	/// <returns>
	/// The file, positioned at its end.
	/// </returns>
	/// <remarks>
	/// On Unix a program is handed the open file itself, and <see cref="FileMode.Append" /> is what moves the system's
	/// position to the end; a <see cref="Stream.Seek" /> would only move .NET's. Windows copies through .NET's position,
	/// and seeks only a file that can seek, since <c>NUL</c> can't.
	/// </remarks>
	// Stryker disable all : each branch only runs on its own OS, so a test run on one OS can't tell them apart.
	private static FileStream OpenAppend(string path)
	{
		if (!OperatingSystem.IsWindows())
		{
			return new FileStream(path, FileMode.Append, FileAccess.Write, Shared);
		}

		var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, Shared);

		if (file.CanSeek)
		{
			_ = file.Seek(0, SeekOrigin.End);
		}

		return file;
	}

	// Stryker restore all

	/// <summary>
	/// Puts a here-document's or here-string's text in a file the command can read like any other.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <returns>
	/// The file, positioned at its start. Nothing is left on disk once it's closed.
	/// </returns>
	/// <remarks>
	/// A real file can be handed to a program as is, so a pipeline or background job needs no copy to feed it. Unix
	/// removes the name right away and keeps the open file; Windows can't remove an open file, so it's deleted on
	/// close.
	/// </remarks>
	private static FileStream TextFile(string text)
	{
		var path = Path.GetTempFileName();

		// Stryker disable once all : the share and delete-on-close flags only matter on Windows.
		var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, Shared, 4096, OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None);

		if (!OperatingSystem.IsWindows())
		{
			File.Delete(path);
		}

		file.Write(Encoding.UTF8.GetBytes(text));
		file.Flush();
		file.Position = 0;

		return file;
	}

	/// <summary>
	/// Adds <paramref name="file" /> to the files <see cref="Dispose" /> closes.
	/// </summary>
	/// <param name="file">A file just opened.</param>
	/// <returns>
	/// <paramref name="file" />.
	/// </returns>
	private FileStream Keep(FileStream file)
	{
		this.opened.Add(file);

		return file;
	}

	/// <summary>
	/// Points stream <paramref name="stream" /> at <paramref name="file" />, and stream 2 too when
	/// <paramref name="both" /> is set.
	/// </summary>
	/// <param name="stream">The stream number.</param>
	/// <param name="file">The opened file.</param>
	/// <param name="both"><see langword="true" /> for <c>&amp;&gt;</c> and <c>&amp;&gt;&gt;</c>.</param>
	private void Set(int stream, FileStream file, bool both = false)
	{
		this.Set(stream, new StreamTarget(file, 0, Closed: false));

		if (both)
		{
			this.Set(2, new StreamTarget(file, 0, Closed: false));
		}
	}

	/// <summary>
	/// Points stream <paramref name="stream" /> at <paramref name="target" />, adding closed streams up to it.
	/// </summary>
	/// <param name="stream">The stream number.</param>
	/// <param name="target">Where it goes.</param>
	private void Set(int stream, StreamTarget target)
	{
		if (this.Targets.Length <= stream)
		{
			var grown = this.Targets;

			Array.Resize(ref grown, stream + 1);
			Array.Fill(grown, StreamTarget.None, this.Targets.Length, stream + 1 - this.Targets.Length);
			this.Targets = grown;
		}

		this.Targets[stream] = target;
	}
}
