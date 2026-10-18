using System.Globalization;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Leaves the loops around it, as <c>break</c> does, or starts their next round, as <c>continue</c> does.
/// </summary>
/// <param name="name"><c>break</c> or <c>continue</c>.</param>
/// <param name="declare">The <c>declare</c> builtin, which holds how many loops are running.</param>
/// <remarks>
/// <c>break n</c> and <c>continue n</c> act on the <c>n</c>th loop out, or the outermost when there are fewer. Outside
/// a loop it warns and succeeds, as bash does.
/// </remarks>
internal sealed class LoopControlCommand(string name, DeclareCommand declare) : ICommand
{
	/// <inheritdoc />
	public string Name => name;

	/// <inheritdoc />
	public string Description => name == "break" ? "Leaves the loops around it" : "Starts the next round of a loop";

	/// <inheritdoc />
	/// <exception cref="LoopControl">A loop is running, which the runner leaves or goes on with.</exception>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var levels = 1;

		if (args is [var count, ..] && (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out levels) || levels < 1))
		{
			new ShellError($"sharpie::{name}::bad_count", $"`{count}` isn't a loop count") { Help = $"{name} takes a whole number from 1 up" }.Write();

			return 1;
		}

		if (declare.LoopDepth == 0)
		{
			new ShellError($"sharpie::{name}::outside_loop", $"`{name}` only works inside a loop")
			{
				Help = "use it between do and done of a for, while or until loop",
			}.Write();

			return 0;
		}

		throw new LoopControl(Math.Min(levels, declare.LoopDepth), next: name == "continue");
	}
}
