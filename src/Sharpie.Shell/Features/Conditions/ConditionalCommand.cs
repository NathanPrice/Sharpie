using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Arithmetic;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Conditions;

/// <summary>
/// Runs a <c>[[ ... ]]</c> condition, as bash's <c>execute_cond_command</c> does.
/// </summary>
/// <remarks>
/// <c>||</c> binds looser than <c>&amp;&amp;</c>, which binds looser than <c>!</c>, and <c>( )</c> groups. Each test
/// is a unary operator and its word, a word, a binary operator and a word, or a lone word, which holds when it isn't
/// empty. The right side of <c>==</c>, <c>=</c> and <c>!=</c> is a pattern, so <c>[[ $x == a* ]]</c> checks that
/// <c>x</c> starts with <c>a</c> and a quoted <c>"a*"</c> only matches itself. <c>=~</c> matches a POSIX extended
/// regular expression anywhere in the word. <c>&lt;</c> and <c>&gt;</c> sort in the locale's order. The number
/// operators read each word as an arithmetic expression, as bash does, so <c>1+1</c> is 2. A test that <c>&amp;&amp;</c> or <c>||</c> skips doesn't run, so it can't fail, though
/// the substitutions in it have already run. A <c>=~</c> sets the array <c>BASH_REMATCH</c> to the match and its
/// groups, or to no elements when it doesn't match, as in bash. <c>-o OPTION</c>, which checks a <c>set</c> option,
/// isn't there. No redirect may follow <c>]]</c>.
/// </remarks>
internal static class ConditionalCommand
{
	/// <summary>
	/// The word that starts a condition, which <see cref="CommandLine.Split" /> gives as an operator.
	/// </summary>
	internal const string Open = "[[";

	/// <summary>
	/// The word that ends it.
	/// </summary>
	internal const string Close = "]]";

	/// <summary>
	/// How long one <c>=~</c> may take before it gives up, so a pattern that backtracks without end can't hang the
	/// shell.
	/// </summary>
	private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

	/// <summary>
	/// Runs the condition.
	/// </summary>
	/// <param name="words">The command's words, starting with the <see cref="Open" /> operator.</param>
	/// <param name="line">The command as typed, which errors point into.</param>
	/// <param name="variables">The shell variables, for <c>-v</c> and the number operators.</param>
	/// <param name="noUnset">
	/// <see langword="true" /> under <c>set -u</c>, where a number operator reading a name that isn't set throws
	/// <see cref="CommandLine.ExpansionError" />, as in bash.
	/// </param>
	/// <returns>
	/// 0 when the condition holds, 1 when it doesn't, and <see cref="TestCommand.ErrorStatus" /> after an error is
	/// reported: no <c>]]</c>, a word after it, a test that's missing a word, or a regular expression or number that
	/// can't be read.
	/// </returns>
	internal static int Run(Word[] words, string line, IReadOnlyDictionary<string, string> variables, bool noUnset = false)
	{
		_ = words.ArgumentNotNull();

		var close = Array.FindIndex(words, word => word is { IsOperator: true, Text: Close });

		try
		{
			// Stryker disable once Equality : words[0] is always the [[, so ]] is never at 0.
			return close < 0 ? throw Syntax(line, words[0], "missing_bracket", "`[[` is missing its closing `]]`", "starts a condition", "end the condition with ]]")
				: close + 1 < words.Length
				? throw Syntax(line, words[close + 1], "after_bracket", $"`{words[close + 1].Text}` comes after the condition's end", "after `]]`", "put ; or && between the condition and the next command")
				: new Parser(words[1..close], words[close], line, variables, noUnset).Run() ? 0 : 1;
		}
		catch (Condition.Failure failure)
		{
			failure.Error.Write();

			return TestCommand.ErrorStatus;
		}
	}

	/// <summary>
	/// Turns a POSIX extended regular expression into one .NET reads the same way.
	/// </summary>
	/// <param name="pattern">The expression.</param>
	/// <returns>
	/// The expression with each bracket expression and <c>$</c> rewritten, or <see langword="null" /> when a bracket
	/// expression names a class that doesn't exist. It's meant to run with <see cref="RegexOptions.Singleline" />, so
	/// <c>.</c> matches a newline as in POSIX.
	/// </returns>
	/// <remarks>
	/// .NET would read <c>[[:digit:]]</c> as a set of <c>[</c>, <c>:</c> and the letters of <c>digit</c>, followed by a
	/// <c>]</c>, without an error, so each POSIX class becomes the .NET ranges for it. A backslash or <c>[</c> inside
	/// the brackets is a plain character in POSIX, so it's escaped. A <c>$</c> becomes <c>\z</c>, since .NET's
	/// <c>$</c> also matches before a newline at the end. The rest is kept as is.
	/// </remarks>
	internal static string? ToDotNetPattern(string pattern)
	{
		_ = pattern.ArgumentNotNull();

		var result = new StringBuilder(pattern.Length);

		for (var index = 0; index < pattern.Length; index++)
		{
			if (pattern[index] == '\\' && index + 1 < pattern.Length)
			{
				_ = result.Append(pattern, index++, 2);
			}
			else if (pattern[index] != '[')
			{
				_ = pattern[index] == '$' ? result.Append(@"\z") : result.Append(pattern[index]);
			}
			else if (AppendBracket(pattern, ref index, result) is false)
			{
				return null;
			}
		}

		return result.ToString();
	}

	/// <summary>
	/// Rewrites the bracket expression that starts at <paramref name="index" />.
	/// </summary>
	/// <param name="pattern">The expression.</param>
	/// <param name="index">The position of the <c>[</c>, moved to the closing <c>]</c>.</param>
	/// <param name="result">Receives the .NET form.</param>
	/// <returns>
	/// <see langword="false" /> for a class that doesn't exist. An unclosed <c>[</c> is copied as is, for .NET to report.
	/// </returns>
	private static bool AppendBracket(string pattern, ref int index, StringBuilder result)
	{
		var set = new StringBuilder("[");
		var at = index + 1;

		if (at < pattern.Length && pattern[at] == '^')
		{
			_ = set.Append('^');
			at++;
		}

		for (var first = true; at < pattern.Length && (first || pattern[at] != ']'); first = false, at++)
		{
			if (pattern.AsSpan(at).StartsWith("[:") && pattern.IndexOf(":]", at + 2, StringComparison.Ordinal) is var end and >= 0)
			{
				if (ClassRanges(pattern[(at + 2)..end]) is not { } ranges)
				{
					return false;
				}

				_ = set.Append(ranges);
				at = end + 1;
			}
			else
			{
				_ = pattern[at] is '\\' or '[' or ']' ? set.Append('\\').Append(pattern[at]) : set.Append(pattern[at]);
			}
		}

		if (at == pattern.Length)
		{
			_ = result.Append(pattern[index]);

			return true;
		}

		_ = result.Append(set).Append(']');
		index = at;

		return true;
	}

	/// <summary>
	/// Gets the .NET ranges for a POSIX character class, the same characters <see cref="Glob" /> puts in it.
	/// </summary>
	/// <param name="name">The class name, such as <c>digit</c>.</param>
	/// <returns>
	/// The ranges, to go inside a .NET set, or <see langword="null" /> for a name that isn't a class.
	/// </returns>
	private static string? ClassRanges(string name) => name switch
	{
		"alpha" => @"\p{L}",
		"digit" => "0-9",
		"alnum" => @"\p{L}\p{Nd}",
		"upper" => @"\p{Lu}",
		"lower" => @"\p{Ll}",
		"space" => @"\s",
		"blank" => @" \t",
		"punct" => @"\p{P}\p{S}",
		"xdigit" => "0-9A-Fa-f",
		"cntrl" => @"\p{Cc}",
		"graph" => @"\p{L}\p{M}\p{N}\p{P}\p{S}",
		"print" => @"\P{Cc}",
		_ => null,
	};

	/// <summary>
	/// Builds the error for a condition that can't be read, pointing at the word that's wrong.
	/// </summary>
	/// <param name="line">The command as typed.</param>
	/// <param name="word">The word to point at.</param>
	/// <param name="code">The last part of the error's code.</param>
	/// <param name="summary">What's wrong.</param>
	/// <param name="label">The text under the word.</param>
	/// <param name="help">How to fix it.</param>
	/// <returns>
	/// The error to throw.
	/// </returns>
	private static Condition.Failure Syntax(string line, Word word, string code, string summary, string label, string help) =>
		new(new ShellError($"sharpie::condition::{code}", summary) { Source = line, Start = word.Start, End = word.End, Label = label, Help = help });

	/// <summary>
	/// Reads and runs the words between <c>[[</c> and <c>]]</c>, from left to right.
	/// </summary>
	/// <param name="words">The words.</param>
	/// <param name="close">The <c>]]</c>, which an error about a missing word points at.</param>
	/// <param name="line">The command as typed.</param>
	/// <param name="variables">The shell variables.</param>
	/// <param name="noUnset"><see langword="true" /> under <c>set -u</c>.</param>
	private sealed class Parser(Word[] words, Word close, string line, IReadOnlyDictionary<string, string> variables, bool noUnset)
	{
		/// <summary>
		/// The word being read.
		/// </summary>
		private int position;

		/// <summary>
		/// Runs the whole condition.
		/// </summary>
		/// <returns>
		/// Whether it holds.
		/// </returns>
		/// <exception cref="Condition.Failure">The words can't be read, or a test can't run.</exception>
		public bool Run()
		{
			var value = this.Or(run: true);

			return this.position < words.Length
				? throw Syntax(line, words[this.position], "unexpected_word", $"`{words[this.position].Text}` doesn't belong to any test", "expected && or || before this", "join the tests with && or ||, or quote a word that has spaces")
				: value;
		}

		/// <summary>
		/// Reads tests joined by <c>||</c>.
		/// </summary>
		/// <param name="run"><see langword="false" /> to only read the tests, when they're skipped.</param>
		/// <returns>
		/// Whether any holds.
		/// </returns>
		private bool Or(bool run)
		{
			var value = this.And(run);

			while (this.Next("||"))
			{
				// Stryker disable once Assignment : a skipped And gives false, so ^= and |= agree.
				value |= this.And(run && !value);
			}

			return value;
		}

		/// <summary>
		/// Reads tests joined by <c>&amp;&amp;</c>.
		/// </summary>
		/// <param name="run"><see langword="false" /> to only read the tests.</param>
		/// <returns>
		/// Whether all hold.
		/// </returns>
		private bool And(bool run)
		{
			var value = this.Term(run);

			while (this.Next("&&"))
			{
				value &= this.Term(run && value);
			}

			return value;
		}

		/// <summary>
		/// Reads one test: <c>! test</c>, <c>( tests )</c>, a unary or binary test, or a lone word.
		/// </summary>
		/// <param name="run"><see langword="false" /> to only read it.</param>
		/// <returns>
		/// Whether it holds, or <see langword="false" /> when it didn't run.
		/// </returns>
		private bool Term(bool run)
		{
			var word = this.Take("a test", "add a test, like -f file or $x == y", operand: false);

			if (word is { IsOperator: true, Text: "(" })
			{
				var value = this.Or(run);

				return this.Next(")") ? value : throw Syntax(line, word, "unclosed_group", "`(` is never closed", "opens a group", "add a ) after the tests it groups");
			}

			if (word is { IsOperator: false, Text: "!" })
			{
				return !this.Term(run) && run;
			}

			if (word.IsOperator)
			{
				throw Syntax(line, word, "unexpected_operator", $"`{word.Text}` can't start a test", "expected a word here", "quote it to compare it as text");
			}

			if (Condition.IsUnary(word.Text))
			{
				var operand = this.Take($"the word `{word.Text}` checks", $"put a word after {word.Text}");

				return run && Condition.Unary(word.Text, operand.Text, variables);
			}

			if (this.position < words.Length && words[this.position] is var middle && IsBinary(middle))
			{
				this.position++;

				var right = this.Take($"the word `{middle.Text}` compares with", $"put a word after {middle.Text}, or quote it to compare it as text");

				return run && this.Binary(word, middle, right);
			}

			return run && word.Text.Length > 0;
		}

		/// <summary>
		/// Checks whether a word is a binary operator of <c>[[</c>.
		/// </summary>
		/// <param name="word">The word after a test's first word.</param>
		/// <returns>
		/// <see langword="true" /> for <c>&lt;</c> and <c>&gt;</c> as operators, and <c>=~</c> and the operators of
		/// <c>test</c> as words.
		/// </returns>
		private static bool IsBinary(Word word) =>
			word.IsOperator ? word.Text is "<" or ">" : word.Text is "=~" || (Condition.IsBinary(word.Text) && word.Text is not ("<" or ">"));

		/// <summary>
		/// Runs a binary test.
		/// </summary>
		/// <param name="left">The word before the operator.</param>
		/// <param name="middle">The operator.</param>
		/// <param name="right">The word after it.</param>
		/// <returns>
		/// Whether it holds.
		/// </returns>
		private bool Binary(Word left, Word middle, Word right)
		{
			try
			{
				return middle.Text switch
				{
					"==" or "=" => Glob.IsMatch(left.Text, right.Pattern ?? right.Text),
					"!=" => !Glob.IsMatch(left.Text, right.Pattern ?? right.Text),
					"=~" => this.Matches(left.Text, right),
					"<" or ">" or "-nt" or "-ot" or "-ef" => Condition.Binary(left.Text, middle.Text, right.Text, StringComparer.CurrentCulture),
					_ => Condition.Binary(this.Number(left.Text), middle.Text, this.Number(right.Text), StringComparer.CurrentCulture),
				};
			}
			catch (Condition.Failure failure) when (failure.Error.Source is null)
			{
				throw new Condition.Failure(failure.Error with { Source = line, Start = left.Start, End = right.End, Label = "compares whole numbers" });
			}
		}

		/// <summary>
		/// Reads a word a number operator compares, as an arithmetic expression, as bash does.
		/// </summary>
		/// <param name="text">The word.</param>
		/// <returns>
		/// The number the expression gives, in decimal; 0 for an empty word.
		/// </returns>
		/// <exception cref="Condition.Failure">The expression can't be worked out.</exception>
		private string Number(string text)
		{
			try
			{
				return ArithmeticExpression.Evaluate(text, variables, noUnset).ToString(CultureInfo.InvariantCulture);
			}
			catch (ArithmeticExpression.Failure failure)
			{
				throw new Condition.Failure(failure.Error);
			}
		}

		/// <summary>
		/// Matches a word against the regular expression after <c>=~</c>.
		/// </summary>
		/// <param name="text">The word before <c>=~</c>.</param>
		/// <param name="pattern">The word after it, whose <see cref="Word.Pattern" /> has its quoted text escaped.</param>
		/// <returns>
		/// <see langword="true" /> when the expression matches anywhere in the word. <c>BASH_REMATCH</c> gets the match,
		/// then each group, empty for a group that matched nothing.
		/// </returns>
		/// <exception cref="Condition.Failure">The expression can't be read, or matching takes too long.</exception>
		private bool Matches(string text, Word pattern)
		{
			var invalid = Syntax(line, pattern, "invalid_pattern", $"`{pattern.Text}` isn't a valid regular expression", "a POSIX extended regular expression", "check its brackets, classes and parentheses, or quote it to match it as text");

			if (ToDotNetPattern(pattern.Pattern ?? Regex.Escape(pattern.Text)) is not { } expression)
			{
				throw invalid;
			}

			try
			{
				var match = Regex.Match(text, expression, RegexOptions.CultureInvariant | RegexOptions.Singleline, MatchTimeout);

				DeclareCommand.AssignArray(variables, "BASH_REMATCH", match.Success ? [.. match.Groups.Values.Select(group => group.Value)] : []);

				return match.Success;
			}
			catch (ArgumentException)
			{
				throw invalid;
			}
			catch (RegexMatchTimeoutException)
			{
				throw Syntax(line, pattern, "pattern_timeout", string.Create(CultureInfo.InvariantCulture, $"Matching `{pattern.Text}` took more than {MatchTimeout.TotalSeconds} seconds"), "backtracks too much", "make the pattern simpler, such as with fewer nested repeats");
			}
		}

		/// <summary>
		/// Moves past the word being read when it's the operator <paramref name="expected" />.
		/// </summary>
		/// <param name="expected">The operator to look for.</param>
		/// <returns>
		/// <see langword="true" /> when it was there.
		/// </returns>
		private bool Next(string expected)
		{
			var found = this.position < words.Length && words[this.position] is { IsOperator: true } word && word.Text == expected;

			this.position += found ? 1 : 0;

			return found;
		}

		/// <summary>
		/// Takes the word being read, which a test needs.
		/// </summary>
		/// <param name="what">What the word is for, for the error when there's none.</param>
		/// <param name="help">How to fix that.</param>
		/// <returns>
		/// The word.
		/// </returns>
		/// <exception cref="Condition.Failure">The condition ends here.</exception>
		/// <param name="operand">
		/// <see langword="true" /> when an operator can't stand in for the word, as after <c>-f</c> or <c>==</c>.
		/// </param>
		private Word Take(string what, string help, bool operand = true) =>
			this.position < words.Length && !(operand && words[this.position].IsOperator)
				? words[this.position++]
				: throw Syntax(line, this.position < words.Length ? words[this.position] : close, "missing_word", $"The condition is missing {what}", "expected a word before this", help);
	}
}
