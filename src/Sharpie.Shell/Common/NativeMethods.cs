using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sharpie.Shell.Common;

/// <summary>
/// Calls into the C library for what .NET has no API for.
/// </summary>
internal static partial class NativeMethods
{
	/// <summary>
	/// Sends signal <paramref name="sig" /> to the process with id <paramref name="pid" />.
	/// </summary>
	/// <param name="pid">The id of the process to signal.</param>
	/// <param name="sig">The signal number.</param>
	/// <returns>
	/// 0 on success, or -1 with the reason in the last error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int Kill(int pid, int sig);

	// GetStdHandle's id for standard output, and the console mode flag that makes the console act on escape sequences
	// instead of printing them.
	private const int StandardOutput = -11;
	private const uint VirtualTerminalProcessing = 0x0004;

	/// <summary>
	/// Turns on escape sequence handling for the console the shell writes to, so the colors and cursor moves of the
	/// line editor work on Windows.
	/// </summary>
	/// <remarks>
	/// Does nothing when standard output isn't a console. Programs the shell starts inherit the console, and with it
	/// this mode.
	/// </remarks>
	[SupportedOSPlatform("windows")]
	internal static void EnableVirtualTerminalOutput()
	{
		var handle = GetStdHandle(StandardOutput);

		if (GetConsoleMode(handle, out var mode))
		{
			_ = SetConsoleMode(handle, mode | VirtualTerminalProcessing);
		}
	}

	/// <summary>
	/// Gets one of the process's standard handles.
	/// </summary>
	/// <param name="id">Which handle: -10 for input, -11 for output, -12 for error.</param>
	/// <returns>
	/// The handle, or an invalid one when the process has none.
	/// </returns>
	[LibraryImport("kernel32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial nint GetStdHandle(int id);

	/// <summary>
	/// Reads a console handle's mode flags.
	/// </summary>
	/// <param name="handle">The console handle.</param>
	/// <param name="mode">Receives the flags.</param>
	/// <returns>
	/// <see langword="false" /> when <paramref name="handle" /> isn't a console.
	/// </returns>
	[LibraryImport("kernel32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetConsoleMode(nint handle, out uint mode);

	/// <summary>
	/// Sets a console handle's mode flags.
	/// </summary>
	/// <param name="handle">The console handle.</param>
	/// <param name="mode">The flags.</param>
	/// <returns>
	/// <see langword="true" /> on success.
	/// </returns>
	[LibraryImport("kernel32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleMode(nint handle, uint mode);
}
