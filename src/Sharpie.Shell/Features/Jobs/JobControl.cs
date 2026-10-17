using System.Runtime.InteropServices;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Jobs;

/// <summary>
/// Hands the terminal between the shell and its jobs the way POSIX job control does, so Ctrl+C and Ctrl+Z reach the
/// job in front and never the shell, and stopped jobs can be resumed with <c>fg</c> and <c>bg</c>.
/// </summary>
/// <remarks>
/// Follows the steps in the GNU C Library manual's "Implementing a Job Control Shell". Only an interactive shell on
/// Linux or macOS turns it on. Windows has no process groups or terminal signals, and a shell reading a pipe has no
/// terminal to hand over.
/// </remarks>
internal static unsafe class JobControl
{
	// Standard input, which is the terminal whenever job control is on.
	private const int Terminal = 0;

	// signal()'s SIG_IGN.
	private const nint Ignore = 1;

	private static int shellGroup;
	private static byte[] shellModes = [];

	/// <summary>
	/// Gets a value indicating whether job control is on.
	/// </summary>
	/// <value>
	/// <see langword="true" /> after <see cref="Initialize" /> in an interactive shell on Linux or macOS.
	/// </value>
	public static bool Enabled { get; internal set; }

	/// <summary>
	/// Waits until the shell is in the foreground, puts it in its own process group, takes the terminal, and ignores
	/// the signals that would otherwise stop the shell itself.
	/// </summary>
	/// <remarks>
	/// SIGINT isn't ignored here. The shell catches it instead, and a caught signal goes back to its default in each
	/// program started, while an ignored one would stay ignored there too. <see cref="PosixSpawn" /> resets the
	/// ignored ones for each program anyway.
	/// </remarks>
	public static void Initialize()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Started in the background by another shell, it must wait for its turn instead of fighting over the terminal.
		shellGroup = NativeMethods.GetProcessGroup();

		while (NativeMethods.GetForegroundGroup(Terminal) is var foreground and >= 0 && foreground != shellGroup)
		{
			_ = NativeMethods.Kill(-shellGroup, NativeMethods.SignalTerminalInput);
		}

		foreach (var signal in new[]
		{
			NativeMethods.SignalQuit, NativeMethods.SignalStop, NativeMethods.SignalTerminalInput,
			NativeMethods.SignalTerminalOutput,
		})
		{
			_ = NativeMethods.SetSignalHandler(signal, Ignore);
		}

		// Fails for a session leader, as under the window, which already leads its own group.
		_ = NativeMethods.SetProcessGroup(0, 0);
		shellGroup = NativeMethods.GetProcessGroup();
		_ = NativeMethods.SetForegroundGroup(Terminal, shellGroup);
		shellModes = SaveModes();
		Enabled = true;
	}

	/// <summary>
	/// Gives <paramref name="job" /> the terminal and waits until all its programs exit or it stops, then takes the
	/// terminal back.
	/// </summary>
	/// <param name="job">The job, already started in its own process group.</param>
	/// <param name="resume">
	/// <see langword="true" /> to send SIGCONT first, as <c>fg</c> does for a stopped job.
	/// </param>
	/// <remarks>
	/// A job stopped by Ctrl+Z keeps the terminal modes it had, like a full-screen program's raw mode, so
	/// <c>fg</c> can give them back. The shell's own modes are put back in either case.
	/// </remarks>
	public static void WaitInForeground(Job job, bool resume)
	{
		ArgumentNullException.ThrowIfNull(job);

		SetModes(job.Modes ?? shellModes);
		_ = NativeMethods.SetForegroundGroup(Terminal, job.ProcessId);

		if (resume)
		{
			_ = NativeMethods.Kill(-job.ProcessId, NativeMethods.SignalContinue);
		}

		job.State = JobState.Running;

		while (job.State == JobState.Running && job.Members.Count > 0)
		{
			var pid = NativeMethods.WaitPid(-job.ProcessId, out var status, NativeMethods.Untraced);

			if (pid > 0)
			{
				job.Record(pid, status);
			}
			else if (Marshal.GetLastPInvokeError() != NativeMethods.Interrupted)
			{
				// No children left in the group, which only happens if something else reaped them.
				job.State = JobState.Done;
			}
		}

		_ = NativeMethods.SetForegroundGroup(Terminal, shellGroup);
		job.Modes = job.State == JobState.Stopped ? SaveModes() : null;
		SetModes(shellModes);
	}

	/// <summary>
	/// Puts back the terminal modes the shell started with, for a program that is about to take over the shell's
	/// process. Does nothing when job control is off.
	/// </summary>
	public static void RestoreModes() => SetModes(shellModes);

	/// <summary>
	/// Reads the terminal's current modes.
	/// </summary>
	/// <returns>
	/// The raw <c>termios</c> bytes, only ever handed back to <see cref="SetModes" />.
	/// </returns>
	private static byte[] SaveModes()
	{
		var modes = new byte[NativeMethods.TermiosSize];

		fixed (byte* buffer = modes)
		{
			_ = NativeMethods.GetTerminalModes(Terminal, buffer);
		}

		return modes;
	}

	/// <summary>
	/// Puts back terminal modes read by <see cref="SaveModes" />, once pending output is written.
	/// </summary>
	/// <param name="modes">The saved modes.</param>
	private static void SetModes(byte[] modes)
	{
		if (modes.Length == 0)
		{
			return;
		}

		fixed (byte* buffer = modes)
		{
			_ = NativeMethods.SetTerminalModes(Terminal, NativeMethods.Drain, buffer);
		}
	}
}
