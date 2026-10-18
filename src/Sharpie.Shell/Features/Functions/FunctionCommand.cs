using System.Diagnostics.CodeAnalysis;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Functions;

/// <summary>
/// Runs a shell function's body, with the words after its name as the positional parameters.
/// </summary>
/// <param name="name">The function's name.</param>
/// <param name="body">The text between the braces as typed, which is expanded each time the function runs.</param>
/// <param name="shadowed">
/// The builtin of the same name that the function hides, which <c>command NAME</c> still runs, or
/// <see langword="null" />.
/// </param>
/// <param name="declare">The <c>declare</c> builtin, which holds the positional parameters and the locals.</param>
/// <param name="runLine">Runs one command line the way the shell runs a typed one, and gives its status.</param>
/// <remarks>
/// The shell keeps a function with its builtins, so it's found first, completed and colored like one, and
/// <c>f &gt; file</c> and <c>f | grep x</c> work as they do for a builtin.
/// </remarks>
internal sealed class FunctionCommand(string name, string body, ICommand? shadowed, DeclareCommand declare, Func<string, int> runLine) : ICommand
{
	/// <summary>
	/// How many calls may be running inside one another before the next one is refused.
	/// </summary>
	/// <remarks>
	/// The same limit as <c>.</c> has, for the same reason. A function that never stops calling itself would otherwise
	/// overflow the stack and end the shell, and with it every tab of a login shell.
	/// </remarks>
	internal const int MaxDepth = 100;

	/// <inheritdoc />
	public string Name => name;

	/// <inheritdoc />
	public string Description => "Runs a shell function";

	/// <summary>
	/// Gets the builtin this function hides.
	/// </summary>
	/// <value>
	/// The builtin of the same name, or <see langword="null" /> when there's none, so <c>command NAME</c> runs a
	/// program instead.
	/// </value>
	internal ICommand? Shadowed => shadowed;

	/// <inheritdoc />
	/// <remarks>
	/// The status is the last command's, or the one <c>return</c> gives. Going past <see cref="MaxDepth" /> prints an
	/// error and ends every call down to the first with status 1, as bash does past <c>FUNCNEST</c>, so a function
	/// that calls itself twice can't run on for 2^100 calls.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var outermost = declare.FunctionDepth == 0;

		if (declare.FunctionDepth >= MaxDepth)
		{
			new ShellError("sharpie::function::too_deep", $"`{this.Name}` is nested more than {MaxDepth} calls deep")
			{
				Help = "a function that calls itself needs a test that stops it",
			}.Write();

			throw new TooDeep();
		}

		try
		{
			return declare.RunFunction(args, () => ReturnCommand.Catch(() => runLine(body)));
		}
		catch (TooDeep) when (outermost)
		{
			return 1;
		}
	}

	/// <summary>
	/// Defines the function that <paramref name="command" /> holds, if it holds one.
	/// </summary>
	/// <param name="commands">The builtins and functions, by name, which get the new function.</param>
	/// <param name="command">One command, as <see cref="CommandList" /> gives it.</param>
	/// <param name="declare">The <c>declare</c> builtin, which the function's calls use.</param>
	/// <param name="runLine">Runs the function's body.</param>
	/// <returns>
	/// <see langword="null" /> when <paramref name="command" /> isn't a definition. Otherwise 0, or
	/// <see cref="CommandList.UnsupportedStatus" /> after an error when a redirect follows the body's <c>}</c>, and
	/// then nothing is defined.
	/// </returns>
	/// <remarks>
	/// Read before the command is split into words, so the body isn't expanded until the function runs. A function
	/// defined again keeps the builtin the first one hid.
	/// </remarks>
	internal static int? Define(IDictionary<string, ICommand> commands, string command, DeclareCommand declare, Func<string, int> runLine)
	{
		_ = commands.ArgumentNotNull();

		if (FunctionDefinition.Parse(command) is not (var name, var body, var rest))
		{
			return null;
		}

		// shortcut: bash saves these with the function and applies them to every call. Add that if an rc file needs it.
		if (!string.IsNullOrWhiteSpace(rest))
		{
			new ShellError("sharpie::function::redirect_after_body", "A redirect after a function's `}` isn't supported")
			{
				Source = command,
				Start = command.Length - rest.TrimStart().Length,
				End = command.Length,
				Label = "would apply to every call",
				Help = "put it inside the body, or on the command that calls the function",
			}.Write();

			return CommandList.UnsupportedStatus;
		}

		var old = commands.TryGetValue(name, out var found) ? found : null;

		commands[name] = new FunctionCommand(name, body, old is FunctionCommand function ? function.Shadowed : old, declare, runLine);

		return 0;
	}

	/// <summary>
	/// Ends every call down to the first once one goes past <see cref="MaxDepth" />.
	/// </summary>
	[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only Run throws it, and only Run catches it.")]
	private sealed class TooDeep : Exception;
}
