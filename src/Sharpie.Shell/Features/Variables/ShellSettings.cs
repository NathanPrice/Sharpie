namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// The shell options <c>set -o</c> turns on and <c>set +o</c> turns off.
/// </summary>
internal sealed class ShellSettings
{
	/// <summary>
	/// The option names <c>set -o</c> takes, sorted as <c>set -o</c> lists them, each with the letter <c>set -</c>
	/// takes for it, if any.
	/// </summary>
	internal static readonly (string Name, char? Letter)[] Options = [("errexit", 'e'), ("nounset", 'u'), ("pipefail", null), ("xtrace", 'x')];

	/// <summary>
	/// The names of the options that are on.
	/// </summary>
	private readonly HashSet<string> on = [];

	/// <summary>
	/// Gets a value indicating whether <c>set -e</c> is on, so a failed command ends the shell.
	/// </summary>
	/// <value>
	/// <see langword="true" /> when the shell exits after a command fails.
	/// </value>
	public bool ErrExit => this.on.Contains("errexit");

	/// <summary>
	/// Gets a value indicating whether <c>set -u</c> is on, so expanding a variable that isn't set is an error.
	/// </summary>
	/// <value>
	/// <see langword="true" /> when an unset variable is an error.
	/// </value>
	public bool NoUnset => this.on.Contains("nounset");

	/// <summary>
	/// Gets a value indicating whether <c>set -x</c> is on, so each command is printed before it runs.
	/// </summary>
	/// <value>
	/// <see langword="true" /> when commands are traced.
	/// </value>
	public bool XTrace => this.on.Contains("xtrace");

	/// <summary>
	/// Gets a value indicating whether <c>set -o pipefail</c> is on, so a pipeline fails when any of its programs does.
	/// </summary>
	/// <value>
	/// <see langword="true" /> when a pipeline's status is its last failing program's.
	/// </value>
	public bool PipeFail => this.on.Contains("pipefail");

	/// <summary>
	/// Gets or sets a value indicating whether the shell is interactive.
	/// </summary>
	/// <value>
	/// <see langword="true" /> for a shell reading the user's typed lines, which an unset variable doesn't end.
	/// </value>
	public bool Interactive { get; set; }

	/// <summary>
	/// Gets the option that <c>set -</c> takes <paramref name="letter" /> for.
	/// </summary>
	/// <param name="letter">The letter after <c>-</c> or <c>+</c>.</param>
	/// <returns>
	/// The option name, or <see langword="null" /> when no option has that letter.
	/// </returns>
	public static string? NameOf(char letter) => Array.Find(Options, option => option.Letter == letter).Name;

	/// <summary>
	/// Checks whether the option <paramref name="name" /> is on.
	/// </summary>
	/// <param name="name">One of the names in <see cref="Options" />.</param>
	/// <returns>
	/// <see langword="true" /> when it's on.
	/// </returns>
	public bool IsOn(string name) => this.on.Contains(name);

	/// <summary>
	/// Turns the option <paramref name="name" /> on or off.
	/// </summary>
	/// <param name="name">The option name.</param>
	/// <param name="value"><see langword="true" /> to turn it on.</param>
	/// <returns>
	/// <see langword="false" /> when <paramref name="name" /> isn't in <see cref="Options" />, which changes nothing.
	/// </returns>
	public bool Set(string name, bool value)
	{
		if (!Array.Exists(Options, option => option.Name == name))
		{
			return false;
		}

		_ = value ? this.on.Add(name) : this.on.Remove(name);

		return true;
	}
}
