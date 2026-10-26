using System.Diagnostics.CodeAnalysis;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Functions;

/// <summary>
/// Ends the function or sourced file that's running, with the status given or the last command's.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which knows whether a function is running and holds the last status.</param>
/// <param name="inSourcedFile">Tells whether <c>.</c> or <c>source</c> is running a file.</param>
/// <remarks>
/// Inside <c>$(...)</c> in a function it ends only the substitution, as it ends bash's subshell.
/// </remarks>
internal sealed class ReturnCommand(DeclareCommand declare, Func<bool> inSourcedFile) : ICommand
{
	/// <inheritdoc />
	public string Name => "return";

	/// <inheritdoc />
	public string Description => "Ends a function or a sourced file";

	/// <inheritdoc />
	/// <exception cref="Signal">A function or sourced file is running, which ends with the status.</exception>
	/// <remarks>
	/// The status is worked out as <c>exit</c> does, so an argument that isn't a number gives 2 after an error, and
	/// more than one skips the rest of the line. Outside a function and a sourced file it prints bash's error and gives 2.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (declare.FunctionDepth == 0 && !inSourcedFile())
		{
			new ShellError("sharpie::return::outside_function", "`return` only works in a function or a sourced file")
			{
				Help = "use exit to end the shell",
			}.Write();

			return 2;
		}

		throw new Signal(ExitCommand.Code(args, declare.Status, this.Name) ?? 2);
	}

	/// <summary>
	/// Runs <paramref name="run" />, ending it early when <c>return</c> runs inside.
	/// </summary>
	/// <param name="run">Runs a function's body or a sourced file.</param>
	/// <returns>
	/// The status <paramref name="run" /> gives, or the one <c>return</c> gave.
	/// </returns>
	internal static int Catch(Func<int> run)
	{
		_ = run.ArgumentNotNull();

		try
		{
			return run();
		}
		catch (Signal signal)
		{
			return signal.Status;
		}
	}

	/// <summary>
	/// Carries the status of <c>return</c> out to the function, file or command substitution it ends.
	/// </summary>
	/// <param name="status">The status, already cut to eight bits.</param>
	/// <remarks>
	/// A <see cref="ExitCommand.SubshellExit" />, so a substitution catches it the way it catches <c>exit</c>.
	/// </remarks>
	[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only return throws it, and always with a status.")]
	internal sealed class Signal(int status) : ExitCommand.SubshellExit(status);
}
