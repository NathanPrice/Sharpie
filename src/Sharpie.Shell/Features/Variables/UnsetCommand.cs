using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// Removes shell variables, exported ones included.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
/// <remarks>
/// <c>unset NAME...</c> removes each name, and a name that isn't set is skipped quietly, as POSIX requires. A leading
/// <c>-v</c>, POSIX's flag for variables, is accepted. Sharpie has no functions, so <c>-f</c> isn't.
/// </remarks>
internal sealed class UnsetCommand(DeclareCommand declare) : ICommand
{
	/// <inheritdoc />
	public string Name => "unset";

	/// <inheritdoc />
	public string Description => "Removes shell variables";

	/// <inheritdoc />
	public void Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		foreach (var name in args is ["-v", .. var rest] ? rest : args)
		{
			if (DeclareCommand.IsIdentifier(name))
			{
				declare.Remove(name);
			}
			else
			{
				Console.Error.WriteLine($"{this.Name}: `{name}': not a valid identifier");
			}
		}
	}
}
