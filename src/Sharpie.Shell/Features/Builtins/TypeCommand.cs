using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Reports whether each argument is an alias, a shell builtin, an executable on <c>PATH</c>, or unknown.
/// </summary>
/// <param name="builtins">
/// The registered builtins, keyed by <see cref="ICommand.Name" />. The shell may still be adding entries when this
/// instance is created, so the lookup happens on each call to <see cref="Run" />.
/// </param>
/// <param name="aliases">The aliases, keyed by name, as a live view that later definitions show up in.</param>
internal class TypeCommand(IReadOnlyDictionary<string, ICommand> builtins, IReadOnlyDictionary<string, string> aliases) : ICommand
{
	/// <inheritdoc />
	private readonly IReadOnlyDictionary<string, ICommand> builtins = builtins.ArgumentNotNull();
	private readonly IReadOnlyDictionary<string, string> aliases = aliases.ArgumentNotNull();

	public string Name => "type";
	/// <inheritdoc />
	public string Description => "Shows how a command would be interpreted";

	/// <inheritdoc />
	/// <remarks>
	/// Aliases are checked first, then builtins, then <c>PATH</c>, because that is the order the shell's main loop
	/// dispatches in, so the answer matches what would actually run.
	/// </remarks>
	public int Run(string[] arguments)
	{
		_ = arguments.ArgumentNotNull();

		var status = 0;

		foreach (var commandName in arguments)
		{
			if (this.aliases.TryGetValue(commandName, out var value))
			{
				Console.WriteLine($@"{commandName} is an alias for {value}");
			}
			else if (this.builtins.ContainsKey(commandName))
			{
				Console.WriteLine($@"{commandName} is a shell builtin");
			}
			else if (PathFinder.FindExecutable(commandName) is { } fullPath)
			{
				Console.WriteLine($@"{commandName} is {fullPath}");
			}
			else
			{
				Console.WriteLine($@"{commandName}: not found");
				status = 1;
			}
		}

		return status;
	}
}
