using System.ComponentModel;
using System.IO.Pipes;

namespace Sharpie.Shell.Common;

/// <content>
/// Process substitution through <c>/dev/fd</c>, for Linux and macOS.
/// </content>
internal static partial class ProcessSubstitution
{
	/// <summary>
	/// Starts the copy on one end of a pipe and keeps the other end open as <c>/dev/fd/N</c>.
	/// </summary>
	/// <param name="command">The copy's program path, then its arguments.</param>
	/// <param name="assignments">The <c>NAME=VALUE</c> words the copy gets in its environment.</param>
	/// <param name="writes"><see langword="true" /> when the command writes to the pipe.</param>
	/// <returns>
	/// The open pipe.
	/// </returns>
	/// <exception cref="CommandLine.ExpansionError">The copy couldn't be started.</exception>
	/// <remarks>
	/// The shell's end is copied with <c>dup</c>, which leaves it open in the programs the command starts, so
	/// <c>diff &lt;(a) &lt;(b)</c> can open both paths. The copy starts first and closes every other substitution's
	/// end, since a copy holding a pipe's read end would never get SIGPIPE from it, and one holding a write end would
	/// keep its reader from ever seeing the end.
	/// </remarks>
	private static Opened StartDescriptor(string[] command, string[] assignments, bool writes)
	{
		using var pipe = new AnonymousPipeServerStream(writes ? PipeDirection.Out : PipeDirection.In);

		var copyEnd = (int)pipe.ClientSafePipeHandle.DangerousGetHandle();
		int pid;

		try
		{
			pid = PosixSpawn.Start(command[0], command, -1, [(copyEnd, writes ? 0 : 1)], [.. Pipes.Select(open => open.Descriptor)], assignments: assignments);
		}
		catch (Win32Exception exception)
		{
			throw CannotStart(exception.Message);
		}

		pipe.DisposeLocalCopyOfClientHandle();
		ExternalProgram.CollectInBackground(pid);

		var fd = NativeMethods.Duplicate((int)pipe.SafePipeHandle.DangerousGetHandle());

		return fd < 0 ? throw CannotStart("the shell has no descriptor left for the pipe") : new Opened($"/dev/fd/{fd}", fd, () => _ = NativeMethods.Close(fd));
	}
}
