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

	/// <summary>
	/// Gets the function's body.
	/// </summary>
	/// <value>
	/// The compound command a call runs, as typed, which a copy of the shell defines the function from.
	/// </value>
	internal string Body => body;

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
	/// Defines the function the parser read, in place of any function of the same name.
	/// </summary>
	/// <param name="commands">The builtins and functions, by name, which get the new function.</param>
	/// <param name="definition">The definition.</param>
	/// <param name="declare">The <c>declare</c> builtin, which the function's calls use.</param>
	/// <param name="runLine">Runs the function's body.</param>
	/// <remarks>
	/// The body is the compound command as typed, its redirects included, so <c>f() { ...; } &gt; log</c> sends every
	/// call's output to <c>log</c>, as in bash. It's parsed again on each call, so it isn't expanded until it runs. A
	/// function defined again keeps the builtin the first one hid.
	/// </remarks>
	internal static void Define(IDictionary<string, ICommand> commands, FunctionDefinition definition, DeclareCommand declare, Func<string, int> runLine)
	{
		_ = commands.ArgumentNotNull();
		_ = definition.ArgumentNotNull();

		var old = commands.TryGetValue(definition.Name, out var found) ? found : null;

		commands[definition.Name] = new FunctionCommand(definition.Name, definition.Body.Text, old is FunctionCommand function ? function.Shadowed : old, declare, runLine);
	}

	/// <summary>
	/// Removes the function <paramref name="name" />, as <c>unset -f</c> does.
	/// </summary>
	/// <param name="commands">The builtins and functions, by name.</param>
	/// <param name="name">The function's name. A name that isn't a function is left alone.</param>
	/// <remarks>
	/// The builtin the function hid comes back.
	/// </remarks>
	internal static void Remove(IDictionary<string, ICommand> commands, string name)
	{
		if (!commands.ArgumentNotNull().TryGetValue(name, out var found) || found is not FunctionCommand function)
		{
			return;
		}

		if (function.Shadowed is { } builtin)
		{
			commands[name] = builtin;
		}
		else
		{
			_ = commands.Remove(name);
		}
	}

	/// <summary>
	/// Ends every call down to the first once one goes past <see cref="MaxDepth" />.
	/// </summary>
	[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only Run throws it, and only Run catches it.")]
	private sealed class TooDeep : Exception;
}
