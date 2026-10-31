using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Completion;

/// <summary>
/// Sets how Tab completes a command's words, as in <c>complete -F _git git</c>, lists the specs with <c>complete -p</c>
/// and removes them with <c>complete -r</c>.
/// </summary>
/// <param name="engine">The completion engine, which keeps the specs and runs them.</param>
/// <remarks>
/// It takes bash's options, which <see cref="CompletionSpec.Parse" /> reads. <c>-D</c> sets the spec for a command
/// that has none. shortcut: <c>-E</c> and <c>-I</c>, for an empty line and the command word, are accepted and not
/// used; use them in <see cref="CompletionEngine.Complete" /> when a script needs them.
/// </remarks>
internal sealed class CompleteCommand(CompletionEngine engine) : ICommand
{
	/// <inheritdoc />
	public string Name => "complete";

	/// <inheritdoc />
	public string Description => "Sets how Tab completes a command's words";

	/// <summary>
	/// Gets the completion engine, which Tab runs the specs through.
	/// </summary>
	/// <value>
	/// The engine.
	/// </value>
	public CompletionEngine Engine => engine;

	/// <inheritdoc />
	/// <remarks>
	/// The status is 0, 1 when <c>-p</c> or <c>-r</c> names a command with no spec, and 2 for a bad option or no
	/// command to set.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (CompletionSpec.Parse(this.Name, args, "prDEI", out var flags, out var first) is not { } spec)
		{
			return 2;
		}

		var names = args[first..];

		if (flags.Contains('r'))
		{
			if (names is [])
			{
				engine.Clear();
			}

			return names.Aggregate(0, (status, name) => engine.Remove(name) ? status : this.NotFound(name));
		}

		if (flags.Contains('p') || args is [])
		{
			foreach (var (name, found) in engine.Specs.OrderBy(pair => pair.Key, StringComparer.Ordinal).Where(pair => names is [] || names.Contains(pair.Key)))
			{
				Console.WriteLine(found.Format(name));
			}

			return names.Where(name => !engine.Specs.ContainsKey(name)).Aggregate(0, (_, name) => this.NotFound(name));
		}

		if (flags.Contains('D'))
		{
			engine.Default = spec;

			return 0;
		}

		if (names is [] && !flags.Overlaps("EI"))
		{
			new ShellError("sharpie::complete::missing_name", "`complete` needs the commands to set it for")
			{
				Help = "complete [-abcdefgjksuv] [-o option] [-A action] [-W words] [-F function] [-C command] name...",
			}.Write();

			return 2;
		}

		foreach (var name in names)
		{
			engine.Set(name, spec);
		}

		return 0;
	}

	/// <summary>
	/// Reports a command with no spec.
	/// </summary>
	/// <param name="name">The command name.</param>
	/// <returns>
	/// 1.
	/// </returns>
	private int NotFound(string name)
	{
		new ShellError($"sharpie::{this.Name}::not_found", $"No completion is set for `{name}`").Write();

		return 1;
	}
}
