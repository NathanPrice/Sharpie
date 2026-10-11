using System.Diagnostics;

namespace Sharpie.Shell.Tests;

/// <summary>
/// Starts the built shell as a real process with piped standard streams, the way the terminal window drives it.
/// </summary>
internal static class ShellProcess
{
	/// <summary>
	/// The collection name that every process test class passes to <see cref="CollectionAttribute" />.
	/// </summary>
	/// <remarks>
	/// The process tests ran one at a time when they were all in one class. Keeping them in one collection keeps it that
	/// way after the split, so timing-based tests like the SIGINT one still see an idle machine.
	/// </remarks>
	public const string Collection = "Shell process";

	/// <summary>
	/// The shell's launcher, which the build copies into this test's output folder because the shell is an Exe
	/// project reference.
	/// </summary>
	private static readonly string ShellPath = Path.Combine(AppContext.BaseDirectory, "Sharpie.Shell");

	/// <summary>
	/// How long <see cref="RunAsync" /> waits for the shell to exit once its output has closed.
	/// </summary>
	private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Starts the shell with its standard input, output, and error all redirected.
	/// </summary>
	/// <param name="workingDirectory">The folder the shell starts in, or <see langword="null" /> for this process's folder.</param>
	/// <param name="environment">Variables to set or replace in the shell's environment, or <see langword="null" /> for none.</param>
	/// <returns>
	/// The running shell. The caller disposes it.
	/// </returns>
	public static Process Start(string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null)
	{
		var startInfo = new ProcessStartInfo(ShellPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = workingDirectory ?? string.Empty,
		};

		// A test run started from a Sharpie window inherits this variable, and the shell would then run fastfetch,
		// print OSC 7 before each prompt, and save to the user's real history. Tests that want the window set it again.
		_ = startInfo.Environment.Remove(Program.WindowVariable);

		foreach (var (name, value) in environment ?? new Dictionary<string, string>())
		{
			startInfo.Environment[name] = value;
		}

		return Process.Start(startInfo)!;
	}

	/// <summary>
	/// Starts the shell, sends it <paramref name="input" />, closes its standard input, and collects everything it
	/// prints.
	/// </summary>
	/// <param name="input">The command lines to send. Input without <c>exit</c> ends when standard input closes.</param>
	/// <param name="workingDirectory">The folder the shell starts in, or <see langword="null" /> for this process's folder.</param>
	/// <param name="environment">Variables to set or replace in the shell's environment, or <see langword="null" /> for none.</param>
	/// <returns>
	/// What the shell wrote to standard output and standard error, and how it ended.
	/// </returns>
	/// <remarks>
	/// Reading the output waits for every writer of the pipe to close, background programs included, so any job the
	/// input started has finished by the time this returns.
	/// </remarks>
	public static async Task<ShellRun> RunAsync(string input, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null)
	{
		using var shell = Start(workingDirectory, environment);

		await shell.StandardInput.WriteAsync(input).ConfigureAwait(true);
		shell.StandardInput.Close();

		// Both pipes are read at once, so a shell that fills one while the test waits on the other can't hang.
		var error = shell.StandardError.ReadToEndAsync();
		var output = await shell.StandardOutput.ReadToEndAsync().ConfigureAwait(true);
		var exited = await ExitsWithinAsync(shell, ExitTimeout).ConfigureAwait(true);

		if (!exited)
		{
			shell.Kill(entireProcessTree: true);
		}

		return new ShellRun(output, await error.ConfigureAwait(true), exited, exited ? shell.ExitCode : null);
	}

	/// <summary>
	/// Waits for <paramref name="process" /> to end, but no longer than <paramref name="timeout" />.
	/// </summary>
	/// <param name="process">The shell process.</param>
	/// <param name="timeout">How long to wait.</param>
	/// <returns>
	/// <see langword="true" /> when the process ended in time.
	/// </returns>
	public static async Task<bool> ExitsWithinAsync(Process process, TimeSpan timeout)
	{
		using var cancellation = new CancellationTokenSource(timeout);

		try
		{
			await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(true);
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
	}
}

/// <summary>
/// What one <see cref="ShellProcess.RunAsync" /> call saw.
/// </summary>
/// <param name="Output">Everything the shell and its programs wrote to standard output, prompts included.</param>
/// <param name="Error">Everything the shell and its programs wrote to standard error.</param>
/// <param name="Exited"><see langword="true" /> when the shell exited within the timeout.</param>
/// <param name="ExitCode">The shell's exit code, or <see langword="null" /> when it didn't exit in time.</param>
internal sealed record ShellRun(string Output, string Error, bool Exited, int? ExitCode);
