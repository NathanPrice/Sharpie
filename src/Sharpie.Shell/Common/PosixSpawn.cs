using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Sharpie.Shell.Common;

/// <summary>
/// Starts programs with <c>posix_spawn</c> on Linux and macOS, which can do what <see cref="System.Diagnostics.Process" />
/// can't: put the program in a process group, and give it any open file as standard input, output or error.
/// </summary>
/// <remarks>
/// Job control needs both. A job's programs share a process group so the terminal can send Ctrl+C and Ctrl+Z to all
/// of them and not the shell, and a pipeline needs real pipes between its programs, so a program that exits early
/// gives the one before it SIGPIPE as in any other shell.
/// </remarks>
internal static unsafe class PosixSpawn
{
	/// <summary>
	/// Starts the program at <paramref name="path" /> and returns without waiting for it.
	/// </summary>
	/// <param name="path">The program's full path.</param>
	/// <param name="arguments">The program name as typed, which becomes its <c>argv[0]</c>, then its arguments.</param>
	/// <param name="processGroup">
	/// 0 to start a new process group led by the program, another id to join that group, or -1 to stay in the
	/// shell's group, as a shell without job control does.
	/// </param>
	/// <param name="input">The open file the program reads as standard input, or -1 to share the shell's.</param>
	/// <param name="output">The open file the program writes as standard output, or -1 to share the shell's.</param>
	/// <param name="error">The open file the program writes as standard error, or -1 to share the shell's.</param>
	/// <param name="newSession">
	/// <see langword="true" /> to start the program in a new session with no controlling terminal, so it can't read,
	/// draw on or take over the shell's terminal. <paramref name="processGroup" /> must be -1 then.
	/// </param>
	/// <param name="assignments">
	/// <c>NAME=VALUE</c> words that only this program gets in its environment, as in <c>LANG=C sort</c>, or
	/// <see langword="null" /> for none. A later one wins over an earlier one with the same name.
	/// </param>
	/// <returns>
	/// The new process id.
	/// </returns>
	/// <exception cref="Win32Exception">The program couldn't be started, for example because it isn't a program.</exception>
	/// <remarks>
	/// The signals the shell ignores or catches for itself go back to their defaults in the program, and none are
	/// blocked, so Ctrl+C, Ctrl+Z and a closed pipe act on it normally. The environment is .NET's copy, which holds
	/// changes the shell made with <see cref="Environment.SetEnvironmentVariable(string, string)" /> that the C
	/// library's own copy doesn't. <paramref name="assignments" /> are written over it here rather than set in the
	/// shell's environment first, because .NET deletes a variable set to an empty value, and
	/// <c>GIT_PAGER= git log</c> needs <c>GIT_PAGER</c> present and empty.
	/// </remarks>
	public static int Start(string path, IReadOnlyList<string> arguments, int processGroup, int input = -1, int output = -1, int error = -1, bool newSession = false, IReadOnlyList<string>? assignments = null)
	{
		var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);

		foreach (var assignment in assignments ?? [])
		{
			var parts = assignment.Split('=', 2);

			environment[parts[0]] = parts[1];
		}

		var fileActions = NativeMemory.AllocZeroed(NativeMethods.OpaqueSize);
		var attributes = NativeMemory.AllocZeroed(NativeMethods.OpaqueSize);
		var signals = NativeMemory.AllocZeroed(NativeMethods.OpaqueSize);
		var argv = ToCStrings(arguments);
		var envp = ToCStrings([.. environment.Select(entry => $"{entry.Key}={entry.Value}")]);

		try
		{
			_ = NativeMethods.FileActionsInit(fileActions);
			_ = NativeMethods.AttributesInit(attributes);

			// The sources are pipes and files the shell opened, which .NET marks close-on-exec. dup2 clears that flag on
			// the copy, so the program keeps exactly these three and nothing else of the shell's.
			foreach (var (source, target) in new[] { (input, 0), (output, 1), (error, 2) })
			{
				if (source >= 0)
				{
					_ = NativeMethods.FileActionsAddDup2(fileActions, source, target);
				}
			}

			var flags = (short)(NativeMethods.SpawnSetSignalDefaults | NativeMethods.SpawnSetSignalMask);

			if (processGroup >= 0)
			{
				flags |= NativeMethods.SpawnSetProcessGroup;
				_ = NativeMethods.AttributesSetProcessGroup(attributes, processGroup);
			}

			if (newSession)
			{
				flags |= NativeMethods.SpawnSetSession;
			}

			_ = NativeMethods.AttributesSetFlags(attributes, flags);

			_ = NativeMethods.SignalSetEmpty(signals);
			_ = NativeMethods.AttributesSetSignalMask(attributes, signals);

			foreach (var signal in new[]
			{
				NativeMethods.SignalInterrupt, NativeMethods.SignalQuit, NativeMethods.SignalPipe, NativeMethods.SignalChild,
				NativeMethods.SignalStop, NativeMethods.SignalTerminalInput, NativeMethods.SignalTerminalOutput,
			})
			{
				_ = NativeMethods.SignalSetAdd(signals, signal);
			}

			_ = NativeMethods.AttributesSetSignalDefaults(attributes, signals);

			var result = NativeMethods.PosixSpawn(out var pid, path, fileActions, attributes, argv, envp);

			return result == 0 ? pid : throw new Win32Exception(result);
		}
		finally
		{
			_ = NativeMethods.FileActionsDestroy(fileActions);
			_ = NativeMethods.AttributesDestroy(attributes);
			NativeMemory.Free(fileActions);
			NativeMemory.Free(attributes);
			NativeMemory.Free(signals);
			FreeCStrings(argv);
			FreeCStrings(envp);
		}
	}

	/// <summary>
	/// Copies <paramref name="values" /> into native memory as a null-terminated list of UTF-8 C strings, the shape
	/// <c>argv</c> and <c>envp</c> take.
	/// </summary>
	/// <param name="values">The strings.</param>
	/// <returns>
	/// The list, which <see cref="FreeCStrings" /> releases.
	/// </returns>
	private static byte** ToCStrings(IReadOnlyList<string> values)
	{
		var list = (byte**)NativeMemory.AllocZeroed((nuint)(values.Count + 1), (nuint)sizeof(byte*));

		for (var i = 0; i < values.Count; i++)
		{
			var bytes = Encoding.UTF8.GetBytes(values[i]);
			var text = (byte*)NativeMemory.AllocZeroed((nuint)bytes.Length + 1);

			bytes.CopyTo(new Span<byte>(text, bytes.Length));
			list[i] = text;
		}

		return list;
	}

	/// <summary>
	/// Releases a list made by <see cref="ToCStrings" />.
	/// </summary>
	/// <param name="list">The list.</param>
	private static void FreeCStrings(byte** list)
	{
		for (var entry = list; *entry is not null; entry++)
		{
			NativeMemory.Free(*entry);
		}

		NativeMemory.Free(list);
	}
}
