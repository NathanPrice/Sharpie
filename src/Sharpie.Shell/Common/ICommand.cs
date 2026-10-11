namespace Sharpie.Shell.Common;

/// <summary>
/// Defines a builtin command that the shell runs itself instead of starting an external program.
/// </summary>
internal interface ICommand
{
	/// <summary>
	/// Gets the name the user types to run the command.
	/// </summary>
	/// <value>
	/// The command name, matched case-sensitively against the first word of the input line.
	/// </value>
	public string Name { get; }

	/// <summary>
	/// Gets a short, human-readable description of what the command does.
	/// </summary>
	/// <value>
	/// A one-line description.
	/// </value>
	public string Description { get; }

	/// <summary>
	/// Runs the command.
	/// </summary>
	/// <param name="args">The words that followed the command name on the input line.</param>
	/// <returns>
	/// The exit status: 0 for success, anything else for failure, which <c>$?</c>, <c>&amp;&amp;</c> and <c>||</c> read.
	/// </returns>
	public int Run(string[] args);
}
