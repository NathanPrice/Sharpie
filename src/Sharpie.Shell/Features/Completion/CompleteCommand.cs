using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Completion;

/// <summary>
/// Registers programmable completions for commands, such as <c>complete -C /path/to/completer_script git</c>, and
/// removes them with <c>complete -r git</c>.
/// </summary>
internal class CompleteCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "complete";
	/// <inheritdoc />
	public string Description => "Registers programmable completions for commands";

	// Command name to the completer script path registered for it with -C.
	private readonly Dictionary<string, string> completers = [];

	/// <inheritdoc />
	public void Run(string[] args)
	{
		switch (args)
		{
			case ["-C", var script, var command, ..]:
				this.completers[command] = script;

				break;
			case ["-r", var command, ..]:
				_ = this.completers.Remove(command);

				break;
			case ["-p", var command, ..]:
				// Rebuilt from the stored parts, not echoed, so the output is normalized however it was typed.
				Console.WriteLine(this.completers.TryGetValue(command, out var registered)
					? $"{this.Name} -C '{registered}' {command}"
					: $"{this.Name}: {command}: no completion specification");
				break;
			default:
				break;
		}
	}

	/// <summary>
	/// Finds the completer script registered for <paramref name="command" /> with <c>-C</c>.
	/// </summary>
	/// <param name="command">The command name, as the first word of the line.</param>
	/// <returns>
	/// The script path, or <see langword="null" /> when nothing is registered for <paramref name="command" />.
	/// </returns>
	public string? FindCompleter(string command) => this.completers.GetValueOrDefault(command);
}
