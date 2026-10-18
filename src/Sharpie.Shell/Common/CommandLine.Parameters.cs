using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Common;

/// <summary>
/// The <c>${...}</c> forms: defaults, lengths, removing or replacing a pattern, and substrings.
/// </summary>
internal static partial class CommandLine
{
	/// <summary>
	/// The forms <c>${...}</c> reads, shown when it meets one it doesn't.
	/// </summary>
	private const string ParameterForms = "it reads ${X}, ${#X}, ${X:-d}, ${X:=d}, ${X:?msg}, ${X:+alt}, ${X#pat}, ${X%pat}, ${X/pat/text} and ${X:offset:length}";

	/// <summary>
	/// Finds the <c>}</c> that closes the <c>${</c> at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">The position of the <c>$</c>.</param>
	/// <param name="quoted">
	/// <see langword="true" /> inside double quotes, where a single quote in the <c>${...}</c> is an ordinary character.
	/// </param>
	/// <returns>
	/// The position of the <c>}</c>, or -1 when there is none.
	/// </returns>
	/// <remarks>
	/// Escaped and quoted text, nested <c>${...}</c>, <c>$(...)</c> and backquotes are skipped, so
	/// <c>${X:-${Y}}</c> and <c>${X:-"}"}</c> end at their last <c>}</c>, as in bash.
	/// </remarks>
	private static int BraceEnd(string line, int index, bool quoted) => UnquotedIndex(line, index + 2, "}", quoted);

	/// <summary>
	/// Finds the first of <paramref name="targets" /> that isn't quoted, escaped or inside a nested expansion.
	/// </summary>
	/// <param name="text">The text to look through.</param>
	/// <param name="start">Where to start looking, outside any quote.</param>
	/// <param name="targets">The characters to look for.</param>
	/// <param name="quoted">
	/// <see langword="true" /> when the text is inside double quotes, where a single quote is an ordinary character.
	/// </param>
	/// <returns>
	/// The position of the character, or -1 when there is none.
	/// </returns>
	private static int UnquotedIndex(string text, int start, ReadOnlySpan<char> targets, bool quoted)
	{
		char? quote = null;

		for (var index = start; index < text.Length; index++)
		{
			var character = text[index];

			if (quote == '\'')
			{
				quote = character == '\'' ? null : quote;
			}
			else if (character == '\\')
			{
				index++;
			}
			else if (text.AsSpan(index).StartsWith("${", StringComparison.Ordinal))
			{
				// Nothing after a nested ${ that never closes can close this one, and scanning on would go over the
				// nested ones again at every level.
				index = BraceEnd(text, index, quoted || quote is not null);

				if (index < 0)
				{
					return -1;
				}
			}
			else if (SubstitutionEnd(text, index) is var close and >= 0)
			{
				index = close;
			}
			else if (character == '"')
			{
				quote = quote is null ? '"' : null;
			}
			else if (quote is null && character == '\'' && !quoted)
			{
				quote = '\'';
			}
			else if (quote is null && targets.Contains(character))
			{
				return index;
			}
		}

		return -1;
	}

	/// <summary>
	/// Expands the <c>${...}</c> that starts at the <c>$</c> at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The command line being split.</param>
	/// <param name="index">The position of the <c>$</c>, with a <c>{</c> after it.</param>
	/// <param name="variables">The shell variables, positional parameters included.</param>
	/// <param name="quoted"><see langword="true" /> inside double quotes.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">Runs a command substitution in a word, as <see cref="Split" /> describes.</param>
	/// <param name="parts">
	/// The words it gives: one, one per positional parameter for <c>@</c> and <c>*</c>, or for an unquoted default such
	/// as <c>${X:-a b}</c>, one per word of the default.
	/// </param>
	/// <param name="patterns">
	/// For an unquoted default, each part as a pattern, with the wildcards that were quoted in the word behind a
	/// backslash; otherwise, <see langword="null" />.
	/// </param>
	/// <param name="end">The position of the closing <c>}</c>.</param>
	/// <returns>
	/// <see langword="false" /> when the <c>${</c> is never closed, so it stays as typed.
	/// </returns>
	/// <exception cref="ExpansionError">
	/// <c>${X:?}</c> finds <c>X</c> unset, or the <c>${...}</c> can't be expanded.
	/// </exception>
	/// <remarks>
	/// The name is a variable name, digits for a positional parameter, or one of <c>? # $ ! - @ *</c>, where
	/// <c>@</c> and <c>*</c> stand for the positional parameters joined by spaces. The word after an operator is
	/// expanded as a word is, and inside double quotes as if it were quoted too. A pattern's quoted wildcards match
	/// only themselves, and one from a variable stays a wildcard, as in bash.
	/// </remarks>
	private static bool TryExpandBraced(string line, int index, IReadOnlyDictionary<string, string> variables, bool quoted, Action<Word>? unset, Func<string, string>? substitute, out string[] parts, out string[]? patterns, out int end)
	{
		parts = [];
		patterns = null;
		end = BraceEnd(line, index, quoted);

		if (end < 0)
		{
			end = index;

			return false;
		}

		var inside = line[(index + 2)..end];
		var name = ReadName(inside, 0);

		// ${#} is the count and ${#NAME} the length, but ${#:-x} is $# with a default.
		var measured = name == "#" ? ReadName(inside, 1) : string.Empty;
		var length = measured.Length > 0 && measured.Length == inside.Length - 1;

		name = length ? measured : name;

		var operation = inside[((length ? 1 : 0) + name.Length)..];
		var positional = name is "@" or "*";
		var parameters = positional ? DeclareCommand.ReadPositional(variables) : null;
		var value = parameters is null ? Lookup(variables, name) : string.Join(' ', parameters);
		var expansion = new BracedExpansion(line, index, end, name, value, parameters, quoted, variables, unset, substitute);

		// The default forms decide what an unset name gives, so set -u leaves them alone, as bash does.
		if (!BracedExpansion.IsDefault(operation) && unset is not null && !positional && !IsSet(variables, name))
		{
			unset(new Word(name, IsOperator: false, index, end + 1));
		}

		parts = !length ? expansion.Apply(operation, out patterns)
			: [(parameters?.Count ?? Runes(value).Length).ToString(CultureInfo.InvariantCulture)];

		return true;
	}

	/// <summary>
	/// One <c>${...}</c> being expanded, with what its operator needs.
	/// </summary>
	/// <param name="Line">The text it's in, which an error shows.</param>
	/// <param name="Start">The position of its <c>$</c>.</param>
	/// <param name="Close">The position of its <c>}</c>.</param>
	/// <param name="Name">The name it reads, or an empty string when none starts it.</param>
	/// <param name="Value">The name's value: for <c>@</c> and <c>*</c>, the positional parameters joined by spaces.</param>
	/// <param name="Parameters">For <c>@</c> and <c>*</c>, the positional parameters; otherwise, <see langword="null" />.</param>
	/// <param name="Quoted"><see langword="true" /> inside double quotes.</param>
	/// <param name="Variables">The shell variables.</param>
	/// <param name="Unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="Substitute">Runs a command substitution, as <see cref="Split" /> describes.</param>
	private sealed record BracedExpansion(string Line, int Start, int Close, string Name, string Value, IReadOnlyList<string>? Parameters, bool Quoted, IReadOnlyDictionary<string, string> Variables, Action<Word>? Unset, Func<string, string>? Substitute)
	{
		/// <summary>
		/// Checks whether an operator is one of <c>-</c>, <c>=</c>, <c>?</c> or <c>+</c>, with or without a colon.
		/// </summary>
		/// <param name="operation">The text after the name.</param>
		/// <returns>
		/// <see langword="true" /> for a default form.
		/// </returns>
		public static bool IsDefault(string operation) =>
			operation is [':', '-' or '=' or '?' or '+', ..] or ['-' or '=' or '?' or '+', ..];

		/// <summary>
		/// Gets what the operator gives.
		/// </summary>
		/// <param name="operation">The text after the name, empty for <c>${NAME}</c>.</param>
		/// <param name="patterns">Each word as a pattern, for an unquoted default, as <see cref="TryExpandBraced" /> says.</param>
		/// <returns>
		/// The words.
		/// </returns>
		/// <exception cref="ExpansionError">The operator can't be applied.</exception>
		/// <remarks>
		/// For <c>@</c> and <c>*</c> the patterns apply to each parameter, as in bash.
		/// </remarks>
		public string[] Apply(string operation, out string[]? patterns)
		{
			patterns = null;

			return operation switch
			{
				_ when this.Name.Length == 0 || (this.Name is "@" or "*" && operation is [':', ..] && !IsDefault(operation)) => throw this.Unsupported(),
				"" => this.Map(value => value),
				[':', '-' or '=' or '?' or '+', .. var word] => this.Default(colon: true, operation[1], word, out patterns),
				['-' or '=' or '?' or '+', .. var word] => this.Default(colon: false, operation[0], word, out patterns),
				['#', '#', .. var pattern] => this.Remove(pattern, suffix: false, longest: true),
				['#', .. var pattern] => this.Remove(pattern, suffix: false, longest: false),
				['%', '%', .. var pattern] => this.Remove(pattern, suffix: true, longest: true),
				['%', .. var pattern] => this.Remove(pattern, suffix: true, longest: false),
				['/', '#' or '%', ..] => throw this.Unsupported(),
				['/', '/', .. var rest] => this.Replace(rest, all: true),
				['/', .. var rest] => this.Replace(rest, all: false),
				[':', .. var rest] => [this.Substring(rest)],
				_ => throw this.Unsupported(),
			};
		}

		/// <summary>
		/// Gets what <c>-</c>, <c>=</c>, <c>?</c> or <c>+</c> gives.
		/// </summary>
		/// <param name="colon"><see langword="true" /> when an empty value counts as unset.</param>
		/// <param name="form">The operator after the colon, if any.</param>
		/// <param name="word">The word after the operator, as typed.</param>
		/// <param name="patterns">Each word as a pattern, for an unquoted <c>-</c> or <c>+</c> word.</param>
		/// <returns>
		/// The value, or the word in its place.
		/// </returns>
		/// <exception cref="ExpansionError"><c>?</c> finds the name unset, or <c>=</c> a name that can't be assigned.</exception>
		private string[] Default(bool colon, char form, string word, out string[]? patterns)
		{
			var given = IsSet(this.Variables, this.Name) && !(colon && this.Value.Length == 0);

			patterns = null;

			switch (form)
			{
				case '-' or '=' or '?' when given:
					return this.Map(value => value);
				case '+' when !given:
					return [string.Empty];
				case '+' or '-' when this.Quoted:
					return [this.Text(word)];
				case '+' or '-':
					// Unquoted, the word splits as bash's does, though a variable's value never does here, and each word
					// keeps which of its wildcards were quoted.
					var words = this.Nested(unset => SplitWords(Protect(word, split: true), this.Variables, unset, this.Substitute, out _, WordForm.Spliced));

					patterns = [.. words.Select(part => part.Pattern!)];

					return [.. words.Select(part => part.Text)];
				case '=' when !DeclareCommand.IsIdentifier(this.Name):
					throw this.Failure("sharpie::shell::bad_substitution", fatal: false, $"${this.Name}: cannot assign in this way", "not a variable name", "only a variable name can be given a value this way");
				case '=':
					var assigned = this.Text(word);

					DeclareCommand.Assign(this.Variables, this.Name, assigned);

					return [assigned];
				default:
					var message = this.Text(word);

					throw this.Failure("sharpie::shell::parameter_unset", fatal: true, $"{this.Name}: {(message.Length > 0 ? message : colon ? "parameter null or not set" : "parameter not set")}", colon ? "empty or not set" : "not set", null);
			}
		}

		/// <summary>
		/// Expands a word that isn't split or matched against file names: a default, a message, a replacement or an
		/// offset.
		/// </summary>
		/// <param name="word">The word as typed.</param>
		/// <returns>
		/// The text, quotes removed.
		/// </returns>
		private string Text(string word)
		{
			// shortcut: double quotes inside a double-quoted ${...} word end the wrapping quotes, so their spaces collapse; give the word its own quote state if that shows up.
			var typed = this.Quoted ? $"\"{word}\"" : Protect(word, split: false);

			return this.Nested(unset => ExpandText(typed, this.Variables, unset, this.Substitute));
		}

		/// <summary>
		/// Expands a word into a pattern, which stays a pattern inside double quotes too, as in bash.
		/// </summary>
		/// <param name="word">The word as typed.</param>
		/// <returns>
		/// The pattern, for <see cref="Glob.IsMatch" />.
		/// </returns>
		private string Pattern(string word) =>
			this.Nested(unset => CommandLine.Pattern(Protect(word, split: false), this.Variables, unset, this.Substitute));

		/// <summary>
		/// Runs an expansion of a word inside the braces, so what it reports points at the whole <c>${...}</c> in
		/// <see cref="Line" /> rather than into the word.
		/// </summary>
		/// <typeparam name="T">What the expansion gives.</typeparam>
		/// <param name="expand">The expansion, given the callback for an unset variable.</param>
		/// <returns>
		/// What it gives.
		/// </returns>
		/// <exception cref="ExpansionError">A nested <c>${...}</c> can't expand.</exception>
		private T Nested<T>(Func<Action<Word>?, T> expand)
		{
			try
			{
				return expand(this.Unset is null ? null : name => this.Unset(name with { Start = this.Start, End = this.Close + 1 }));
			}
			catch (ExpansionError inner)
			{
				throw new ExpansionError(inner.Error with { Source = this.Line, Start = this.Start, End = this.Close + 1 }, inner.Fatal);
			}
		}

		/// <summary>
		/// Gives the value with <paramref name="change" /> made to it, or to each positional parameter for <c>@</c> and
		/// <c>*</c>.
		/// </summary>
		/// <param name="change">What to make of one value.</param>
		/// <returns>
		/// One word, or one per parameter, except <c>"${*...}"</c>, which joins them with spaces.
		/// </returns>
		private string[] Map(Func<string, string> change) =>
			this.Parameters is null ? [change(this.Value)]
			: this.Name == "*" && this.Quoted ? [string.Join(' ', this.Parameters.Select(change))]
			: [.. this.Parameters.Select(change)];

		/// <summary>
		/// Removes the shortest or longest start or end that a pattern matches, as <c>#</c>, <c>##</c>, <c>%</c> and
		/// <c>%%</c> do.
		/// </summary>
		/// <param name="typed">The pattern as typed.</param>
		/// <param name="suffix"><see langword="true" /> to remove from the end.</param>
		/// <param name="longest"><see langword="true" /> to remove the longest match.</param>
		/// <returns>
		/// The words, with the match removed.
		/// </returns>
		private string[] Remove(string typed, bool suffix, bool longest)
		{
			var pattern = this.Pattern(typed);

			return this.Map(value => CommandLine.Remove(value, pattern, suffix, longest));
		}

		/// <summary>
		/// Replaces the pattern before the first unquoted <c>/</c> of <paramref name="rest" /> with the text after it.
		/// </summary>
		/// <param name="rest">The text after <c>/</c> or <c>//</c>.</param>
		/// <param name="all"><see langword="true" /> to replace every match.</param>
		/// <returns>
		/// The words, with the matches replaced, or removed when no <c>/</c> gives a replacement.
		/// </returns>
		private string[] Replace(string rest, bool all)
		{
			var slash = UnquotedIndex(rest, 0, "/", quoted: false);
			var pattern = this.Pattern(slash < 0 ? rest : rest[..slash]);
			var replacement = slash < 0 ? string.Empty : this.Text(rest[(slash + 1)..]);

			return this.Map(value => ReplacePattern(value, pattern, replacement, all));
		}

		/// <summary>
		/// Takes a substring of the value, counted in runes as <c>${#NAME}</c> counts.
		/// </summary>
		/// <param name="rest">The offset, then optionally <c>:</c> and the length, as typed.</param>
		/// <returns>
		/// The substring. An offset past either end gives an empty string, as in bash.
		/// </returns>
		/// <exception cref="ExpansionError">The offset or length isn't a number, or the length ends before the offset.</exception>
		/// <remarks>
		/// A negative offset counts from the end, and a negative length is where to stop, counted from the end.
		/// </remarks>
		private string Substring(string rest)
		{
			var colon = UnquotedIndex(rest, 0, ":", quoted: false);
			var runes = Runes(this.Value);
			var start = this.Number(colon < 0 ? rest : rest[..colon], out _);

			start += start < 0 ? runes.Length : 0;

			if (start < 0 || start > runes.Length)
			{
				return string.Empty;
			}

			var stop = (long)runes.Length;

			if (colon >= 0)
			{
				var count = this.Number(rest[(colon + 1)..], out var typed);

				stop = count < 0 ? runes.Length + count : start + Math.Min(count, runes.Length - start);

				if (stop < start)
				{
					throw this.Failure("sharpie::shell::bad_substitution", fatal: false, $"{typed}: substring expression < 0", "ends before its offset", "a negative length counts back from the end, which has to stay after the offset");
				}
			}

			return string.Concat(runes[(int)start..(int)stop]);
		}

		/// <summary>
		/// Reads an offset or a length.
		/// </summary>
		/// <param name="word">The word as typed.</param>
		/// <param name="typed">The word expanded, for an error to show.</param>
		/// <returns>
		/// The whole number the word gives, or the one in the variable it names, 0 when that's empty.
		/// </returns>
		/// <exception cref="ExpansionError">The word gives anything else.</exception>
		private long Number(string word, out string typed)
		{
			typed = this.Text(word).Trim();

			// shortcut: offsets are plain integers or a variable name, no arithmetic; use the arithmetic evaluator once $(( )) exists.
			var number = DeclareCommand.IsIdentifier(typed) ? Lookup(this.Variables, typed) : typed;

			return number.Length == 0 ? 0
				: long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed
				: throw this.Failure("sharpie::shell::bad_substitution", fatal: false, $"`{typed}` isn't a whole number", "not a number", "Sharpie has no arithmetic yet, so an offset is a number or a variable holding one");
		}

		/// <summary>
		/// Makes the error for a form Sharpie doesn't read.
		/// </summary>
		/// <returns>
		/// The error.
		/// </returns>
		private ExpansionError Unsupported() =>
			this.Failure("sharpie::shell::bad_substitution", fatal: false, $"Sharpie can't expand `{this.Line[this.Start..(this.Close + 1)]}`", "not a form Sharpie reads", ParameterForms);

		/// <summary>
		/// Makes an error that points at the whole <c>${...}</c>.
		/// </summary>
		/// <param name="code">The error's code.</param>
		/// <param name="fatal"><see langword="true" /> to end a shell that isn't interactive, as <see cref="ExpansionError.Fatal" /> says.</param>
		/// <param name="summary">What went wrong.</param>
		/// <param name="label">The note under the <c>${...}</c>.</param>
		/// <param name="help">The hint, or <see langword="null" />.</param>
		/// <returns>
		/// The error.
		/// </returns>
		private ExpansionError Failure(string code, bool fatal, string summary, string label, string? help) =>
			new(new ShellError(code, summary) { Source = this.Line, Start = this.Start, End = this.Close + 1, Label = label, Help = help }, fatal);
	}

	/// <summary>
	/// Reads the name at the start of a <c>${...}</c>.
	/// </summary>
	/// <param name="text">The text between the braces.</param>
	/// <param name="start">Where the name starts.</param>
	/// <returns>
	/// A variable name, all the digits of a positional parameter, one of <c>? # $ ! - @ *</c>, or an empty string
	/// when no name starts there.
	/// </returns>
	private static string ReadName(string text, int start)
	{
		if (start >= text.Length)
		{
			return string.Empty;
		}

		var first = text[start];

		if (first is '?' or '#' or '$' or '!' or '-' or '@' or '*')
		{
			return first.ToString();
		}

		var digits = char.IsAsciiDigit(first);

		if (!digits && !char.IsAsciiLetter(first) && first != '_')
		{
			return string.Empty;
		}

		var stop = start + 1;

		while (stop < text.Length && (char.IsAsciiDigit(text[stop]) || (!digits && (char.IsAsciiLetter(text[stop]) || text[stop] == '_'))))
		{
			stop++;
		}

		return text[start..stop];
	}

	/// <summary>
	/// Checks whether a variable counts as set, for <c>set -u</c> and the default forms.
	/// </summary>
	/// <param name="variables">The shell variables.</param>
	/// <param name="name">The name.</param>
	/// <returns>
	/// <see langword="true" /> when it's a shell or environment variable, or <c>?</c>, which is always set though it
	/// isn't stored until the first command has run. <c>@</c> and <c>*</c> are set when there's a positional parameter.
	/// </returns>
	private static bool IsSet(IReadOnlyDictionary<string, string> variables, string name) => name switch
	{
		"?" => true,
		"@" or "*" => DeclareCommand.ReadPositional(variables).Count > 0,
		_ => variables.ContainsKey(name) || Environment.GetEnvironmentVariable(name) is not null,
	};

	/// <summary>
	/// Puts a backslash before each unquoted character of a <c>${...}</c> word that <see cref="SplitWords" /> would
	/// read as an operator or a comment, and before each blank unless <paramref name="split" />.
	/// </summary>
	/// <param name="word">The word as typed.</param>
	/// <param name="split"><see langword="true" /> to let blanks split the word.</param>
	/// <returns>
	/// The word, which <see cref="SplitWords" /> then reads as one piece of text, as bash reads it.
	/// </returns>
	private static string Protect(string word, bool split)
	{
		var text = new StringBuilder(word.Length);
		var targets = split ? "|&;<>()#" : "|&;<>()# \t\n";
		var from = 0;

		for (var index = UnquotedIndex(word, 0, targets, quoted: false); index >= 0; index = UnquotedIndex(word, index + 1, targets, quoted: false))
		{
			_ = text.Append(word, from, index - from).Append('\\');
			from = index;
		}

		return text.Append(word, from, word.Length - from).ToString();
	}

	/// <summary>
	/// Gets the characters of <paramref name="value" /> as bash counts them in a UTF-8 locale.
	/// </summary>
	/// <param name="value">The text.</param>
	/// <returns>
	/// One <see cref="Rune" /> per Unicode character, so an emoji counts once.
	/// </returns>
	private static Rune[] Runes(string value) => [.. value.EnumerateRunes()];

	/// <summary>
	/// Removes the shortest or longest start or end of <paramref name="value" /> that <paramref name="pattern" />
	/// matches, as <c>#</c>, <c>##</c>, <c>%</c> and <c>%%</c> do.
	/// </summary>
	/// <param name="value">The variable's value.</param>
	/// <param name="pattern">The pattern, for <see cref="Glob.IsMatch" />.</param>
	/// <param name="suffix"><see langword="true" /> to remove from the end.</param>
	/// <param name="longest"><see langword="true" /> to remove the longest match.</param>
	/// <returns>
	/// The value without the match, or the whole value when nothing matches.
	/// </returns>
	private static string Remove(string value, string pattern, bool suffix, bool longest)
	{
		for (var step = 0; step <= value.Length; step++)
		{
			// The shortest start and the longest end both come from cuts nearest the start.
			var cut = suffix == longest ? step : value.Length - step;

			if (suffix ? Glob.IsMatch(value.AsSpan(cut), pattern) : Glob.IsMatch(value.AsSpan(0, cut), pattern))
			{
				return suffix ? value[..cut] : value[cut..];
			}
		}

		return value;
	}

	/// <summary>
	/// Replaces the first match of <paramref name="pattern" /> in <paramref name="value" />, or each one, as
	/// <c>/</c> and <c>//</c> do.
	/// </summary>
	/// <param name="value">The variable's value.</param>
	/// <param name="pattern">The pattern, for <see cref="Glob.IsMatch" />.</param>
	/// <param name="replacement">The text each match becomes.</param>
	/// <param name="all"><see langword="true" /> to replace every match.</param>
	/// <returns>
	/// The value with the matches replaced. An empty pattern changes nothing.
	/// </returns>
	/// <remarks>
	/// Each match is the longest one at the earliest position, and the search goes on after it, as in bash.
	/// </remarks>
	private static string ReplacePattern(string value, string pattern, string replacement, bool all)
	{
		if (pattern.Length == 0)
		{
			return value;
		}

		if (value.Length == 0)
		{
			return Glob.IsMatch(value, pattern) ? replacement : value;
		}

		var text = new StringBuilder(value.Length);
		var start = 0;

		while (start < value.Length)
		{
			if (MatchEnd(value, start, pattern) is var stop and >= 0)
			{
				_ = text.Append(replacement);
				start = stop;

				if (!all)
				{
					break;
				}
			}
			else
			{
				_ = text.Append(value[start++]);
			}
		}

		return text.Append(value, start, value.Length - start).ToString();
	}

	/// <summary>
	/// Finds the longest match of <paramref name="pattern" /> that starts at <paramref name="start" />.
	/// </summary>
	/// <param name="value">The text to look in.</param>
	/// <param name="start">Where the match starts.</param>
	/// <param name="pattern">The pattern.</param>
	/// <returns>
	/// Where the match ends, or -1 when no text of one character or more there matches.
	/// </returns>
	private static int MatchEnd(string value, int start, string pattern)
	{
		// With no * each character matches one element of the pattern, so no match is longer than the pattern. With
		// one, a test that any start of the rest matches spares the test per end at each position with no match.
		var star = pattern.Contains('*', StringComparison.Ordinal);

		if (star && !Glob.IsMatch(value.AsSpan(start), pattern + "*"))
		{
			return -1;
		}

		for (var stop = star ? value.Length : Math.Min(value.Length, start + pattern.Length); stop > start; stop--)
		{
			if (Glob.IsMatch(value.AsSpan(start, stop - start), pattern))
			{
				return stop;
			}
		}

		return -1;
	}

	/// <summary>
	/// A <c>${...}</c> that can't expand, like <c>${X:?}</c> with <c>X</c> unset, which stops the command.
	/// </summary>
	/// <param name="error">What went wrong, pointing at the <c>${...}</c>.</param>
	/// <param name="fatal">
	/// <see langword="true" /> for <c>${X:?}</c>, which ends a shell that isn't interactive with 127, as bash's does.
	/// </param>
	[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only expansion throws it, and always with a report.")]
	internal sealed class ExpansionError(ShellError error, bool fatal) : Exception(error.ArgumentNotNull().Summary)
	{
		/// <summary>
		/// Gets the report.
		/// </summary>
		/// <value>
		/// What went wrong.
		/// </value>
		public ShellError Error => error;

		/// <summary>
		/// Gets a value that indicates whether the error ends a shell that isn't interactive.
		/// </summary>
		/// <value>
		/// <see langword="true" /> for <c>${X:?}</c>; otherwise, <see langword="false" />, and only the rest of the line is
		/// skipped, as in bash.
		/// </value>
		public bool Fatal => fatal;
	}
}
