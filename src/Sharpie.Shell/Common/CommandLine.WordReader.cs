using System.Text;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Common;

/// <content>
/// The reader <see cref="SplitWords" /> uses to read a line one character at a time.
/// </content>
internal static partial class CommandLine
{
	/// <summary>
	/// Reads the first line of a command line into words, as <see cref="SplitWords" /> describes, keeping the state of
	/// the word being read.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="variables">The shell variables, or <see langword="null" /> to expand nothing.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">Runs a command substitution, as <see cref="Split" /> describes.</param>
	/// <param name="separators">
	/// What an unquoted expansion splits on, as <see cref="FieldSeparators" /> gives it, or <see langword="null" /> to
	/// split none.
	/// </param>
	/// <param name="form">What each finished word becomes.</param>
	private sealed class WordReader(string line, IReadOnlyDictionary<string, string>? variables, Action<Word>? unset, Func<string, string>? substitute, string? separators, WordForm form)
	{
		/// <summary>
		/// What <c>$*</c> joins its parameters with where it gives one word, as <see cref="ParameterJoiner" /> gives it.
		/// </summary>
		private readonly string joiner = variables is null ? " " : ParameterJoiner(variables);

		/// <summary>
		/// The finished words, which a reader of brace expansion's words shares with the reader of the line.
		/// </summary>
		private List<Word> words = [];

		/// <summary>
		/// <see langword="true" /> for a reader of one word that brace expansion gave, which expands no braces again,
		/// starts no comment, so <c>{#a,b}</c> gives <c>#a</c> and <c>b</c>, and expands <c>~</c> only at its start.
		/// </summary>
		private bool braced;

		/// <summary>
		/// The word being read, quotes removed.
		/// </summary>
		private readonly StringBuilder word = new();

		/// <summary>
		/// The word again for <see cref="Glob.Expand" />, with each quoted or escaped wildcard behind a backslash so it
		/// matches only itself.
		/// </summary>
		private readonly StringBuilder pattern = new();

		/// <summary>
		/// For <see cref="WordForm.Value" />, one <see cref="QuotedMark" /> or <see cref="UnquotedMark" /> for each
		/// character of the word, and an <see cref="EmptyQuoteMark" /> for each quoted part.
		/// </summary>
		private readonly StringBuilder quoting = new();

		/// <summary>
		/// Where the words are between <c>[[</c> and <c>]]</c>, which read differently.
		/// </summary>
		private readonly ConditionWords condition = new();

		/// <summary>
		/// The variables the word being read expands, which are none in a here-document's delimiter. Set when each
		/// word starts.
		/// </summary>
		private IReadOnlyDictionary<string, string>? expand;

		/// <summary>
		/// What runs the word's substitutions, which is nothing in a here-document's delimiter.
		/// </summary>
		private Func<string, string>? run;

		/// <summary>
		/// Where the here-document bodies start, after the first unquoted newline.
		/// </summary>
		private int body = line.Length;

		/// <summary>
		/// The quote character that's currently open, or <see langword="null" /> outside quotes.
		/// </summary>
		private char? quote;

		/// <summary>
		/// <see langword="true" /> right after an escaping backslash, so the next character goes into the word with no
		/// special meaning.
		/// </summary>
		private bool escaped;

		/// <summary>
		/// <see langword="true" /> while a word is being read, tracked apart from its length because <c>''</c> on its
		/// own is still an argument, just an empty one.
		/// </summary>
		private bool hasWord;

		/// <summary>
		/// <see langword="true" /> while the word being read holds a <c>NAME=(...)</c> list, which marks it
		/// <see cref="Word.Compound" />.
		/// </summary>
		private bool compound;

		/// <summary>
		/// <see langword="true" /> when an expansion's blank ended the last word, so a separator such as <c>:</c> right
		/// after it belongs to that blank and closes no empty field, as in bash.
		/// </summary>
		private bool blankEnded;

		/// <summary>
		/// Where the current word starts in the line, so digits typed right before a redirect can be told apart.
		/// </summary>
		private int wordStart;

		/// <summary>
		/// Just past the last <c>$NAME</c> or substitution, so a <c>#</c> right after one that added nothing isn't a
		/// comment.
		/// </summary>
		private int afterExpansion = -1;

		/// <summary>
		/// Where the word being typed starts, which splitting can turn into several words or none.
		/// </summary>
		private int typedStart;

		/// <summary>
		/// How many words there were when the word being typed started.
		/// </summary>
		private int typedWords;

		/// <summary>
		/// Reads the words.
		/// </summary>
		/// <param name="bodyStart">
		/// Where the text after the first unquoted newline starts, or the line's length when there is none.
		/// </param>
		/// <returns>
		/// The words.
		/// </returns>
		public Word[] Read(out int bodyStart)
		{
			for (var index = 0; index < line.Length; index++)
			{
				if (!this.hasWord)
				{
					this.StartWord(index);
				}

				if (this.escaped)
				{
					this.Append(KeptBackslash(this.quote, line[index]), quoted: true);
					this.Append(line.AsSpan(index, 1), quoted: true);
					this.escaped = false;
				}
				else
				{
					index = this.ReadCharacter(index);
				}
			}

			if (this.escaped)
			{
				this.Append("\\", quoted: true);
			}

			this.EndTypedWord(line.Length);
			bodyStart = this.body;

			return [.. this.words];
		}

		/// <summary>
		/// Reads the character at <paramref name="index" />, which no backslash escapes.
		/// </summary>
		/// <param name="index">Its position.</param>
		/// <returns>
		/// The position of the last character it used.
		/// </returns>
		private int ReadCharacter(int index)
		{
			var character = line[index];

			switch (this.quote)
			{
				case null when index == this.typedStart && !this.braced && this.ReadBraces(index) is var last and >= 0:
					return last;
				case null when character == '$' && index + 1 < line.Length && line[index + 1] == '\'' && QuoteEnd(line, index + 1) is var quoteEnd and >= 0:
					this.Append(Escapes.Decode(line.AsSpan(index + 2, quoteEnd - index - 2), EscapeRules.AnsiC, out _), quoted: true);
					this.hasWord = true;

					return quoteEnd;
				case null or '"' when character == '\\':
					this.escaped = true;
					this.hasWord = true;

					return index;
				case null or '"' when character == '$' && ArithmeticEnd(line, index) is var close and >= 0:
					this.Arithmetic(index, close);

					return close;
				case null or '"' when SubstitutionEnd(line, index) is var close and >= 0:
					this.Substitute(index, close);

					return close;
				case null or '"' when TryExpand(line, index, this.expand, this.quote is not null, this.SplitOn(), this.joiner, unset, this.run, out var parts, out var quoting, out var end):
					this.AppendParts(parts, quoted: this.quote is not null, end, quoting, this.SplitOn());
					this.afterExpansion = end + 1;

					return end;
				// Bash expands a ~ after = or : only in a word as typed, so echo a=~/{x,y} keeps both.
				case null when !(this.braced && this.hasWord) && TryExpandTilde(line, index, this.word, this.hasWord, this.expand, out var home, out var tildeEnd):
					this.Append(home, quoted: true);
					this.hasWord = true;

					return tildeEnd;
				case null when !this.braced && StartsComment(character, this.hasWord, index, this.afterExpansion):
					return LineEnd(line, index) - 1;
				case null when character == '(' && !this.braced && IsCompoundStart(line.AsSpan(this.wordStart, index - this.wordStart)) && CompoundEnd(line, index) is var listEnd and >= 0:
					this.Append(this.expand is { } expand ? ExpandCompound(line, index, listEnd, expand, unset, this.run) : line[index..(listEnd + 1)], quoted: true);
					this.hasWord = true;
					this.compound = true;

					return listEnd;
				case null when character is '\'' or '"':
					this.quote = character;
					this.hasWord = true;
					this.Append(string.Empty, quoted: true);

					return index;
				default:
					return this.ReadPlain(character, index);
			}
		}

		/// <summary>
		/// Reads the typed word that starts at <paramref name="index" /> as the words its braces expand to, each read as
		/// if it had been typed there, as bash expands braces before anything else.
		/// </summary>
		/// <param name="index">Where the typed word starts.</param>
		/// <returns>
		/// The position of the word's last character, or -1 when it has no braces to expand, so it's read as usual.
		/// </returns>
		/// <remarks>
		/// As in bash, braces don't expand in a here-document delimiter, a here-string, between <c>[[</c> and
		/// <c>]]</c>, or in a <c>NAME=value</c> word before the command's name, though they do after <c>export</c>.
		/// The words it adds span the whole typed word, which is where an unset variable or an expansion error in them
		/// is reported.
		/// </remarks>
		/// <exception cref="ExpansionError">One of the words can't expand.</exception>
		private int ReadBraces(int index)
		{
			if (form != WordForm.Expanded || this.expand is null || this.condition.Active || !line.AsSpan(index).Contains('{')
				|| TargetAfter(this.words) == TargetKind.Text || StartsComment(line[index], this.hasWord, index, this.afterExpansion))
			{
				return -1;
			}

			// An unclosed ${ also reads as running to the end of the line, which only matters on a line bash refuses.
			var end = UnquotedIndex(line, index, WordEnds) is var found and >= 0 ? found : line.Length;
			var typed = line[index..end];

			if ((DeclareCommand.IsAssignment(typed) && CommandName(this.words) is null) || ExpandBraces(typed) is not { } choices)
			{
				return -1;
			}

			var first = this.words.Count;
			Action<Word>? spanned = unset is null ? null : name => unset(name with { Start = index, End = end });

			try
			{
				foreach (var choice in choices)
				{
					_ = new WordReader(choice, variables, spanned, substitute, separators, form) { words = this.words, braced = true }.Read(out _);
				}
			}
			catch (ExpansionError inner)
			{
				throw new ExpansionError(inner.Error with { Source = line, Start = index, End = end }, inner.Fatal);
			}

			for (var added = first; added < this.words.Count; added++)
			{
				this.words[added] = this.words[added] with { Start = index, End = end };
			}

			return end - 1;
		}

		/// <summary>
		/// Gets ready for a word that may start at <paramref name="index" />.
		/// </summary>
		/// <param name="index">The position.</param>
		private void StartWord(int index)
		{
			this.wordStart = index;
			_ = this.pattern.Clear();

			(this.expand, this.run) = ExpansionsFor(this.words, variables, substitute);
		}

		/// <summary>
		/// Reads a character that isn't a quote opening, an escape or an expansion, or a process substitution in place of
		/// a redirect.
		/// </summary>
		/// <param name="character">The character.</param>
		/// <param name="index">Its position.</param>
		/// <returns>
		/// The position of the last character it used.
		/// </returns>
		private int ReadPlain(char character, int index)
		{
			if (character == this.quote)
			{
				this.quote = null;
			}
			else if (this.quote is null && ProcessSubstitutionEnd(line, index) is var close and >= 0)
			{
				// Kept as typed where nothing runs, as a $(...) is.
				this.Append(this.run is null ? line[index..(close + 1)] : ProcessSubstitution.Start(line[(index + 2)..close], writes: character == '>'), quoted: true);
				this.hasWord = true;

				return close;
			}
			else if (this.quote is null && this.condition.OperatorAt(line, index) is { } typed)
			{
				// With no word yet the span is empty, since wordStart is then this very index.
				var stream = this.condition.IsStreamNumber(line.AsSpan(this.wordStart, index - this.wordStart), typed) ? this.TakeWord() : string.Empty;

				// Taken digits are exactly the text typed before the operator, since IsStreamNumber allows nothing else.
				var start = index - stream.Length;

				this.EndTypedWord(start);
				this.words.Add(new Word(stream + typed, IsOperator: true, start, index + typed.Length));
				this.NextTypedWord(index + typed.Length);

				return index + typed.Length - 1;
			}
			else if (this.quote is null && form != WordForm.Value && this.condition.EndsWord(character))
			{
				this.EndTypedWord(index);
				this.NextTypedWord(index + 1);

				return character == '\n' ? this.EndLine(index) : index;
			}
			else
			{
				this.Append(line.AsSpan(index, 1), quoted: this.quote is not null);

				this.hasWord = true;
			}

			return index;
		}

		/// <summary>
		/// Ends the words at the newline at <paramref name="index" />, since the here-document bodies follow it.
		/// </summary>
		/// <param name="index">The newline's position.</param>
		/// <returns>
		/// The position that ends the loop.
		/// </returns>
		private int EndLine(int index)
		{
			this.body = index + 1;

			return line.Length;
		}

		/// <summary>
		/// Gives the word read so far and starts a new one.
		/// </summary>
		/// <returns>
		/// The word.
		/// </returns>
		private string TakeWord()
		{
			var text = this.word.ToString();

			_ = this.word.Clear();
			this.hasWord = false;

			return text;
		}

		/// <summary>
		/// Adds the output of the substitution that runs from <paramref name="start" /> to <paramref name="close" />,
		/// or the text as typed with no substitute, split on the separators where an expansion splits.
		/// </summary>
		/// <param name="start">The position of the <c>$(</c> or backquote.</param>
		/// <param name="close">The position of the closing <c>)</c> or backquote.</param>
		private void Substitute(int start, int close)
		{
			var (fields, quoted, splitOn) = SubstitutionFields(line, start, close, this.quote, this.run, this.SplitOn());

			this.AppendParts(fields, quoted, close, quoting: null, splitOn);
			this.afterExpansion = close + 1;
		}

		/// <summary>
		/// Adds the number the <c>$((...))</c> from <paramref name="start" /> to <paramref name="close" /> gives, or the
		/// text as typed when nothing expands here, as <see cref="Substitute" /> adds a substitution's output.
		/// </summary>
		/// <param name="start">The position of the <c>$((</c>.</param>
		/// <param name="close">The position of its last <c>)</c>.</param>
		private void Arithmetic(int start, int close)
		{
			if (this.expand is { } expand)
			{
				this.AppendParts([ExpandArithmetic(line, start, close, expand, unset, this.run)], quoted: this.quote is not null, close, quoting: null, this.SplitOn());
			}
			else
			{
				this.AppendParts([line[start..(close + 1)]], quoted: true, close, quoting: null, separators: null);
			}

			this.afterExpansion = close + 1;
		}

		/// <summary>
		/// Gets the separators an expansion read here splits on.
		/// </summary>
		/// <returns>
		/// What <see cref="SeparatorsHere" /> gives.
		/// </returns>
		private string? SplitOn() => SeparatorsHere(this.quote, this.condition.Active, this.word.ToString(), this.words, separators);

		/// <summary>
		/// Adds text to the word, to its pattern, which for the word after <c>=~</c> is a regular expression, and to
		/// <see cref="quoting" />.
		/// </summary>
		/// <param name="text">The characters to add.</param>
		/// <param name="quoted"><see langword="true" /> when they were quoted or escaped.</param>
		private void Append(ReadOnlySpan<char> text, bool quoted)
		{
			_ = this.word.Append(text);
			AppendPattern(this.pattern, text, quoted, this.condition.Regex);

			if (form == WordForm.Value)
			{
				// Quoted text that's empty, like "", still makes a word, so it leaves a mark of its own.
				_ = text.IsEmpty && quoted ? this.quoting.Append(EmptyQuoteMark) : this.quoting.Append(quoted ? QuotedMark : UnquotedMark, text.Length);
			}
		}

		/// <summary>
		/// Adds what an expansion gave, each part as a word of its own, the first joined to the text before it and the
		/// last to the text after it.
		/// </summary>
		/// <param name="parts">The parts.</param>
		/// <param name="quoted"><see langword="true" /> when the expansion was quoted.</param>
		/// <param name="end">The position of the expansion's last character.</param>
		/// <param name="quoting">
		/// For an unquoted <c>${X:-word}</c>, which characters of each part were quoted, as
		/// <see cref="WordForm.Value" /> gives them; otherwise, <see langword="null" />.
		/// </param>
		/// <param name="separators">What the parts split on, or <see langword="null" />.</param>
		private void AppendParts(string[] parts, bool quoted, int end, string[]? quoting, string? separators)
		{
			for (var part = 0; part < parts.Length; part++)
			{
				if (part > 0)
				{
					this.EndWord(end + 1);
				}

				if (quoting is not null)
				{
					this.AppendValue(parts[part], quoting[part], end, separators);
				}
				else
				{
					this.AppendSplit(parts[part], quoted, end, separators);
				}
			}

			// "$@" with no parameters is no word at all, not an empty one, unless more text touches it.
			this.hasWord &= parts.Length + this.word.Length > 0;
		}

		/// <summary>
		/// Adds one word of an unquoted <c>${X:-word}</c>, splitting only the characters that weren't quoted, as bash
		/// reads the word as if it were typed there.
		/// </summary>
		/// <param name="text">The word.</param>
		/// <param name="quoting">Which of its characters were quoted.</param>
		/// <param name="end">The position of the expansion's last character.</param>
		/// <param name="separators">What the unquoted characters split on, or <see langword="null" />.</param>
		private void AppendValue(string text, string quoting, int end, string? separators)
		{
			// A word with nothing in it, as in ${X-}, is an empty unquoted part, which a condition keeps as it keeps $X.
			if (quoting.Length == 0)
			{
				this.AppendSplit(text, quoted: false, end, separators);
			}

			foreach (var (run, quoted) in QuotedRuns(text, quoting))
			{
				this.AppendSplit(run, quoted, end, quoted ? null : separators);
			}
		}

		/// <summary>
		/// Adds an expansion's text, ending a word at each separator when it's unquoted.
		/// </summary>
		/// <param name="text">The text.</param>
		/// <param name="quoted"><see langword="true" /> when it was quoted.</param>
		/// <param name="end">The position of the expansion's last character.</param>
		/// <param name="separators">What the text splits on, or <see langword="null" />.</param>
		/// <remarks>
		/// Blanks (space, tab and newline) run together and add nothing at either end, while any other separator ends
		/// a field by itself, an empty one included, as POSIX field splitting does. An empty part adds a word only when
		/// quoted or in a condition. A <see cref="WordForm.Value" /> word keeps its separators, since the word it's
		/// spliced into splits them.
		/// </remarks>
		private void AppendSplit(string text, bool quoted, int end, string? separators)
		{
			foreach (var (run, blank) in SeparatorRuns(text, form == WordForm.Value ? null : separators))
			{
				if (run is not null)
				{
					this.Append(run, quoted);
					this.hasWord = true;
				}
				else
				{
					// EndWord forgets blankEnded, so what comes after is worked out first.
					(this.hasWord, var after) = Separate(blank, this.hasWord, this.blankEnded);
					this.EndWord(end + 1);
					this.blankEnded = after;
				}
			}

			if (this.condition.KeepsPart(string.Empty, quoted))
			{
				// Added for the mark an empty quoted part leaves in a Value word.
				this.Append(string.Empty, quoted);
				this.hasWord = true;
			}
		}

		/// <summary>
		/// Adds the word read so far, if there is one, ending at <paramref name="end" />, in the form asked for.
		/// </summary>
		/// <param name="end">Where the word ends.</param>
		private void EndWord(int end)
		{
			this.blankEnded = false;

			if (this.hasWord)
			{
				var text = this.TakeWord();
				var first = this.words.Count;

				this.words.AddRange(this.FormWords(text, end));

				for (var added = first; this.compound && added < this.words.Count; added++)
				{
					this.words[added] = this.words[added] with { Compound = true };
				}
			}

			this.compound = false;

			_ = this.pattern.Clear();
			_ = this.quoting.Clear();
		}

		/// <summary>
		/// Ends the word that was typed, which splitting may have made into several words or none, and refuses it as
		/// the target of a redirect unless it's exactly one.
		/// </summary>
		/// <param name="end">Where it ends.</param>
		/// <remarks>
		/// Bash refuses <c>&gt; $F</c> with "ambiguous redirect" when <c>$F</c> splits into more than one word or into
		/// none, or a wildcard in it matches more than one file. The target then becomes one word that's
		/// <see cref="Word.Ambiguous" />, holding the text as typed, which <see cref="Redirection" /> refuses when the
		/// command runs, so only that command fails.
		/// </remarks>
		private void EndTypedWord(int end)
		{
			this.EndWord(end);

			if (end > this.typedStart && this.typedWords > 0 && this.words[this.typedWords - 1] is { IsOperator: true, Text: var typed }
				&& RedirectKind(typed) is TargetKind.File or TargetKind.Stream && this.words.Count - this.typedWords != 1)
			{
				this.words.RemoveRange(this.typedWords, this.words.Count - this.typedWords);
				this.words.Add(new Word(line[this.typedStart..end], IsOperator: false, this.typedStart, end, Ambiguous: true));
			}
		}

		/// <summary>
		/// Notes that the next typed word can start at <paramref name="start" />.
		/// </summary>
		/// <param name="start">The position just past the blank or operator that ended the last one.</param>
		private void NextTypedWord(int start)
		{
			this.typedStart = start;
			this.typedWords = this.words.Count;
		}

		/// <summary>
		/// Gets the words a finished word adds, in the form asked for.
		/// </summary>
		/// <param name="text">The word with its quotes removed.</param>
		/// <param name="end">Where it ends.</param>
		/// <returns>
		/// The word, or for <see cref="WordForm.Expanded" /> what <see cref="FinishedWords" /> gives.
		/// </returns>
		private IEnumerable<Word> FormWords(string text, int end) => form switch
		{
			WordForm.Text => [new Word(text, IsOperator: false, this.wordStart, end)],
			WordForm.Pattern => [new Word(this.pattern.ToString(), IsOperator: false, this.wordStart, end)],
			WordForm.Value => [new Word(text, IsOperator: false, this.wordStart, end, this.quoting.ToString())],
			WordForm.Expanded or _ => FinishedWords(this.condition, text, this.wordStart, end, this.pattern, line, variables, this.words),
		};
	}
}
