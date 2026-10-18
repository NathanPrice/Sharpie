using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Aliases;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.Conditions;
using Sharpie.Shell.Features.DotNetDocs;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.History;
using Sharpie.Shell.Features.Jobs;
using Sharpie.Shell.Features.LineEditing;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell;

/// <summary>
/// Hosts the shell's read-eval-print loop.
/// </summary>
internal static class Program
{
	/// <summary>
	/// The environment variable the terminal window sets to <c>1</c> so the shell reports its working directory before
	/// each prompt.
	/// </summary>
	internal const string WindowVariable = "SHARPIE_WINDOW";

	/// <summary>
	/// The environment variable that, set to <c>0</c>, keeps the shell away from the encrypted history file and its
	/// key even in the window.
	/// </summary>
	/// <remarks>
	/// The window's own tests run a real shell in window mode. Without this, the lines they type would be saved into
	/// the user's history.
	/// </remarks>
	internal const string HistoryVariable = "SHARPIE_HISTORY";

	/// <summary>
	/// The environment variable that, set to <c>0</c>, keeps the shell from copying the variables and aliases of the
	/// user's own shell even in the window.
	/// </summary>
	/// <remarks>
	/// The window's own tests run a real shell in window mode. Without this, an alias in the user's startup files
	/// could change what the commands they type print.
	/// </remarks>
	internal const string ImportVariable = "SHARPIE_IMPORT";

	/// <summary>
	/// The start of the private OSC sequence the shell writes, followed by a base64 command line and BEL, to have the
	/// terminal window show a man page in its own reader.
	/// </summary>
	/// <remarks>
	/// Base64 so no character in the line can end the sequence early.
	/// </remarks>
	internal const string ManPageOsc = "5150;man;";

	/// <summary>
	/// The argument that keeps an interactive shell from running <see cref="RcFileName" />, like bash's
	/// <c>--norc</c>.
	/// </summary>
	/// <remarks>
	/// The window's own tests pass it, so a command in the user's startup file can't change what their commands print.
	/// </remarks>
	internal const string NoRcOption = "--norc";

	/// <summary>
	/// The startup file in the user's home folder that an interactive shell runs before its first prompt.
	/// </summary>
	internal const string RcFileName = ".sharpierc";

	/// <summary>
	/// Gets where commands with no <c>&gt;</c> of their own write.
	/// </summary>
	/// <value>
	/// <see langword="null" /> for the terminal, or the stream of the command substitution or builtin being run.
	/// </value>
	internal static Stream? StandardOutput { get; private set; }

	/// <summary>
	/// Creates one instance of every builtin command, as <see cref="BuiltinCommands.Create" /> describes, whose lines
	/// run through <see cref="RunLine" />.
	/// </summary>
	/// <param name="commands">The dictionary the shell looks commands up in.</param>
	/// <returns>
	/// The builtins, not yet added to <paramref name="commands" />.
	/// </returns>
	/// <remarks>
	/// The <c>alias</c> and <c>declare</c> builtins are looked up when a line runs, by which time
	/// <paramref name="commands" /> holds them.
	/// </remarks>
	internal static ICommand[] CreateBuiltins(Dictionary<string, ICommand> commands) =>
		BuiltinCommands.Create(commands, line => RunLine(commands, (AliasCommand)commands["alias"], (DeclareCommand)commands["declare"], line), () => CompoundCommands.SubshellDepth > 0);

	/// <summary>
	/// Registers the builtins, then runs the <c>-c</c> command or the script file, or else reads and runs one command
	/// line at a time until <c>exit</c> ends the process or standard input closes.
	/// </summary>
	/// <remarks>
	/// When standard input and standard error are both terminals, the shell edits lines itself with
	/// <see cref="LineEditor" /> and controls jobs. Otherwise it reads plain lines after a <c>$ </c> prompt, which is
	/// how its own process tests drive it.
	/// </remarks>
	/// <param name="args">
	/// The options <see cref="ShellOptions.Parse" /> reads, or <see cref="ShellImport.PrintEnvironmentOption" /> alone
	/// to print the environment and exit.
	/// </param>
	/// <returns>
	/// The last command's status, or 2 when the arguments were refused.
	/// </returns>
	private static int Main(string[] args)
	{
		AppLog.Start("shell");

		// Windows consoles default to a code page without ❯ and most other non-ASCII characters, and a non-UTF-8 locale
		// does the same on Unix. Sharpie's window decodes the shell's output as UTF-8.
		Console.OutputEncoding = Encoding.UTF8;

		// Error reports are colored only on a terminal, and a Windows console prints the color codes as text until
		// escape handling is on.
		if (!Console.IsErrorRedirected && OperatingSystem.IsWindows())
		{
			NativeMethods.EnableVirtualTerminalOutput();
		}

		ShellError.UseColor = !Console.IsErrorRedirected;

		if (args is [ShellImport.PrintEnvironmentOption])
		{
			ShellImport.PrintEnvironment();

			return 0;
		}

		if (ShellOptions.Parse(NativeMethods.ProgramName(), args, out var error) is not { } options)
		{
			new ShellError("sharpie::options::invalid", error!).Write();

			return 2;
		}

		var commands = new Dictionary<string, ICommand>();
		var terminal = !Console.IsInputRedirected && !Console.IsErrorRedirected;

		// Ctrl+C stops the running program but not the shell. Without this, .NET ends the shell on SIGINT. On a terminal
		// the line discipline already sends SIGINT to the program, so passing it on would make a second one.
		using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
		{
			context.Cancel = true;
			ExternalProgram.CountInterrupt();
			ExternalProgram.StopWaiting();

			if (!terminal)
			{
				ExternalProgram.InterruptRunning();
			}
		});

		foreach (var command in CreateBuiltins(commands))
		{
			commands.Add(command.Name, command);
		}

		var declare = (DeclareCommand)commands["declare"];

		// The parent may have passed a PWD from before it changed folders, so $PWD starts out right.
		declare.Export("PWD", Directory.GetCurrentDirectory());
		declare.RaiseShellLevel();

		var aliases = (AliasCommand)commands["alias"];
		var interactive = options.IsInteractive(terminal);

		var settings = ((SetCommand)commands["set"]).Settings;

		settings.Interactive = interactive;
		_ = settings.Set("noclobber", options.NoClobber);

		// -c takes $0 from the first word after the command, as sh does. The script file's arguments become its
		// positional parameters through the . builtin below.
		declare.Set("0", options switch
		{
			{ Command: not null, Arguments: [var name, ..] } => name,
			{ ScriptFile: { } script } => script,
			_ => NativeMethods.ProgramName() ?? "Sharpie",
		});

		if (options is { Command: not null, Arguments.Count: > 1 })
		{
			declare.SetPositional([.. options.Arguments.Skip(1)]);
		}

		if (options.Login)
		{
			ShellImport.RunProfile(Environment.ProcessPath!, aliases, commands.Keys);
		}

		// A command or script runs on its own, with no banner, import, history or job control. Programs started from a
		// tab inherit the window variable, and vim's :! would otherwise print fastfetch above every command's output.
		if (options.Command is not null || options.ScriptFile is not null)
		{
			if (interactive && !options.NoRc)
			{
				RunRcFile(commands["."]);
			}

			if (options.Command is null)
			{
				return BuiltinCommands.CreateScriptRunner(declare, line => RunLine(commands, aliases, declare, line)).Run([options.ScriptFile!, .. options.Arguments]);
			}

			declare.LineNumber = 1;

			return RunLine(commands, aliases, declare, options.Command);
		}

		return RunPrompts(commands, declare, aliases, terminal, interactive && !options.NoRc);
	}

	/// <summary>
	/// Shows the banner and imports the login shell's aliases, for a shell running in the Sharpie window.
	/// </summary>
	/// <param name="commands">The builtins, by name, which an imported alias may start with.</param>
	/// <param name="aliases">The <c>alias</c> builtin, which gets the imported aliases.</param>
	/// <returns>
	/// <see langword="true" /> when the banner was printed.
	/// </returns>
	private static bool StartWindow(Dictionary<string, ICommand> commands, AliasCommand aliases)
	{
		// Run gives null when fastfetch isn't installed, and prints nothing.
		var bannerShown = ExternalProgram.Run("fastfetch", []) is not null;

		// After the banner, so the window isn't blank while the user's shell starts up.
		if (Environment.GetEnvironmentVariable(ImportVariable) != "0")
		{
			ShellImport.Run(Environment.GetEnvironmentVariable("SHELL"), Environment.ProcessPath!, aliases, commands.Keys);
		}

		return bannerShown;
	}

	/// <summary>
	/// Reads and runs one command line at a time until <c>exit</c> ends the process or standard input closes.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables and the last status.</param>
	/// <param name="aliases">The <c>alias</c> builtin.</param>
	/// <param name="terminal">
	/// <see langword="true" /> when standard input and standard error are both terminals, so lines are edited and jobs
	/// controlled.
	/// </param>
	/// <param name="runRcFile"><see langword="true" /> to run <see cref="RcFileName" /> before the first prompt.</param>
	/// <returns>
	/// The last command's status once standard input closes.
	/// </returns>
	private static int RunPrompts(Dictionary<string, ICommand> commands, DeclareCommand declare, AliasCommand aliases, bool terminal, bool runRcFile)
	{
		var variables = declare.Variables;

		// The window features below are off when the variable is unset, so the shell's own process tests never touch
		// the keyring.
		var inWindow = Environment.GetEnvironmentVariable(WindowVariable) == "1";
		var bannerShown = inWindow && StartWindow(commands, aliases);

		// After the import so the user's own settings win, and before the history starts so a HISTFILE set there is used.
		if (runRcFile)
		{
			RunRcFile(commands["."]);
		}

		var historyCommand = (HistoryCommand)commands["history"];

		// A shell variable, so HISTFILE=... in the startup file works without export, as in bash. Empty means none.
		var histFile = CommandLine.Lookup(variables, "HISTFILE") is { Length: > 0 } name ? name : null;
		var history = HistorySession.Start(historyCommand, useEncryptedFile: inWindow && Environment.GetEnvironmentVariable(HistoryVariable) != "0", histFile);

		// Runs for both the exit builtin, which calls Environment.Exit, and Main returning when input closes.
		AppDomain.CurrentDomain.ProcessExit += (_, _) => history.WriteHistFile();

		// exec replaces the process, so no exit handler runs. bash saves its history there too.
		((ExecCommand)commands["exec"]).BeforeReplace = history.WriteHistFile;

		// Closing the window sends SIGHUP, and the runtime ends the process on it without raising ProcessExit. bash also
		// saves its history on hangup.
		using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => history.WriteHistFile());
		var editor = terminal ? CreateEditor(commands, aliases, historyCommand) : null;

		if (terminal && OperatingSystem.IsWindows())
		{
			NativeMethods.EnableVirtualTerminalOutput();
		}
		else if (terminal)
		{
			JobControl.Initialize();
		}

		// Times each line for the "took" part of the next header. Zero before the first command.
		var took = TimeSpan.Zero;

		// An empty screen gets no blank line above the header: the very first prompt when no banner was printed, and the
		// prompt right after clear.
		var blankLine = bannerShown;

		// The lines typed so far, here-document bodies included, for $LINENO.
		var typed = 0;

		while (true)
		{
			// Every path through the loop comes back here, so a finished job's Done line lands after the last
			// command's output and before the next prompt.
			JobsCommand.Reap(listRunning: false);

			var line = editor is null ? Prompt(inWindow) : ReadInteractive(editor, took, blankLine);

			// Standard input closed, like Ctrl+D in bash. The window or a test can close the pipe without sending exit,
			// and looping on would spin forever.
			if (line is null)
			{
				return declare.Status;
			}

			// The history file keeps one entry per line, so a here-document's body isn't saved.
			history.Record(line);

			if (ReadBodies(line, editor) is not { } command)
			{
				took = TimeSpan.Zero;

				continue;
			}

			line = command;
			declare.LineNumber = typed + 1;
			typed += line.Split('\n').Length;

			if (inWindow && editor is not null && CommandLine.PagedCommand(line) is { } paged
				&& (paged[0] == "man" ? ManPageExists(paged[1..]) : DmanCommand.Find(paged[1..])))
			{
				Console.Write($"\e]{ManPageOsc}{Convert.ToBase64String(Encoding.UTF8.GetBytes(line))}\a");
				took = TimeSpan.Zero;

				continue;
			}

			var clock = Stopwatch.StartNew();

			_ = RunLine(commands, aliases, declare, line);
			took = clock.Elapsed;
			blankLine = line.Trim() != "clear";
		}

		// Writes the prompt and reads one line. Returns null once standard input is closed.
		static string? Prompt(bool inWindow)
		{
			// Written in one call with the prompt so whatever reads the pipe gets them together.
			Console.Write(inWindow
				? $"\e]7;{new Uri(Directory.GetCurrentDirectory()).AbsoluteUri}\a$ "
				: "$ ");

			return Console.ReadLine();
		}

		// Draws the header and reads one edited line.
		static string? ReadInteractive(LineEditor editor, TimeSpan took, bool blankLine) =>
			ReadEdited(editor, PromptHeader.Build(Directory.GetCurrentDirectory(), took, Console.WindowWidth, blankLine), continuation: false);
	}

	/// <summary>
	/// Reads one line with the line editor.
	/// </summary>
	/// <param name="editor">The editor.</param>
	/// <param name="header">The text drawn above the prompt.</param>
	/// <param name="continuation"><see langword="true" /> for a line of a here-document's body.</param>
	/// <returns>
	/// What <see cref="LineEditor.ReadLine" /> gives.
	/// </returns>
	/// <remarks>
	/// Ctrl+C is read as a key only while the line is being edited, so programs the line starts still get it as
	/// SIGINT, or as a console Ctrl+C event on Windows.
	/// </remarks>
	private static string? ReadEdited(LineEditor editor, string header, bool continuation)
	{
		Console.TreatControlCAsInput = true;

		try
		{
			return editor.ReadLine(header, continuation);
		}
		finally
		{
			Console.TreatControlCAsInput = false;
		}
	}

	/// <summary>
	/// Reads the bodies of the here-documents <paramref name="line" /> starts, and the rest of a compound command or a
	/// line that ends in <c>&amp;&amp;</c>, <c>||</c> or <c>|</c>, a line at a time after a <c>&gt; </c> prompt, until
	/// the command is whole.
	/// </summary>
	/// <param name="line">The line just read.</param>
	/// <param name="editor">The line editor, or <see langword="null" /> for plain lines from standard input.</param>
	/// <returns>
	/// The command with its bodies. At the end of plain input, the lines read so far, as bash runs them.
	/// <see langword="null" /> after Ctrl+C or Ctrl+D in the editor, which drops the command.
	/// </returns>
	[SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "bash's PS2 prompt, the same in every language, like the $ prompt.")]
	private static string? ReadBodies(string line, LineEditor? editor)
	{
		while (CommandList.NeedsMoreLines(line))
		{
			if (editor is null)
			{
				Console.Write(LineEditor.ContinuationPrompt);
			}

			if ((editor is null ? Console.ReadLine() : ReadEdited(editor, string.Empty, continuation: true)) is not { } next)
			{
				return editor is null ? line : null;
			}

			line += "\n" + next;
		}

		return line;
	}

	/// <summary>
	/// Runs <see cref="RcFileName" /> from the user's home folder when it exists.
	/// </summary>
	/// <param name="source">The <c>.</c> builtin, which runs the file's lines.</param>
	/// <remarks>
	/// The home folder is <c>$HOME</c> on Linux and macOS and <c>%USERPROFILE%</c> on Windows. Only an interactive shell
	/// calls this, as bash only reads <c>~/.bashrc</c> then.
	/// </remarks>
	private static void RunRcFile(ICommand source)
	{
		var rcFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), RcFileName);

		if (File.Exists(rcFile))
		{
			_ = source.Run([rcFile]);
		}
	}

	/// <summary>
	/// Checks quietly whether <c>man</c> has a page for <paramref name="arguments" />, so the window is only asked to
	/// show pages that exist and <c>man</c>'s own message prints for the rest.
	/// </summary>
	/// <param name="arguments">The arguments after <c>man</c>.</param>
	/// <returns>
	/// <see langword="true" /> when <c>man -w</c> found the page.
	/// </returns>
	private static bool ManPageExists(string[] arguments)
	{
		if (PathFinder.FindExecutable("man") is not { } man)
		{
			return false;
		}

		// All three are redirected. .NET treats a child with any of them on the terminal as one that uses it, and its
		// terminal handling for that child stops the shell ignoring SIGTTOU. The shell is then stopped when it takes the
		// terminal back from man, and the next prompt crashes.
		var startInfo = new ProcessStartInfo(man)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		startInfo.ArgumentList.Add("-w");

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		try
		{
			using var process = Process.Start(startInfo);

			if (process is null)
			{
				return false;
			}

			process.StandardInput.Close();

			// Both pipes are drained, or a long answer could fill one and hang the check.
			var errors = process.StandardError.ReadToEndAsync();

			_ = process.StandardOutput.ReadToEnd();
			errors.Wait();
			process.WaitForExit();

			return process.ExitCode == 0;
		}
		catch (Win32Exception exception)
		{
			Log.Debug(exception, "Can't start {Man} to check for a page", man);

			return false;
		}
	}

	/// <summary>
	/// Creates the line editor for an interactive shell, reading keys from the terminal.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin, whose names are completed and colored like the builtins'.</param>
	/// <param name="history">The <c>history</c> builtin, whose lines Up and Down step through.</param>
	/// <returns>
	/// The editor.
	/// </returns>
	private static LineEditor CreateEditor(Dictionary<string, ICommand> commands, AliasCommand aliases, HistoryCommand history)
	{
		var completions = (CompleteCommand)commands["complete"];
		var names = new CommandNames(commands, aliases.Aliases);

		return new LineEditor(
			() => Console.ReadKey(intercept: true),
			Console.Out,
			() => Console.WindowWidth,
			new TabCompleter(names, completions.FindCompleter),
			history.Lines,
			names);
	}

	/// <summary>
	/// Runs the commands of one line, as <see cref="CommandList.Run(string, int, ShellHooks, Action{int})" /> describes.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin, whose aliases are expanded first.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables and the last status.</param>
	/// <param name="line">The line the user entered.</param>
	/// <param name="output">
	/// Where the line's commands write when they have no <c>&gt;</c> of their own, or <see langword="null" /> to keep
	/// <see cref="StandardOutput" />.
	/// </param>
	/// <returns>
	/// The last command's status.
	/// </returns>
	/// <remarks>
	/// The status is stored after each command, so <c>false; echo $?</c> prints 1, and again after the line, for one
	/// that <see cref="CommandList" /> refuses. With <c>set -e</c>, a failed command ends the shell with its status
	/// through <see cref="Environment.Exit" />, so the history is still saved. A failure anywhere inside a command
	/// that <c>&amp;&amp;</c> or <c>||</c> tests doesn't, so <c>eval false || echo ok</c> prints ok, as in bash.
	/// A <c>${...}</c> that can't expand, such as <c>${X:?}</c> with <c>X</c> unset, ends the line there, as
	/// <see cref="CompoundCommands.RunList" /> describes.
	/// </remarks>
	internal static int RunLine(Dictionary<string, ICommand> commands, AliasCommand aliases, DeclareCommand declare, string line, Stream? output = null)
	{
		var outer = StandardOutput;

		// An eval or $(...) counts from the line it's on, as bash's eval does.
		var first = declare.LineNumber;

		StandardOutput = output ?? StandardOutput;

		try
		{
			return declare.Status = CompoundCommands.RunList(commands, aliases, declare, line, first);
		}
		finally
		{
			StandardOutput = outer;
		}
	}

	/// <summary>
	/// Splits one command and runs it as a pipeline, a function or builtin, or a program on <c>PATH</c>.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin, whose aliases are expanded first.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables that <c>$NAME</c> expands to.</param>
	/// <param name="settings">The <c>set</c> options, which <c>-u</c>, <c>-x</c> and pipefail change here.</param>
	/// <param name="line">
	/// The command, with no <c>;</c> left in it, which error reports show and point into. It may end with
	/// <c>&amp;</c>, and then it may also hold <c>&amp;&amp;</c> or <c>||</c>, for the whole and-or list to run in the
	/// background.
	/// </param>
	/// <returns>
	/// The command's status: <see cref="ExternalProgram.NotFound" /> when it isn't found, 1 when a redirect file
	/// can't be opened or a variable isn't set under <c>set -u</c>, and <see cref="CommandList.UnsupportedStatus" />
	/// when it holds an operator the shell can't run. A line of only <c>NAME=VALUE</c> words, or of words that all
	/// expand to nothing, gives the last command substitution's status, as in bash, so
	/// <c>out=$(cmd) || echo failed</c> works.
	/// </returns>
	/// <remarks>
	/// Under <c>set -u</c> a shell that isn't interactive exits with 127 at the first unset variable, as bash does.
	/// </remarks>
	[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each opened Redirection goes into the list, which the finally disposes.")]
	internal static int RunCommand(Dictionary<string, ICommand> commands, AliasCommand aliases, DeclareCommand declare, ShellSettings settings, string line)
	{
		var substituted = 0;

		string Substitution(string list)
		{
			(var text, substituted) = CompoundCommands.Substitute(commands, aliases, declare, settings, list);

			return text;
		}

		Word? unset = null;
		var split = CommandLine.Split(line, declare.Variables, settings.NoUnset ? name => unset ??= name : null, Substitution);

		if (unset is { } missing)
		{
			return ReportUnset(line, missing, settings.Interactive);
		}

		var expanded = aliases.Expand(split, declare.Variables, Substitution);
		var words = CommandCommand.Unwrap(expanded);

		// A trailing & runs the command in the background.
		var background = words is [.., { IsOperator: true, Text: "&" }];

		if (background)
		{
			words = words[..^1];
		}

		if (WholeCommandStatus(words, background, substituted, line, declare, settings) is { } whole)
		{
			return whole;
		}

		var parts = CommandLine.SplitPipeline(words).Select(CommandLine.SplitRedirect).ToArray();

		if (background && NeedsShellCopy(parts, commands))
		{
			using var none = Redirection.Open([], line, noClobber: false, CompoundCommands.InheritedTargets)!;

			// One level down, since the copy raises it again and bash's background subshell stays on the same level.
			string[] level = [$"SHLVL={DeclareCommand.ShellLevel - 1}"];

			return RunProgram(new Word(Environment.ProcessPath!, IsOperator: false), CompoundCommands.CopyArguments(commands, aliases, declare, settings, line.EndsWith('&') ? line[..^1] : line, tested: false), level, StandardOutput, background: true, none);
		}

		// Operators the shell can't run yet, like <>, are refused rather than passed on as arguments.
		if (Unusable(words, parts) is { } unusable)
		{
			ReportUnusable(line, unusable);

			return CommandList.UnsupportedStatus;
		}

		var redirections = new List<Redirection>();

		try
		{
			// Every stage's files are opened before anything runs, so a redirect that fails runs nothing, and > f on
			// its own still creates f.
			return !CompoundCommands.OpenStages(parts, line, settings.NoClobber, redirections) ? 1
				: parts is [{ Words: [] }] ? substituted
				: Run([.. parts.Select(part => part.Words)], [.. redirections]);
		}
		finally
		{
			foreach (var redirection in redirections)
			{
				redirection.Dispose();
			}
		}

		// Runs the stages once their redirects are open.
		int Run(Word[][] stageWords, Redirection[] redirections)
		{
			var stages = stageWords.Select(stage => stage.Select(word => word.Text).ToArray()).ToArray();

			Trace(settings, stages);

			// A line of nothing but NAME=VALUE words sets shell variables, as in POSIX.
			if (stages is [var only] && Array.TrueForAll(only, DeclareCommand.IsAssignment))
			{
				return AssignmentStatus(declare.Run(only), substituted);
			}

			// NAME=VALUE words in front of a command go into that command's environment only, as in POSIX. The last
			// word is always kept as the command, so A=1 | cat still reports A=1 as not found rather than running
			// nothing.
			var assignments = stages.Select(stage => stage.TakeWhile(DeclareCommand.IsAssignment).Take(stage.Length - 1).ToArray()).ToArray();

			stages = [.. stages.Select((stage, index) => stage[assignments[index].Length..])];

			// The word each stage's command name came from, in stage order, for a not-found report to point at.
			var names = stageWords.Select((stage, index) => stage[assignments[index].Length]).ToArray();
			var arguments = stages[0][1..];

			// As one stage of a longer pipeline a function runs in a copy of the shell, like any compound stage, so it
			// can read the stage before it and runs beside the others, as bash runs every stage in a subshell.
			stages = CompoundCommands.CopyFunctionStages(commands, aliases, declare, settings, stages, [.. names.Select(name => stages.Length > 1 && Find(name) is FunctionCommand)]);
			names = [.. names.Select((name, index) => name with { Text = stages[index][0] })];

			return stages.Length > 1 ? RunPipeline(stages, assignments, names, redirections)
				: Find(names[0]) is { } command ? RunBuiltinOrExec(command, declare, settings, arguments, assignments[0], redirections[0].Targets)
				: RunProgram(names[0], arguments, assignments[0], StandardOutput, background, redirections[0]);
		}

		// Runs the stages as a pipeline, the last one writing to StandardOutput. A two-stage pipeline, left | right, may have a builtin on either side. No
		// builtin reads standard input, so nothing has to run side by side once one is involved: a builtin on the
		// left writes everything into a buffer that becomes the right program's input, and a builtin on the right
		// ignores the left side's output, which is thrown away. Builtins run in the shell itself, not a subshell as
		// in bash, so cd or exit in a pipeline still affects the shell. A left program that never ends, like tail -f,
		// hangs a pipeline into a builtin, because bash relies on the broken pipe to stop it. With three or more
		// stages builtins aren't looked up, so echo runs the program on PATH and type isn't found. Handling them
		// there means running the stages before the last builtin into Stream.Null.
		// The status is the last stage's, as in bash, or with pipefail the last failing stage's.
		int RunPipeline(string[][] stages, string[][] assignments, Word[] names, Redirection[] redirections)
		{
			var left = stages[0];
			var right = stages[^1];
			var leftBuiltin = stages.Length == 2 ? Find(names[0]) : null;
			var rightBuiltin = stages.Length == 2 ? Find(names[^1]) : null;

			if (leftBuiltin is null && rightBuiltin is null)
			{
				return ExternalProgram.RunPipeline(stages, index => ReportNotFound(commands, aliases, line, names[index], redirections[index].Targets, null), assignments, settings.PipeFail, StandardOutput, redirections);
			}

			if (rightBuiltin is not null)
			{
				var leftStatus = leftBuiltin is not null
					? RunBuiltin(leftBuiltin, declare, left[1..], assignments[0], Stream.Null, redirections[0].Targets)
					: RunProgram(names[0], left[1..], assignments[0], Stream.Null, background: false, redirections[0]);

				return ExternalProgram.PipelineStatus([leftStatus, RunBuiltin(rightBuiltin, declare, right[1..], assignments[^1], StandardOutput, PipeEnd(redirections[^1].Targets))], settings.PipeFail);
			}

			using var buffer = new MemoryStream();

			var builtinStatus = RunBuiltin(leftBuiltin!, declare, left[1..], assignments[0], buffer, redirections[0].Targets);

			buffer.Position = 0;

			return ExternalProgram.PipelineStatus([builtinStatus, RunProgram(names[^1], right[1..], assignments[^1], StandardOutput, background: false, redirections[^1], buffer)], settings.PipeFail);
		}

		// The function or builtin a stage's name runs, past a function when command came before it, or null for a program.
		ICommand? Find(Word name) => CommandCommand.Find(commands, expanded, name);

		// Runs a program on PATH with its NAME=VALUE words in its environment, or reports that it isn't there.
		int RunProgram(Word name, string[] arguments, string[] assignments, Stream? output, bool background, Redirection redirection, Stream? input = null)
		{
			if (ExternalProgram.Run(name.Text, arguments, output, null, background, input, assignments, redirection, listJob: CompoundCommands.SubshellDepth == 0) is { } status)
			{
				return status;
			}

			ReportNotFound(commands, aliases, line, name, redirection.Targets, output);

			return ExternalProgram.NotFound;
		}
	}

	/// <summary>
	/// Runs a command that isn't cut into pipeline stages and redirects: one that expands to nothing, or a
	/// <c>[[ ... ]]</c> condition.
	/// </summary>
	/// <param name="words">The command's words, without a trailing <c>&amp;</c>.</param>
	/// <param name="background"><see langword="true" /> when the command ended in <c>&amp;</c>.</param>
	/// <param name="substituted">The last command substitution's status, or 0.</param>
	/// <param name="line">The command as typed.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
	/// <param name="settings">The <c>set</c> options, for <c>-x</c>.</param>
	/// <returns>
	/// <paramref name="substituted" /> for no words, as in bash, so <c>$(true)</c> gives its status, the condition's
	/// status for <c>[[</c>, or <see langword="null" /> for any other command. A <c>[[</c> in the background gives
	/// <see langword="null" /> too, and its operator makes the copy of the shell run it.
	/// </returns>
	private static int? WholeCommandStatus(Word[] words, bool background, int substituted, string line, DeclareCommand declare, ShellSettings settings)
	{
		if (words.Length == 0)
		{
			return substituted;
		}

		if (background || words[0] is not { IsOperator: true, Text: ConditionalCommand.Open })
		{
			return null;
		}

		// bash shows the words of a condition unquoted.
		if (settings.XTrace)
		{
			Console.Error.WriteLine($"+ {string.Join(' ', words.Select(word => word.Text))}");
		}

		return ConditionalCommand.Run(words, line, declare.Variables);
	}

	/// <summary>
	/// Gets the streams of a builtin on the right of a pipe, which reads nothing.
	/// </summary>
	/// <param name="targets">Its streams once its redirects are applied.</param>
	/// <returns>
	/// The streams, with stream 0 closed unless a redirect set it, so <c>echo x | read y</c> doesn't read the input
	/// of a loop around it. The left side's output is thrown away, so there's nothing else for it to read.
	/// </returns>
	private static StreamTarget[] PipeEnd(StreamTarget[] targets) =>
		targets[0] == StreamTarget.Own(0) ? [StreamTarget.None, .. targets[1..]] : targets;

	/// <summary>
	/// Finds the operator that keeps a command from running.
	/// </summary>
	/// <param name="words">The command's words.</param>
	/// <param name="parts">Its stages, with their redirects taken out.</param>
	/// <returns>
	/// The first operator left in a stage, which is one Sharpie can't run or a redirect with nothing usable after it,
	/// else a <c>|</c> with no command on one side. <see langword="null" /> when the command can run.
	/// </returns>
	/// <remarks>
	/// A command of nothing but redirects can run on its own, as <c>&gt; f</c> does, but not as one stage of a
	/// pipeline. Bash runs those too; this reports the <c>|</c> before them instead.
	/// </remarks>
	private static Word? Unusable(Word[] words, (Word[] Words, Redirect[] Redirects)[] parts)
	{
		if (parts.SelectMany(part => part.Words).FirstOrDefault(word => word.IsOperator) is { IsOperator: true } left)
		{
			return left;
		}

		var empty = parts.Length > 1 ? Array.FindIndex(parts, part => part.Words is []) : -1;

		return empty < 0 ? null : Array.FindAll(words, word => word is { IsOperator: true, Text: "|" })[Math.Max(empty - 1, 0)];
	}

	/// <summary>
	/// Runs a builtin with <see cref="Console.Out" /> and <see cref="Console.Error" /> pointed where its streams 1 and 2
	/// go, then puts the terminal back.
	/// </summary>
	/// <param name="command">The builtin.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
	/// <param name="arguments">The words after the builtin's name.</param>
	/// <param name="assignments">The <c>NAME=VALUE</c> words typed in front of the builtin's name.</param>
	/// <param name="output">Where the builtin's own output goes, or <see langword="null" /> for the terminal.</param>
	/// <param name="targets">Where its streams go once its redirects are applied.</param>
	/// <returns>
	/// The builtin's status.
	/// </returns>
	/// <remarks>
	/// A builtin runs inside the shell, so <paramref name="assignments" /> are set in the shell's environment while it
	/// runs, as bash does. That way <c>PATH=/opt/bin type tool</c> searches <c>/opt/bin</c>. They are shell variables
	/// for the run too, as POSIX says. The shell reads its own variables before the environment, so otherwise an
	/// earlier <c>IFS=:</c> would win over the <c>IFS=</c> in <c>IFS= read -r line</c>.
	/// <para>
	/// <see cref="StandardOutput" /> becomes <paramref name="output" /> too, so the programs that <c>. file</c> runs
	/// write where the builtin does.
	/// </para>
	/// </remarks>
	private static int RunBuiltin(ICommand command, DeclareCommand declare, string[] arguments, string[] assignments, Stream? output, StreamTarget[] targets)
	{
		// Every old value is read before any is set, so A=1 A=2 still puts back the value from before both.
		var saved = assignments.Select(assignment => assignment.Split('=', 2))
			.Select(parts => (Name: parts[0], Value: parts[1], Old: Environment.GetEnvironmentVariable(parts[0])))
			.ToArray();

		return WithStreams(targets, output, () =>
		{
			var outer = StandardOutput;

			StandardOutput = targets[1].Opened ?? (targets[1] == StreamTarget.Own(1) ? output : StandardOutput);

			try
			{
				foreach (var (name, value, _) in saved)
				{
					Environment.SetEnvironmentVariable(name, value);
				}

				return declare.RunWith(assignments, () => command.Run(arguments));
			}
			finally
			{
				foreach (var (name, _, old) in saved)
				{
					Environment.SetEnvironmentVariable(name, old);
				}

				StandardOutput = outer;
			}
		});
	}

	/// <summary>
	/// Runs <paramref name="run" /> inside the shell with <see cref="Console.Out" /> and <see cref="Console.Error" />
	/// pointed where a command's streams 1 and 2 go, then puts the terminal back.
	/// </summary>
	/// <param name="targets">Where the command's streams go.</param>
	/// <param name="output">The command's own output, or <see langword="null" /> for the terminal.</param>
	/// <param name="run">What to run, which gives a status.</param>
	/// <param name="autoFlush">
	/// <see langword="true" /> to write each piece of text out at once, for the redirects of a compound command, where
	/// the shell's own reports and <c>set -x</c> lines must land in order with what the programs inside write, and
	/// before <c>set -e</c> ends the shell.
	/// </param>
	/// <returns>
	/// The status <paramref name="run" /> gives.
	/// </returns>
	/// <remarks>
	/// When both streams end up in the same place, as with <c>&gt;f 2&gt;&amp;1</c>, they share one writer, so the
	/// lines keep the order they were written in. A closed stream, or one copied from stream 0, takes the text and
	/// drops it; bash would report a write error there.
	/// </remarks>
	internal static int WithStreams(StreamTarget[] targets, Stream? output, Func<int> run, bool autoFlush = false)
	{
		var terminalOut = Console.Out;
		var terminalError = Console.Error;
		var terminalInput = ReadCommand.RedirectedInput;
		var terminalColor = ShellError.UseColor;
		var writers = new Dictionary<object, TextWriter>();
		var created = new List<IDisposable>();

		Console.SetOut(Writer(targets[1]));
		Console.SetError(Writer(targets[2]));

		// Every read under one redirect shares a reader, so none loses the text another read ahead.
		ReadCommand.RedirectedInput = targets[0] switch
		{
			{ Opened: { } file } when terminalInput is StreamReader { BaseStream: var shared } && shared == file => terminalInput,
			{ Opened: { } file } => Read(file),
			{ Closed: true } => TextReader.Null,
			_ => terminalInput,
		};

		// A report written into a file or a pipe shouldn't carry color codes.
		ShellError.UseColor &= targets[2] == StreamTarget.Own(2);

		try
		{
			return run();
		}
		finally
		{
			Console.SetOut(terminalOut);
			Console.SetError(terminalError);
			ReadCommand.RedirectedInput = terminalInput;
			ShellError.UseColor = terminalColor;

			foreach (var stream in created)
			{
				stream.Dispose();
			}
		}

		TextWriter Writer(StreamTarget target)
		{
			var key = target.Opened ?? (object)(target.Closed ? -1 : target.Inherited);

			if (!writers.TryGetValue(key, out var writer))
			{
				var stream = target.Opened ?? (target is { Closed: false, Inherited: 1 } ? output : null);

				writer = stream is not null ? Create(stream)
					: target is { Closed: false, Inherited: 1 } ? terminalOut
					: target is { Closed: false, Inherited: 2 } ? terminalError
					: TextWriter.Null;
				writers[key] = writer;
			}

			return writer;
		}

		StreamWriter Create(Stream stream)
		{
			var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = autoFlush };

			created.Add(writer);

			return writer;
		}

		StreamReader Read(FileStream file)
		{
			var reader = new StreamReader(file, leaveOpen: true);

			created.Add(reader);

			return reader;
		}
	}

	/// <summary>
	/// Gives the status of a line of only <c>NAME=VALUE</c> words.
	/// </summary>
	/// <param name="status">What <c>declare</c> gave for the words.</param>
	/// <param name="substituted">The last command substitution's status, or 0 when there was none.</param>
	/// <returns>
	/// <paramref name="status" /> when it failed, or else <paramref name="substituted" />, as in bash.
	/// </returns>
	private static int AssignmentStatus(int status, int substituted) => status != 0 ? status : substituted;

	/// <summary>
	/// Reports a variable that <c>set -u</c> finds unset, pointing at where it was typed.
	/// </summary>
	/// <param name="line">The command as typed.</param>
	/// <param name="name">The variable's name and place in <paramref name="line" />.</param>
	/// <param name="interactive">
	/// <see langword="true" /> when the shell reads typed lines and keeps going. Otherwise it exits with 127, as bash
	/// does.
	/// </param>
	/// <returns>
	/// 1, the command's status.
	/// </returns>
	internal static int ReportUnset(string line, Word name, bool interactive) =>
		Fail(
			new ShellError("sharpie::shell::unset_variable", $"`{name.Text}` isn't set")
			{
				Source = line,
				Start = name.Start,
				End = name.End,
				Label = "set -u makes this an error",
				Help = "set it first, or turn the check off with set +u",
			},
			127,
			interactive);

	/// <summary>
	/// Reports an error that ends the line, and the shell too when it isn't interactive, as bash's expansion errors do.
	/// </summary>
	/// <param name="error">The report.</param>
	/// <param name="status">The status a shell that isn't interactive exits with.</param>
	/// <param name="interactive">
	/// <see langword="true" /> when the shell reads typed lines and keeps going.
	/// </param>
	/// <returns>
	/// 1, the command's status.
	/// </returns>
	internal static int Fail(ShellError error, int status, bool interactive)
	{
		error.Write();

		// A substitution is a subshell in bash, which isn't interactive, so the error ends it, with 1 as bash's does.
		if (CompoundCommands.SubshellDepth > 0)
		{
			throw new ExitCommand.SubshellExit(1);
		}

		if (!interactive)
		{
			Environment.Exit(status);
		}

		return 1;
	}

	/// <summary>
	/// Checks whether a command that ends in <c>&amp;</c> has to run in a copy of the shell.
	/// </summary>
	/// <param name="parts">The command's stages without the <c>&amp;</c>, with their redirects taken out.</param>
	/// <param name="commands">The builtins, by name.</param>
	/// <returns>
	/// <see langword="true" /> for anything but one program, like <c>cd</c>, <c>A=1</c>, <c>&gt; f</c>, a function, a
	/// pipeline or <c>a &amp;&amp; b</c>, so it can't change this shell, as bash runs it in a subshell. A program's own
	/// redirects, as in <c>cmd &gt; f &amp;</c>, don't need one.
	/// </returns>
	private static bool NeedsShellCopy((Word[] Words, Redirect[] Redirects)[] parts, Dictionary<string, ICommand> commands) =>
		parts is not [var (words, _)]
		|| Array.Exists(words, word => word.IsOperator)
		|| words.Select(word => word.Text).FirstOrDefault(text => !DeclareCommand.IsAssignment(text)) is not { } name
		|| commands.ContainsKey(name);

	/// <summary>
	/// Runs a builtin found for a command, or <c>exec</c> with the command's redirect files.
	/// </summary>
	/// <param name="command">The builtin.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
	/// <param name="settings">The <c>set</c> options, which tell <c>exec</c> whether the shell is interactive.</param>
	/// <param name="arguments">The words after the builtin's name.</param>
	/// <param name="assignments">The <c>NAME=VALUE</c> words typed in front of the builtin's name.</param>
	/// <param name="targets">Where the command's streams go once its redirects are applied.</param>
	/// <returns>
	/// The builtin's status.
	/// </returns>
	/// <remarks>
	/// <c>exec</c> keeps the redirect files past its own run, which <see cref="RunBuiltin" /> would undo. Inside a
	/// command substitution it can't replace the shell, so it runs the program and ends the substitution instead.
	/// shortcut: <c>exec</c> only gets the files that streams 1 and 2 were pointed at, so <c>exec 2&gt;&amp;1</c> and
	/// <c>exec &lt; f</c> change nothing; hand it the whole <see cref="Redirection" /> if those are needed.
	/// </remarks>
	private static int RunBuiltinOrExec(ICommand command, DeclareCommand declare, ShellSettings settings, string[] arguments, string[] assignments, StreamTarget[] targets) =>
		command is not ExecCommand exec ? RunBuiltin(command, declare, arguments, assignments, StandardOutput, targets)
		: CompoundCommands.SubshellDepth > 0 ? ExecCommand.RunInSubstitution(arguments, assignments, targets[1].Opened ?? StandardOutput, targets[2].Opened)
		: exec.Run(arguments, assignments, targets[1].Opened, targets[2].Opened, settings.Interactive);

	/// <summary>
	/// Prints each stage of a command to standard error before it runs, when <c>set -x</c> is on.
	/// </summary>
	/// <param name="settings">The <c>set</c> options.</param>
	/// <param name="stages">The stages, each starting with its <c>NAME=VALUE</c> words.</param>
	/// <remarks>
	/// Each <c>NAME=VALUE</c> word gets a line of its own, then the command, every line after <c>+ </c>, with the
	/// words quoted only where needed, the way bash prints them. Redirects aren't shown.
	/// </remarks>
	private static void Trace(ShellSettings settings, string[][] stages)
	{
		if (!settings.XTrace)
		{
			return;
		}

		foreach (var stage in stages)
		{
			var count = stage.TakeWhile(DeclareCommand.IsAssignment).Count();

			foreach (var parts in stage[..count].Select(assignment => assignment.Split('=', 2)))
			{
				// bash leaves an empty value as NAME= here, though an empty argument shows as ''.
				Console.Error.WriteLine($"+ {parts[0]}={(parts[1].Length == 0 ? string.Empty : CommandLine.QuoteIfNeeded(parts[1]))}");
			}

			if (count < stage.Length)
			{
				Console.Error.WriteLine($"+ {string.Join(' ', stage[count..].Select(CommandLine.QuoteIfNeeded))}");
			}
		}
	}

	/// <summary>
	/// Reports the operator that keeps a command from running, pointing at where it was typed.
	/// </summary>
	/// <param name="line">The command as typed.</param>
	/// <param name="unusable">The operator, from <see cref="Unusable" />.</param>
	internal static void ReportUnusable(string line, Word unusable) =>
		new ShellError("sharpie::parser::unsupported_operator", $"`{unusable.Text}` can't be used here")
		{
			Source = line,
			Start = unusable.Start,
			End = unusable.End,
			Label = unusable.Text == "|" ? "needs a command on both sides"
				: CommandLine.RedirectKind(unusable.Text) is not null && CommandLine.StreamOf(unusable.Text) is null ? $"stream numbers go up to {CommandLine.MaxStream}"
				: CommandLine.RedirectKind(unusable.Text) switch
				{
					TargetKind.File => "needs a file name after it",
					TargetKind.Stream => "needs a stream number after it",
					TargetKind.Text => "needs a delimiter after it",
					_ => "Sharpie can't run this operator yet",
				},
		}.Write();

	/// <summary>
	/// Reports that a command is neither a builtin, an alias nor a program on <c>PATH</c>, suggesting a close name
	/// that is.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin.</param>
	/// <param name="line">The command as typed.</param>
	/// <param name="name">The word the command name came from.</param>
	/// <param name="targets">
	/// Where the command's streams go, so its stream 2 gets the report as bash's does, and <c>2&gt;/dev/null</c> hides
	/// it.
	/// </param>
	/// <param name="output">The command's own output, or <see langword="null" /> for the terminal.</param>
	private static void ReportNotFound(Dictionary<string, ICommand> commands, AliasCommand aliases, string line, Word name, StreamTarget[] targets, Stream? output)
	{
		var suggestion = ShellError.DidYouMean(name.Text, commands.Keys.Concat(aliases.Aliases.Keys).Concat(PathFinder.FindFileNames()))
			.FirstOrDefault(match => commands.ContainsKey(match) || aliases.Aliases.ContainsKey(match) || PathFinder.FindExecutable(match) is not null);

		var report = new ShellError("sharpie::shell::command_not_found", $"Command `{name.Text}` not found")
		{
			Source = line,
			Start = name.Start,
			End = name.End,
			Label = "not a builtin, alias or program on PATH",
			Help = suggestion is null ? null : $"did you mean `{suggestion}`?",
		};

		_ = WithStreams(targets, output, () =>
		{
			report.Write();

			return 0;
		});
	}

	/// <summary>
	/// A live list of the builtin, function and alias names and the reserved words, which the line editor completes and
	/// colors as commands.
	/// </summary>
	/// <param name="builtins">The builtins and functions, by name.</param>
	/// <param name="aliases">The aliases, by name.</param>
	private sealed class CommandNames(IReadOnlyDictionary<string, ICommand> builtins, IReadOnlyDictionary<string, string> aliases) : IReadOnlyCollection<string>
	{
		/// <inheritdoc />
		public int Count => builtins.Count + aliases.Count + CommandParser.ReservedWords.Length;

		/// <inheritdoc />
		public IEnumerator<string> GetEnumerator() => builtins.Keys.Concat(aliases.Keys).Concat(CommandParser.ReservedWords).GetEnumerator();

		/// <inheritdoc />
		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
	}
}
