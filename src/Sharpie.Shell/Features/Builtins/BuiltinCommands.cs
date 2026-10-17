using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Aliases;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.DotNetDocs;
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
	/// <returns>
	/// The builtins, not yet added to <paramref name="commands" />.
	/// </returns>
	/// <remarks>
	/// Built in groups, since the build also caps how many types one method may use.
	/// </remarks>
	internal static ICommand[] Create(Dictionary<string, ICommand> commands, Func<string, int> runLine)
	{
		var alias = new AliasCommand();
		var declare = new DeclareCommand();

		// Gets the live dictionary before it's filled, so type can see every builtin, itself included.
		var type = new TypeCommand(commands, alias.Aliases);

		return
		[
			alias,
			new UnaliasCommand(alias),
			new EchoCommand(),
			new ClearCommand(),
			new CompleteCommand(),
			new HistoryCommand(),
			new StatusCommand(":", 0, "Does nothing and succeeds"),
			new StatusCommand("true", 0, "Does nothing and succeeds"),
			new StatusCommand("false", 1, "Does nothing and fails"),
			new DmanCommand(),
			new UmaskCommand(NativeMethods.SetFileMask),
			new UlimitCommand(),
			.. CreateVariableCommands(declare, runLine),
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
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
	/// <param name="runLine">Runs one command line the way the shell runs a typed one and gives its status.</param>
	/// <returns>
	/// <c>declare</c> and the builtins built around it.
	/// </returns>
	private static ICommand[] CreateVariableCommands(DeclareCommand declare, Func<string, int> runLine) =>
		[
			declare,
			new ExitCommand(declare),
			new ExportCommand(declare),
			new UnsetCommand(declare),
			new SetCommand(declare, new ShellSettings()),
			new ShiftCommand(declare),
			new ReadCommand(declare, Console.IsInputRedirected ? null : () => Console.ReadKey(intercept: true)),
			new SourceCommand(".", declare, runLine),
			new SourceCommand("source", declare, runLine),
			new EvalCommand(runLine)
		];

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
