using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Aliases;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.DotNetDocs;
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
	/// Creates one instance of every builtin command.
	/// </summary>
	/// <param name="commands">
	/// The dictionary the shell looks commands up in, which <see cref="TypeCommand" /> reads.
	/// </param>
	/// <returns>
	/// The builtins, not yet added to <paramref name="commands" />.
	/// </returns>
	internal static ICommand[] CreateBuiltins(Dictionary<string, ICommand> commands)
	{
		var alias = new AliasCommand();
		var declare = new DeclareCommand();

		return
		[
			alias,
			new EchoCommand(),
			new ExitCommand(declare),
			new PwdCommand(),
			new CdCommand(),
			new ClearCommand(),
			new CompleteCommand(),
			new JobsCommand(),
			new FgCommand(),
			new BgCommand(),
			new HistoryCommand(),
			declare,
			new ExportCommand(declare),
			new UnsetCommand(declare),
			new SourceCommand(".", line => RunLine(commands, alias, declare, line)),
			new SourceCommand("source", line => RunLine(commands, alias, declare, line)),
			new DmanCommand(),

			// Gets the live dictionary before it's filled, so type can see every builtin, itself included.
			new TypeCommand(commands, alias.Aliases)
		];
	}

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
		// Windows consoles default to a code page without ❯ and most other non-ASCII characters, and a non-UTF-8 locale
		// does the same on Unix. Sharpie's window decodes the shell's output as UTF-8.
		Console.OutputEncoding = Encoding.UTF8;

		if (args is [ShellImport.PrintEnvironmentOption])
		{
			ShellImport.PrintEnvironment();

			return 0;
		}

		if (ShellOptions.Parse(NativeMethods.ProgramName(), args, out var error) is not { } options)
		{
			Console.Error.WriteLine($"sharpie: {error}");

			return 2;
		}

		var commands = new Dictionary<string, ICommand>();
		var terminal = !Console.IsInputRedirected && !Console.IsErrorRedirected;

		// Ctrl+C stops the running program but not the shell. Without this, .NET ends the shell on SIGINT. On a terminal
		// the line discipline already sends SIGINT to the program, so passing it on would make a second one.
		using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
		{
			context.Cancel = true;

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
		var aliases = (AliasCommand)commands["alias"];
		var interactive = options.IsInteractive(terminal);

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

			return options.Command is not null
				? RunLine(commands, aliases, declare, options.Command)
				: commands["."].Run([options.ScriptFile!]);
		}

		return RunPrompts(commands, declare, aliases, terminal, interactive && !options.NoRc);
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
		// the keyring. Run gives null when fastfetch isn't installed, and prints nothing.
		var inWindow = Environment.GetEnvironmentVariable(WindowVariable) == "1";
		var bannerShown = inWindow && ExternalProgram.Run("fastfetch", []) is not null;

		// After the banner, so the window isn't blank while the user's shell starts up.
		if (inWindow && Environment.GetEnvironmentVariable(ImportVariable) != "0")
		{
			ShellImport.Run(Environment.GetEnvironmentVariable("SHELL"), Environment.ProcessPath!, aliases, commands.Keys);
		}

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

			history.Record(line);

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

		// Draws the header and reads one edited line. Ctrl+C is read as a key only while the line is being edited, so
		// programs the line starts still get it as SIGINT, or as a console Ctrl+C event on Windows.
		static string? ReadInteractive(LineEditor editor, TimeSpan took, bool blankLine)
		{
			var header = PromptHeader.Build(Directory.GetCurrentDirectory(), took, Console.WindowWidth, blankLine);

			Console.TreatControlCAsInput = true;

			try
			{
				return editor.ReadLine(header);
			}
			finally
			{
				Console.TreatControlCAsInput = false;
			}
		}
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
		catch (Win32Exception)
		{
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
	/// Runs the commands of one line, joined by <c>;</c>, <c>&amp;&amp;</c> and <c>||</c>, as
	/// <see cref="CommandList.Run" /> describes.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin, whose aliases are expanded first.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables and the last status.</param>
	/// <param name="line">The line the user entered.</param>
	/// <returns>
	/// The last command's status.
	/// </returns>
	/// <remarks>
	/// The status is stored after each command, so <c>false; echo $?</c> prints 1, and again after the line, for one
	/// that <see cref="CommandList" /> refuses.
	/// </remarks>
	private static int RunLine(Dictionary<string, ICommand> commands, AliasCommand aliases, DeclareCommand declare, string line) =>
		declare.Status = CommandList.Run(line, declare.Status, command => declare.Status = RunCommand(commands, aliases, declare.Variables, command));

	/// <summary>
	/// Splits one command and runs it as a pipeline, a builtin, or a program on <c>PATH</c>.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin, whose aliases are expanded first.</param>
	/// <param name="variables">The shell variables that <c>$NAME</c> expands to.</param>
	/// <param name="line">The command, with no <c>;</c>, <c>&amp;&amp;</c> or <c>||</c> left in it.</param>
	/// <returns>
	/// The command's status: <see cref="ExternalProgram.NotFound" /> when it isn't found, and 1 when a redirect file
	/// can't be opened.
	/// </returns>
	private static int RunCommand(Dictionary<string, ICommand> commands, AliasCommand aliases, IReadOnlyDictionary<string, string> variables, string line)
	{
		var (words, outputPath, appendOutput, errorPath, appendError) = CommandLine.SplitRedirect(aliases.Expand(CommandLine.Split(line, variables), variables));

		// A trailing & runs an external program in the background. Builtins still run in the foreground.
		var background = words is [.., "&"];

		if (background)
		{
			words = words[..^1];
		}

		if (words.Length == 0)
		{
			return 0;
		}

		// A line of nothing but NAME=VALUE words sets shell variables, as in POSIX.
		if (Array.TrueForAll(words, DeclareCommand.IsAssignment))
		{
			return commands["declare"].Run(words);
		}

		var commandName = words[0];
		var arguments = words[1..];

		using var output = OpenRedirect(outputPath, appendOutput, out var outputFailed);

		if (outputFailed)
		{
			return 1;
		}

		using var error = OpenRedirect(errorPath, appendError, out var errorFailed);

		if (errorFailed)
		{
			return 1;
		}

		// Redirects and & aren't applied to a pipeline.
		var stages = CommandLine.SplitPipeline(words);

		return stages.Length > 1 ? RunPipeline(commands, stages)
			: commands.TryGetValue(commandName, out var command) ? RunBuiltin(command, arguments, output, error)
			: RunProgram(commandName, arguments, output, error, background);

		// Opens the redirect file at path, or gives null when path is null. The file is created if missing, then
		// emptied, or positioned at its end when append is true. Sets failed after printing the reason when the file
		// can't be opened.
		static FileStream? OpenRedirect(string? path, bool append, out bool failed)
		{
			failed = false;

			if (path is null)
			{
				return null;
			}

			try
			{
				return append ? new FileStream(path, FileMode.Append) : File.Create(path);
			}
			// A missing folder, a read-only file, or an empty name like > "" would otherwise end the whole shell.
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
			{
				Console.Error.WriteLine($"{path}: {exception.Message}");
				failed = true;

				return null;
			}
		}

		// Runs the stages as a pipeline. A two-stage pipeline, left | right, may have a builtin on either side. No
		// builtin reads standard input, so nothing has to run side by side once one is involved: a builtin on the
		// left writes everything into a buffer that becomes the right program's input, and a builtin on the right
		// ignores the left side's output, which is thrown away. Builtins run in the shell itself, not a subshell as
		// in bash, so cd or exit in a pipeline still affects the shell. A left program that never ends, like tail -f,
		// hangs a pipeline into a builtin, because bash relies on the broken pipe to stop it. With three or more
		// stages builtins aren't looked up, so echo runs the program on PATH and type isn't found. Handling them
		// there means running the stages before the last builtin into Stream.Null.
		// The status is the last stage's, as in bash without pipefail.
		static int RunPipeline(Dictionary<string, ICommand> commands, string[][] stages)
		{
			var left = stages[0];
			var right = stages[^1];
			var leftBuiltin = stages.Length == 2 ? commands.GetValueOrDefault(left[0]) : null;
			var rightBuiltin = stages.Length == 2 ? commands.GetValueOrDefault(right[0]) : null;

			if (leftBuiltin is null && rightBuiltin is null)
			{
				return ExternalProgram.RunPipeline(stages);
			}

			if (rightBuiltin is not null)
			{
				_ = leftBuiltin is not null
					? RunBuiltin(leftBuiltin, left[1..], Stream.Null, null)
					: RunProgram(left[0], left[1..], Stream.Null, null, background: false);

				return RunBuiltin(rightBuiltin, right[1..], null, null);
			}

			using var buffer = new MemoryStream();

			_ = RunBuiltin(leftBuiltin!, left[1..], buffer, null);
			buffer.Position = 0;

			return RunProgram(right[0], right[1..], null, null, background: false, buffer);
		}

		// Runs a program on PATH, or says it isn't there.
		static int RunProgram(string commandName, string[] arguments, Stream? output, Stream? error, bool background, Stream? input = null)
		{
			if (ExternalProgram.Run(commandName, arguments, output, error, background, input) is { } status)
			{
				return status;
			}

			Console.WriteLine($@"{commandName}: command not found");

			return ExternalProgram.NotFound;
		}

		// Runs a builtin with Console.Out and Console.Error pointed at output and error when given, then puts the
		// terminal back.
		static int RunBuiltin(ICommand command, string[] arguments, Stream? output, Stream? error)
		{
			var terminalOut = Console.Out;
			var terminalError = Console.Error;

			using var outWriter = output is null ? null : new StreamWriter(output, leaveOpen: true);
			using var errorWriter = error is null ? null : new StreamWriter(error, leaveOpen: true);

			Console.SetOut(outWriter ?? terminalOut);
			Console.SetError(errorWriter ?? terminalError);

			try
			{
				return command.Run(arguments);
			}
			finally
			{
				Console.SetOut(terminalOut);
				Console.SetError(terminalError);
			}
		}
	}

	/// <summary>
	/// A live list of the builtin and alias names, which the line editor completes and colors as commands.
	/// </summary>
	/// <param name="builtins">The builtins, by name.</param>
	/// <param name="aliases">The aliases, by name.</param>
	private sealed class CommandNames(IReadOnlyDictionary<string, ICommand> builtins, IReadOnlyDictionary<string, string> aliases) : IReadOnlyCollection<string>
	{
		/// <inheritdoc />
		public int Count => builtins.Count + aliases.Count;

		/// <inheritdoc />
		public IEnumerator<string> GetEnumerator() => builtins.Keys.Concat(aliases.Keys).GetEnumerator();

		/// <inheritdoc />
		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
	}
}
