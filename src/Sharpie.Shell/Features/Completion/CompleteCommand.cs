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
	public int Run(string[] args)
	{
		switch (args)
		{
			case ["-C", var script, var command, ..]:
				this.completers[command] = script;

				return 0;
			case ["-r", var command, ..]:
				_ = this.completers.Remove(command);

				return 0;
			case ["-p", var command, ..] when this.completers.TryGetValue(command, out var registered):
				// Rebuilt from the stored parts, not echoed, so the output is normalized however it was typed.
				Console.WriteLine($"{this.Name} -C '{registered}' {command}");

				return 0;
			case ["-p", var command, ..]:
				Console.WriteLine($"{this.Name}: {command}: no completion specification");

				return 1;
			default:
				return 0;
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
