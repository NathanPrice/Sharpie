using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Reports whether each argument is a shell builtin, an executable on <c>PATH</c>, or unknown.
/// </summary>
/// <param name="builtins">
/// The registered builtins, keyed by <see cref="ICommand.Name" />. The shell may still be adding entries when this
/// instance is created, so the lookup happens on each call to <see cref="Run" />.
/// </param>
internal class TypeCommand(IReadOnlyDictionary<string, ICommand> builtins) : ICommand
{
	/// <inheritdoc />
	private readonly IReadOnlyDictionary<string, ICommand> builtins = builtins.ArgumentNotNull();

	public string Name => "type";
	/// <inheritdoc />
	public string Description => "Shows how a command would be interpreted";

	/// <inheritdoc />
	/// <remarks>
	/// Builtins are checked before <c>PATH</c> because that is the order the shell's main loop dispatches in, so the
	/// answer matches what would actually run.
	/// </remarks>
	public void Run(string[] arguments)
	{
		_ = arguments.ArgumentNotNull();

		foreach (var commandName in arguments)
		{
			if (this.builtins.ContainsKey(commandName))
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
			}
		}
	}
}
