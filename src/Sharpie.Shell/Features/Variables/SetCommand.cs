using System.Collections;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// Sets the positional parameters, or lists the shell variables.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables and positional parameters.</param>
/// <remarks>
/// <c>set -- a b</c> or <c>set a b</c> makes <c>a</c> and <c>b</c> the positional parameters, and <c>set --</c> alone
/// clears them. <c>set</c> with no arguments prints every variable as <c>NAME=value</c>, sorted, with the value quoted
/// only when it needs to be, as bash prints them.
/// </remarks>
internal sealed class SetCommand(DeclareCommand declare) : ICommand
{
	/// <inheritdoc />
	public string Name => "set";

	/// <inheritdoc />
	public string Description => "Sets the positional parameters or lists variables";

	/// <inheritdoc />
	/// <remarks>
	/// The status is 0, or 2 for an option, since none is supported yet.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args is [])
		{
			this.PrintVariables();

			return 0;
		}

		if (args is ["--", .. var rest])
		{
			declare.SetPositional(rest);

			return 0;
		}

		// A lone - or + also ends the options, the older spelling, but with nothing after it bash keeps the parameters.
		if (args is ["-" or "+", .. var words])
		{
			if (words.Length > 0)
			{
				declare.SetPositional(words);
			}

			return 0;
		}

		if (args[0] is ['-' or '+', ..])
		{
			new ShellError("sharpie::set::invalid_option", $"`{args[0]}` isn't an option of set").Write();

			return 2;
		}

		declare.SetPositional(args);

		return 0;
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
