using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Features.Arithmetic;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Common;

/// <summary>
/// The <c>${...}</c> forms: defaults, lengths, indirection, removing or replacing a pattern, case changes and
/// substrings.
/// </summary>
internal static partial class CommandLine
{
	/// <summary>
	/// The forms <c>${...}</c> reads, shown when it meets one it doesn't.
	/// </summary>
	private const string ParameterForms = "it reads ${X}, ${#X}, ${!X}, ${X:-d}, ${X:=d}, ${X:?msg}, ${X:+alt}, ${X#pat}, ${X%pat}, ${X/pat/text}, ${X/#pat/text}, ${X/%pat/text}, ${X^pat}, ${X,pat}, ${X:offset:length} and ${@:offset:length}";

	/// <summary>
	/// Finds the <c>}</c> that closes the <c>${</c> at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">The position of the <c>$</c>.</param>
	/// <returns>
	/// The position of the <c>}</c>, or -1 when there is none.
	/// </returns>
	/// <remarks>
	/// Escaped and quoted text, nested <c>${...}</c>, <c>$(...)</c> and backquotes are skipped, so
	/// <c>${X:-${Y}}</c> and <c>${X:-"}"}</c> end at their last <c>}</c>, as in bash. Single quotes pair up inside
	/// double quotes too, so <c>"${X:+'}'}"</c> ends at its last <c>}</c> and <c>"${X:-it's}"</c> never closes.
	/// </remarks>
	internal static int BraceEnd(string line, int index) => UnquotedIndex(line, index + 2, "}");

	/// <summary>
	/// Finds the first of <paramref name="targets" /> that isn't quoted, escaped or inside a nested expansion.
	/// </summary>
	/// <param name="text">The text to look through.</param>
	/// <param name="start">Where to start looking, outside any quote.</param>
	/// <param name="targets">The characters to look for.</param>
	/// <returns>
	/// The position of the character, or -1 when there is none.
	/// </returns>
	internal static int UnquotedIndex(string text, int start, ReadOnlySpan<char> targets)
	{
		char? quote = null;

		for (var index = start; index < text.Length; index++)
		{
			var character = text[index];

			if (character == '\\')
			{
				index++;
			}
			else if (text.AsSpan(index).StartsWith("${", StringComparison.Ordinal))
			{
				// Nothing after a nested ${ that never closes can close this one, and scanning on would go over the
				// nested ones again at every level.
				index = BraceEnd(text, index);

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
			else if (quote is null && character == '\'')
			{
				// Nothing after a quote that never closes is outside it.
				index = QuoteEnd(text, index);

				if (index < 0)
				{
					return -1;
				}
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
	/// <param name="separators">
	/// What an unquoted default's word splits on, or <see langword="null" /> where it isn't split, as
	/// <see cref="TryExpand" /> says.
	/// </param>
	/// <param name="joiner">What <c>*</c> joins the parameters with, as <see cref="ListParts" /> says.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">Runs a command substitution in a word, as <see cref="Split" /> describes.</param>
	/// <param name="parts">
	/// The words it gives: one, one per positional parameter for <c>@</c> and <c>*</c>, or for an unquoted default such
	/// as <c>${X:-a b}</c>, one per word of the default.
	/// </param>
	/// <param name="quoting">
	/// For an unquoted default, which characters of each part were quoted, as <see cref="WordForm.Value" /> gives
	/// them; otherwise, <see langword="null" />.
	/// </param>
	/// <param name="end">The position of the closing <c>}</c>.</param>
	/// <returns>
	/// <see langword="false" /> when the <c>${</c> is never closed, so it stays as typed.
	/// </returns>
	/// <exception cref="ExpansionError">
	/// <c>${X:?}</c> finds <c>X</c> unset, <c>${!n}</c> finds no name in <c>n</c>, or the <c>${...}</c> can't be
	/// expanded.
	/// </exception>
	/// <remarks>
	/// The name is a variable name, digits for a positional parameter, or one of <c>? # $ ! - @ *</c>, where
	/// <c>@</c> and <c>*</c> stand for the positional parameters joined by spaces. A name can take a subscript, as in
	/// <c>${a[i]}</c>, <c>${a[@]}</c> and <c>${#a[@]}</c>, and <c>${!a[@]}</c> lists an array's indexes. A <c>!</c>
	/// before the name reads the parameter its value names instead, as in bash. The word after an operator is
	/// expanded as a word is, and inside double quotes as if it were quoted too. A pattern's quoted wildcards match
	/// only themselves, and one from a variable stays a wildcard, as in bash.
	/// </remarks>
	private static bool TryExpandBraced(string line, int index, IReadOnlyDictionary<string, string> variables, bool quoted, string? separators, string joiner, Action<Word>? unset, Func<string, string>? substitute, out string[] parts, out string[]? quoting, out int end)
	{
		parts = [];
		quoting = null;
		end = BraceEnd(line, index);

		if (end < 0)
		{
			end = index;

			return false;
		}

		var inside = line[(index + 2)..end];

		if (TryExpandIndexes(inside, variables, quoted, separators, joiner, out parts))
		{
			return true;
		}

		// ${!n} reads the variable n names, then goes on as ${name...} would; ${!} alone is $!, and ${!prefix*} isn't read.
		if (inside is ['!', _, ..] && ReadName(inside, 1) is { Length: > 0 } pointer and not ("@" or "*") && inside[(1 + pointer.Length)..] is not ("@" or "*"))
		{
			var target = Lookup(variables, pointer);

			if (target.Length == 0 || ReadName(target, 0) != target)
			{
				var summary = IsSet(variables, pointer) ? $"{target}: invalid variable name" : $"{pointer}: invalid indirect expansion";

				throw new ExpansionError(new ShellError("sharpie::shell::bad_substitution", summary) { Source = line, Start = index, End = end + 1, Label = $"`{pointer}` doesn't hold a name", Help = "${!n} needs n to hold a variable name or a parameter number" }, fatal: false);
			}

			inside = target + inside[(1 + pointer.Length)..];
		}

		var name = ReadName(inside, 0);

		// ${#} is the count and ${#NAME} the length, but ${#:-x} is $# with a default. A subscript belongs to the name.
		var measured = name == "#" ? ReadName(inside, 1) : string.Empty;

		measured += SubscriptText(inside, 1 + measured.Length, measured);

		var length = measured.Length > 0 && measured.Length == inside.Length - 1;

		name = length ? measured : name + SubscriptText(inside, name.Length, name);

		var operation = inside[((length ? 1 : 0) + name.Length)..];

		var close = end;
		Action<Word>? spanned = unset is null ? null : missing => unset(missing with { Start = index, End = close + 1 });

		name = ResolveSubscript(name, variables, spanned, substitute);

		var parameters = name is "@" or "*" ? DeclareCommand.ReadPositional(variables) : ListedElements(variables, name);
		var value = parameters is null ? Lookup(variables, name) : string.Join(' ', parameters);
		var expansion = new BracedExpansion(line, index, end, name, value, parameters, quoted, separators, joiner, variables, unset, substitute);

		// The default forms decide what an unset name gives, so set -u leaves them alone, as bash does.
		if (!BracedExpansion.IsDefault(operation) && unset is not null && parameters is null && !IsSet(variables, name))
		{
			unset(new Word(name, IsOperator: false, index, end + 1));
		}

		parts = !length ? expansion.Apply(operation, out quoting)
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
	/// <param name="Separators">What an unquoted default's word splits on, or <see langword="null" /> where it isn't split.</param>
	/// <param name="Joiner">What <c>*</c> joins the parameters with, as <see cref="ListParts" /> says.</param>
	/// <param name="Variables">The shell variables.</param>
	/// <param name="Unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="Substitute">Runs a command substitution, as <see cref="Split" /> describes.</param>
	private sealed record BracedExpansion(string Line, int Start, int Close, string Name, string Value, IReadOnlyList<string>? Parameters, bool Quoted, string? Separators, string Joiner, IReadOnlyDictionary<string, string> Variables, Action<Word>? Unset, Func<string, string>? Substitute)
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
		/// Gets a value indicating whether the name is <c>*</c> or <c>NAME[*]</c>, whose words join into one inside
		/// double quotes.
		/// </summary>
		/// <value>
		/// <see langword="true" /> for those names.
		/// </value>
		private bool Star => this.Name is "*" || this.Name.EndsWith("[*]", StringComparison.Ordinal);

		/// <summary>
		/// Gets what the operator gives.
		/// </summary>
		/// <param name="operation">The text after the name, empty for <c>${NAME}</c>.</param>
		/// <param name="quoting">Which characters of each word were quoted, for an unquoted default, as <see cref="TryExpandBraced" /> says.</param>
		/// <returns>
		/// The words.
		/// </returns>
		/// <exception cref="ExpansionError">The operator can't be applied.</exception>
		/// <remarks>
		/// For <c>@</c> and <c>*</c> the patterns and case changes apply to each parameter, and <c>:offset:length</c>
		/// picks parameters, as in bash.
		/// </remarks>
		public string[] Apply(string operation, out string[]? quoting)
		{
			quoting = null;

			return operation switch
			{
				_ when this.Name.Length == 0 => throw this.Unsupported(),
				"" => this.Map(value => value),
				[':', '-' or '=' or '?' or '+', .. var word] => this.Default(colon: true, operation[1], word, out quoting),
				['-' or '=' or '?' or '+', .. var word] => this.Default(colon: false, operation[0], word, out quoting),
				['#', '#', .. var pattern] => this.Remove(pattern, suffix: false, longest: true),
				['#', .. var pattern] => this.Remove(pattern, suffix: false, longest: false),
				['%', '%', .. var pattern] => this.Remove(pattern, suffix: true, longest: true),
				['%', .. var pattern] => this.Remove(pattern, suffix: true, longest: false),
				['/', '#' or '%' or '/', .. var rest] => this.Replace(rest, operation[1]),
				['/', .. var rest] => this.Replace(rest, '\0'),
				['^', '^', .. var pattern] => this.ChangeCase(pattern, upper: true, all: true),
				['^', .. var pattern] => this.ChangeCase(pattern, upper: true, all: false),
				[',', ',', .. var pattern] => this.ChangeCase(pattern, upper: false, all: true),
				[',', .. var pattern] => this.ChangeCase(pattern, upper: false, all: false),
				[':', .. var rest] => this.Slice(rest),
				_ => throw this.Unsupported(),
			};
		}

		/// <summary>
		/// Gets what <c>-</c>, <c>=</c>, <c>?</c> or <c>+</c> gives.
		/// </summary>
		/// <param name="colon"><see langword="true" /> when an empty value counts as unset.</param>
		/// <param name="form">The operator after the colon, if any.</param>
		/// <param name="word">The word after the operator, as typed.</param>
		/// <param name="quoting">Which characters of each word were quoted, for an unquoted <c>-</c> or <c>+</c> word.</param>
		/// <returns>
		/// The value, or the word in its place.
		/// </returns>
		/// <exception cref="ExpansionError"><c>?</c> finds the name unset, or <c>=</c> a name that can't be assigned.</exception>
		private string[] Default(bool colon, char form, string word, out string[]? quoting)
		{
			var given = IsSet(this.Variables, this.Name) && !(colon && this.Value.Length == 0);

			quoting = null;

			switch (form)
			{
				case '-' or '=' or '?' when given:
					return this.Map(value => value);
				// "${@:+x}" with no parameters is no word at all, as "$@" is, though "${*:+x}" is an empty one.
				case '+' when !given:
					return this.Parameters is not null && !this.Star ? [] : [string.Empty];
				case '+' or '-' when this.Quoted:
					return [this.Text(word)];
				case '+' or '-':
					// Unquoted, bash reads the word as if typed in place of the ${...}, except that a blank in it doesn't
					// end a word: its unquoted text splits on IFS together with the text around it, and wildcards in
					// it match file names. So each word keeps which of its characters were quoted.
					var words = this.Nested(unset => SplitWords(Protect(word, split: true), this.Variables, unset, this.Substitute, this.Separators, out _, WordForm.Value));

					// A word that reads as nothing, as in ${X-}, is still one empty unquoted part, which a condition keeps.
					quoting = words is [] ? [string.Empty] : [.. words.Select(part => part.Pattern!)];

					return words is [] ? [string.Empty] : [.. words.Select(part => part.Text)];
				case '=' when !DeclareCommand.IsIdentifier(this.Name) && !(DeclareCommand.TrySplitElement(this.Name, out _, out _) && this.Parameters is null):
					throw this.Failure("sharpie::shell::bad_substitution", fatal: false, $"${this.Name}: cannot assign in this way", "not a variable name", "only a variable name can be given a value this way");
				case '=':
					var assigned = this.Text(word);

					try
					{
						DeclareCommand.Assign(this.Variables, this.Name, assigned);
					}
					catch (ArithmeticExpression.Failure failure)
					{
						throw new ExpansionError(failure.Error, fatal: false);
					}

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
			// Inside outer double quotes an inner "..." only marks text already double-quoted, so it's dropped before the
			// word is wrapped, and "${U:-"a  b"}" keeps both spaces, as in bash.
			var typed = this.Quoted ? $"\"{DropQuotes(word)}\"" : Protect(word, split: false);

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
		/// One word, or for <c>@</c> and <c>*</c> what <see cref="ListParts" /> gives.
		/// </returns>
		private string[] Map(Func<string, string> change) =>
			this.Parameters is null ? [change(this.Value)]
			: ListParts(this.Parameters.Select(change), this.Star, this.Quoted, this.Separators, this.Joiner);

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
		/// <param name="rest">The text after <c>/</c>, <c>//</c>, <c>/#</c> or <c>/%</c>.</param>
		/// <param name="mode">
		/// <c>/</c> to replace every match, <c>#</c> or <c>%</c> for the longest match at the start or end, or anything
		/// else for the first match.
		/// </param>
		/// <returns>
		/// The words, with the matches replaced, or removed when no <c>/</c> gives a replacement.
		/// </returns>
		private string[] Replace(string rest, char mode)
		{
			// An empty pattern matches an empty value, but bash leaves an unset one alone.
			if (this.Parameters is null && !IsSet(this.Variables, this.Name))
			{
				return [string.Empty];
			}

			var slash = UnquotedIndex(rest, 0, "/");
			var pattern = this.Pattern(slash < 0 ? rest : rest[..slash]);
			var pieces = slash < 0 ? [string.Empty] : this.Pieces(rest[(slash + 1)..]);

			return this.Map(value => mode switch
			{
				'#' => Cut(value, pattern, suffix: false, longest: true) is var cut and >= 0 ? string.Join(value[..cut], pieces) + value[cut..] : value,
				'%' => Cut(value, pattern, suffix: true, longest: true) is var cut and >= 0 ? value[..cut] + string.Join(value[cut..], pieces) : value,
				_ => ReplacePattern(value, pattern, pieces, all: mode == '/'),
			});
		}

		/// <summary>
		/// Expands a replacement and splits it at each <c>&amp;</c> that isn't quoted or escaped, which stands for the
		/// matched text.
		/// </summary>
		/// <param name="word">The replacement as typed.</param>
		/// <returns>
		/// The pieces, which the matched text joins.
		/// </returns>
		/// <remarks>
		/// bash 5.2's patsub_replacement, on by default, looks for the <c>&amp;</c> after expansion, so one from a
		/// variable counts, and an unquoted backslash there escapes a <c>&amp;</c> or a backslash after it. The word
		/// expands as if it weren't inside double quotes, so <c>"${X/l/'x'}"</c> drops the single quotes, as bash 5.3
		/// does.
		/// </remarks>
		private string[] Pieces(string word)
		{
			var pieces = new List<string>();
			var piece = new StringBuilder();

			// A typed & is split off first, since Protect escapes it so the reader doesn't take it for an operator.
			for (int from = 0, amp; from <= word.Length; from = amp + 1)
			{
				amp = UnquotedIndex(word, from, "&") is var found and >= 0 ? found : word.Length;

				var words = this.Nested(unset => SplitWords(Protect(word[from..amp], split: false), this.Variables, unset, this.Substitute, separators: null, out _, WordForm.Value));

				foreach (var (run, quoted) in words is [var expanded] ? QuotedRuns(expanded.Text, expanded.Pattern!) : [])
				{
					for (var index = 0; index < run.Length; index++)
					{
						if (!quoted && run[index] == '&')
						{
							pieces.Add(piece.ToString());
							_ = piece.Clear();
						}
						else
						{
							var escaped = !quoted && run[index] == '\\' && index + 1 < run.Length && run[index + 1] is '&' or '\\';

							_ = piece.Append(run[escaped ? ++index : index]);
						}
					}
				}

				if (amp < word.Length)
				{
					pieces.Add(piece.ToString());
					_ = piece.Clear();
				}
			}

			pieces.Add(piece.ToString());

			return [.. pieces];
		}

		/// <summary>
		/// Changes the case of the characters a pattern matches, as <c>^</c>, <c>^^</c>, <c>,</c> and <c>,,</c> do.
		/// </summary>
		/// <param name="typed">The pattern as typed, which matches any one character when it's empty.</param>
		/// <param name="upper"><see langword="true" /> to make them upper case.</param>
		/// <param name="all"><see langword="true" /> to change every character, not only the first.</param>
		/// <returns>
		/// The words, changed.
		/// </returns>
		private string[] ChangeCase(string typed, bool upper, bool all)
		{
			var pattern = this.Pattern(typed) is { Length: > 0 } given ? given : "?";

			return this.Map(value =>
			{
				var text = new StringBuilder(value.Length);
				var first = true;

				foreach (var rune in value.EnumerateRunes())
				{
					var changed = (all || first) && Glob.IsMatch(rune.ToString(), pattern) ? upper ? Rune.ToUpperInvariant(rune) : Rune.ToLowerInvariant(rune) : rune;

					_ = text.Append(changed.ToString());
					first = false;
				}

				return text.ToString();
			});
		}

		/// <summary>
		/// Takes a substring of the value, counted in runes as <c>${#NAME}</c> counts, or for <c>@</c> and <c>*</c>
		/// some of <c>$0</c> and the positional parameters.
		/// </summary>
		/// <param name="rest">The offset, then optionally <c>:</c> and the length, as typed.</param>
		/// <returns>
		/// The substring, or what <see cref="ListParts" /> gives for the parameters picked.
		/// </returns>
		/// <exception cref="ExpansionError">The offset or length isn't a number, or the length ends before the offset.</exception>
		private string[] Slice(string rest)
		{
			if (this.Parameters is null)
			{
				var runes = Runes(this.Value);

				var (start, stop) = this.Range(rest, runes.Length);

				return [string.Concat(runes[start..stop])];
			}

			// For the positional parameters offset 0 is $0, as in bash. shortcut: an array's offset counts the elements
			// there are, where bash counts indexes, which differs only for an array with gaps; count indexes if one matters.
			string[] list = this.Name is "@" or "*" ? [Lookup(this.Variables, "0"), .. this.Parameters] : [.. this.Parameters];
			var (from, to) = this.Range(rest, list.Length);

			return ListParts(list[from..to], this.Star, this.Quoted, this.Separators, this.Joiner);
		}

		/// <summary>
		/// Works out which of <paramref name="count" /> items an offset and a length pick.
		/// </summary>
		/// <param name="rest">The offset, then optionally <c>:</c> and the length, as typed.</param>
		/// <param name="count">How many items there are.</param>
		/// <returns>
		/// The first item picked and where the items picked end. An offset past either end picks nothing, as in bash.
		/// </returns>
		/// <exception cref="ExpansionError">
		/// The offset or length isn't a number, the length ends before the offset, or it's negative for <c>@</c> or
		/// <c>*</c>.
		/// </exception>
		/// <remarks>
		/// A negative offset counts from the end, and for a value a negative length is where to stop, counted from the end.
		/// </remarks>
		private (int Start, int Stop) Range(string rest, int count)
		{
			var colon = UnquotedIndex(rest, 0, ":");
			var offset = this.Number(colon < 0 ? rest : rest[..colon], out _);

			offset += offset < 0 ? count : 0;

			if (offset < 0 || offset > count)
			{
				return (0, 0);
			}

			var end = (long)count;

			if (colon >= 0)
			{
				var length = this.Number(rest[(colon + 1)..], out var typed);

				end = length < 0 ? count + length : offset + Math.Min(length, count - offset);

				if (end < offset || (length < 0 && this.Parameters is not null))
				{
					throw this.Failure("sharpie::shell::bad_substitution", fatal: false, $"{typed}: substring expression < 0", this.Parameters is null ? "ends before its offset" : "a negative count", this.Parameters is null ? "a negative length counts back from the end, which has to stay after the offset" : "the number of parameters to take can't be negative");
				}
			}

			return ((int)offset, (int)end);
		}

		/// <summary>
		/// Reads an offset or a length.
		/// </summary>
		/// <param name="word">The word as typed.</param>
		/// <param name="typed">The word expanded, for an error to show.</param>
		/// <returns>
		/// The number the word gives as an arithmetic expression, as in bash, 0 when it's empty.
		/// </returns>
		/// <exception cref="ExpansionError">The expression can't be worked out.</exception>
		private long Number(string word, out string typed)
		{
			typed = this.Text(word).Trim();

			try
			{
				return ArithmeticExpression.Evaluate(typed, this.Variables, noUnset: this.Unset is not null);
			}
			catch (ArithmeticExpression.Failure failure)
			{
				throw new ExpansionError(failure.Error, fatal: false);
			}
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
	/// isn't stored until the first command has run. <c>@</c> and <c>*</c> are set when there's a positional parameter,
	/// and <c>NAME[@]</c> when the array has an element.
	/// </returns>
	internal static bool IsSet(IReadOnlyDictionary<string, string> variables, string name) => name switch
	{
		"?" => true,
		"@" or "*" => DeclareCommand.ReadPositional(variables).Count > 0,
		_ when IsList(name, out var array) => DeclareCommand.Elements(variables, array).Count > 0,
		_ => variables.ContainsKey(name) || Environment.GetEnvironmentVariable(name) is not null,
	};

	/// <summary>
	/// Builds the report for a variable that <c>set -u</c> finds unset.
	/// </summary>
	/// <param name="line">The text the name was typed in.</param>
	/// <param name="name">The name and its place in <paramref name="line" />.</param>
	/// <returns>
	/// The report, pointing at the name.
	/// </returns>
	internal static ShellError UnsetError(string line, Word name) =>
		new("sharpie::shell::unset_variable", $"`{name.Text}` isn't set")
		{
			Source = line,
			Start = name.Start,
			End = name.End,
			Label = "set -u makes this an error",
			Help = "set it first, or turn the check off with set +u",
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

		for (var index = UnquotedIndex(word, 0, targets); index >= 0; index = UnquotedIndex(word, index + 1, targets))
		{
			_ = text.Append(word, from, index - from).Append('\\');
			from = index;
		}

		return text.Append(word, from, word.Length - from).ToString();
	}

	/// <summary>
	/// Removes the double quotes of a word that aren't escaped or inside a nested expansion.
	/// </summary>
	/// <param name="word">The word as typed, inside double quotes.</param>
	/// <returns>
	/// The word without them. Between a pair of them a backslash escapes any character, as in bash, so it's dropped
	/// unless the outer double quotes still need it.
	/// </returns>
	private static string DropQuotes(string word)
	{
		var text = new StringBuilder(word.Length);
		var inner = false;

		for (var index = 0; index < word.Length; index++)
		{
			var close = word[index] == '\\' ? index + 1
				: word.AsSpan(index).StartsWith("${", StringComparison.Ordinal) ? BraceEnd(word, index)
				: SubstitutionEnd(word, index);

			if (word[index] == '\\' && inner && close < word.Length && word[close] is not ('$' or '`' or '"' or '\\' or '\n'))
			{
				continue;
			}

			if (close >= 0)
			{
				close = Math.Min(close, word.Length - 1);
				_ = text.Append(word, index, close - index + 1);
				index = close;
			}
			else if (word[index] == '"')
			{
				inner = !inner;
			}
			else
			{
				_ = text.Append(word[index]);
			}
		}

		return text.ToString();
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
	private static string Remove(string value, string pattern, bool suffix, bool longest) =>
		Cut(value, pattern, suffix, longest) is not (var cut and >= 0) ? value
		: suffix ? value[..cut] : value[cut..];

	/// <summary>
	/// Finds where the shortest or longest start or end of <paramref name="value" /> that <paramref name="pattern" />
	/// matches stops.
	/// </summary>
	/// <param name="value">The variable's value.</param>
	/// <param name="pattern">The pattern, for <see cref="Glob.IsMatch" />.</param>
	/// <param name="suffix"><see langword="true" /> for an end.</param>
	/// <param name="longest"><see langword="true" /> for the longest match.</param>
	/// <returns>
	/// The position that splits the match from the rest, or -1 when nothing matches.
	/// </returns>
	private static int Cut(string value, string pattern, bool suffix, bool longest)
	{
		for (var step = 0; step <= value.Length; step++)
		{
			// The shortest start and the longest end both come from cuts nearest the start.
			var cut = suffix == longest ? step : value.Length - step;

			if (suffix ? Glob.IsMatch(value.AsSpan(cut), pattern) : Glob.IsMatch(value.AsSpan(0, cut), pattern))
			{
				return cut;
			}
		}

		return -1;
	}

	/// <summary>
	/// Replaces the first match of <paramref name="pattern" /> in <paramref name="value" />, or each one, as
	/// <c>/</c> and <c>//</c> do.
	/// </summary>
	/// <param name="value">The variable's value.</param>
	/// <param name="pattern">The pattern, for <see cref="Glob.IsMatch" />.</param>
	/// <param name="pieces">The replacement, split where the matched text goes, as <c>&amp;</c> marks it.</param>
	/// <param name="all"><see langword="true" /> to replace every match.</param>
	/// <returns>
	/// The value with the matches replaced. An empty pattern changes nothing.
	/// </returns>
	/// <remarks>
	/// Each match is the longest one at the earliest position, and the search goes on after it, as in bash.
	/// </remarks>
	private static string ReplacePattern(string value, string pattern, string[] pieces, bool all)
	{
		if (pattern.Length == 0)
		{
			return value;
		}

		if (value.Length == 0)
		{
			return Glob.IsMatch(value, pattern) ? string.Join(value, pieces) : value;
		}

		var text = new StringBuilder(value.Length);
		var start = 0;

		while (start < value.Length)
		{
			if (MatchEnd(value, start, pattern) is var stop and >= 0)
			{
				_ = text.AppendJoin(value[start..stop], pieces);
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
	/// A <c>${...}</c> that can't expand, like <c>${X:?}</c> with <c>X</c> unset, which stops the command. Arithmetic
	/// that fails and <c>exit</c> or <c>return</c> with too many arguments throw it too, as bash skips the line for them.
	/// </summary>
	/// <param name="error">What went wrong, pointing at the <c>${...}</c>.</param>
	/// <param name="fatal">
	/// <see langword="true" /> for <c>${X:?}</c> and an unset name an arithmetic expression reads under <c>set -u</c>,
	/// which end a shell that isn't interactive, as bash's do, with <see cref="ShellSettings.ExpansionExitStatus" />.
	/// </param>
	[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only the shell throws it, and always with a report.")]
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
		/// <see langword="true" /> for <c>${X:?}</c>, and for an unset name an arithmetic expression reads under
		/// <c>set -u</c>; otherwise, <see langword="false" />, and only the rest of the line is skipped, as in bash.
		/// </value>
		public bool Fatal => fatal;
	}
}
