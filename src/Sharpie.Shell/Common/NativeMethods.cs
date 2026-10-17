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

	// GetStdHandle's ids for standard output and standard error, and the console mode flag that makes the console act
	// on escape sequences instead of printing them.
	private const int StandardOutput = -11;
	private const int StandardError = -12;
	private const uint VirtualTerminalProcessing = 0x0004;

	// DuplicateHandle's option to give the copy the same access as the original.
	private const uint DuplicateSameAccess = 2;

	/// <summary>
	/// Turns on escape sequence handling for the console the shell writes to, so the colors and cursor moves of the
	/// line editor and the colors of error reports work on Windows.
	/// </summary>
	/// <remarks>
	/// Does nothing for a standard output or standard error that isn't a console. Programs the shell starts inherit the console, and with it
	/// this mode.
	/// </remarks>
	[SupportedOSPlatform("windows")]
	internal static void EnableVirtualTerminalOutput()
	{
		foreach (var id in (ReadOnlySpan<int>)[StandardOutput, StandardError])
		{
			var handle = GetStdHandle(id);

			if (GetConsoleMode(handle, out var mode))
			{
				_ = SetConsoleMode(handle, mode | VirtualTerminalProcessing);
			}
		}
	}

	/// <summary>
	/// Makes <paramref name="file" /> the process's standard output or standard error for good, so programs started
	/// later write to it too.
	/// </summary>
	/// <param name="fd">1 for standard output, 2 for standard error.</param>
	/// <param name="file">The open file.</param>
	/// <remarks>
	/// The handle is copied as inheritable, because a program started without redirects gets the shell's standard
	/// handles only when they are. The copy is never closed: it is standard output from now on, and the caller closes
	/// its own.
	/// </remarks>
	[SupportedOSPlatform("windows")]
	internal static void SetStandardHandle(int fd, SafeHandle file)
	{
		var process = GetCurrentProcess();

		if (DuplicateHandle(process, file, process, out var copy, 0, inheritHandle: true, DuplicateSameAccess))
		{
			_ = SetStdHandle(fd == 1 ? StandardOutput : StandardError, copy);
		}
	}

	/// <summary>
	/// Replaces one of the process's standard handles.
	/// </summary>
	/// <param name="id">Which handle: -10 for input, -11 for output, -12 for error.</param>
	/// <param name="handle">The new handle.</param>
	/// <returns>
	/// <see langword="true" /> on success.
	/// </returns>
	[LibraryImport("kernel32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetStdHandle(int id, nint handle);

	/// <summary>
	/// Gets a handle that stands for this process in calls like <see cref="DuplicateHandle" />.
	/// </summary>
	/// <returns>
	/// The handle, which needs no closing.
	/// </returns>
	[LibraryImport("kernel32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial nint GetCurrentProcess();

	/// <summary>
	/// Copies a handle, here within this process.
	/// </summary>
	/// <param name="sourceProcess">The process that owns <paramref name="source" />.</param>
	/// <param name="source">The handle to copy.</param>
	/// <param name="targetProcess">The process that gets the copy.</param>
	/// <param name="target">Receives the copy.</param>
	/// <param name="access">The copy's access, ignored with <see cref="DuplicateSameAccess" />.</param>
	/// <param name="inheritHandle"><see langword="true" /> to let programs started later inherit the copy.</param>
	/// <param name="options">Flags like <see cref="DuplicateSameAccess" />.</param>
	/// <returns>
	/// <see langword="true" /> on success.
	/// </returns>
	[LibraryImport("kernel32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DuplicateHandle(nint sourceProcess, SafeHandle source, nint targetProcess, out nint target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

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
