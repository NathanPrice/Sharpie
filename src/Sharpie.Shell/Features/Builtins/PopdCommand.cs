using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Changes to the folder on top of the folder stack and drops it from the stack.
/// </summary>
/// <param name="dirs">The <c>dirs</c> builtin, which holds the stack and prints it.</param>
/// <param name="cd">The <c>cd</c> builtin, which makes the change and reports a folder it can't change to.</param>
/// <remarks>
/// bash's <c>-n</c>, <c>+N</c> and <c>-N</c> aren't supported.
/// </remarks>
internal sealed class PopdCommand(DirsCommand dirs, CdCommand cd) : ICommand
{
	/// <inheritdoc />
	public string Name => "popd";

	/// <inheritdoc />
	public string Description => "Goes back to the folder on top of the folder stack";

	/// <inheritdoc />
	/// <remarks>
	/// Prints the stack the way <c>dirs</c> does once the folder has changed. The status is 0 then; 1 when the stack is
	/// empty or <c>cd</c> fails, which leaves the stack as it was; and 2 for any argument, as bash gives.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if ((args is ["--", .. var rest] ? rest : args) is [var argument, ..])
		{
			new ShellError("sharpie::popd::unsupported_argument", $"`popd` doesn't take `{argument}`") { Help = "popd takes no arguments" }.Write();

			return 2;
		}

		if (dirs.Stack.Count == 0)
		{
			new ShellError("sharpie::popd::stack_empty", "The folder stack is empty") { Help = "pushd a folder first" }.Write();

			return 1;
		}

		if (cd.Run([dirs.Stack[0]]) != 0)
		{
			return 1;
		}

		dirs.Stack.RemoveAt(0);
		dirs.Print();

		return 0;
	}
}
