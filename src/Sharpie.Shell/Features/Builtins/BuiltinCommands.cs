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
	internal static ICommand[] Create(Dictionary<string, ICommand> commands, Func<string, int> runLine)
	{
		var alias = new AliasCommand();
		var declare = new DeclareCommand();

		// Gets the live dictionary before it's filled, so type can see every builtin, itself included.
		var type = new TypeCommand(commands, alias.Aliases);

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
			new SetCommand(declare, new ShellSettings()),
			new ShiftCommand(declare),
			new ReadCommand(declare, Console.IsInputRedirected ? null : () => Console.ReadKey(intercept: true)),
			new SourceCommand(".", declare, runLine),
			new SourceCommand("source", declare, runLine),
			new EvalCommand(runLine),
			new StatusCommand(":", 0, "Does nothing and succeeds"),
			new StatusCommand("true", 0, "Does nothing and succeeds"),
			new StatusCommand("false", 1, "Does nothing and fails"),
			new UnaliasCommand(alias),
			new DmanCommand(),
			new CommandCommand(type),
			new ExecCommand(),
			type
		];
	}
}
