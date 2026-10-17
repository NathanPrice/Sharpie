using System.Diagnostics.CodeAnalysis;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Prints or sets the file mode creation mask, the permission bits new files and folders start without.
/// </summary>
/// <param name="exchange">Sets the mask and gives back the old one, as the C <c>umask</c> function does.</param>
/// <remarks>
/// <c>umask</c> prints the mask as four octal digits, <c>-S</c> prints the permissions it leaves, like
/// <c>u=rwx,g=rx,o=rx</c>, and <c>-p</c> prints it as a <c>umask</c> command. A mode sets it: octal digits, or a
/// symbolic mode like <c>u=rwx,g=rx,o=</c> or <c>g+w</c> that changes the permissions the mask leaves, read the way
/// bash reads it. Windows has no mask, so there it's an error.
/// </remarks>
internal sealed class UmaskCommand(Func<int, int> exchange) : ICommand
{
	private const int All = 0b111_111_111;

	/// <inheritdoc />
	public string Name => "umask";

	/// <inheritdoc />
	public string Description => "Prints or sets the permissions new files start without";

	/// <inheritdoc />
	/// <remarks>
	/// The status is 0, 1 for a bad mode or on Windows, and 2 for an option that doesn't exist, as in bash. Words
	/// after the mode are ignored, as bash ignores them.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (OperatingSystem.IsWindows())
		{
			new ShellError("sharpie::umask::unsupported", "`umask` doesn't work on Windows") { Help = "Windows sets file permissions through access control lists instead" }.Write();

			return 1;
		}

		var symbolic = false;
		var asCommand = false;
		var index = 0;

		for (; index < args.Length && args[index] is ['-', _, ..] option; index++)
		{
			if (option == "--")
			{
				index++;

				break;
			}

			foreach (var letter in option[1..])
			{
				if (letter is not ('S' or 'p'))
				{
					new ShellError("sharpie::umask::invalid_option", $"`-{letter}` isn't an option of umask") { Help = "umask takes -p and -S" }.Write();

					return 2;
				}

				symbolic |= letter == 'S';
				asCommand |= letter == 'p';
			}
		}

		var current = exchange(0);

		_ = exchange(current);
		current &= All;

		if (index == args.Length)
		{
			Console.WriteLine((asCommand ? symbolic ? "umask -S " : "umask " : string.Empty) + Format(current, symbolic));

			return 0;
		}

		if (!TryParse(args[index], current, out var mask, out var error))
		{
			error.Write();

			return 1;
		}

		_ = exchange(mask);

		if (symbolic)
		{
			Console.WriteLine(Format(mask, symbolic: true));
		}

		return 0;
	}

	/// <summary>
	/// Writes a mask the way <c>umask</c> prints it.
	/// </summary>
	/// <param name="mask">The mask, 0 to 0777.</param>
	/// <param name="symbolic">
	/// <see langword="true" /> for the permissions the mask leaves, like <c>u=rwx,g=rx,o=rx</c>; otherwise,
	/// <see langword="false" /> for four octal digits, like <c>0022</c>.
	/// </param>
	/// <returns>
	/// The text.
	/// </returns>
	internal static string Format(int mask, bool symbolic) => !symbolic
		? Convert.ToString(mask, 8).PadLeft(4, '0')
		: string.Join(',', "ugo".Select((letter, at) =>
		{
			var allowed = ~mask >> (6 - (3 * at));

			return $"{letter}={((allowed & 0b100) != 0 ? "r" : string.Empty)}{((allowed & 0b010) != 0 ? "w" : string.Empty)}{((allowed & 0b001) != 0 ? "x" : string.Empty)}";
		}));

	/// <summary>
	/// Reads a mode given to <c>umask</c> as the new mask.
	/// </summary>
	/// <param name="mode">Octal digits up to 7777, or a symbolic mode.</param>
	/// <param name="current">The mask now, which a symbolic mode changes.</param>
	/// <param name="mask">Receives the new mask, 0 to 0777.</param>
	/// <param name="error">Receives what's wrong with <paramref name="mode" />, or <see langword="null" />.</param>
	/// <returns>
	/// <see langword="true" /> when <paramref name="mode" /> is valid; otherwise, <see langword="false" />.
	/// </returns>
	/// <remarks>
	/// A symbolic mode is comma-separated clauses. Each is any of <c>u</c>, <c>g</c>, <c>o</c> and <c>a</c>, then one
	/// or more of <c>+</c>, <c>-</c> or <c>=</c> each followed by permissions: <c>r</c>, <c>w</c> and <c>x</c>;
	/// <c>X</c>, which is <c>x</c> when anyone has execute before the mode; <c>u</c>, <c>g</c> or <c>o</c>, which copy
	/// that class's permissions from before the mode; and <c>s</c> and <c>t</c>, which a mask can't hold and are
	/// skipped. No class means all three. This is what bash accepts, down to a copy replacing the letters before it, as
	/// in <c>o=wg</c>.
	/// </remarks>
	internal static bool TryParse(string mode, int current, out int mask, [NotNullWhen(false)] out ShellError? error)
	{
		_ = mode.ArgumentNotNull();

		if (mode is not [>= '0' and <= '9', ..])
		{
			return TryParseSymbolic(mode, ~current & All, out mask, out error);
		}

		var value = 0;

		mask = current;
		error = null;

		foreach (var digit in mode)
		{
			value = (value * 8) + digit - '0';

			if (digit is < '0' or > '7' || value > 0xFFF)
			{
				error = new ShellError("sharpie::umask::out_of_range", $"`{mode}` isn't an octal number from 0 to 7777")
				{
					Help = "use octal digits like 022, or a symbolic mode like u=rwx,g=rx,o=rx",
				};

				return false;
			}
		}

		mask = value & All;

		return true;
	}

	/// <summary>
	/// Applies the clauses of a symbolic mode, one after another, to the permissions the mask leaves.
	/// </summary>
	/// <param name="mode">The symbolic mode.</param>
	/// <param name="initial">The permissions the mask leaves before the mode.</param>
	/// <param name="mask">Receives the new mask.</param>
	/// <param name="error">Receives what's wrong with <paramref name="mode" />, or <see langword="null" />.</param>
	/// <returns>
	/// <see langword="true" /> when every clause is valid; otherwise, <see langword="false" />.
	/// </returns>
	private static bool TryParseSymbolic(string mode, int initial, out int mask, [NotNullWhen(false)] out ShellError? error)
	{
		var allowed = initial;
		var index = 0;

		mask = 0;
		error = null;

		while (true)
		{
			var who = 0;

			for (; index < mode.Length && Who(mode[index]) is var bits and not 0; index++)
			{
				who |= bits;
			}

			do
			{
				if (index == mode.Length || mode[index] is not ('+' or '-' or '='))
				{
					error = Invalid(mode, index < mode.Length ? $"expected +, - or = where `{mode[index]}` is" : "expected +, - or = at the end");

					return false;
				}

				var operation = mode[index++];
				var permissions = 0;

				for (; index < mode.Length && Permissions(mode[index], initial, permissions) is { } next; index++)
				{
					permissions = next;
				}

				var classes = who == 0 ? All : who;

				permissions &= classes;
				allowed = operation switch
				{
					'+' => allowed | permissions,
					'-' => allowed & ~permissions,
					_ => (allowed & ~classes) | permissions,
				};
			}
			while (index < mode.Length && mode[index] is '+' or '-' or '=');

			if (index == mode.Length)
			{
				mask = ~allowed & All;

				return true;
			}

			if (mode[index] != ',')
			{
				error = Invalid(mode, $"`{mode[index]}` isn't a permission: use r, w, x, X, s, t, u, g or o");

				return false;
			}

			index++;
		}
	}

	/// <summary>
	/// Gets the permission bits of a class letter.
	/// </summary>
	/// <param name="letter">The letter.</param>
	/// <returns>
	/// The bits of <c>u</c>, <c>g</c> or <c>o</c>, all of them for <c>a</c>, or 0 for any other letter.
	/// </returns>
	private static int Who(char letter) => letter switch
	{
		'u' => 0b111_000_000,
		'g' => 0b000_111_000,
		'o' => 0b000_000_111,
		'a' => All,
		_ => 0,
	};

	/// <summary>
	/// Adds one permission letter to the permissions a clause names so far.
	/// </summary>
	/// <param name="letter">The letter.</param>
	/// <param name="initial">The permissions the mask leaves before the mode, which <c>X</c> and copies read.</param>
	/// <param name="permissions">The permissions so far, in all three classes.</param>
	/// <returns>
	/// The new permissions, or <see langword="null" /> when <paramref name="letter" /> isn't a permission.
	/// </returns>
	private static int? Permissions(char letter, int initial, int permissions) => letter switch
	{
		'r' => permissions | 0b100_100_100,
		'w' => permissions | 0b010_010_010,
		'x' => permissions | 0b001_001_001,
		'X' when (initial & 0b001_001_001) != 0 => permissions | 0b001_001_001,
		'X' or 's' or 't' => permissions,
		'u' => ((initial >> 6) & 0b111) * 0b001_001_001,
		'g' => ((initial >> 3) & 0b111) * 0b001_001_001,
		'o' => (initial & 0b111) * 0b001_001_001,
		_ => null,
	};

	/// <summary>
	/// Builds the error for a symbolic mode that can't be read.
	/// </summary>
	/// <param name="mode">The mode.</param>
	/// <param name="help">What's wrong, as a hint.</param>
	/// <returns>
	/// The error.
	/// </returns>
	private static ShellError Invalid(string mode, string help) =>
		new("sharpie::umask::invalid_mode", $"`{mode}` isn't a valid mode") { Help = help };
}
