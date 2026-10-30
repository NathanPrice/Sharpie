using System.Globalization;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Features.Arithmetic;

namespace Sharpie.Shell.Common;

/// <content>
/// Arithmetic: finding where <c>$((...))</c> and <c>((...))</c> end, and expanding <c>$((...))</c>.
/// </content>
internal static partial class CommandLine
{
	/// <summary>
	/// Finds where the arithmetic that starts at <paramref name="index" /> ends.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">The position of a <c>$((</c> or <c>((</c>.</param>
	/// <returns>
	/// The position of the last <c>)</c> of the closing <c>))</c>, or -1 when nothing starts arithmetic at
	/// <paramref name="index" />, it never closes, or a <c>)</c> closes its first parenthesis alone, as in
	/// <c>$((echo hi) )</c>, which bash then reads as a command substitution and <c>((a); (b))</c> as subshells.
	/// </returns>
	/// <remarks>
	/// Parentheses outside quotes are counted. Quoted and escaped text, <c>${...}</c>, <c>$(...)</c> and backquotes are
	/// skipped.
	/// </remarks>
	internal static int ArithmeticEnd(string line, int index)
	{
		_ = line.ArgumentNotNull();

		var open = line.AsSpan(index).StartsWith("$((", StringComparison.Ordinal) ? 3
			: line.AsSpan(index).StartsWith("((", StringComparison.Ordinal) ? 2
			: 0;

		if (open == 0)
		{
			return -1;
		}

		var depth = 0;
		char? quote = null;

		for (var end = index + open; end < line.Length; end++)
		{
			var character = line[end];

			if (character == '\\')
			{
				end++;
			}
			else if (line.AsSpan(end).StartsWith("${", StringComparison.Ordinal) || IsSubstitutionStart(line, end))
			{
				end = character == '$' && line[end + 1] == '{' ? BraceEnd(line, end) : SubstitutionEnd(line, end);

				if (end < 0)
				{
					return -1;
				}
			}
			else if (quote == '"')
			{
				quote = character == '"' ? null : quote;
			}
			else if (character == '\'')
			{
				end = QuoteEnd(line, end);

				if (end < 0)
				{
					return -1;
				}
			}
			else if (character == '"')
			{
				quote = character;
			}
			else if (character == '(')
			{
				depth++;
			}
			else if (character == ')' && depth-- == 0)
			{
				return end + 1 < line.Length && line[end + 1] == ')' ? end + 1 : -1;
			}
		}

		return -1;
	}

	/// <summary>
	/// Expands an arithmetic expression as typed, for <see cref="ArithmeticExpression.Evaluate" />.
	/// </summary>
	/// <param name="expression">The text between the parentheses.</param>
	/// <param name="variables">The shell variables.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">Runs a command substitution, as <see cref="Split" /> describes.</param>
	/// <returns>
	/// The expression expanded as if it were inside double quotes, as bash's is: variables and substitutions expand,
	/// double quotes are removed, and nothing splits or matches file names, so <c>"1" + 2</c> gives <c>1 + 2</c> and
	/// <c>2*3</c> stays as it is.
	/// </returns>
	internal static string ExpandExpression(string expression, IReadOnlyDictionary<string, string> variables, Action<Word>? unset = null, Func<string, string>? substitute = null) =>
		ExpandText($"\"{expression}\"", variables, unset is null ? null : name => unset(name with { Start = name.Start - 1, End = name.End - 1 }), substitute);

	/// <summary>
	/// Expands the <c>$((...))</c> from <paramref name="start" /> to <paramref name="close" />.
	/// </summary>
	/// <param name="line">The text holding it.</param>
	/// <param name="start">The position of the <c>$((</c>.</param>
	/// <param name="close">The position of its last <c>)</c>.</param>
	/// <param name="variables">The shell variables.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">Runs a command substitution, as <see cref="Split" /> describes.</param>
	/// <returns>
	/// The value in decimal.
	/// </returns>
	/// <exception cref="ExpansionError">The expression can't be worked out, which skips the rest of the line.</exception>
	private static string ExpandArithmetic(string line, int start, int close, IReadOnlyDictionary<string, string> variables, Action<Word>? unset, Func<string, string>? substitute)
	{
		var inner = start + 3;

		try
		{
			var expanded = ExpandExpression(line[inner..(close - 1)], variables, unset is null ? null : name => unset(name with { Start = name.Start + inner, End = name.End + inner }), substitute);

			// The callback comes only under set -u.
			return ArithmeticExpression.Evaluate(expanded, variables, noUnset: unset is not null).ToString(CultureInfo.InvariantCulture);
		}
		catch (ArithmeticExpression.Failure failure)
		{
			throw new ExpansionError(failure.Error, fatal: false);
		}
	}
}
