using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Ends the shell process with exit code 0.
/// </summary>
internal class ExitCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "exit";
	/// <inheritdoc />
	public string Description => "Closes the app";

	/// <inheritdoc />
	public void Run(string[] args) => Environment.Exit(0);
}
