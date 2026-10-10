using System.Runtime.InteropServices;

namespace Sharpie.Shell.Common;

/// <summary>
/// The POSIX calls for starting programs and handing the terminal between them. Linux and macOS share every function
/// here. Where a number differs between them, the constant says so.
/// </summary>
internal static unsafe partial class NativeMethods
{
	// Flags for posix_spawnattr_setflags, the same on glibc and macOS.
	internal const short SpawnSetProcessGroup = 0x02;
	internal const short SpawnSetSignalDefaults = 0x04;
	internal const short SpawnSetSignalMask = 0x08;

	// waitpid options. WCONTINUED is the one that differs.
	internal const int NoHang = 1;
	internal const int Untraced = 2;

	// errno values, the same on Linux and macOS.
	internal const int Interrupted = 4;

	// tcsetattr: apply once pending output is written.
	internal const int Drain = 1;

	// glibc's posix_spawn_file_actions_t is 80 bytes, posix_spawnattr_t 336 and sigset_t 128. On macOS each is a
	// pointer or smaller. These buffers cover both with room to spare, so no layout has to be copied here. Same for
	// termios, which is 60 bytes on Linux and 72 on macOS.
	internal const int OpaqueSize = 1024;
	internal const int TermiosSize = 256;

	/// <summary>
	/// Gets the number of SIGINT.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static int SignalInterrupt => 2;

	/// <summary>
	/// Gets the number of SIGQUIT.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static int SignalQuit => 3;

	/// <summary>
	/// Gets the number of SIGPIPE.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static int SignalPipe => 13;

	/// <summary>
	/// Gets the number of SIGCHLD.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static int SignalChild => OperatingSystem.IsMacOS() ? 20 : 17;

	/// <summary>
	/// Gets the number of SIGCONT.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static int SignalContinue => OperatingSystem.IsMacOS() ? 19 : 18;

	/// <summary>
	/// Gets the number of SIGTSTP, the stop Ctrl+Z sends.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static int SignalStop => OperatingSystem.IsMacOS() ? 18 : 20;

	/// <summary>
	/// Gets the number of SIGTTIN.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static int SignalTerminalInput => 21;

	/// <summary>
	/// Gets the number of SIGTTOU.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static int SignalTerminalOutput => 22;

	/// <summary>
	/// Gets posix_spawnattr_setflags' <c>POSIX_SPAWN_SETSID</c> flag, which starts the child in a new session with no
	/// controlling terminal.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static short SpawnSetSession => OperatingSystem.IsMacOS() ? (short)0x400 : (short)0x80;

	/// <summary>
	/// Gets the number of waitpid's WCONTINUED option.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static int Continued => OperatingSystem.IsMacOS() ? 0x10 : 8;

	/// <summary>
	/// Starts a program, as <c>fork</c> then <c>exec</c> would, with file actions and attributes applied in the child.
	/// </summary>
	/// <param name="pid">Receives the new process id.</param>
	/// <param name="path">The program's full path.</param>
	/// <param name="fileActions">The file actions, or null.</param>
	/// <param name="attributes">The attributes, or null.</param>
	/// <param name="argv">The null-terminated argument list, starting with the program name.</param>
	/// <param name="envp">The null-terminated environment list.</param>
	/// <returns>
	/// 0, or the error number when the program couldn't be started.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "posix_spawn", StringMarshalling = StringMarshalling.Utf8)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int PosixSpawn(out int pid, string path, void* fileActions, void* attributes, byte** argv, byte** envp);

	/// <summary>
	/// Prepares an empty list of file actions for <see cref="PosixSpawn" />.
	/// </summary>
	/// <param name="fileActions">The list to prepare.</param>
	/// <returns>
	/// 0, or an error number.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int FileActionsInit(void* fileActions);

	/// <summary>
	/// Releases a list of file actions.
	/// </summary>
	/// <param name="fileActions">The list.</param>
	/// <returns>
	/// 0, or an error number.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int FileActionsDestroy(void* fileActions);

	/// <summary>
	/// Adds a step that copies <paramref name="fd" /> onto <paramref name="newFd" /> in the child.
	/// </summary>
	/// <param name="fileActions">The list.</param>
	/// <param name="fd">The open file to copy.</param>
	/// <param name="newFd">The descriptor number it gets in the child.</param>
	/// <returns>
	/// 0, or an error number.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int FileActionsAddDup2(void* fileActions, int fd, int newFd);

	/// <summary>
	/// Prepares default spawn attributes.
	/// </summary>
	/// <param name="attributes">The attributes to prepare.</param>
	/// <returns>
	/// 0, or an error number.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "posix_spawnattr_init")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int AttributesInit(void* attributes);

	/// <summary>
	/// Releases spawn attributes.
	/// </summary>
	/// <param name="attributes">The attributes.</param>
	/// <returns>
	/// 0, or an error number.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "posix_spawnattr_destroy")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int AttributesDestroy(void* attributes);

	/// <summary>
	/// Chooses which spawn attributes take effect.
	/// </summary>
	/// <param name="attributes">The attributes.</param>
	/// <param name="flags">Flags like <see cref="SpawnSetProcessGroup" />.</param>
	/// <returns>
	/// 0, or an error number.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "posix_spawnattr_setflags")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int AttributesSetFlags(void* attributes, short flags);

	/// <summary>
	/// Sets the process group the child joins, 0 for a new one it leads.
	/// </summary>
	/// <param name="attributes">The attributes.</param>
	/// <param name="processGroup">The group id.</param>
	/// <returns>
	/// 0, or an error number.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "posix_spawnattr_setpgroup")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int AttributesSetProcessGroup(void* attributes, int processGroup);

	/// <summary>
	/// Sets the signals that go back to their default action in the child.
	/// </summary>
	/// <param name="attributes">The attributes.</param>
	/// <param name="signals">The signal set.</param>
	/// <returns>
	/// 0, or an error number.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "posix_spawnattr_setsigdefault")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int AttributesSetSignalDefaults(void* attributes, void* signals);

	/// <summary>
	/// Sets the signals the child starts with blocked.
	/// </summary>
	/// <param name="attributes">The attributes.</param>
	/// <param name="signals">The signal set.</param>
	/// <returns>
	/// 0, or an error number.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "posix_spawnattr_setsigmask")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int AttributesSetSignalMask(void* attributes, void* signals);

	/// <summary>
	/// Clears a signal set.
	/// </summary>
	/// <param name="signals">The set.</param>
	/// <returns>
	/// 0, or -1 on error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "sigemptyset")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int SignalSetEmpty(void* signals);

	/// <summary>
	/// Adds a signal to a signal set.
	/// </summary>
	/// <param name="signals">The set.</param>
	/// <param name="signal">The signal number.</param>
	/// <returns>
	/// 0, or -1 on error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "sigaddset")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int SignalSetAdd(void* signals, int signal);

	/// <summary>
	/// Waits for a child process to change state.
	/// </summary>
	/// <param name="pid">A process id, or minus a process group id to wait for any process in that group.</param>
	/// <param name="status">Receives the encoded status.</param>
	/// <param name="options">Flags like <see cref="NoHang" /> and <see cref="Untraced" />.</param>
	/// <returns>
	/// The id of the process that changed, 0 with <see cref="NoHang" /> when none did, or -1 with the reason in the
	/// last error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int WaitPid(int pid, out int status, int options);

	/// <summary>
	/// Gets the process group the terminal sends its signals and input to.
	/// </summary>
	/// <param name="fd">An open descriptor of the terminal.</param>
	/// <returns>
	/// The group id, or -1 when <paramref name="fd" /> isn't this process's terminal.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "tcgetpgrp", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int GetForegroundGroup(int fd);

	/// <summary>
	/// Makes a process group the terminal's foreground group.
	/// </summary>
	/// <param name="fd">An open descriptor of the terminal.</param>
	/// <param name="processGroup">The group id.</param>
	/// <returns>
	/// 0, or -1 with the reason in the last error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "tcsetpgrp", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int SetForegroundGroup(int fd, int processGroup);

	/// <summary>
	/// Gets this process's process group.
	/// </summary>
	/// <returns>
	/// The group id.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "getpgrp")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int GetProcessGroup();

	/// <summary>
	/// Gets this process's id.
	/// </summary>
	/// <returns>
	/// The process id.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "getpid")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int GetProcessId();

	/// <summary>
	/// Moves a process into a process group.
	/// </summary>
	/// <param name="pid">The process, 0 for this one.</param>
	/// <param name="processGroup">The group, 0 for a new one led by <paramref name="pid" />.</param>
	/// <returns>
	/// 0, or -1 with the reason in the last error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "setpgid", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int SetProcessGroup(int pid, int processGroup);

	/// <summary>
	/// Reads a terminal's modes.
	/// </summary>
	/// <param name="fd">An open descriptor of the terminal.</param>
	/// <param name="termios">Receives the modes.</param>
	/// <returns>
	/// 0, or -1 with the reason in the last error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int GetTerminalModes(int fd, byte* termios);

	/// <summary>
	/// Sets a terminal's modes.
	/// </summary>
	/// <param name="fd">An open descriptor of the terminal.</param>
	/// <param name="action">When to apply them, like <see cref="Drain" />.</param>
	/// <param name="termios">The modes.</param>
	/// <returns>
	/// 0, or -1 with the reason in the last error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int SetTerminalModes(int fd, int action, byte* termios);

	/// <summary>
	/// Sets what happens when <paramref name="signal" /> arrives.
	/// </summary>
	/// <param name="signal">The signal number.</param>
	/// <param name="handler">1 to ignore the signal.</param>
	/// <returns>
	/// The previous handler.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "signal")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial nint SetSignalHandler(int signal, nint handler);
}
