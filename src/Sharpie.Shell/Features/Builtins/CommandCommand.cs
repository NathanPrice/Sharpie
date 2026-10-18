using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Aliases;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Tells how a name would be run, with <c>-v</c> or <c>-V</c>.
/// </summary>
/// <param name="type">The <c>type</c> builtin, whose lookup this shares so both give the same answer.</param>
/// <remarks>
/// <c>command name args</c> runs <c>name</c> past any alias of the same name. The shell does that before this builtin
/// is reached, through <see cref="Unwrap" />, so redirects and pipelines work the same as without <c>command</c>.
/// </remarks>
internal sealed class CommandCommand(TypeCommand type) : ICommand
{
	private readonly TypeCommand type = type.ArgumentNotNull();

	/// <inheritdoc />
	public string Name => "command";

	/// <inheritdoc />
	public string Description => "Runs a command past any alias, or tells how a name would be run";

	/// <summary>
	/// Drops each <c>command</c> word that stands where a command name goes and has a name after it, so the shell runs
	/// that name directly.
	/// </summary>
	/// <param name="words">The words after aliases are expanded.</param>
	/// <returns>
	/// The words without those <c>command</c> words, or the same words when there are none.
	/// </returns>
	/// <remarks>
	/// A command name goes first, after <c>|</c>, and after <c>NAME=VALUE</c> words in either place. Aliases are
	/// already expanded, and only a command name is ever expanded, so the name after <c>command</c> never was. A
	/// <c>command</c> followed by an option such as <c>-v</c>, or by nothing, stays for the builtin.
	/// </remarks>
	internal static Word[] Unwrap(Word[] words)
	{
		_ = words.ArgumentNotNull();

		var result = new List<Word>(words);
		var commandPosition = true;

		for (var index = 0; index < result.Count; index++)
		{
			var word = result[index];

			if (commandPosition && word is { IsOperator: false, Text: "command" }
				&& index + 1 < result.Count && result[index + 1] is { IsOperator: false, Text: var next } && !next.StartsWith('-'))
			{
				result.RemoveAt(index--);

				continue;
			}

			commandPosition = word is { IsOperator: true, Text: "|" } || (commandPosition && !word.IsOperator && DeclareCommand.IsAssignment(word.Text));
		}

		return [.. result];
	}

	/// <summary>
	/// Finds the function or builtin a command name runs.
	/// </summary>
	/// <param name="commands">The builtins and functions, by name.</param>
	/// <param name="words">The command's words before <see cref="Unwrap" /> dropped any <c>command</c> word.</param>
	/// <param name="name">The command name, one of <paramref name="words" />.</param>
	/// <returns>
	/// The function or builtin, or <see langword="null" /> when the name is a program's. When <c>command</c> came right
	/// before the name, a function gives way to the builtin it hides, or to the program when it hides none.
	/// </returns>
	/// <remarks>
	/// Skipping the function is what lets <c>cd() { command cd "$@" &amp;&amp; ls; }</c> run the builtin rather than
	/// call itself forever. A <c>command</c> word right before a command name is always one that
	/// <see cref="Unwrap" /> dropped, since the name couldn't be in command position otherwise.
	/// </remarks>
	internal static ICommand? Find(IReadOnlyDictionary<string, ICommand> commands, Word[] words, Word name) =>
		commands.ArgumentNotNull().GetValueOrDefault(name.Text) is FunctionCommand function
		&& Array.IndexOf(words.ArgumentNotNull(), name) is > 0 and var index && words[index - 1] is { IsOperator: false, Text: "command" }
			? function.Shadowed
			: commands.GetValueOrDefault(name.Text);

	/// <inheritdoc />
	/// <remarks>
	/// <c>-v</c> prints, for each name, the alias definition, the builtin's name or the program's path, and nothing
	/// for a name that isn't found. Its status is 1 only when none was found, as in bash. <c>-V</c> prints what
	/// <c>type</c> does. With no arguments it does nothing, and any other option is an error with status 2.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var options = args.TakeWhile(arg => arg.StartsWith('-')).ToArray();
		var names = args[options.Length..];

		if (options.FirstOrDefault(option => option is not ("-v" or "-V")) is { } invalid)
		{
			new ShellError("sharpie::command::invalid_option", $"`{invalid}` isn't an option of command")
			{
				Help = "use -v to print what a name runs, or -V to describe it",
			}.Write();

			return 2;
		}

		if (options.Contains("-V"))
		{
			return this.type.Run(names);
		}

		var found = names.Length == 0;

		foreach (var name in names)
		{
			var (kind, value) = this.type.Resolve(name);

			if (kind != TypeCommand.Kind.Missing)
			{
				Console.WriteLine(kind == TypeCommand.Kind.Alias ? AliasCommand.Format(name, value) : value);
				found = true;
			}
		}

		return found ? 0 : 1;
	}
}
