using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Arithmetic;
using Sharpie.Shell.Features.Functions;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// Removes shell variables, exported ones included.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
/// <param name="commands">
/// The builtins and functions, by name, which <c>-f</c> removes functions from, or <see langword="null" /> for none.
/// </param>
/// <remarks>
/// <c>unset NAME...</c> removes each name, and a name that isn't set is skipped quietly, as POSIX requires. A leading
/// <c>-v</c>, POSIX's flag for variables, is accepted, and <c>-f</c> removes functions instead. <c>unset 'a[1]'</c>
/// removes one element of an array.
/// </remarks>
internal sealed class UnsetCommand(DeclareCommand declare, IDictionary<string, ICommand>? commands = null) : ICommand
{
	/// <inheritdoc />
	public string Name => "unset";

	/// <inheritdoc />
	public string Description => "Removes shell variables and functions";

	/// <inheritdoc />
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args is ["-f", .. var functions])
		{
			foreach (var function in functions)
			{
				FunctionCommand.Remove(commands ?? new Dictionary<string, ICommand>(), function);
			}

			return 0;
		}

		var status = 0;

		foreach (var name in args is ["-v", .. var rest] ? rest : args)
		{
			if (DeclareCommand.IsIdentifier(name) || DeclareCommand.TrySplitElement(name, out _, out _))
			{
				try
				{
					declare.Remove(name);
				}
				catch (ArithmeticExpression.Failure failure)
				{
					failure.Error.Write();
					status = 1;
				}
			}
			else
			{
				new ShellError("sharpie::unset::invalid_identifier", $"`{name}` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Write();
				status = 1;
			}
		}

		return status;
	}
}
