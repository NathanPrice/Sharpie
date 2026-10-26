using System.Collections;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// Turns shell options on and off, sets the positional parameters, or lists the shell variables.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables and positional parameters.</param>
/// <param name="settings">The shell options this command changes.</param>
/// <remarks>
/// <c>-e</c>, <c>-C</c>, <c>-u</c> and <c>-x</c> turn on errexit, noclobber, nounset and xtrace, <c>-o NAME</c> turns
/// on an option by name, pipefail included, and <c>+</c> in place of <c>-</c> turns it off. Letters can be joined, as
/// in <c>set -euo pipefail</c>. <c>set -o</c> alone lists the options and <c>set +o</c> prints the commands that would
/// set them again. The words after the options, or after <c>--</c>, become the positional parameters, and
/// <c>set --</c> alone clears them. <c>set</c> with no arguments prints every variable as <c>NAME=value</c>, sorted,
/// with the value quoted only when it needs to be, as bash prints them.
/// </remarks>
internal sealed class SetCommand(DeclareCommand declare, ShellSettings settings) : ICommand
{
	/// <summary>
	/// Gets the shell options this command changes.
	/// </summary>
	/// <value>
	/// The one set of options the shell reads before each command.
	/// </value>
	public ShellSettings Settings => settings;

	/// <inheritdoc />
	public string Name => "set";

	/// <inheritdoc />
	public string Description => "Sets shell options and positional parameters, or lists variables";

	/// <inheritdoc />
	/// <remarks>
	/// The status is 0, or 2 for an option that doesn't exist, as in bash. Options before the bad one stay changed.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args is [])
		{
			this.PrintVariables();

			return 0;
		}

		for (var index = 0; index < args.Length; index++)
		{
			var arg = args[index];

			if (arg == "--")
			{
				declare.SetPositionalFromSet(args[(index + 1)..]);

				return 0;
			}

			// A lone - or + also ends the options, the older spelling, but with nothing after it bash keeps the
			// parameters. The - also turns tracing off.
			if (arg is "-" or "+")
			{
				if (arg == "-")
				{
					_ = settings.Set("xtrace", false);
				}

				if (index + 1 < args.Length)
				{
					declare.SetPositionalFromSet(args[(index + 1)..]);
				}

				return 0;
			}

			if (arg is not ['-' or '+', ..])
			{
				declare.SetPositionalFromSet(args[index..]);

				return 0;
			}

			foreach (var letter in arg[1..])
			{
				if (letter == 'o' && index + 1 == args.Length)
				{
					PrintOptions(settings, arg[0] == '-');

					return 0;
				}

				var name = letter == 'o' ? args[++index] : ShellSettings.NameOf(letter);

				if (name is null || !settings.Set(name, arg[0] == '-'))
				{
					Report(letter == 'o' ? $"`{name}` isn't an option name of set" : $"`{arg[0]}{letter}` isn't an option of set");

					return 2;
				}
			}
		}

		return 0;
	}

	/// <summary>
	/// Reports an option <c>set</c> doesn't have, with the ones it does have as a hint.
	/// </summary>
	/// <param name="message">The message.</param>
	private static void Report(string message) =>
		new ShellError("sharpie::set::invalid_option", message) { Help = "set takes -e, -C, -u, -x and -o with errexit, noclobber, nounset, pipefail or xtrace" }.Write();

	/// <summary>
	/// Prints every option and whether it's on.
	/// </summary>
	/// <param name="settings">The options.</param>
	/// <param name="asTable">
	/// <see langword="true" /> for <c>set -o</c>'s table of names and <c>on</c> or <c>off</c>, or
	/// <see langword="false" /> for <c>set +o</c>'s commands that set each option the way it is now.
	/// </param>
	private static void PrintOptions(ShellSettings settings, bool asTable)
	{
		foreach (var (name, _) in ShellSettings.Options)
		{
			var on = settings.IsOn(name);

			Console.WriteLine(asTable ? $"{name,-15}\t{(on ? "on" : "off")}" : $"set {(on ? '-' : '+')}o {name}");
		}
	}

	/// <summary>
	/// Prints every shell and environment variable whose name is a valid identifier, sorted by name.
	/// </summary>
	/// <remarks>
	/// Environment variables count because POSIX makes them shell variables when the shell starts. A shell variable of
	/// the same name wins, as it does when <c>$NAME</c> expands.
	/// </remarks>
	private void PrintVariables()
	{
		var all = new SortedDictionary<string, string>(StringComparer.Ordinal);

		foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
		{
			all[(string)entry.Key] = (string)entry.Value!;
		}

		foreach (var (name, value) in declare.Variables)
		{
			all[name] = value;
		}

		foreach (var (name, value) in all.Where(pair => DeclareCommand.IsIdentifier(pair.Key)))
		{
			// bash leaves an empty value as NAME= here, though set -x shows it as ''.
			Console.WriteLine($"{name}={(value.Length == 0 ? string.Empty : CommandLine.QuoteIfNeeded(value))}");
		}
	}
}
