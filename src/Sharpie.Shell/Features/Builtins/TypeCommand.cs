using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Functions;

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
	private readonly IReadOnlyDictionary<string, ICommand> builtins = builtins.ArgumentNotNull();
	private readonly IReadOnlyDictionary<string, string> aliases = aliases.ArgumentNotNull();

	/// <summary>
	/// What a command name stands for.
	/// </summary>
	internal enum Kind
	{
		/// <summary>An alias, whose value replaces the name.</summary>
		Alias,

		/// <summary>A builtin the shell runs itself.</summary>
		Builtin,

		/// <summary>A function defined in the shell, which hides a builtin or program of the same name.</summary>
		Function,

		/// <summary>A program on <c>PATH</c>.</summary>
		Program,

		/// <summary>None of the others.</summary>
		Missing,
	}

	/// <inheritdoc />
	public string Name => "type";

	/// <inheritdoc />
	public string Description => "Shows how a command would be interpreted";

	/// <inheritdoc />
	public int Run(string[] arguments)
	{
		_ = arguments.ArgumentNotNull();

		var status = 0;

		foreach (var commandName in arguments)
		{
			var (kind, value) = this.Resolve(commandName);

			Console.WriteLine(kind switch
			{
				Kind.Alias => $"{commandName} is an alias for {value}",
				Kind.Builtin => $"{commandName} is a shell builtin",
				Kind.Function => $"{commandName} is a function",
				Kind.Program => $"{commandName} is {value}",
				Kind.Missing or _ => $"{commandName}: not found",
			});

			if (kind == Kind.Missing)
			{
				status = 1;
			}
		}

		return status;
	}

	/// <summary>
	/// Works out what <paramref name="commandName" /> would run, for <c>type</c> and <c>command -v</c>.
	/// </summary>
	/// <param name="commandName">The name as typed.</param>
	/// <returns>
	/// The kind, with the alias's value, the function's or builtin's name, the program's full path, or the name itself
	/// when missing.
	/// </returns>
	/// <remarks>
	/// Aliases are checked first, then functions and builtins, then <c>PATH</c>, because that is the order the shell's
	/// main loop dispatches in, so the answer matches what would actually run.
	/// </remarks>
	internal (Kind Kind, string Value) Resolve(string commandName) =>
		this.aliases.TryGetValue(commandName, out var value) ? (Kind.Alias, value)
		: this.builtins.TryGetValue(commandName, out var command) ? (command is FunctionCommand ? Kind.Function : Kind.Builtin, commandName)
		: PathFinder.FindExecutable(commandName) is { } fullPath ? (Kind.Program, fullPath)
		: (Kind.Missing, commandName);
}
