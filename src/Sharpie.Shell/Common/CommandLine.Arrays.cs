using System.Globalization;
using Sharpie.Shell.Features.Arithmetic;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Common;

/// <content>
/// Arrays on the command line: the <c>NAME=(...)</c> list that sets one, and the subscripts that read one, as in
/// <c>${a[1]}</c> and <c>"${a[@]}"</c>.
/// </content>
internal static partial class CommandLine
{
	/// <summary>
	/// Checks whether a word typed so far is the start of a <c>NAME=(...)</c> list.
	/// </summary>
	/// <param name="typed">The word up to a <c>(</c>, as typed.</param>
	/// <returns>
	/// <see langword="true" /> for <c>NAME=</c> or <c>NAME+=</c>, after which bash reads a <c>(</c> as the start of a
	/// list of elements.
	/// </returns>
	internal static bool IsCompoundStart(ReadOnlySpan<char> typed) =>
		typed.EndsWith("=") && DeclareCommand.IsIdentifier(typed[..^(typed.EndsWith("+=") ? 2 : 1)].ToString());

	/// <summary>
	/// Finds the <c>)</c> that ends the list of a <c>NAME=(...)</c> word.
	/// </summary>
	/// <param name="line">The text.</param>
	/// <param name="open">The position of the list's <c>(</c>.</param>
	/// <returns>
	/// The position of the <c>)</c>, or -1 when there is none. The list is read as the text of a <c>$(...)</c> is, so
	/// quotes, substitutions and comments in it are skipped, and it can run over several lines.
	/// </returns>
	internal static int CompoundEnd(string line, int open) => ParenthesisEnd(line, open - 1);

	/// <summary>
	/// Expands the words of a <c>NAME=(...)</c> list.
	/// </summary>
	/// <param name="line">The text.</param>
	/// <param name="open">The position of the list's <c>(</c>.</param>
	/// <param name="close">The position of its <c>)</c>.</param>
	/// <param name="variables">The shell variables.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">Runs a command substitution, as <see cref="Split" /> describes.</param>
	/// <returns>
	/// The list again, as <c>('a' 'b')</c>, its words split, matched against file names and quoted, for
	/// <see cref="DeclareCommand.SetArray" /> to read back. Each line of the list is read on its own, so a newline
	/// between words is a blank and a comment ends at the end of its line.
	/// </returns>
	/// <exception cref="ExpansionError">A word in the list can't expand. It points at the whole list.</exception>
	private static string ExpandCompound(string line, int open, int close, IReadOnlyDictionary<string, string> variables, Action<Word>? unset, Func<string, string>? substitute)
	{
		var list = line[(open + 1)..close];
		var elements = new List<string>();
		Action<Word>? spanned = unset is null ? null : name => unset(name with { Start = open, End = close + 1 });

		try
		{
			for (var from = 0; from < list.Length;)
			{
				var words = SplitWords(list[from..], variables, spanned, substitute, FieldSeparators(variables), out var next);

				elements.AddRange(words.Where(word => !word.IsOperator).Select(word => Quote(word.Text)));
				from += next;
			}
		}
		catch (ExpansionError inner)
		{
			throw new ExpansionError(inner.Error with { Source = line, Start = open, End = close + 1 }, inner.Fatal);
		}

		return $"({string.Join(' ', elements)})";
	}

	/// <summary>
	/// Gets the subscript typed after a name in a <c>${...}</c>.
	/// </summary>
	/// <param name="inside">The text between the braces.</param>
	/// <param name="at">Where the name ends.</param>
	/// <param name="name">The name.</param>
	/// <returns>
	/// The subscript with its brackets, as in <c>[i+1]</c>, or an empty string when none follows a variable name.
	/// </returns>
	private static string SubscriptText(string inside, int at, string name) =>
		DeclareCommand.IsIdentifier(name) && at < inside.Length && inside[at] == '[' && DeclareCommand.SubscriptEnd(inside, at) is var close and > 0
			? inside[at..(close + 1)]
			: string.Empty;

	/// <summary>
	/// Works out the subscript of a name such as <c>a[$i+1]</c> in a <c>${...}</c>.
	/// </summary>
	/// <param name="name">The name, with its subscript as typed or none.</param>
	/// <param name="variables">The shell variables.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">Runs a command substitution, as <see cref="Split" /> describes.</param>
	/// <returns>
	/// The name with the number its subscript gives, as in <c>a[3]</c>, which <see cref="Lookup" /> reads. A name with
	/// no subscript, or with <c>[@]</c> or <c>[*]</c>, comes back as it is.
	/// </returns>
	/// <exception cref="ExpansionError">The subscript can't be worked out.</exception>
	/// <remarks>
	/// The subscript expands as a word does, then reads as an arithmetic expression, as in bash, so
	/// <c>${words[cword-1]}</c> and <c>${words[$i]}</c> both work.
	/// </remarks>
	private static string ResolveSubscript(string name, IReadOnlyDictionary<string, string> variables, Action<Word>? unset, Func<string, string>? substitute)
	{
		if (!DeclareCommand.TrySplitElement(name, out var array, out var subscript) || subscript is "@" or "*")
		{
			return name;
		}

		try
		{
			var index = ArithmeticExpression.Evaluate(ExpandText(Protect(subscript, split: false), variables, unset, substitute), variables, noUnset: unset is not null);

			return $"{array}[{index.ToString(CultureInfo.InvariantCulture)}]";
		}
		catch (ArithmeticExpression.Failure failure)
		{
			throw new ExpansionError(failure.Error, fatal: false);
		}
	}

	/// <summary>
	/// Gets the elements a name such as <c>a[@]</c> stands for.
	/// </summary>
	/// <param name="variables">The shell variables.</param>
	/// <param name="name">The name.</param>
	/// <returns>
	/// The values in index order for <c>NAME[@]</c> or <c>NAME[*]</c>, or <see langword="null" /> for any other name.
	/// </returns>
	private static string[]? ListedElements(IReadOnlyDictionary<string, string> variables, string name) =>
		IsList(name, out var array) ? [.. DeclareCommand.Elements(variables, array).Select(element => element.Value)] : null;

	/// <summary>
	/// Checks whether a name is <c>NAME[@]</c> or <c>NAME[*]</c>, which stand for every element.
	/// </summary>
	/// <param name="name">The name.</param>
	/// <param name="array">The name before the subscript.</param>
	/// <returns>
	/// <see langword="true" /> when it is.
	/// </returns>
	private static bool IsList(string name, out string array) => DeclareCommand.TrySplitElement(name, out array, out var subscript) && subscript is "@" or "*";

	/// <summary>
	/// Expands <c>${!NAME[@]}</c> or <c>${!NAME[*]}</c>, which list an array's indexes.
	/// </summary>
	/// <param name="inside">The text between the braces.</param>
	/// <param name="variables">The shell variables.</param>
	/// <param name="quoted"><see langword="true" /> inside double quotes.</param>
	/// <param name="separators">What the words split on, or <see langword="null" /> where they aren't split.</param>
	/// <param name="joiner">What <c>*</c> joins the indexes with.</param>
	/// <param name="parts">The words, as <see cref="ListParts" /> gives them.</param>
	/// <returns>
	/// <see langword="true" /> when <paramref name="inside" /> is one of those forms.
	/// </returns>
	private static bool TryExpandIndexes(string inside, IReadOnlyDictionary<string, string> variables, bool quoted, string? separators, string joiner, out string[] parts)
	{
		parts = [];

		if (inside is not ['!', ..] || !IsList(inside[1..], out var array))
		{
			return false;
		}

		var indexes = DeclareCommand.Elements(variables, array).Select(element => element.Key.ToString(CultureInfo.InvariantCulture));

		parts = ListParts(indexes, star: inside[^2] == '*', quoted, separators, joiner);

		return true;
	}
}
