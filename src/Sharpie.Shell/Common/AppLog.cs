using Serilog;
using Serilog.Core;
using Serilog.Formatting.Compact;
using Sharpie.Shell.Features.History;

namespace Sharpie.Shell.Common;

/// <summary>
/// Sets up the Serilog log files that the shell and the terminal window write crashes and hidden failures to.
/// </summary>
/// <remarks>
/// Logs go only to files, never the console. The shell's console is the user's terminal, so a log line there would
/// show up in the middle of their output.
/// </remarks>
internal static class AppLog
{
	/// <summary>
	/// The size at which a day's log file is closed and logging continues in a new one.
	/// </summary>
	/// <remarks>
	/// Without a size roll, the file sink stops writing for the rest of the day once its 1 GB default is reached.
	/// </remarks>
	internal const long FileSizeLimit = 10 * 1024 * 1024;

	/// <summary>
	/// The number of log files kept per app; older ones are deleted when a new file starts.
	/// </summary>
	/// <remarks>
	/// With <see cref="FileSizeLimit" />, this keeps each app's logs under about 310 MB.
	/// </remarks>
	internal const int RetainedFileCount = 31;

	/// <summary>
	/// Gets how old a log file can get before it's deleted, even when fewer than <see cref="RetainedFileCount" /> exist.
	/// </summary>
	/// <value>
	/// Thirty days.
	/// </value>
	internal static TimeSpan RetainedFileAge { get; } = TimeSpan.FromDays(30);

	/// <summary>
	/// Gets the folder the log files are written to.
	/// </summary>
	/// <value>
	/// <c>logs</c> in <see cref="HistorySession.Folder" />, so <c>~/.local/share/sharpie/logs</c> on Linux.
	/// </value>
	internal static string Folder { get; } = Path.Combine(HistorySession.Folder, "logs");

	/// <summary>
	/// Builds a logger that writes compact JSON lines to a daily file in <paramref name="folder" />.
	/// </summary>
	/// <param name="app">The app name, which starts each file name and is added to every event as <c>App</c>.</param>
	/// <param name="folder">The folder the files go in, created when the first event is written.</param>
	/// <param name="fileSizeLimit">The size in bytes at which a new file starts; tests pass a small one.</param>
	/// <param name="retainedFileCount">The number of files to keep; tests pass a small one.</param>
	/// <returns>
	/// A logger the caller owns and must dispose to flush it.
	/// </returns>
	/// <remarks>
	/// The files are opened shared, because every open tab runs its own shell and they all write to the same file.
	/// </remarks>
	internal static Logger Create(string app, string folder, long fileSizeLimit = FileSizeLimit, int retainedFileCount = RetainedFileCount) =>
		new LoggerConfiguration()
#if DEBUG
			.MinimumLevel.Debug()
#else
			.MinimumLevel.Information()
#endif
			.Enrich.WithProperty("App", app)

			// Several shells write to one file, and the process id tells their lines apart.
			.Enrich.WithProperty("Pid", Environment.ProcessId)
			.WriteTo.File(
				new CompactJsonFormatter(),
				Path.Combine(folder, $"{app}-.clef"),
				fileSizeLimitBytes: fileSizeLimit,
				shared: true,
				rollingInterval: RollingInterval.Day,
				rollOnFileSizeLimit: true,
				retainedFileCountLimit: retainedFileCount,
				retainedFileTimeLimit: RetainedFileAge)
			.CreateLogger();

	/// <summary>
	/// Points <see cref="Log.Logger" /> at <paramref name="app" />'s files in <see cref="Folder" />, through a
	/// <see cref="LazyLogger" />, and logs crashes and unobserved task exceptions there.
	/// </summary>
	/// <param name="app">The app name, <c>shell</c> or <c>window</c>.</param>
	internal static void Start(string app)
	{
		Log.Logger = new LazyLogger(() => Create(app, Folder));

		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
	}

	/// <summary>
	/// Logs the exception that is about to end the process and flushes the log, since <see cref="OnProcessExit" />
	/// doesn't run after a crash.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
		Log.CloseAndFlush();
	}

	/// <summary>
	/// Logs an exception from a background task that nothing waited on, which .NET otherwise drops without a trace.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
		Log.Warning(e.Exception, "Unobserved task exception");

	/// <summary>
	/// Flushes the log when the process exits, either through <see cref="Features.Builtins.ExitCommand" /> or when <c>Main</c> returns.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private static void OnProcessExit(object? sender, EventArgs e) => Log.CloseAndFlush();
}
