using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Sharpie.Shell.Common;

/// <content>
/// Process substitution through named pipes, for Windows, which has no <c>/dev/fd</c>.
/// </content>
internal static partial class ProcessSubstitution
{
	/// <summary>
	/// How many named pipes this shell has made, which keeps each name new.
	/// </summary>
	private static int pipeCount;

	/// <summary>
	/// Starts the copy with its standard input or output redirected, and copies between it and a new named pipe once
	/// the command opens the pipe.
	/// </summary>
	/// <param name="command">The copy's program path, then its arguments.</param>
	/// <param name="assignments">The <c>NAME=VALUE</c> words the copy gets in its environment.</param>
	/// <param name="writes"><see langword="true" /> when the command writes to the pipe.</param>
	/// <returns>
	/// The open pipe, at <c>\\.\pipe\sharpie-PID-N</c>.
	/// </returns>
	/// <exception cref="CommandLine.ExpansionError">The copy couldn't be started.</exception>
	/// <remarks>
	/// Closing a pipe the command never opened ends the wait for it. One it did open closes once the copying is done,
	/// so what the command wrote to a <c>&gt;(...)</c> all reaches the copy.
	/// </remarks>
	[SupportedOSPlatform("windows")]
	[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The copying task or the returned Close disposes the pipe.")]
	private static Opened StartNamedPipe(string[] command, string[] assignments, bool writes)
	{
		var name = $"sharpie-{Environment.ProcessId}-{Interlocked.Increment(ref pipeCount)}";
		var server = new NamedPipeServerStream(name, writes ? PipeDirection.In : PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		var startInfo = new ProcessStartInfo(command[0], command[1..])
		{
			UseShellExecute = false,
			RedirectStandardInput = writes,
			RedirectStandardOutput = !writes,
		};

		foreach (var parts in assignments.Select(assignment => assignment.Split('=', 2)))
		{
			startInfo.Environment[parts[0]] = parts[1];
		}

		Process copy;

		try
		{
			copy = Process.Start(startInfo)!;
		}
		catch (Win32Exception exception)
		{
			server.Dispose();

			throw CannotStart(exception.Message);
		}

		_ = Task.Run(async () =>
		{
			try
			{
				await server.WaitForConnectionAsync().ConfigureAwait(false);

				if (writes)
				{
					await server.CopyToAsync(copy.StandardInput.BaseStream).ConfigureAwait(false);
					copy.StandardInput.Close();
				}
				else
				{
					await copy.StandardOutput.BaseStream.CopyToAsync(server).ConfigureAwait(false);
				}
			}
			// The command never opened the pipe, or closed it before the copy finished writing.
			catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
			{
			}
			finally
			{
				await server.DisposeAsync().ConfigureAwait(false);
				copy.Dispose();
			}
		});

		return new Opened($@"\\.\pipe\{name}", -1, () =>
		{
			if (!writes || !server.IsConnected)
			{
				server.Dispose();
			}
		});
	}
}
