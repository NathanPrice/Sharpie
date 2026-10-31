namespace Sharpie.Shell.Common;

/// <content>
/// Finds a <c>&lt;(...)</c> or <c>&gt;(...)</c> in a command line, which <see cref="ProcessSubstitution" /> runs.
/// </content>
internal static partial class CommandLine
{
	/// <summary>
	/// Checks whether a process substitution starts at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">A position outside quotes.</param>
	/// <returns>
	/// <see langword="true" /> for a <c>&lt;(</c> or <c>&gt;(</c>, which bash reads as a process substitution
	/// anywhere in a word, as in <c>--file=&lt;(cmd)</c>, rather than as a redirect.
	/// </returns>
	internal static bool IsProcessSubstitutionStart(string line, int index) =>
		index + 1 < line.Length && line[index] is '<' or '>' && line[index + 1] == '(';

	/// <summary>
	/// Finds the <c>)</c> that ends the process substitution at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">A position outside quotes.</param>
	/// <returns>
	/// The position of the <c>)</c>, or -1 when no process substitution starts there or it never closes.
	/// </returns>
	internal static int ProcessSubstitutionEnd(string line, int index) =>
		IsProcessSubstitutionStart(line, index) ? ParenthesisEnd(line, index) : -1;
}
