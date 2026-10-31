using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Completion;

/// <summary>
/// Prints the words a completion spec gives for a word, one per line, as in <c>compgen -W "add commit" -- c</c>.
/// </summary>
/// <param name="engine">The completion engine, which generates the words.</param>
internal sealed class CompgenCommand(CompletionEngine engine) : ICommand
{
	/// <inheritdoc />
	public string Name => "compgen";

	/// <inheritdoc />
	public string Description => "Prints the words a completion spec gives";

	/// <inheritdoc />
	/// <remarks>
	/// It takes <c>complete</c>'s options. The word comes after them, and is empty when there is none. The status is 0
	/// when any word matched or there were no arguments at all, 1 when none did, and 2 for a bad option, as in bash.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (CompletionSpec.Parse(this.Name, args, string.Empty, out _, out var first) is not { } spec)
		{
			return 2;
		}

		var words = engine.Generate(spec, first < args.Length ? args[first] : string.Empty);

		foreach (var word in words)
		{
			Console.WriteLine(word);
		}

		return words.Count > 0 || args is [] ? 0 : 1;
	}
}
