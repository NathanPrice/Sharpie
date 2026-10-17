using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Changes to a folder and saves the one you left on the folder stack, or swaps the current folder with the top of the
/// stack.
/// </summary>
/// <param name="dirs">The <c>dirs</c> builtin, which holds the stack and prints it.</param>
/// <param name="cd">The <c>cd</c> builtin, which makes the change and reports a folder it can't change to.</param>
/// <remarks>
/// bash's <c>-n</c>, <c>+N</c> and <c>-N</c> aren't supported.
/// </remarks>
internal sealed class PushdCommand(DirsCommand dirs, CdCommand cd) : ICommand
{
	/// <inheritdoc />
	public string Name => "pushd";

	/// <inheritdoc />
	public string Description => "Changes folder and saves the one you left on the folder stack";

	/// <inheritdoc />
	/// <remarks>
	/// Prints the stack the way <c>dirs</c> does once the folder has changed. The status is 0 then; 1 when <c>cd</c>
	/// fails, the stack is empty with no folder given, the folder is empty, or there's more than one; and 2 for an
	/// option, as bash gives. The stack only changes when the folder does.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var operands = args is ["--", .. var rest] ? rest : args;

		switch (operands)
		{
			case [_, _, ..]:
				new ShellError("sharpie::pushd::too_many_arguments", "`pushd` takes at most one folder").Write();

				return 1;

			case [""]:
				// cd would read an empty word as the home folder.
				new ShellError("sharpie::pushd::empty_folder", "The folder name is empty").Write();

				return 1;

			case [['-' or '+', _, ..] and var option] when operands == args:
				new ShellError("sharpie::pushd::unsupported_option", $"Option `{option}` isn't supported")
				{
					Help = "to change to a folder whose name starts with - or +, write pushd -- name",
				}.Write();

				return 2;

			case [] when dirs.Stack.Count == 0:
				new ShellError("sharpie::pushd::stack_empty", "No other folder to swap with") { Help = "pushd a folder first" }.Write();

				return 1;

			default:
				break;
		}

		var left = Directory.GetCurrentDirectory();

		if (cd.Run([operands is [var folder] ? folder : dirs.Stack[0]]) != 0)
		{
			return 1;
		}

		if (operands.Length == 0)
		{
			dirs.Stack[0] = left;
		}
		else
		{
			dirs.Stack.Insert(0, left);
		}

		dirs.Print();

		return 0;
	}
}
