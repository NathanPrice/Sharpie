using System.Runtime.InteropServices;
using Serilog;

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
	/// Gets <c>RLIM_INFINITY</c>, the resource limit value that means no limit.
	/// </summary>
	/// <value>
	/// The value for the current OS: every bit set on Linux, the largest signed 64-bit number on macOS.
	/// </value>
	internal static ulong ResourceUnlimited => OperatingSystem.IsMacOS() ? long.MaxValue : nuint.MaxValue;

	/// <summary>
	/// Gets <c>PIPE_BUF</c>, the most bytes one write to a pipe carries without mixing with other writers.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static int PipeBufferSize => OperatingSystem.IsMacOS() ? 512 : 4096;

	/// <summary>
	/// Sets the file mode creation mask and gives back the old one.
	/// </summary>
	/// <param name="mask">The new mask.</param>
	/// <returns>
	/// The old mask. <c>mode_t</c> is 16 bits on macOS and 32 on Linux, so callers keep only the low 9 bits.
	/// </returns>
	/// <remarks>
	/// There is no call that only reads the mask, so reading it means setting it and putting it back.
	/// </remarks>
	[LibraryImport("libc", EntryPoint = "umask")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int SetFileMask(int mask);

	/// <summary>
	/// Reads a resource limit of this process.
	/// </summary>
	/// <param name="resource">The <c>RLIMIT_*</c> number, which differs between Linux and macOS.</param>
	/// <param name="limit">Receives the soft and hard limits.</param>
	/// <returns>
	/// 0, or -1 with the reason in the last error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int GetResourceLimit(int resource, out ResourceLimit limit);

	/// <summary>
	/// Changes a resource limit of this process, which the programs it starts afterwards inherit.
	/// </summary>
	/// <param name="resource">The <c>RLIMIT_*</c> number, which differs between Linux and macOS.</param>
	/// <param name="limit">The new soft and hard limits.</param>
	/// <returns>
	/// 0, or -1 with the reason in the last error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int SetResourceLimit(int resource, in ResourceLimit limit);

	/// <summary>
	/// Gets sigprocmask's <c>SIG_SETMASK</c>, which replaces the blocked signals with the given set.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static int SignalMaskReplace => OperatingSystem.IsMacOS() ? 3 : 2;

	/// <summary>
	/// Gets ioctl's <c>FIOCLEX</c> request, which marks a descriptor to close when the process runs another program.
	/// </summary>
	/// <value>
	/// The value for the current OS.
	/// </value>
	internal static nuint CloseOnExec => OperatingSystem.IsMacOS() ? 0x20006601u : 0x5451u;

	/// <summary>
	/// Replaces this process with the program at <paramref name="path" />, keeping its id, open descriptors and
	/// ignored signals.
	/// </summary>
	/// <param name="path">The program's full path.</param>
	/// <param name="argv">The null-terminated argument list, starting with the program name.</param>
	/// <param name="envp">The null-terminated environment list.</param>
	/// <returns>
	/// Only returns when the program couldn't be started: -1, with the reason in the last error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "execve", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int Execute(string path, byte** argv, byte** envp);

	/// <summary>
	/// Copies an open descriptor to the lowest free number.
	/// </summary>
	/// <param name="fd">The descriptor to copy.</param>
	/// <returns>
	/// The new descriptor, or -1 on error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "dup")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int Duplicate(int fd);

	/// <summary>
	/// Copies <paramref name="fd" /> onto <paramref name="newFd" />, closing what <paramref name="newFd" /> was first.
	/// The copy stays open when the process runs another program.
	/// </summary>
	/// <param name="fd">The open descriptor to copy.</param>
	/// <param name="newFd">The descriptor number it gets.</param>
	/// <returns>
	/// <paramref name="newFd" />, or -1 on error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "dup2")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int DuplicateTo(int fd, int newFd);

	/// <summary>
	/// Closes an open descriptor.
	/// </summary>
	/// <param name="fd">The descriptor.</param>
	/// <returns>
	/// 0, or -1 on error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "close")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int Close(int fd);

	/// <summary>
	/// Sends a device request with no argument, such as <see cref="CloseOnExec" />.
	/// </summary>
	/// <param name="fd">The descriptor.</param>
	/// <param name="request">The request.</param>
	/// <returns>
	/// 0, or -1 on error.
	/// </returns>
	/// <remarks>
	/// ioctl takes a variable argument list, which .NET can't pass. A request that reads no argument is safe to call
	/// this way on every platform, which is why <see cref="CloseOnExec" /> is used rather than fcntl.
	/// </remarks>
	[LibraryImport("libc", EntryPoint = "ioctl")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int Control(int fd, nuint request);

	/// <summary>
	/// Changes which signals the calling thread blocks.
	/// </summary>
	/// <param name="how">What to do with <paramref name="signals" />, like <see cref="SignalMaskReplace" />.</param>
	/// <param name="signals">The new set, or null to only read the current one.</param>
	/// <param name="old">Receives the set from before, or null.</param>
	/// <returns>
	/// 0, or -1 on error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "sigprocmask")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int SetSignalMask(int how, void* signals, void* old);

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
	/// Adds a step that closes <paramref name="fd" /> in the child.
	/// </summary>
	/// <param name="fileActions">The list.</param>
	/// <param name="fd">The descriptor to close.</param>
	/// <returns>
	/// 0, or an error number.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addclose")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int FileActionsAddClose(void* fileActions, int fd);

	/// <summary>
	/// Checks whether <paramref name="file" /> is a regular file, not a device, pipe or socket.
	/// </summary>
	/// <param name="file">The open file.</param>
	/// <returns>
	/// <see langword="true" /> for a regular file, or when the check fails.
	/// </returns>
	/// <remarks>
	/// The C library's <c>struct stat</c> is laid out differently on every OS and processor, and glibc only exports
	/// <c>fstat</c> itself from 2.33. .NET's own native library wraps it in one layout everywhere, with the file type
	/// in the second field, and .NET's file classes call it the same way.
	/// </remarks>
	internal static bool IsRegularFile(SafeHandle file)
	{
		// FileStatus is about 130 bytes; the type is in Mode, after the 4-byte Flags.
		const int ModeOffset = 4;
		const int TypeMask = 0xF000;
		const int Regular = 0x8000;

		var status = stackalloc byte[512];

		return FileStatus(file, status) != 0 || (*(int*)(status + ModeOffset) & TypeMask) == Regular;
	}

	/// <summary>
	/// Reads what the system knows about an open file, through .NET's own native library.
	/// </summary>
	/// <param name="file">The open file.</param>
	/// <param name="status">Receives .NET's <c>FileStatus</c>.</param>
	/// <returns>
	/// 0, or -1 with the reason in the last error.
	/// </returns>
	[LibraryImport("libSystem.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	private static partial int FileStatus(SafeHandle file, byte* status);

	/// <summary>
	/// Reads what the system knows about the file at <paramref name="path" />, as the C <c>stat</c> and <c>lstat</c>
	/// functions do.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <param name="followLinks">
	/// <see langword="true" /> to describe what a symbolic link points to, like <c>stat</c>; otherwise,
	/// <see langword="false" /> to describe the link itself, like <c>lstat</c>.
	/// </param>
	/// <returns>
	/// The file's details, or <see langword="null" /> when it doesn't exist or can't be reached.
	/// </returns>
	/// <remarks>
	/// Read from the same <c>FileStatus</c> layout as <see cref="IsRegularFile" />: mode at 4, owner at 8, group at
	/// 12, size at 16, device at 88 and inode at 104.
	/// </remarks>
	internal static UnixFileStatus? Status(string path, bool followLinks)
	{
		var status = stackalloc byte[512];

		return (followLinks ? PathStatus(path, status) : LinkStatus(path, status)) != 0 ? null
			: new UnixFileStatus(*(int*)(status + 4), *(uint*)(status + 8), *(uint*)(status + 12), *(long*)(status + 16), *(long*)(status + 88), *(long*)(status + 104));
	}

	/// <summary>
	/// Reads what the system knows about the file a path names, following symbolic links.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <param name="status">Receives .NET's <c>FileStatus</c>.</param>
	/// <returns>
	/// 0, or -1 with the reason in the last error.
	/// </returns>
	[LibraryImport("libSystem.Native", EntryPoint = "SystemNative_Stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	private static partial int PathStatus(string path, byte* status);

	/// <summary>
	/// Reads what the system knows about the file a path names, describing a symbolic link itself.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <param name="status">Receives .NET's <c>FileStatus</c>.</param>
	/// <returns>
	/// 0, or -1 with the reason in the last error.
	/// </returns>
	[LibraryImport("libSystem.Native", EntryPoint = "SystemNative_LStat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	private static partial int LinkStatus(string path, byte* status);

	/// <summary>
	/// Checks whether this process may read, write or run the file at <paramref name="path" />, as the user and group
	/// it acts as, the way bash's <c>eaccess</c> does.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <param name="mode">4 to read, 2 to write, 1 to run or search, as <c>R_OK</c>, <c>W_OK</c> and <c>X_OK</c>.</param>
	/// <returns>
	/// 0 when it may, or -1.
	/// </returns>
	/// <remarks>
	/// <c>AT_FDCWD</c> is -100 on Linux and -2 on macOS, and <c>AT_EACCESS</c> is 0x200 on Linux and 0x10 on macOS.
	/// </remarks>
	internal static int Access(string path, int mode) =>
		AccessAt(OperatingSystem.IsMacOS() ? -2 : -100, path, mode, OperatingSystem.IsMacOS() ? 0x10 : 0x200);

	/// <summary>
	/// Checks access to a file the way <c>faccessat</c> does.
	/// </summary>
	/// <param name="folder">The folder a relative path starts from, or <c>AT_FDCWD</c> for the current one.</param>
	/// <param name="path">The path.</param>
	/// <param name="mode">The access to check.</param>
	/// <param name="flags"><c>AT_EACCESS</c> to check as the effective user and group.</param>
	/// <returns>
	/// 0 when the access is allowed, or -1.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "faccessat", StringMarshalling = StringMarshalling.Utf8)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	private static partial int AccessAt(int folder, string path, int mode, int flags);

	/// <summary>
	/// Gets the user id this process acts as.
	/// </summary>
	/// <returns>
	/// The effective user id.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "geteuid")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial uint EffectiveUser();

	/// <summary>
	/// Gets the group id this process acts as.
	/// </summary>
	/// <returns>
	/// The effective group id.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "getegid")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial uint EffectiveGroup();

	/// <summary>
	/// Moves or reads the system's position in an open file, as <c>lseek</c> does, through .NET's own native library.
	/// </summary>
	/// <param name="file">The open file.</param>
	/// <param name="offset">The position, counted from where <paramref name="whence" /> says.</param>
	/// <param name="whence">0 for the start of the file, 1 for the current position, 2 for the end.</param>
	/// <returns>
	/// The new position from the start of the file, or -1 with the reason in the last error.
	/// </returns>
	[LibraryImport("libSystem.Native", EntryPoint = "SystemNative_LSeek", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial long Seek(SafeHandle file, long offset, int whence);

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
	/// Gets the id of the process that started this one.
	/// </summary>
	/// <returns>
	/// The parent's process id.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "getppid")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	private static partial int GetParentProcessId();

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

	/// <summary>
	/// Sets what happens when <paramref name="signal" /> arrives, keeping the whole action, flags included, unlike
	/// <see cref="SetSignalHandler" />.
	/// </summary>
	/// <param name="signal">The signal number.</param>
	/// <param name="action">The new <c>struct sigaction</c>, or null to only read the current one.</param>
	/// <param name="old">Receives the action from before, or null.</param>
	/// <returns>
	/// 0, or -1 on error.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "sigaction")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	internal static partial int SetSignalAction(int signal, void* action, void* old);

	/// <summary>
	/// Finds the home folder of the user named <paramref name="user" />, for <c>~user</c>.
	/// </summary>
	/// <param name="user">The login name.</param>
	/// <returns>
	/// The home folder, or <see langword="null" /> when no user has that name.
	/// </returns>
	/// <remarks>
	/// getpwnam asks the system's user database, so users from LDAP or systemd-homed are found too, which reading
	/// <c>/etc/passwd</c> would miss. It reuses one static buffer and isn't thread-safe, which is fine because the
	/// shell splits command lines on one thread.
	/// </remarks>
	internal static string? HomeOf(string user)
	{
		var entry = GetPasswordEntry(user);

		return entry is null
			? null
			: Marshal.PtrToStringUTF8(OperatingSystem.IsLinux() ? ((LinuxPasswordEntry*)entry)->Directory : ((BsdPasswordEntry*)entry)->Directory);
	}

	/// <summary>
	/// Looks up a user in the system's user database.
	/// </summary>
	/// <param name="name">The login name.</param>
	/// <returns>
	/// The user's <c>struct passwd</c>, or null when there is no such user.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "getpwnam", StringMarshalling = StringMarshalling.Utf8)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	private static partial void* GetPasswordEntry(string name);

	/// <summary>
	/// A <c>struct rlimit</c>: the soft limit the system enforces, and the hard limit the soft one can be raised to.
	/// </summary>
	/// <param name="current">The soft limit.</param>
	/// <param name="maximum">The hard limit.</param>
	/// <remarks>
	/// <c>rlim_t</c> is pointer-sized on glibc and 64 bits on macOS, which only runs 64-bit.
	/// </remarks>
	[StructLayout(LayoutKind.Sequential)]
	internal readonly struct ResourceLimit(nuint current, nuint maximum)
	{
		/// <summary>
		/// Gets the soft limit.
		/// </summary>
		/// <value>
		/// The limit, or <see cref="ResourceUnlimited" />.
		/// </value>
		public nuint Current { get; } = current;

		/// <summary>
		/// Gets the hard limit.
		/// </summary>
		/// <value>
		/// The limit, or <see cref="ResourceUnlimited" />.
		/// </value>
		public nuint Maximum { get; } = maximum;
	}

	/// <summary>
	/// The start of glibc's and musl's <c>struct passwd</c>, up to <c>pw_dir</c>.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	private readonly struct LinuxPasswordEntry
	{
		private readonly nint name;
		private readonly nint password;
		private readonly uint userId;
		private readonly uint groupId;
		private readonly nint gecos;

		/// <summary>
		/// Gets the home folder, <c>pw_dir</c>.
		/// </summary>
		/// <value>
		/// A pointer to a UTF-8 string ending in a zero byte.
		/// </value>
		public nint Directory { get; }
	}

	/// <summary>
	/// The start of the <c>struct passwd</c> that macOS and the BSDs use, which has <c>pw_change</c> and
	/// <c>pw_class</c> before <c>pw_gecos</c>.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	private readonly struct BsdPasswordEntry
	{
		private readonly nint name;
		private readonly nint password;
		private readonly uint userId;
		private readonly uint groupId;
		private readonly long change;
		private readonly nint userClass;
		private readonly nint gecos;

		/// <summary>
		/// Gets the home folder, <c>pw_dir</c>.
		/// </summary>
		/// <value>
		/// A pointer to a UTF-8 string ending in a zero byte.
		/// </value>
		public nint Directory { get; }
	}

	/// <summary>
	/// Gets the name this process was started under, its <c>argv[0]</c>.
	/// </summary>
	/// <returns>
	/// The name, or <see langword="null" /> on Windows or when it can't be read.
	/// </returns>
	/// <remarks>
	/// login, su and tmux start a login shell with a <c>-</c> in front of its name, and
	/// <see cref="Environment.GetCommandLineArgs" /> puts the program's own path in that place instead. Linux keeps the
	/// name in <c>/proc/self/cmdline</c>, and macOS keeps the C runtime's <c>argv</c>, which the .NET launcher leaves
	/// alone.
	/// </remarks>
	internal static string? ProgramName()
	{
		if (OperatingSystem.IsMacOS())
		{
			return Marshal.PtrToStringUTF8((nint)(*GetArgv())[0]);
		}

		if (!OperatingSystem.IsLinux())
		{
			return null;
		}

		try
		{
			var commandLine = File.ReadAllBytes("/proc/self/cmdline");
			var end = Array.IndexOf(commandLine, (byte)0);

			return System.Text.Encoding.UTF8.GetString(commandLine, 0, end < 0 ? commandLine.Length : end);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Log.Debug(exception, "Can't read /proc/self/cmdline");

			return null;
		}
	}

	/// <summary>
	/// Gets where macOS keeps the process's <c>argv</c>.
	/// </summary>
	/// <returns>
	/// A pointer to the <c>argv</c> pointer.
	/// </returns>
	[LibraryImport("libc", EntryPoint = "_NSGetArgv")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
	private static partial byte*** GetArgv();
}

/// <summary>
/// The parts of a file's <c>stat</c> details that <c>test</c> reads.
/// </summary>
/// <param name="Mode">The file type and permission bits, as <c>st_mode</c>.</param>
/// <param name="User">The owner's user id.</param>
/// <param name="Group">The owner's group id.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Device">The id of the device that holds the file.</param>
/// <param name="Inode">The file's number on that device, which every hard link to it shares.</param>
internal readonly record struct UnixFileStatus(int Mode, uint User, uint Group, long Size, long Device, long Inode);
