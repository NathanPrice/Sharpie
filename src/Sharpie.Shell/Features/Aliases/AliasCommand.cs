using System.Text.RegularExpressions;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Aliases;

/// <summary>
/// Defines and lists aliases, short names that stand for the start of a command line.
/// </summary>
/// <remarks>
/// <c>alias NAME=VALUE</c> stores or overwrites an alias, <c>alias NAME</c> prints it, and <c>alias</c> on its own
/// prints them all, each as <c>alias NAME='VALUE'</c> so the output can be typed back in. A name may hold letters,
/// digits and <c>_ . - ! % , @</c>, the characters POSIX allows; anything else is reported and nothing is stored.
/// </remarks>
internal sealed partial class AliasCommand : ICommand
{
	/// <summary>
	/// The aliases defined so far, keyed by name.
	/// </summary>
	/// <remarks>
	/// Kept on the command itself because the shell creates one <see cref="AliasCommand" /> for the whole session.
	/// </remarks>
	private readonly Dictionary<string, string> aliases = [];

	/// <summary>
	/// Gets the aliases defined so far, keyed by name.
	/// </summary>
	/// <value>
	/// A live view, so a later <c>alias</c> shows up without fetching it again.
	/// </value>
	public IReadOnlyDictionary<string, string> Aliases => this.aliases;

	/// <inheritdoc />
	public string Name => "alias";
	/// <inheritdoc />
	public string Description => "Defines and lists command aliases";

	/// <inheritdoc />
	public void Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args.Length == 0)
		{
			foreach (var (name, value) in this.aliases.OrderBy(entry => entry.Key, StringComparer.Ordinal))
			{
				Console.WriteLine(Format(name, value));
			}

			return;
		}

		foreach (var arg in args)
		{
			// Split on the first '=' only, so a value can itself contain '='
			var parts = arg.Split('=', 2);

			if (parts.Length == 2)
			{
				this.Define(parts[0], parts[1]);
			}
			else if (this.aliases.TryGetValue(arg, out var value))
			{
				Console.WriteLine(Format(arg, value));
			}
			else
			{
				Console.Error.WriteLine($"{this.Name}: {arg}: not found");
			}
		}
	}

	/// <summary>
	/// Stores an alias, or reports the name when it isn't a valid alias name.
	/// </summary>
	/// <param name="name">The alias name.</param>
	/// <param name="value">The text the name stands for.</param>
	public void Define(string name, string value)
	{
		_ = name.ArgumentNotNull();
		_ = value.ArgumentNotNull();

		if (NameRegex().IsMatch(name))
		{
			this.aliases[name] = value;
		}
		else
		{
			Console.Error.WriteLine($"{this.Name}: `{name}': invalid alias name");
		}
	}

	/// <summary>
	/// Replaces each command name in <paramref name="words" /> that is an alias with the words of its value.
	/// </summary>
	/// <param name="words">The words from <see cref="CommandLine.Split" />.</param>
	/// <param name="variables">The shell variables that <c>$NAME</c> in an alias value expands from.</param>
	/// <returns>
	/// The words with every alias replaced, or the same words when none is used.
	/// </returns>
	/// <remarks>
	/// A command name is the first word and the word after each <c>|</c>, including a <c>|</c> that came from an
	/// alias value. A value can start with another alias, which is expanded too, but an alias is never expanded
	/// again inside its own expansion, so <c>alias grep='grep -n'</c> doesn't loop. Like
	/// <see cref="CommandLine.SplitPipeline" />, this works on words that are already split, so a quoted or
	/// backslashed name such as <c>\grep</c> is still expanded, where POSIX would run the program itself.
	/// </remarks>
	public string[] Expand(string[] words, IReadOnlyDictionary<string, string>? variables)
	{
		_ = words.ArgumentNotNull();

		var result = new List<string>(words);

		for (var index = 0; index < result.Count; index++)
		{
			if (index > 0 && result[index - 1] != "|")
			{
				continue;
			}

			var used = new HashSet<string>(StringComparer.Ordinal);

			while (index < result.Count && this.aliases.TryGetValue(result[index], out var value) && used.Add(result[index]))
			{
				result.RemoveAt(index);
				result.InsertRange(index, CommandLine.Split(value, variables));
			}
		}

		return [.. result];
	}

	/// <summary>
	/// Formats one alias the way <c>alias</c> lists it.
	/// </summary>
	/// <param name="name">The alias name.</param>
	/// <param name="value">The alias value.</param>
	/// <returns>
	/// <c>alias NAME='VALUE'</c>, with each <c>'</c> in the value written as <c>'\''</c>.
	/// </returns>
	private static string Format(string name, string value) => $"alias {name}='{value.Replace("'", @"'\''", StringComparison.Ordinal)}'";

	/// <summary>
	/// Matches a valid alias name.
	/// </summary>
	/// <returns>
	/// A compiled regex that matches one or more letters, digits or the characters <c>_ . - ! % , @</c>.
	/// </returns>
	[GeneratedRegex(@"^[A-Za-z0-9_.!%,@-]+\z")]
	private static partial Regex NameRegex();
}
