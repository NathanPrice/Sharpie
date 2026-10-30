using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Prints its arguments joined by single spaces, following bash's <c>echo</c>.
/// </summary>
/// <remarks>
/// Leading words made only of <c>-</c> and the letters <c>n</c>, <c>e</c> and <c>E</c> are options:
/// <c>-n</c> leaves off the trailing newline, <c>-e</c> turns on backslash escapes and <c>-E</c> turns
/// them off again, with the last one winning. The first word that is not an option, and every word
/// after it, is printed as is.
/// </remarks>
internal class EchoCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "echo";
	/// <inheritdoc />
	public string Description => "Prints the text you give it (-n: no newline, -e: read backslash escapes)";

	/// <inheritdoc />
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var newline = true;
		var escapes = false;
		var first = 0;
		for (; first < args.Length && IsOption(args[first]); first++)
		{
			foreach (var letter in args[first].AsSpan(1))
			{
				switch (letter)
				{
					case 'n':
						newline = false;
						break;
					case 'e':
						escapes = true;
						break;
					default:
						escapes = false;
						break;
				}
			}
		}

		var text = string.Join(" ", args, first, args.Length - first);
		if (escapes)
		{
			text = Escapes.Decode(text, EscapeRules.Echo, out var stopped);
			newline &= !stopped;
		}

		Console.Write(text);
		if (newline)
		{
			Console.WriteLine();
		}

		return 0;
	}

	/// <summary>
	/// Checks whether a word is an echo option such as <c>-n</c>, <c>-e</c> or <c>-neE</c>.
	/// </summary>
	/// <param name="word">The word to check.</param>
	/// <returns>
	/// <see langword="true" /> when <paramref name="word" /> is a <c>-</c> followed by one or more of
	/// the letters <c>n</c>, <c>e</c> and <c>E</c>; <see langword="false" /> for <c>-</c>, <c>--</c> and any other word.
	/// </returns>
	private static bool IsOption(string word) =>
		word.Length > 1 && word[0] == '-' && word.AsSpan(1).IndexOfAnyExcept("neE") < 0;
}
