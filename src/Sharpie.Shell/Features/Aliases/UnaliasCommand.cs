using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Aliases;

/// <summary>
/// Removes aliases.
/// </summary>
/// <param name="alias">The <c>alias</c> builtin, which holds the aliases.</param>
/// <remarks>
/// <c>unalias NAME...</c> removes each name and <c>unalias -a</c> removes them all, ignoring any names after it, as
/// bash does. <c>--</c> ends the options.
/// </remarks>
internal sealed class UnaliasCommand(AliasCommand alias) : ICommand
{
	/// <summary>
	/// The hint shown when <c>unalias</c> is used wrongly.
	/// </summary>
	private const string Usage = "use unalias NAME... or unalias -a";

	/// <inheritdoc />
	public string Name => "unalias";

	/// <inheritdoc />
	public string Description => "Removes command aliases";

	/// <inheritdoc />
	/// <remarks>
	/// The status is 0, 1 when a name isn't an alias, or 2 for an unknown option or no names at all, as in bash. The
	/// other names are still removed.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var names = args;

		if (args is [['-', _, ..] option, ..])
		{
			if (option == "-a")
			{
				alias.Clear();

				return 0;
			}

			if (option != "--")
			{
				new ShellError("sharpie::unalias::invalid_option", $"`{option}` isn't an option of unalias") { Help = Usage }.Write();

				return 2;
			}

			names = args[1..];
		}

		if (names is [])
		{
			new ShellError("sharpie::unalias::missing_name", "`unalias` needs an alias name") { Help = Usage }.Write();

			return 2;
		}

		var status = 0;

		foreach (var name in names)
		{
			if (!alias.Remove(name))
			{
				new ShellError("sharpie::unalias::not_found", $"No alias named `{name}`").Write();
				status = 1;
			}
		}

		return status;
	}
}
