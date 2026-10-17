using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Joins its arguments into one command line and runs it in the current shell.
/// </summary>
/// <param name="runLine">Runs one command line, the same way the shell runs a typed one, and gives its status.</param>
/// <remarks>
/// The arguments are joined with spaces, then the line is split and expanded again, so <c>eval echo '$1'</c> prints
/// the first positional parameter and <c>eval 'a=1; echo $a'</c> runs two commands. A leading <c>--</c> is dropped,
/// as in bash.
/// </remarks>
internal sealed class EvalCommand(Func<string, int> runLine) : ICommand
{
	/// <inheritdoc />
	public string Name => "eval";

	/// <inheritdoc />
	public string Description => "Runs its arguments as a command line in the current shell";

	/// <inheritdoc />
	/// <remarks>
	/// The status is the line's, or 0 when there's nothing to run, even after a failed command, as in bash.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var line = string.Join(' ', args is ["--", .. var rest] ? rest : args);

		return string.IsNullOrWhiteSpace(line) ? 0 : runLine(line);
	}
}
