using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Jobs;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Sets what the shell runs when a signal arrives or the shell ends, as POSIX <c>trap</c> does.
/// </summary>
/// <param name="runLine">Runs one command line, the same way the shell runs a typed one, and gives its status.</param>
/// <param name="declare">The <c>declare</c> builtin, which holds <c>$?</c>.</param>
/// <remarks>
/// Signals belong to the whole process, so the trap table is shared by every instance. Each trap keeps the instance
/// that set it, which runs its action.
/// </remarks>
internal sealed class TrapCommand(Func<string, int> runLine, DeclareCommand declare) : ICommand
{
	/// <summary>
	/// The number <c>trap</c> gives the shell's own end, <c>EXIT</c>.
	/// </summary>
	internal const int ExitTrap = 0;

	// HUP, INT and TERM have the same numbers on every OS.
	private const int Hangup = 1;
	private const int Interrupt = 2;
	private const int Terminate = 15;

	private const string Usage = "trap [-lp] [[ACTION] SIGNAL...]";

	// What sigaction calls SIG_IGN, the same on Linux and macOS.
	private const nint IgnoreHandler = 1;

	// How long a signal that ends the shell waits for the main thread to end it at its next command. A main thread
	// blocked on a program or on read doesn't get there, and the signal's own thread ends the shell instead.
	private static readonly TimeSpan EndDelay = TimeSpan.FromMilliseconds(200);

	// How long kill waits for a signal it sent the shell itself to reach the handler, which runs on another thread.
	private static readonly TimeSpan ArrivalTimeout = TimeSpan.FromSeconds(1);

	// The signals that arrived and haven't been dealt with yet, each with whether a program was running then and its
	// place in the count of arrivals.
	private static readonly ConcurrentQueue<(int Signal, bool DuringProgram, int Number)> Pending = new();

	// .NET runs each signal's handler on a thread of its own, so two can arrive at once.
	private static readonly Lock ArrivalLock = new();

	// The handlers set up for caught signals, by number. Only the main thread touches it.
	private static readonly Dictionary<int, PosixSignalRegistration> Registrations = [];

	// The signals set to be ignored, each with what it did before: a saved sigaction on Unix, nothing on Windows.
	// Only the main thread touches it.
	private static readonly Dictionary<int, nint> Ignored = [];

	// Read by the signal handlers' thread, so it's replaced whole, never changed in place.
	private static volatile ImmutableSortedDictionary<int, Trap> sTraps = ImmutableSortedDictionary<int, Trap>.Empty;

	private static int sArrivals;
	private static volatile int sLastSignal;

	// The number of the last arrival the main thread took from the queue.
	private static volatile int sHandled;

	// The thread ending the shell, or 0 before any does.
	private static int sEndingThread;

	// The signal ending the shell, or 0 when none is.
	private static volatile int sEndingSignal;

	// Whether a trap's action is running, which holds off the others until it's done, as in bash.
	private static bool sInAction;

	/// <inheritdoc />
	public string Name => "trap";

	/// <inheritdoc />
	public string Description => "Runs a command when a signal arrives or the shell ends";

	/// <summary>
	/// Gets how many signals the shell has caught.
	/// </summary>
	/// <value>
	/// The count since the shell started. <c>wait</c> ends when it changes.
	/// </value>
	internal static int Arrivals => Volatile.Read(ref sArrivals);

	/// <summary>
	/// Gets the last signal the shell caught.
	/// </summary>
	/// <value>
	/// Its number, or 0 before any arrived.
	/// </value>
	internal static int LastSignal => sLastSignal;

	/// <summary>
	/// Gets the signals <c>trap</c> takes.
	/// </summary>
	/// <value>
	/// <see cref="KillCommand.Signals" />, and on Windows also HUP and QUIT, which .NET raises there for a closed console
	/// and Ctrl+Break.
	/// </value>
	// Stryker disable once all : each OS only takes its own branch, so a test run on one OS can't tell them apart.
	internal static IReadOnlyDictionary<int, string> Signals { get; } = OperatingSystem.IsWindows()
		? new SortedDictionary<int, string>(KillCommand.Signals.ToDictionary()) { [Hangup] = "HUP", [3] = "QUIT" }
		: KillCommand.Signals;

	/// <inheritdoc />
	/// <remarks>
	/// Takes <c>trap [-lp] [[ACTION] SIGNAL...]</c>, as bash does. An empty action ignores the signals, and programs
	/// the shell starts ignore them too. <c>-</c> as the action, a lone signal, or a first word that is a signal number
	/// puts the signals back to what they did before. With no words, or with <c>-p</c>, it prints the traps as commands
	/// that set them again. A signal the shell can't catch, like KILL, is kept and listed but does nothing. The status
	/// is 1 when a signal is unknown, after the others are set, and 2 for an unknown option or a missing signal.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var list = false;
		var print = false;
		var index = 0;

		for (; index < args.Length && args[index] is ['-', _, ..] option; index++)
		{
			if (option == "--")
			{
				index++;

				break;
			}

			foreach (var letter in option[1..])
			{
				if (letter is not ('l' or 'p'))
				{
					new ShellError("sharpie::trap::invalid_option", $"Unknown option `-{letter}`") { Help = Usage }.Write();

					return 2;
				}

				list |= letter == 'l';
				print |= letter == 'p';
			}
		}

		var words = args[index..];

		if (list)
		{
			return KillCommand.List([], Signals);
		}

		if (print || words.Length == 0)
		{
			return Print(words);
		}

		// As in bash, a first word made of digits that names a signal, or a lone signal, puts the signals back.
		if ((words[0].All(char.IsAsciiDigit) && Parse(words[0]) is not null) || (words is [var only] && Parse(only) is not null))
		{
			return this.Set(null, words);
		}

		if (words.Length == 1)
		{
			new ShellError("sharpie::trap::missing_signal", $"`{words[0]}` needs a signal after it") { Help = Usage }.Write();

			return 2;
		}

		return this.Set(words[0] == "-" ? null : words[0], words[1..]);
	}

	/// <summary>
	/// Tells whether a trap ignores <paramref name="signal" />, so programs the shell starts keep ignoring it.
	/// </summary>
	/// <param name="signal">The signal number.</param>
	/// <returns>
	/// <see langword="true" /> when <c>trap '' SIGNAL</c> is in effect.
	/// </returns>
	internal static bool IsIgnored(int signal) => sTraps.TryGetValue(signal, out var trap) && trap.Action.Length == 0;

	/// <summary>
	/// Notes that <paramref name="signal" /> arrived, from the thread .NET runs signal handlers on.
	/// </summary>
	/// <param name="signal">The signal number.</param>
	/// <param name="endsShell">
	/// <see langword="true" /> when the signal ends the shell unless trapped: TERM and HUP, and INT in a shell that
	/// isn't interactive.
	/// </param>
	/// <remarks>
	/// The signal waits in a queue for <see cref="RunPending" />. One that ends the shell while no program runs also
	/// starts a thread that ends it after <see cref="EndDelay" /> unless the main thread took it by then, in case the
	/// main thread is blocked, which is how bash ends at once on TERM even while a program runs. An INT while a program
	/// runs is left to <see cref="RunPending" />, which looks at how the program ended. shortcut: Windows ends the
	/// process soon after a console close, logoff or shutdown handler returns, so the <c>EXIT</c> trap may not get to
	/// run there; run it inside the handler if that's ever needed.
	/// </remarks>
	internal static void Arrived(int signal, bool endsShell)
	{
		// Windows has no way to ignore most signals, so their handler gets them and drops them here.
		if (sTraps.TryGetValue(signal, out var trap) && trap.Action.Length == 0)
		{
			return;
		}

		var trapped = trap is not null;

		var duringProgram = ExternalProgram.ProgramRunning;
		int number;

		// The count moves only once the signal is queued, as kill and wait take a new count to mean it's there to run,
		// and one at a time keeps the queue in number order for sHandled.
		lock (ArrivalLock)
		{
			number = sArrivals + 1;
			sLastSignal = signal;
			Pending.Enqueue((signal, duringProgram, number));
			Volatile.Write(ref sArrivals, number);
		}

		if (endsShell && !trapped && !(signal == Interrupt && duringProgram))
		{
			new Thread(() =>
			{
				Thread.Sleep(EndDelay);

				if (sHandled < number)
				{
					EndBySignal(signal, null);
				}
			})
			{ IsBackground = true }.Start();
		}
	}

	/// <summary>
	/// Waits for a signal that <c>kill</c> sent the shell itself to reach its handler, so its trap runs right after
	/// <c>kill</c>, as in bash.
	/// </summary>
	/// <param name="signal">The signal sent.</param>
	/// <param name="arrivals"><see cref="Arrivals" /> from before it was sent.</param>
	/// <remarks>
	/// Returns at once for a signal the shell doesn't catch, which never arrives.
	/// </remarks>
	internal static void WaitForArrival(int signal, int arrivals)
	{
		if (!IsIgnored(signal) && (signal == Interrupt || Registrations.ContainsKey(signal)))
		{
			_ = SpinWait.SpinUntil(() => Arrivals != arrivals, ArrivalTimeout);
		}
	}

	/// <summary>
	/// Deals with the signals that arrived since the last call, from the main thread between commands.
	/// </summary>
	/// <param name="interactive"><see langword="true" /> when the shell reads typed lines.</param>
	/// <param name="status">The last command's status, which <c>$?</c> keeps through each action.</param>
	/// <remarks>
	/// A trapped signal runs its action. An untrapped TERM or HUP ends the shell as <see cref="EndBySignal" /> does, and
	/// so does an untrapped INT in a shell that isn't interactive, unless a program was running and didn't die of it, the way
	/// bash lets a script go on after ping catches Ctrl+C and exits normally. Nothing runs while a trap's action does.
	/// </remarks>
	internal static void RunPending(bool interactive, int status)
	{
		if (sInAction)
		{
			return;
		}

		while (Pending.TryDequeue(out var arrival))
		{
			sHandled = arrival.Number;

			if (sTraps.TryGetValue(arrival.Signal, out var trap))
			{
				trap.Owner.RunAction(trap.Action, status);
			}
			else if (arrival.Signal is Hangup or Terminate || (arrival.Signal == Interrupt && !interactive && (!arrival.DuringProgram || status == 128 + Interrupt)))
			{
				EndBySignal(arrival.Signal, status);
			}
		}
	}

	/// <summary>
	/// Runs the <c>EXIT</c> trap before the shell ends.
	/// </summary>
	/// <param name="status">The status the shell ends with, which <c>$?</c> holds in the action.</param>
	/// <param name="keepStatus"><see langword="true" /> to leave <c>$?</c> as it is instead.</param>
	/// <returns>
	/// <paramref name="status" />. An <c>exit</c> inside the action ends the shell there with its own status.
	/// </returns>
	/// <remarks>
	/// Only the first thread to end the shell runs it. That thread gets its status straight back the second time, which
	/// is how <c>exit</c> inside the action ends the shell. Any other thread waits for the first to end the process.
	/// </remarks>
	internal static int RunExitTrap(int status, bool keepStatus = false)
	{
		var thread = Environment.CurrentManagedThreadId;
		var ending = Interlocked.CompareExchange(ref sEndingThread, thread, 0);

		if (ending == thread)
		{
			return status;
		}

		if (ending != 0)
		{
			Thread.Sleep(Timeout.Infinite);
		}

		if (sTraps.TryGetValue(ExitTrap, out var trap))
		{
			trap.Owner.RunAction(trap.Action, keepStatus ? null : status);
		}

		return status;
	}

	/// <summary>
	/// Ends the shell for a signal it doesn't trap, after the <c>EXIT</c> trap.
	/// </summary>
	/// <param name="signal">The signal number.</param>
	/// <param name="status">What <c>$?</c> holds in the <c>EXIT</c> trap, or <see langword="null" /> to leave it.</param>
	/// <remarks>
	/// On Unix the shell dies of the signal itself, as bash does, so whatever started it can tell. bash stops a script
	/// whose program died of Ctrl+C but goes on after one that exited. Windows has no such thing, so the shell exits
	/// with 128 plus the number there. An <c>exit</c> inside the trap doesn't change that, as in bash. A second signal that arrives while the <c>EXIT</c> trap runs ends the shell at
	/// once, as bash does, so a trap that hangs can't keep it alive.
	/// <para>
	/// The thread <see cref="Arrived" /> starts can get here while the main thread is inside a subshell. Then
	/// <c>exit</c> in the trap still ends the process by the signal, and the trap runs with the subshell's traps and output.
	/// shortcut: bash would run the shell's own <c>EXIT</c> trap on its own output; keep the shell's table aside in
	/// <see cref="Save" /> if that's ever needed.
	/// </para>
	/// </remarks>
	[DoesNotReturn]
	private static void EndBySignal(int signal, int? status)
	{
		var ending = Volatile.Read(ref sEndingThread);

		if (ending != 0 && ending != Environment.CurrentManagedThreadId)
		{
			Die(signal);
		}

		sEndingSignal = signal;

		try
		{
			_ = RunExitTrap(status ?? 0, keepStatus: status is null);
		}
		catch (ExitCommand.SubshellExit)
		{
			Die(signal);
		}

		// Stryker disable once all : ending the process would end the test run, so the shell's process tests check it.
		if (!OperatingSystem.IsWindows())
		{
			// Added last, so the other exit handlers, like the one saving the history, run first.
			AppDomain.CurrentDomain.ProcessExit += (_, _) => Die(signal);
		}

		// Stryker disable once Statement : ending the process would end the test run, so the shell's process tests check it.
		Environment.Exit(128 + signal);
	}

	/// <summary>
	/// Ends the process by <paramref name="signal" /> on Unix, or with 128 plus its number on Windows.
	/// </summary>
	/// <param name="signal">The signal number.</param>
	[DoesNotReturn]
	private static void Die(int signal)
	{
		// Stryker disable once all : ending the process would end the test run, so the shell's process tests check it.
		if (!OperatingSystem.IsWindows())
		{
			_ = NativeMethods.SetSignalHandler(signal, 0);
			_ = NativeMethods.Kill(Environment.ProcessId, signal);
		}

		// Stryker disable once Statement : ending the process would end the test run, so the shell's process tests check it.
		Environment.Exit(128 + signal);
	}

	/// <summary>
	/// Ends the shell with <paramref name="status" />, after the <c>EXIT</c> trap.
	/// </summary>
	/// <param name="status">The status.</param>
	/// <remarks>
	/// Inside an <c>EXIT</c> trap that a signal started, the shell still ends by that signal, as in bash.
	/// </remarks>
	[DoesNotReturn]
	internal static void Exit(int status)
	{
		var code = RunExitTrap(status);

		if (sEndingSignal != 0)
		{
			Die(sEndingSignal);
		}

		// Stryker disable once Statement : ending the process would end the test run, so the shell's process tests check it.
		Environment.Exit(code);
	}

	/// <summary>
	/// Gets the trap table, for a subshell to put back at its end.
	/// </summary>
	/// <returns>
	/// The table as it is now.
	/// </returns>
	internal static ImmutableSortedDictionary<int, Trap> Save() => sTraps;

	/// <summary>
	/// Takes out the <c>EXIT</c> trap a subshell set, which runs at the subshell's end, as in bash.
	/// </summary>
	/// <param name="saved">The table from before the subshell, from <see cref="Save" />.</param>
	/// <returns>
	/// What runs the trap with the subshell's status and gives that status back, or <see langword="null" /> when the
	/// subshell set none. The shell's own <c>EXIT</c> trap doesn't run at a subshell's end.
	/// </returns>
	internal static Func<int, int>? TakeSubshellExitTrap(ImmutableSortedDictionary<int, Trap> saved)
	{
		if (!sTraps.TryGetValue(ExitTrap, out var trap) || ReferenceEquals(trap, saved.GetValueOrDefault(ExitTrap)))
		{
			return null;
		}

		sTraps = sTraps.Remove(ExitTrap);

		return status =>
		{
			trap.Owner.RunAction(trap.Action, status);

			return status;
		};
	}

	/// <summary>
	/// Puts back the trap table from before a subshell, and the signals with it.
	/// </summary>
	/// <param name="saved">The table from <see cref="Save" />.</param>
	internal static void Restore(ImmutableSortedDictionary<int, Trap> saved)
	{
		if (sTraps != saved)
		{
			sTraps = saved;
			Sync();
		}
	}

	/// <summary>
	/// Reads a signal for <c>trap</c>.
	/// </summary>
	/// <param name="spec"><c>EXIT</c> or <c>0</c>, or a signal as <see cref="KillCommand.ParseSignal" /> reads it.</param>
	/// <returns>
	/// The signal number, 0 for <c>EXIT</c>, or <see langword="null" /> when it isn't one.
	/// </returns>
	private static int? Parse(string spec) =>
		spec == "0" || spec.Equals("EXIT", StringComparison.OrdinalIgnoreCase) ? ExitTrap : KillCommand.ParseSignal(spec, Signals);

	/// <summary>
	/// Gets the name <c>trap</c> prints for a signal.
	/// </summary>
	/// <param name="signal">The signal number, or 0 for <c>EXIT</c>.</param>
	/// <returns>
	/// <c>EXIT</c>, or the name with the <c>SIG</c> prefix, as bash prints it.
	/// </returns>
	private static string NameOf(int signal) => signal == ExitTrap ? "EXIT" : "SIG" + Signals[signal];

	/// <summary>
	/// Prints that <paramref name="spec" /> isn't a signal.
	/// </summary>
	/// <param name="spec">The word that was read as a signal.</param>
	private static void InvalidSignal(string spec) =>
		new ShellError("sharpie::trap::invalid_signal", $"`{spec}` isn't a signal") { Help = "trap -l lists the signals" }.Write();

	/// <summary>
	/// Prints traps as the commands that set them, <c>EXIT</c> first and then by signal number, as bash does.
	/// </summary>
	/// <param name="specs">The signals to print, or none for every trap.</param>
	/// <returns>
	/// 0, or 1 when any of <paramref name="specs" /> isn't a signal.
	/// </returns>
	private static int Print(string[] specs)
	{
		var traps = sTraps;
		var status = 0;
		var signals = new List<int>();

		foreach (var spec in specs)
		{
			if (Parse(spec) is { } signal)
			{
				signals.Add(signal);
			}
			else
			{
				InvalidSignal(spec);
				status = 1;
			}
		}

		foreach (var (signal, trap) in traps.Where(entry => specs.Length == 0 || signals.Contains(entry.Key)))
		{
			Console.WriteLine($"trap -- {CommandLine.Quote(trap.Action)} {NameOf(signal)}");
		}

		return status;
	}

	/// <summary>
	/// Sets or removes the trap for each of <paramref name="specs" />, then makes the OS match.
	/// </summary>
	/// <param name="action">What to run, empty to ignore the signals, or <see langword="null" /> to remove the traps.</param>
	/// <param name="specs">The signals.</param>
	/// <returns>
	/// 0, or 1 when any of <paramref name="specs" /> isn't a signal.
	/// </returns>
	private int Set(string? action, string[] specs)
	{
		var status = 0;

		foreach (var spec in specs)
		{
			if (Parse(spec) is not { } signal)
			{
				InvalidSignal(spec);
				status = 1;
			}
			else
			{
				sTraps = action is null ? sTraps.Remove(signal) : sTraps.SetItem(signal, new(action, this));
			}
		}

		Sync();

		return status;
	}

	/// <summary>
	/// Runs a trap's action with <c>$?</c> set to <paramref name="status" />, and puts <c>$?</c> back after.
	/// </summary>
	/// <param name="action">The action.</param>
	/// <param name="status">The status <c>$?</c> holds, or <see langword="null" /> to leave it as it is.</param>
	private void RunAction(string action, int? status)
	{
		if (action.Length == 0)
		{
			return;
		}

		var outer = sInAction;
		var kept = status ?? declare.Status;

		sInAction = true;
		declare.Status = kept;

		try
		{
			_ = runLine(action);
		}
		finally
		{
			sInAction = outer;
			declare.Status = kept;
		}
	}

	/// <summary>
	/// Sets up the OS's signal handling to match the trap table. Changes nothing when it already does.
	/// </summary>
	/// <remarks>
	/// An ignored signal is ignored by the OS itself, so the programs the shell starts inherit it. A signal with an
	/// action gets a handler that holds it for <see cref="RunPending" />, and so do TERM and HUP while an <c>EXIT</c>
	/// trap is set, so it can run before the shell ends. Without one they keep .NET's own handling, which ends the shell
	/// at once. INT always has the handler <see cref="Program" /> sets up.
	/// </remarks>
	private static void Sync()
	{
		var traps = sTraps;

		foreach (var signal in Signals.Keys.Where(signal => !IsInert(signal)))
		{
			var trap = traps.GetValueOrDefault(signal);

			// Windows can only ignore Ctrl+C. Other ignored signals get a handler that drops them, in Arrived.
			var ignore = trap?.Action.Length == 0 && (!OperatingSystem.IsWindows() || signal == Interrupt);
			var caught = signal != Interrupt && !ignore && (trap is not null || (signal is Terminate or Hangup && traps.ContainsKey(ExitTrap)));

			// The old action comes back before a handler goes in, and the handler before the signal is ignored, since
			// .NET puts its own action in place when the first handler for a signal is set up.
			if (!ignore)
			{
				SetIgnored(signal, ignore: false);
			}

			SetCaught(signal, caught);

			if (ignore)
			{
				SetIgnored(signal, ignore: true);
			}
		}
	}

	/// <summary>
	/// Tells whether a trap on <paramref name="signal" /> is only listed and does nothing.
	/// </summary>
	/// <param name="signal">The signal number.</param>
	/// <returns>
	/// <see langword="true" /> for KILL and STOP, which can't be caught, and for the signals .NET needs for itself:
	/// SEGV and the other faults for its null checks, CHLD for collecting programs that ended, and RTMIN for pausing
	/// threads.
	/// </returns>
	private static bool IsInert(int signal) =>
		Signals[signal] is "KILL" or "STOP" or "ILL" or "TRAP" or "ABRT" or "BUS" or "FPE" or "SEGV" or "CHLD" or "RTMIN";

	/// <summary>
	/// Adds or removes the handler that holds <paramref name="signal" /> for <see cref="RunPending" />.
	/// </summary>
	/// <param name="signal">The signal number.</param>
	/// <param name="caught"><see langword="true" /> to have the handler.</param>
	private static void SetCaught(int signal, bool caught)
	{
		if (caught && !Registrations.ContainsKey(signal))
		{
			Registrations[signal] = PosixSignalRegistration.Create(ToPosixSignal(signal), context =>
			{
				context.Cancel = true;
				Arrived(signal, endsShell: true);
			});
		}
		else if (!caught && Registrations.Remove(signal, out var registration))
		{
			registration.Dispose();
		}
	}

	/// <summary>
	/// Makes the OS ignore <paramref name="signal" />, or puts back what it did before.
	/// </summary>
	/// <param name="signal">The signal number.</param>
	/// <param name="ignore"><see langword="true" /> to ignore it.</param>
	/// <remarks>
	/// On Unix the action from before is saved whole, so .NET's own handler comes back with its flags. On Windows only
	/// Ctrl+C can be ignored, which programs started after inherit.
	/// </remarks>
	private static unsafe void SetIgnored(int signal, bool ignore)
	{
		if (ignore == Ignored.ContainsKey(signal))
		{
			return;
		}

		// Stryker disable once all : each OS only takes its own branch, so a test run on one OS can't tell them apart.
		if (OperatingSystem.IsWindows())
		{
			_ = NativeMethods.SetConsoleCtrlHandler(0, ignore);

			if (ignore)
			{
				Ignored[signal] = 0;
			}
			else
			{
				_ = Ignored.Remove(signal);
			}

			return;
		}

		if (ignore)
		{
			var saved = NativeMemory.AllocZeroed(NativeMethods.OpaqueSize);
			var ignoring = NativeMemory.AllocZeroed(NativeMethods.OpaqueSize);

			// The handler is the first field of struct sigaction on both Linux and macOS.
			*(nint*)ignoring = IgnoreHandler;
			_ = NativeMethods.SetSignalAction(signal, ignoring, saved);
			NativeMemory.Free(ignoring);
			Ignored[signal] = (nint)saved;
		}
		else if (Ignored.Remove(signal, out var saved))
		{
			_ = NativeMethods.SetSignalAction(signal, (void*)saved, null);
			NativeMemory.Free((void*)saved);
		}
	}

	/// <summary>
	/// Turns a signal number into what <see cref="PosixSignalRegistration.Create" /> takes.
	/// </summary>
	/// <param name="signal">The signal number.</param>
	/// <returns>
	/// The number itself on Unix, which .NET accepts as is, or on Windows the named signal.
	/// </returns>
	// Stryker disable once all : each OS only takes its own branch, so a test run on one OS can't tell them apart.
	private static PosixSignal ToPosixSignal(int signal) => !OperatingSystem.IsWindows() ? (PosixSignal)signal : signal switch
	{
		Hangup => PosixSignal.SIGHUP,
		Terminate => PosixSignal.SIGTERM,
		_ => PosixSignal.SIGQUIT,
	};

	/// <summary>
	/// One signal's trap.
	/// </summary>
	/// <param name="Action">The command line to run, or empty to ignore the signal.</param>
	/// <param name="Owner">The <c>trap</c> builtin that set it, which runs the action.</param>
	internal sealed record Trap(string Action, TrapCommand Owner);
}
