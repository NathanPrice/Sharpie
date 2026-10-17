using System.Globalization;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// Drops positional parameters from the front, so <c>$2</c> becomes <c>$1</c>.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which holds the positional parameters.</param>
/// <remarks>
/// <c>shift</c> drops one and <c>shift N</c> drops N. Asking for more than <c>$#</c> drops nothing and gives status 1,
/// quietly, as in bash.
/// </remarks>
internal sealed class ShiftCommand(DeclareCommand declare) : ICommand
{
	/// <inheritdoc />
	public string Name => "shift";

	/// <inheritdoc />
	public string Description => "Drops positional parameters from the front";

	/// <inheritdoc />
	/// <remarks>
	/// The status is 0 once shifted, 1 for a count past <c>$#</c>, a negative count or more than one argument, and 2
	/// for a count that isn't a number, as bash gives.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args.Length > 1)
		{
			new ShellError("sharpie::shift::too_many_arguments", "`shift` takes at most one count").Write();

			return 1;
		}

		var count = 1;

		if (args is [var text] && !int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out count))
		{
			new ShellError("sharpie::shift::not_a_number", $"`{text}` isn't a number") { Help = "shift takes a whole number of parameters to drop" }.Write();

			return 2;
		}

		if (count < 0)
		{
			new ShellError("sharpie::shift::out_of_range", $"Can't shift by {count}") { Help = "the count can't be negative" }.Write();

			return 1;
		}

		var positional = declare.Positional;

		if (count > positional.Count)
		{
			return 1;
		}

		declare.SetPositional([.. positional.Skip(count)]);

		return 0;
	}
}
