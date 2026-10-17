using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Jobs;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Replaces the shell with a program, or with no program makes the shell's own redirects permanent.
/// </summary>
/// <remarks>
/// The shell runs <c>exec</c> through <see cref="Run(string[], string[], FileStream?, FileStream?, bool)" />, which
/// gets the command's open redirect files. A builtin normally only sees them for the length of its run.
/// </remarks>
internal sealed class ExecCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "exec";

	/// <inheritdoc />
	public string Description => "Replaces the shell with a program, or redirects the shell's own output";

	/// <summary>
	/// Gets or sets what to do just before the shell is replaced, such as saving the history, since no exit handler
	/// runs after that.
	/// </summary>
	/// <value>
	/// The action, or <see langword="null" /> for none.
	/// </value>
	public Action? BeforeReplace { get; set; }

	/// <inheritdoc />
	/// <remarks>
	/// Only a pipeline reaches this, where bash would replace a subshell. Sharpie runs builtins in the shell itself, so
	/// it refuses rather than replace the whole shell.
	/// </remarks>
	public int Run(string[] args)
	{
		new ShellError("sharpie::exec::in_pipeline", "`exec` can't run in a pipeline")
		{
			Help = "run the program without exec",
		}.Write();

		return 1;
	}

	/// <summary>
	/// Replaces the shell with the program in <paramref name="arguments" />, or with no arguments points the shell's
	/// standard output and standard error at <paramref name="output" /> and <paramref name="error" /> for good.
	/// </summary>
	/// <param name="arguments">The program name as typed, then its arguments, or none.</param>
	/// <param name="assignments">
	/// The <c>NAME=VALUE</c> words typed in front of <c>exec</c>, which the program gets in its environment.
	/// </param>
	/// <param name="output">The open <c>&gt;</c> file, or <see langword="null" />.</param>
	/// <param name="error">The open <c>2&gt;</c> file, or <see langword="null" />.</param>
	/// <param name="interactive">
	/// <see langword="true" /> when the shell reads commands from a person, and keeps going when the program can't
	/// start, as bash does. Otherwise the shell exits with the status.
	/// </param>
	/// <returns>
	/// 0 with no program, <see cref="ExternalProgram.NotFound" /> when the program isn't found, and
	/// <see cref="ExternalProgram.CannotRun" /> when it couldn't start. Doesn't return when it started.
	/// </returns>
	/// <remarks>
	/// Windows can't replace a process, so there the program runs as usual and the shell exits with its status.
	/// </remarks>
	public int Run(string[] arguments, string[] assignments, FileStream? output, FileStream? error, bool interactive)
	{
		_ = arguments.ArgumentNotNull();
		_ = assignments.ArgumentNotNull();

		if (arguments is [])
		{
			Redirect(output, error);

			return 0;
		}

		if (PathFinder.FindExecutable(arguments[0], ExternalProgram.AssignedPath(assignments)) is not { } path)
		{
			new ShellError("sharpie::exec::not_found", $"Command `{arguments[0]}` not found")
			{
				Help = "not a program on PATH",
			}.Write();

			return Failed(ExternalProgram.NotFound, interactive);
		}

		// Stryker disable once Conditional : each branch only runs on its own OS, so a test run on one OS can't tell them apart.
		if (OperatingSystem.IsWindows())
		{
			Environment.Exit(ExternalProgram.Run(arguments[0], arguments[1..], output, error, assignments: assignments) ?? ExternalProgram.NotFound);
		}

		this.BeforeReplace?.Invoke();

		var reason = Replace(path, arguments, assignments, output, error);

		new ShellError("sharpie::exec::cannot_start", $"Can't start `{arguments[0]}`: {Marshal.GetPInvokeErrorMessage(reason)}").Write();

		return Failed(ExternalProgram.CannotRun, interactive);
	}

	/// <summary>
	/// Gives <paramref name="status" /> back, or ends the shell with it when it isn't interactive.
	/// </summary>
	/// <param name="status">The status.</param>
	/// <param name="interactive"><see langword="true" /> to keep the shell going.</param>
	/// <returns>
	/// <paramref name="status" />.
	/// </returns>
	private static int Failed(int status, bool interactive)
	{
		if (!interactive)
		{
			Environment.Exit(status);
		}

		return status;
	}

	/// <summary>
	/// Points standard output and standard error at the given files for the rest of the shell's life.
	/// </summary>
	/// <param name="output">The new standard output, or <see langword="null" /> to keep it.</param>
	/// <param name="error">The new standard error, or <see langword="null" /> to keep it.</param>
	/// <remarks>
	/// The writers behind <see cref="Console.Out" /> and <see cref="Console.Error" /> hold their own copy of the old
	/// descriptor or handle, so new ones are opened after the switch.
	/// </remarks>
	[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The writers become Console.Out and Console.Error for the rest of the shell's life.")]
	private static void Redirect(FileStream? output, FileStream? error)
	{
		if (output is not null)
		{
			MoveStandard(output, 1);
			Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
		}

		if (error is not null)
		{
			MoveStandard(error, 2);
			Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

			// Reports now land in a file, which shouldn't get color codes.
			ShellError.UseColor = false;
		}
	}

	/// <summary>
	/// Makes <paramref name="file" /> the process's descriptor <paramref name="fd" />, or the matching standard handle
	/// on Windows.
	/// </summary>
	/// <param name="file">The open file.</param>
	/// <param name="fd">1 for standard output, 2 for standard error.</param>
	private static void MoveStandard(FileStream file, int fd)
	{
		// Stryker disable once Conditional : each branch only runs on its own OS, so a test run on one OS can't tell them apart.
		if (OperatingSystem.IsWindows())
		{
			NativeMethods.SetStandardHandle(fd, file.SafeFileHandle);
		}
		else
		{
			_ = NativeMethods.DuplicateTo((int)file.SafeFileHandle.DangerousGetHandle(), fd);
		}
	}

	/// <summary>
	/// Replaces the process with the program at <paramref name="path" />, on Linux and macOS.
	/// </summary>
	/// <param name="path">The program's full path.</param>
	/// <param name="arguments">The program name as typed, which becomes its <c>argv[0]</c>, then its arguments.</param>
	/// <param name="assignments">The <c>NAME=VALUE</c> words the program gets in its environment.</param>
	/// <param name="output">The program's standard output, or <see langword="null" /> for the shell's.</param>
	/// <param name="error">The program's standard error, or <see langword="null" /> for the shell's.</param>
	/// <returns>
	/// Only returns when the program couldn't start: the error number, with the shell put back as it was.
	/// </returns>
	/// <remarks>
	/// Ignored signals stay ignored in the new program, so the ones the shell ignores go back to their defaults, and
	/// none are left blocked, as <see cref="PosixSpawn" /> does for the programs it starts. Caught ones reset by
	/// themselves.
	/// </remarks>
	private static unsafe int Replace(string path, string[] arguments, string[] assignments, FileStream? output, FileStream? error)
	{
		var argv = PosixSpawn.ToCStrings(arguments);
		var envp = PosixSpawn.ToEnvironment(assignments);
		var empty = NativeMemory.AllocZeroed(NativeMethods.OpaqueSize);
		var oldMask = NativeMemory.AllocZeroed(NativeMethods.OpaqueSize);

		try
		{
			var savedOutput = Save(output, 1);
			var savedError = Save(error, 2);
			var handlers = new[]
			{
				NativeMethods.SignalPipe, NativeMethods.SignalQuit, NativeMethods.SignalStop,
				NativeMethods.SignalTerminalInput, NativeMethods.SignalTerminalOutput,
			}.Select(signal => (Signal: signal, Old: NativeMethods.SetSignalHandler(signal, 0))).ToArray();

			_ = NativeMethods.SignalSetEmpty(empty);
			_ = NativeMethods.SetSignalMask(NativeMethods.SignalMaskReplace, empty, oldMask);
			JobControl.RestoreModes();

			_ = NativeMethods.Execute(path, argv, envp);

			var reason = Marshal.GetLastPInvokeError();

			_ = NativeMethods.SetSignalMask(NativeMethods.SignalMaskReplace, oldMask, null);

			foreach (var (signal, old) in handlers)
			{
				_ = NativeMethods.SetSignalHandler(signal, old);
			}

			Restore(savedOutput, 1);
			Restore(savedError, 2);

			return reason;
		}
		finally
		{
			NativeMemory.Free(empty);
			NativeMemory.Free(oldMask);
			PosixSpawn.FreeCStrings(argv);
			PosixSpawn.FreeCStrings(envp);
		}

		// Points fd at file and gives a copy of what fd was, or -1 when file is null. The copy closes itself when the
		// program starts, so the program only sees the redirect.
		static int Save(FileStream? file, int fd)
		{
			if (file is null)
			{
				return -1;
			}

			var copy = NativeMethods.Duplicate(fd);

			_ = NativeMethods.Control(copy, NativeMethods.CloseOnExec);
			MoveStandard(file, fd);

			return copy;
		}

		// Puts back what Save copied.
		static void Restore(int copy, int fd)
		{
			if (copy >= 0)
			{
				_ = NativeMethods.DuplicateTo(copy, fd);
				_ = NativeMethods.Close(copy);
			}
		}
	}
}
