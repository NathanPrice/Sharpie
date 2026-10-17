using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Does nothing and gives a fixed status, for <c>:</c>, <c>true</c> and <c>false</c>.
/// </summary>
/// <param name="name">The name the command is typed as.</param>
/// <param name="status">The status every run gives.</param>
/// <param name="description">The text <c>type</c> and completion show for the command.</param>
/// <remarks>
/// Arguments are ignored, as in bash, so <c>: &gt; file</c> empties a file through the shell's redirect and
/// <c>true --help</c> still gives 0. As builtins they also save starting a process for each <c>true</c>.
/// </remarks>
internal sealed class StatusCommand(string name, int status, string description) : ICommand
{
	/// <inheritdoc />
	public string Name => name;

	/// <inheritdoc />
	public string Description => description;

	/// <inheritdoc />
	public int Run(string[] args) => status;
}
