using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Clears the terminal screen and its scrollback.
/// </summary>
internal class ClearCommand : ICommand
{
	/// <summary>
	/// The escape sequence that <c>clear</c> prints: move the cursor home, clear the screen, then clear the scrollback.
	/// </summary>
	/// <remarks>
	/// <see cref="Console.Clear" /> isn't used because it throws when output is redirected, as it is when the shell
	/// reads a pipe instead of a terminal.
	/// </remarks>
	public const string ClearScreen = "\e[H\e[2J\e[3J";

	/// <inheritdoc />
	public string Name => "clear";
	/// <inheritdoc />
	public string Description => "Clears the terminal screen";

	/// <inheritdoc />
	public void Run(string[] args) => Console.Write(ClearScreen);
}
