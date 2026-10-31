using System.Text;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Completion;

/// <summary>
/// How to complete the words of one command, as <c>complete</c> sets it and <c>compgen</c> takes it.
/// </summary>
/// <remarks>
/// The fields follow bash's compspec: actions that generate words, a <c>-G</c> pattern, a <c>-W</c> word list, a
/// <c>-F</c> function and a <c>-C</c> command, then a <c>-X</c> filter and a <c>-P</c> prefix and <c>-S</c> suffix
/// for every word they give, and <c>-o</c> options that change how Tab inserts them.
/// </remarks>
internal sealed record CompletionSpec
{
	/// <summary>
	/// The <c>-A</c> actions, by the letter that also selects each one, or a space for those with no letter.
	/// </summary>
	/// <remarks>
	/// In bash's order, which <c>complete -p</c> prints them in.
	/// </remarks>
	internal static readonly (char Letter, string Name)[] ActionNames =
	[
		('a', "alias"), ('b', "builtin"), ('c', "command"), ('d', "directory"), ('e', "export"), ('f', "file"),
		('g', "group"), ('j', "job"), ('k', "keyword"), ('s', "service"), ('u', "user"), ('v', "variable"),
		(' ', "arrayvar"), (' ', "binding"), (' ', "disabled"), (' ', "enabled"), (' ', "function"), (' ', "helptopic"),
		(' ', "hostname"), (' ', "running"), (' ', "setopt"), (' ', "shopt"), (' ', "signal"), (' ', "stopped"),
	];

	/// <summary>
	/// The <c>-o</c> options, in the order <c>complete -p</c> prints them.
	/// </summary>
	internal static readonly string[] OptionNames = ["bashdefault", "default", "dirnames", "filenames", "noquote", "nosort", "nospace", "plusdirs"];

	/// <summary>
	/// Gets the actions that generate words, by name, such as <c>file</c> or <c>variable</c>.
	/// </summary>
	/// <value>
	/// The names from <see cref="ActionNames" />.
	/// </value>
	public IReadOnlySet<string> Actions { get; init; } = new HashSet<string>();

	/// <summary>
	/// Gets the <c>-o</c> options.
	/// </summary>
	/// <value>
	/// The names from <see cref="OptionNames" />.
	/// </value>
	public IReadOnlySet<string> Options { get; init; } = new HashSet<string>();

	/// <summary>
	/// Gets the <c>-G</c> pattern, whose matching file names are words.
	/// </summary>
	/// <value>
	/// The pattern, or <see langword="null" />.
	/// </value>
	public string? Glob { get; init; }

	/// <summary>
	/// Gets the <c>-W</c> word list, split on <c>IFS</c> and expanded when it's used.
	/// </summary>
	/// <value>
	/// The list as given, or <see langword="null" />.
	/// </value>
	public string? WordList { get; init; }

	/// <summary>
	/// Gets the <c>-F</c> function, which fills <c>COMPREPLY</c>.
	/// </summary>
	/// <value>
	/// The function's name, or <see langword="null" />.
	/// </value>
	public string? Function { get; init; }

	/// <summary>
	/// Gets the <c>-C</c> command, whose output lines are words.
	/// </summary>
	/// <value>
	/// The command, or <see langword="null" />.
	/// </value>
	public string? Command { get; init; }

	/// <summary>
	/// Gets the <c>-P</c> text put before each word.
	/// </summary>
	/// <value>
	/// The text, or <see langword="null" />.
	/// </value>
	public string? Prefix { get; init; }

	/// <summary>
	/// Gets the <c>-S</c> text put after each word.
	/// </summary>
	/// <value>
	/// The text, or <see langword="null" />.
	/// </value>
	public string? Suffix { get; init; }

	/// <summary>
	/// Gets the <c>-X</c> pattern; each word it matches is dropped, or kept when it starts with <c>!</c>.
	/// </summary>
	/// <value>
	/// The pattern, or <see langword="null" />.
	/// </value>
	public string? Filter { get; init; }

	/// <summary>
	/// Writes the <c>complete</c> command that sets this spec again for <paramref name="name" />, as
	/// <c>complete -p</c> does.
	/// </summary>
	/// <param name="name">The command name.</param>
	/// <returns>
	/// The line, with the parts in bash's order and each value in single quotes.
	/// </returns>
	public string Format(string name)
	{
		var line = new StringBuilder("complete");

		foreach (var option in OptionNames.Where(this.Options.Contains))
		{
			_ = line.Append(" -o ").Append(option);
		}

		foreach (var (letter, action) in ActionNames.Where(pair => this.Actions.Contains(pair.Name)))
		{
			_ = letter == ' ' ? line.Append(" -A ").Append(action) : line.Append(" -").Append(letter);
		}

		foreach (var (flag, value) in new[] { ('G', this.Glob), ('W', this.WordList), ('P', this.Prefix), ('S', this.Suffix), ('X', this.Filter) })
		{
			if (value is not null)
			{
				_ = line.Append(" -").Append(flag).Append(' ').Append(CommandLine.Quote(value));
			}
		}

		if (this.Function is not null)
		{
			_ = line.Append(" -F ").Append(this.Function);
		}

		if (this.Command is not null)
		{
			_ = line.Append(" -C ").Append(CommandLine.Quote(this.Command));
		}

		return line.Append(' ').Append(name).ToString();
	}

	/// <summary>
	/// Reads the options of <c>complete</c> or <c>compgen</c>.
	/// </summary>
	/// <param name="builtin">The builtin's name, for error reports.</param>
	/// <param name="args">The arguments.</param>
	/// <param name="extra">
	/// The letters this builtin takes besides the shared ones, which are collected in <paramref name="flags" />:
	/// <c>prDEI</c> for <c>complete</c>.
	/// </param>
	/// <param name="flags">Gets each extra letter given.</param>
	/// <param name="first">Gets the index of the first argument after the options.</param>
	/// <returns>
	/// The spec, or <see langword="null" /> after reporting a bad option or action, which gives status 2.
	/// </returns>
	/// <remarks>
	/// Letters can be joined, as in <c>-fd</c>, and a value can follow its letter in the same word, as in
	/// <c>-onospace</c>. <c>--</c> ends the options.
	/// </remarks>
	public static CompletionSpec? Parse(string builtin, string[] args, string extra, out HashSet<char> flags, out int first)
	{
		var actions = new HashSet<string>(StringComparer.Ordinal);
		var options = new HashSet<string>(StringComparer.Ordinal);
		var values = new Dictionary<char, string>();

		flags = [];
		first = 0;

		for (; first < args.Length && args[first] is ['-', _, ..] option; first++)
		{
			if (option == "--")
			{
				first++;

				break;
			}

			for (var index = 1; index < option.Length; index++)
			{
				var letter = option[index];

				if (extra.Contains(letter, StringComparison.Ordinal))
				{
					_ = flags.Add(letter);
				}
				else if (ActionNames.FirstOrDefault(pair => pair.Letter == letter).Name is { } action)
				{
					_ = actions.Add(action);
				}
				else if (!"oAGWFCPSX".Contains(letter, StringComparison.Ordinal))
				{
					return Fail(builtin, "invalid_option", $"`-{letter}` isn't an option of `{builtin}`");
				}
				else if ((index + 1 < option.Length ? option[(index + 1)..] : first + 1 < args.Length ? args[++first] : null) is not { } value)
				{
					return Fail(builtin, "missing_argument", $"`-{letter}` needs a value after it");
				}
				else if (letter == 'o' && !OptionNames.Contains(value))
				{
					return Fail(builtin, "invalid_option", $"`{value}` isn't a completion option");
				}
				else if (letter == 'A' && !ActionNames.Any(pair => pair.Name == value))
				{
					return Fail(builtin, "invalid_action", $"`{value}` isn't an action name");
				}
				else
				{
					if (letter == 'o')
					{
						_ = options.Add(value);
					}
					else if (letter == 'A')
					{
						_ = actions.Add(value);
					}
					else
					{
						values[letter] = value;
					}

					// The value took the rest of the word, or the next word.
					break;
				}
			}
		}

		return new CompletionSpec
		{
			Actions = actions,
			Options = options,
			Glob = values.GetValueOrDefault('G'),
			WordList = values.GetValueOrDefault('W'),
			Function = values.GetValueOrDefault('F'),
			Command = values.GetValueOrDefault('C'),
			Prefix = values.GetValueOrDefault('P'),
			Suffix = values.GetValueOrDefault('S'),
			Filter = values.GetValueOrDefault('X'),
		};
	}

	/// <summary>
	/// Reports a bad option.
	/// </summary>
	/// <param name="builtin">The builtin's name.</param>
	/// <param name="code">The last part of the error code.</param>
	/// <param name="summary">The report.</param>
	/// <returns>
	/// <see langword="null" />, for the caller to return.
	/// </returns>
	private static CompletionSpec? Fail(string builtin, string code, string summary)
	{
		new ShellError($"sharpie::{builtin}::{code}", summary)
		{
			Help = $"{builtin} takes -abcdefgjksuv, -o option, -A action, -G pattern, -W words, -F function, -C command, -X filter, -P prefix and -S suffix",
		}.Write();

		return null;
	}
}
