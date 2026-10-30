using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Functions;

/// <summary>
/// Makes variables local to the function that's running, so their old values come back when it returns.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which holds the variables and the functions' scopes.</param>
internal sealed class LocalCommand(DeclareCommand declare) : ICommand
{
	/// <inheritdoc />
	public string Name => "local";

	/// <inheritdoc />
	public string Description => "Makes variables local to a function";

	/// <inheritdoc />
	/// <remarks>
	/// Each argument is <c>NAME=VALUE</c>, or <c>NAME</c>, which hides the outer value, an exported one included, until
	/// the function returns. A <c>NAME</c> that's already local keeps its value, as in bash. The status is 1 when a
	/// name isn't valid or no function is running. <c>-a</c> makes each name an array, and <c>NAME=(...)</c> sets one, as
	/// in bash.
	/// <para>
	/// bash still passes the outer exported value to the programs a <c>NAME</c> with no value runs. Sharpie keeps
	/// exported variables only in the environment, so the value is hidden from them too until the function returns.
	/// </para>
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (declare.FunctionDepth == 0)
		{
			new ShellError("sharpie::local::outside_function", "`local` only works in a function")
			{
				Help = "set the variable without local outside a function",
			}.Write();

			return 1;
		}

		var status = 0;

		var array = args is ["-a", ..];

		foreach (var arg in array ? args[1..] : args)
		{
			var name = DeclareCommand.TryParseAssignment(arg, out var assignment) && assignment.Subscript is null ? assignment.Name : arg;

			if (!DeclareCommand.IsIdentifier(name))
			{
				new ShellError("sharpie::local::invalid_identifier", $"`{arg}` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Write();
				status = 1;

				continue;
			}

			var saved = declare.Localize(name);

			if (name != arg)
			{
				status = Math.Max(status, declare.AssignWord(arg, compound: null));
			}
			else if (saved)
			{
				declare.Remove(name);
			}

			if (array)
			{
				declare.MakeArray(name);
			}
		}

		return status;
	}
}
