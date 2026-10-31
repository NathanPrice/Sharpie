using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Completion;

/// <summary>
/// Turns completion options on with <c>-o</c> and off with <c>+o</c>, for the completion running now or for the
/// commands named, as in <c>compopt -o filenames +o nospace</c>.
/// </summary>
/// <param name="engine">The completion engine, which keeps the specs and the running completion's options.</param>
internal sealed class CompoptCommand(CompletionEngine engine) : ICommand
{
	/// <inheritdoc />
	public string Name => "compopt";

	/// <inheritdoc />
	public string Description => "Changes the options of a completion";

	/// <inheritdoc />
	/// <remarks>
	/// The status is 0, 1 outside a completion function with no name given or for a name with no spec, and 2 for a bad
	/// option, as in bash. <c>-D</c>, <c>-E</c> and <c>-I</c> are taken and ignored.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var changes = new List<(string Option, bool On)>();
		var index = 0;

		for (; index < args.Length && args[index] is ['-' or '+', _, ..] option && option != "--"; index++)
		{
			if (option is "-D" or "-E" or "-I")
			{
				continue;
			}

			if (option[1] != 'o' || (option.Length > 2 ? option[2..] : index + 1 < args.Length ? args[++index] : null) is not { } name || !CompletionSpec.OptionNames.Contains(name))
			{
				new ShellError("sharpie::compopt::invalid_option", $"`{option}` isn't an option of `{this.Name}` here")
				{
					Help = $"compopt [-o|+o option] [name...], where option is one of {string.Join(", ", CompletionSpec.OptionNames)}",
				}.Write();

				return 2;
			}

			changes.Add((name, option[0] == '-'));
		}

		var names = args[Math.Min(index + (index < args.Length && args[index] == "--" ? 1 : 0), args.Length)..];

		if (names is [])
		{
			if (engine.Running is not { } running)
			{
				new ShellError("sharpie::compopt::not_completing", "No completion function is running") { Help = "name the commands to change instead" }.Write();

				return 1;
			}

			Apply(running);

			return 0;
		}

		var status = 0;

		foreach (var command in names)
		{
			if (!engine.Specs.TryGetValue(command, out var spec))
			{
				new ShellError("sharpie::compopt::not_found", $"No completion is set for `{command}`").Write();
				status = 1;

				continue;
			}

			var options = new HashSet<string>(spec.Options);

			Apply(options);
			engine.Set(command, spec with { Options = options });
		}

		return status;

		void Apply(HashSet<string> options)
		{
			foreach (var (option, on) in changes)
			{
				_ = on ? options.Add(option) : options.Remove(option);
			}
		}
	}
}
