using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Aliases;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.Conditions;
using Sharpie.Shell.Features.DotNetDocs;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.History;
using Sharpie.Shell.Features.Jobs;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// The list of builtin commands the shell starts with.
/// </summary>
/// <remarks>
/// Kept out of <see cref="Program" /> so that adding a builtin doesn't add to everything <see cref="Program" /> depends
/// on, which the build caps.
/// </remarks>
internal static class BuiltinCommands
{
	/// <summary>
	/// Creates one instance of every builtin command.
	/// </summary>
	/// <param name="commands">
	/// The dictionary the shell looks commands up in, which <see cref="TypeCommand" /> reads.
	/// </param>
	/// <param name="runLine">
	/// Runs one command line the way the shell runs a typed one and gives its status, for the builtins that run lines
	/// of their own, like <c>.</c>.
	/// </param>
	/// <param name="inSubshell">Tells whether a command substitution is running, where <c>exit</c> ends only that.</param>
	/// <returns>
	/// The builtins, not yet added to <paramref name="commands" />.
	/// </returns>
	/// <remarks>
	/// Built in groups, since the build also caps how many types one method may use.
	/// </remarks>
	internal static ICommand[] Create(Dictionary<string, ICommand> commands, Func<string, int> runLine, Func<bool>? inSubshell = null)
	{
		var alias = new AliasCommand();
		var settings = new ShellSettings();
		var declare = new DeclareCommand(settings, commands);

		// Gets the live dictionary before it's filled, so type can see every builtin, itself included.
		var type = new TypeCommand(commands, alias.Aliases);

		return
		[
			alias,
			new UnaliasCommand(alias),
			new EchoCommand(),
			new ClearCommand(),
			new HistoryCommand(),
			new StatusCommand(":", 0, "Does nothing and succeeds"),
			new StatusCommand("true", 0, "Does nothing and succeeds"),
			new StatusCommand("false", 1, "Does nothing and fails"),
			new DmanCommand(),
			new UmaskCommand(NativeMethods.SetFileMask),
			new UlimitCommand(),
			.. CreateVariableCommands(commands, declare, settings, runLine, inSubshell),
			.. CreateCompletionCommands(commands, alias.Aliases, declare, runLine),
			.. CreateFolderCommands(declare),
			.. CreateJobCommands(),
			new CommandCommand(type),
			new ExecCommand(),
			type
		];
	}

	/// <summary>
	/// Creates the builtins that work on shell variables, and the ones that run lines, which share them.
	/// </summary>
	/// <param name="commands">The builtins and functions, by name, which <c>unset -f</c> removes functions from.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
	/// <param name="settings">The shell options, which <c>set</c> changes and <c>$-</c> lists.</param>
	/// <param name="runLine">Runs one command line the way the shell runs a typed one and gives its status.</param>
	/// <param name="inSubshell">Tells whether a command substitution is running, for <c>exit</c>.</param>
	/// <returns>
	/// <c>declare</c> and the builtins built around it, <c>test</c> and <c>[</c> among them, whose <c>-v</c> reads its
	/// variables.
	/// </returns>
	private static ICommand[] CreateVariableCommands(Dictionary<string, ICommand> commands, DeclareCommand declare, ShellSettings settings, Func<string, int> runLine, Func<bool>? inSubshell)
	{
		var dot = new SourceCommand(".", declare, runLine);
		var source = new SourceCommand("source", declare, runLine);

		return
		[
			declare,
			new ExitCommand(declare, inSubshell),
			new ExportCommand(declare),
			new UnsetCommand(declare, commands),
			new SetCommand(declare, settings),
			new ShiftCommand(declare),
			new ReadCommand(declare, Console.IsInputRedirected ? null : () => Console.ReadKey(intercept: true)),
			new PrintfCommand(declare),
			dot,
			source,
			new EvalCommand(runLine),
			new TrapCommand(runLine, declare),
			new TestCommand("test", declare),
			new TestCommand("[", declare),
			new ReturnCommand(declare, () => dot.Running || source.Running),
			new LocalCommand(declare),
			new LoopControlCommand("break", declare),
			new LoopControlCommand("continue", declare)
		];
	}

	/// <summary>
	/// Defines a function, as <see cref="FunctionCommand.Define" /> does.
	/// </summary>
	/// <param name="commands">The builtins and functions, by name, which get the new function.</param>
	/// <param name="definition">The definition the parser read.</param>
	/// <param name="declare">The <c>declare</c> builtin, which the function's calls use.</param>
	/// <param name="runLine">Runs the function's body.</param>
	/// <remarks>
	/// Here rather than called straight from <see cref="Program" />, for the same cap on what it depends on.
	/// </remarks>
	internal static void DefineFunction(Dictionary<string, ICommand> commands, FunctionDefinition definition, DeclareCommand declare, Func<string, int> runLine) =>
		FunctionCommand.Define(commands, definition, declare, runLine);

	/// <summary>
	/// Creates what runs a script file the shell was started with, as in <c>Sharpie FILE</c>.
	/// </summary>
	/// <param name="declare">The <c>declare</c> builtin, which holds the positional parameters.</param>
	/// <param name="runLine">Runs one command line the way the shell runs a typed one and gives its status.</param>
	/// <returns>
	/// A <c>.</c> builtin of its own.
	/// </returns>
	/// <remarks>
	/// Kept apart from the <c>.</c> the user types, so <c>return</c> at the script's top level is an error, as in bash,
	/// rather than the end of a sourced file.
	/// </remarks>
	internal static ICommand CreateScriptRunner(DeclareCommand declare, Func<string, int> runLine) => new SourceCommand(".", declare, runLine);

	/// <summary>
	/// Creates the builtins of programmable completion, which share one engine that Tab runs too.
	/// </summary>
	/// <param name="commands">The builtins and functions, by name, which completion functions are found in.</param>
	/// <param name="aliases">The aliases, by name, for <c>compgen -a</c>.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the variables completion functions use.</param>
	/// <param name="runLine">Runs a completion function or sources a completion script.</param>
	/// <returns>
	/// <c>complete</c>, <c>compgen</c> and <c>compopt</c>.
	/// </returns>
	/// <remarks>
	/// <c>COMP_WORDBREAKS</c> starts at bash's default here, so a script can read and change it, as git's adds a
	/// <c>:</c> when it's missing.
	/// </remarks>
	private static ICommand[] CreateCompletionCommands(Dictionary<string, ICommand> commands, IReadOnlyDictionary<string, string> aliases, DeclareCommand declare, Func<string, int> runLine)
	{
		var engine = new CompletionEngine(declare, commands, aliases, runLine);

		declare.Set("COMP_WORDBREAKS", CompletionWords.DefaultBreaks);

		return [new CompleteCommand(engine), new CompgenCommand(engine), new CompoptCommand(engine)];
	}

	/// <summary>
	/// Creates what Tab completes with, running the specs <c>complete</c> sets.
	/// </summary>
	/// <param name="commands">The builtins, <c>complete</c> among them.</param>
	/// <param name="names">The command names Tab completes in a command's place.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
	/// <returns>
	/// The completer.
	/// </returns>
	/// <remarks>
	/// Here rather than in <see cref="Program" />, for the same cap on what it depends on.
	/// </remarks>
	internal static TabCompleter CreateTabCompleter(Dictionary<string, ICommand> commands, IReadOnlyCollection<string> names, DeclareCommand declare) =>
		new(names, ((CompleteCommand)commands["complete"]).Engine, declare.Variables);

	/// <summary>
	/// Creates the builtins that show or change the current folder.
	/// </summary>
	/// <param name="declare">The <c>declare</c> builtin, where <c>cd</c> keeps <c>PWD</c> and <c>OLDPWD</c>.</param>
	/// <returns>
	/// <c>pwd</c>, <c>cd</c>, and the folder stack builtins, which change folder through the same <c>cd</c>.
	/// </returns>
	private static ICommand[] CreateFolderCommands(DeclareCommand declare)
	{
		var cd = new CdCommand(declare);
		var dirs = new DirsCommand();

		return [new PwdCommand(), cd, dirs, new PushdCommand(dirs, cd), new PopdCommand(dirs, cd)];
	}

	/// <summary>
	/// Creates the builtins that list and control jobs.
	/// </summary>
	/// <returns>
	/// The job builtins.
	/// </returns>
	private static ICommand[] CreateJobCommands() =>
		[new JobsCommand(), new FgCommand(), new BgCommand(), new WaitCommand(), new KillCommand(), new DisownCommand()];
}
