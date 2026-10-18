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
	/// name isn't valid or no function is running.
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

		foreach (var arg in args)
		{
			var parts = arg.Split('=', 2);

			if (!DeclareCommand.IsIdentifier(parts[0]))
			{
				new ShellError("sharpie::local::invalid_identifier", $"`{arg}` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Write();
				status = 1;

				continue;
			}

			var saved = declare.Localize(parts[0]);

			if (parts.Length == 2)
			{
				declare.Set(parts[0], parts[1]);
			}
			else if (saved)
			{
				declare.Remove(parts[0]);
			}
		}

		return status;
	}
}
