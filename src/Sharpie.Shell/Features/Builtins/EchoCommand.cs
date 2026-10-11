using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Prints its arguments joined by single spaces.
/// </summary>
internal class EchoCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "echo";
	/// <inheritdoc />
	public string Description => "Prints the text you give it";

	/// <inheritdoc />
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		Console.WriteLine(string.Join(" ", args));

		return 0;
	}
}
