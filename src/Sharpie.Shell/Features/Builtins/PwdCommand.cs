using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Prints the shell's current working directory.
/// </summary>
internal class PwdCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "pwd";
	/// <inheritdoc />
	public string Description => "Prints the current working directory";

	/// <inheritdoc />
	public void Run(string[] args)
	{
		Console.WriteLine(Directory.GetCurrentDirectory());
	}
}
