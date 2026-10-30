using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Common;

/// <summary>
/// A word of a command line, or an operator such as <c>|</c> or <c>&gt;</c>.
/// </summary>
/// <param name="Text">The word with its quotes removed, or the operator as typed.</param>
/// <param name="IsOperator">
/// <see langword="true" /> for an operator. A quoted or escaped one, like <c>'|'</c>, is a plain word.
/// </param>
/// <param name="Start">Where the word starts in the typed text, quotes included, for an error to point at.</param>
/// <param name="End">Where the word ends in the typed text, one past its last character.</param>
/// <param name="Pattern">
/// For a word between <c>[[</c> and <c>]]</c>, the word again as a pattern: with each quoted wildcard behind a
/// backslash, for <c>==</c>, or for the word after <c>=~</c>, with quoted text escaped by
/// <see cref="System.Text.RegularExpressions.Regex.Escape" />. <see langword="null" /> everywhere else.
/// </param>
/// <param name="Ambiguous">
/// <see langword="true" /> for a redirect's file name or stream that split into more than one word or into none, which
/// bash refuses as an "ambiguous redirect". <see cref="Text" /> then holds it as typed, such as <c>$F</c>.
/// </param>
internal readonly record struct Word(string Text, bool IsOperator, int Start = 0, int End = 0, string? Pattern = null, bool Ambiguous = false);

/// <summary>
/// One redirect taken out of a command's words, such as <c>2&gt;&amp;1</c> or <c>&lt; in.txt</c>.
/// </summary>
/// <param name="Stream">
/// The stream number typed before the operator, or the default: 0 for <c>&lt;</c> operators, 1 for the others.
/// </param>
/// <param name="Operator">The operator without its stream number, such as <c>&gt;&amp;</c>.</param>
/// <param name="Target">
/// The word after the operator: a file name, a stream number or <c>-</c>, a here-string's text, or a here-document's
/// body.
/// </param>
internal readonly record struct Redirect(int Stream, string Operator, Word Target);

/// <summary>
/// What the word after a redirect operator names.
/// </summary>
internal enum TargetKind
{
	/// <summary>
	/// A file, as after <c>&gt;</c> or <c>&lt;</c>.
	/// </summary>
	File,

	/// <summary>
	/// Another stream's number, or <c>-</c> to close the stream, as after <c>&gt;&amp;</c> or <c>&lt;&amp;</c>.
	/// </summary>
	Stream,

	/// <summary>
	/// Text the command reads, as after <c>&lt;&lt;</c>, <c>&lt;&lt;-</c> or <c>&lt;&lt;&lt;</c>.
	/// </summary>
	Text,
}

/// <summary>
/// Breaks a typed command line into words the way a POSIX shell does.
/// </summary>
internal static partial class CommandLine
{
	/// <summary>
	/// The highest stream number a redirect may name. A bigger one is refused rather than handed to the system.
	/// </summary>
	internal const int MaxStream = 255;

	/// <summary>
	/// The separators used when <c>IFS</c> isn't set: space, tab and newline.
	/// </summary>
	private const string DefaultSeparators = " \t\n";

	/// <summary>
	/// Marks a quoted character of a <see cref="WordForm.Value" /> word.
	/// </summary>
	private const char QuotedMark = 'q';

	/// <summary>
	/// Marks an unquoted character of a <see cref="WordForm.Value" /> word, which splits and matches file names.
	/// </summary>
	private const char UnquotedMark = 'u';

	/// <summary>
	/// Marks where a quoted part starts in a <see cref="WordForm.Value" /> word, for no character of it, so an empty
	/// one like <c>""</c> still makes a word.
	/// </summary>
	private const char EmptyQuoteMark = 'e';

	/// <summary>
	/// The characters of a stream number typed before a redirect operator.
	/// </summary>
	private static readonly char[] Digits = [.. "0123456789"];

	/// <summary>
	/// The operators bash's parser reads, longest first so that <c>&gt;&gt;</c> isn't read as two <c>&gt;</c>.
	/// </summary>
	private static readonly string[] Operators =
		["<<<", "<<-", "&>>", ";;&", "&&", "||", ";;", ";&", "|&", "<<", ">>", "<&", ">&", "<>", ">|", "&>", "|", "&", ";", "<", ">", "(", ")"];

	/// <summary>
	/// The first characters of <see cref="Operators" />.
	/// </summary>
	private static readonly SearchValues<char> OperatorStarts = SearchValues.Create("|&;<>()");

	/// <summary>
	/// The characters <see cref="QuoteIfNeeded" /> leaves unquoted, the ones bash's own quoting leaves alone.
	/// </summary>
	private static readonly SearchValues<char> PlainCharacters =
		SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-./:,+=@%~");

	/// <summary>
	/// Splits <paramref name="line" /> into words on spaces and tabs and around operators, keeping everything inside
	/// single or double quotes literally.
	/// </summary>
	/// <param name="line">The command line as the user typed it.</param>
	/// <param name="variables">
	/// The shell variables that <c>$NAME</c> expands from, or <see langword="null" /> to leave every <c>$</c> as typed.
	/// </param>
	/// <param name="unset">
	/// Called with the name and place of each variable that's set in neither the shell nor the environment, for
	/// <c>set -u</c>, or <see langword="null" /> to let it expand to nothing. <c>$@</c>, <c>$*</c>, <c>$?</c> and
	/// <c>$#</c> are always set.
	/// </param>
	/// <param name="substitute">
	/// Runs the command list inside a <c>$(...)</c> or backquotes and gives back its output, or
	/// <see langword="null" /> to keep each substitution as typed and run nothing.
	/// </param>
	/// <returns>
	/// The command name followed by its arguments, with the quote characters removed, and each operator as its own
	/// <see cref="Word" />. Quoted and unquoted text that touch with no space between join into one word. An empty
	/// array means the line was blank.
	/// </returns>
	/// <remarks>
	/// An unquoted operator from <see cref="Operators" /> ends the word before it even with no space, so
	/// <c>ls|wc</c> gives <c>ls</c>, <c>|</c> and <c>wc</c>. Quoting or a backslash turns an operator into plain text,
	/// so <c>'|'</c> and <c>\&gt;</c> are ordinary words. Digits that make up the whole word right before a redirect
	/// join it, as in <c>2&gt;</c>, and <c>2&gt;&amp;1</c> gives <c>2&gt;&amp;</c> and the word <c>1</c>, as in bash.
	/// <para>
	/// Inside one kind of quote the other kind is an ordinary character, so <c>"it's"</c> gives <c>it's</c>.
	/// Outside quotes a backslash makes the next character literal and is itself dropped, so <c>a\ b</c> gives
	/// <c>a b</c>. Inside double quotes a backslash only escapes <c>"</c>, <c>\</c>, <c>$</c>, <c>`</c> and a
	/// newline; before any other character it stays, so <c>"a\b"</c> gives <c>a\b</c>. Inside single quotes
	/// backslashes are always kept as typed. A quote that is never
	/// closed takes the rest of the line literally, and a backslash at the very end is kept as is. A real shell
	/// would ask for another line in both cases.
	/// </para>
	/// <para>
	/// An unquoted <c>#</c> at the start of a word begins a comment, which runs to the end of the line.
	/// </para>
	/// <para>
	/// Outside single quotes, <c>$NAME</c> is replaced by the shell variable's value, or the environment variable's when
	/// no shell variable has that name, and a name set in neither gives nothing. An
	/// unquoted <c>$NAME</c> that comes out empty adds no argument, like bash. A <c>$</c> followed by none of the forms
	/// below stays as typed. <c>${NAME}</c> marks where the name ends, so <c>${A}b</c> expands <c>A</c> and keeps
	/// <c>b</c>; a <c>${</c> that is never closed stays as typed. Inside the braces, as in bash, <c>${#A}</c> is the
	/// length of <c>A</c>'s value; <c>${A:-word}</c> gives the word when <c>A</c> is unset or empty, <c>${A:=word}</c>
	/// also stores it in <c>A</c>, <c>${A:?word}</c> stops the command with the word as its error, and
	/// <c>${A:+word}</c> gives the word only when <c>A</c> isn't empty, each without the colon for an unset <c>A</c>
	/// only; <c>${A#pattern}</c> and <c>${A##pattern}</c> remove the shortest and longest start that matches, and
	/// <c>%</c> and <c>%%</c> the end; <c>${A/pattern/text}</c> replaces the first match and <c>//</c> each one; and
	/// <c>${A:offset}</c> and <c>${A:offset:length}</c> take a substring, with a negative offset counting from the end.
	/// Any other form throws <see cref="ExpansionError" />. <c>$?</c> is the last command's exit status,
	/// <c>$$</c> the shell's process id, <c>$!</c> the last background program's and <c>$-</c> the letters of the
	/// options that are on.
	/// </para>
	/// <para>
	/// <c>$1</c> to <c>$9</c> and <c>${10}</c> onwards are the positional parameters, <c>$#</c> their count and
	/// <c>$0</c> the shell or script name. <c>$@</c> and <c>$*</c> give one word per parameter, with the text touching
	/// them joined to the first and last, so <c>"a$@b"</c> with <c>1</c> and <c>2</c> gives <c>a1</c> and <c>2b</c>.
	/// <c>"$*"</c> gives one word with the parameters separated by the first character of <c>IFS</c>, a space when
	/// <c>IFS</c> isn't set and nothing when it's empty. Where nothing is split, as in <c>A=$*</c>, unquoted <c>$*</c>
	/// joins the same way and <c>$@</c> joins with spaces. <c>"$@"</c> with no parameters gives no word at all, as in
	/// bash.
	/// </para>
	/// <para>
	/// When <paramref name="variables" /> is given, an unquoted expansion's value, each parameter of <c>$@</c> and
	/// <c>$*</c> included, is split into words on the characters of <c>IFS</c>, or on space, tab and newline when
	/// <c>IFS</c> isn't set, so <c>ls $F</c> with <c>F="-l -a"</c> passes two arguments. Spaces, tabs and newlines in
	/// <c>IFS</c> run together and add nothing at either end; any other character ends a word by itself, so with
	/// <c>IFS=:</c> the value <c>a::b</c> gives <c>a</c>, an empty word and <c>b</c>. An empty <c>IFS</c> splits
	/// nothing. As in bash, nothing is split inside double quotes, in a <c>NAME=value</c> word before the command or
	/// after <c>export</c>, <c>declare</c> or <c>local</c>, between <c>[[</c> and <c>]]</c> or in a here-string. In an
	/// unquoted <c>${A:-word}</c> the word's unquoted text splits on <c>IFS</c> too, together with the text around it,
	/// and its quoted text doesn't, so with <c>IFS=:</c> the default <c>a b</c> stays one word. A redirect's file name
	/// or stream that splits into more than one word or into none, or matches more than one file, comes back as one
	/// <see cref="Word.Ambiguous" /> word, which bash refuses as an "ambiguous redirect".
	/// </para>
	/// <para>
	/// When <paramref name="variables" /> is given, each typed word's braces expand first, as
	/// <see cref="ExpandBraces" /> describes, so <c>cp x{,.bak}</c> passes <c>x</c> and <c>x.bak</c>, and each word
	/// they give then expands as if it had been typed there. As in bash, braces stay as typed in a <c>NAME=value</c>
	/// word before the command, between <c>[[</c> and <c>]]</c>, in a here-string and in a here-document's delimiter.
	/// </para>
	/// <para>
	/// When <paramref name="variables" /> is given, an unquoted <c>~</c> at the start of a word becomes the home
	/// folder, so <c>~/Downloads</c> gives <c>$HOME/Downloads</c>. In a <c>NAME=value</c> word it also expands right
	/// after the first <c>=</c> and after each <c>:</c>, so <c>PATH=~/bin:~/tools</c> works. <c>~user</c> gives that
	/// user's home folder. A quoted or escaped <c>~</c>, one in the middle of a word, and <c>~user</c> for a user that
	/// doesn't exist all stay as typed, as in bash.
	/// </para>
	/// <para>
	/// When <paramref name="variables" /> is given, a word with an unquoted <c>*</c>, <c>?</c> or <c>[...]</c> becomes
	/// the file names it matches, sorted, each a word of its own, through <see cref="Glob.Expand" />. Wildcards from an
	/// unquoted <c>$NAME</c> count too, as in bash. A quoted or escaped wildcard only matches itself, and a pattern that
	/// matches nothing stays as typed, which is bash's default.
	/// </para>
	/// <para>
	/// Outside single quotes, <c>$(list)</c> and <c>`list`</c> are replaced by what <paramref name="substitute" />
	/// gives for the list, with NUL characters and trailing newlines removed. Inside backquotes a backslash before
	/// <c>`</c>, <c>\</c> or <c>$</c> is dropped, and before <c>"</c> too inside double quotes, as in bash. Inside double
	/// quotes the output stays one word, so <c>"$(true)"</c> is an empty argument. In a <c>NAME=value</c> word it isn't
	/// split either, so <c>A=$(cmd)</c> keeps its spaces. Anywhere else the output splits on <c>IFS</c> as a value does,
	/// and wildcards in it match file names, so <c>kill $(pgrep x)</c> passes each id as its own argument and an empty
	/// output adds none. <c>$((...))</c> is replaced by the number its expression gives, expanded by
	/// <see cref="ExpandExpression" />, which splits as a substitution's output does.
	/// </para>
	/// <para>
	/// An unquoted newline ends the words, and the here-document bodies follow it. The word after each <c>&lt;&lt;</c>
	/// or <c>&lt;&lt;-</c> is its delimiter, read with no expansions, and comes back holding its body as
	/// <see cref="ReadHereDocument" /> and <see cref="ExpandBody" /> describe, keeping its <c>Start</c> and
	/// <c>End</c>. The word after <c>&lt;&lt;&lt;</c> expands variables but no wildcards, as in bash.
	/// </para>
	/// <para>
	/// From an unquoted first word <c>[[</c> to the unquoted word <c>]]</c>, the words are a condition, read as bash
	/// reads them: <c>[[</c> and <c>]]</c> come back as operators, wildcards match nothing and go into each word's
	/// <see cref="Word.Pattern" />, a substitution's output isn't split, an unquoted expansion that comes out empty is
	/// still a word, and digits before <c>&lt;</c> or <c>&gt;</c> aren't a stream number. In the word after
	/// <c>=~</c>, <c>(</c>, <c>)</c> and <c>|</c> are text, and so are blanks inside the parentheses.
	/// </para>
	/// </remarks>
	public static Word[] Split(string line, IReadOnlyDictionary<string, string>? variables = null, Action<Word>? unset = null, Func<string, string>? substitute = null)
	{
		var words = SplitWords(line, variables, unset, substitute, variables is null ? null : FieldSeparators(variables), out var position);

		foreach (var index in HereDocumentTargets(words))
		{
			var delimiter = words[index];
			var (body, end, _) = ReadHereDocument(line, position, delimiter.Text, words[index - 1].Text.EndsWith('-'));

			// A variable that isn't set is reported at the delimiter, since <<- and joined lines move the body's text
			// away from where it was typed.
			Action<Word>? bodyUnset = unset is null ? null : name => unset(name with { Start = delimiter.Start, End = delimiter.End });

			words[index] = delimiter with { Text = variables is null || IsQuoted(line, delimiter) ? body : ExpandBody(body, variables, bodyUnset, substitute) };
			position = end;
		}

		return words;
	}

	/// <summary>
	/// What <see cref="SplitWords" /> makes of each finished word.
	/// </summary>
	private enum WordForm
	{
		/// <summary>The file names its wildcards match, as <see cref="Split" /> gives.</summary>
		Expanded,

		/// <summary>The word with its quotes removed and no wildcards matched.</summary>
		Text,

		/// <summary>The word as a pattern for <see cref="Glob.IsMatch" />, its quoted parts escaped.</summary>
		Pattern,

		/// <summary>
		/// The word with its quotes removed, and in <see cref="Word.Pattern" /> a <see cref="QuotedMark" /> or
		/// <see cref="UnquotedMark" /> for each character and an <see cref="EmptyQuoteMark" /> where each quoted part
		/// starts, for an unquoted <c>${X:-word}</c>. Blanks don't end it, and
		/// nothing in it splits, since the word it's spliced into splits it.
		/// </summary>
		Value,
	}

	/// <summary>
	/// Expands the word a <c>case</c> matches, as the word after <c>&lt;&lt;&lt;</c> expands.
	/// </summary>
	/// <param name="typed">The word as typed.</param>
	/// <param name="variables">The shell variables, or <see langword="null" /> to expand nothing.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">Runs a command substitution, as <see cref="Split" /> describes.</param>
	/// <returns>
	/// The word with its variables and substitutions expanded and its quotes removed, but no wildcards matched.
	/// </returns>
	internal static string ExpandText(string typed, IReadOnlyDictionary<string, string>? variables, Action<Word>? unset = null, Func<string, string>? substitute = null) =>
		string.Join(' ', SplitWords(typed, variables, unset, substitute, separators: null, out _, WordForm.Text).Select(word => word.Text));

	/// <summary>
	/// Expands a <c>case</c> pattern into a pattern for <see cref="Glob.IsMatch" />.
	/// </summary>
	/// <param name="typed">The pattern as typed.</param>
	/// <param name="variables">The shell variables, or <see langword="null" /> to expand nothing.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">Runs a command substitution, as <see cref="Split" /> describes.</param>
	/// <returns>
	/// The pattern, with each quoted or escaped <c>*</c>, <c>?</c>, <c>[</c>, <c>]</c> and <c>\</c> behind a
	/// backslash so it matches only itself, as in bash, where <c>"*")</c> matches only a <c>*</c>.
	/// </returns>
	internal static string Pattern(string typed, IReadOnlyDictionary<string, string>? variables, Action<Word>? unset = null, Func<string, string>? substitute = null) =>
		string.Join(' ', SplitWords(typed, variables, unset, substitute, separators: null, out _, WordForm.Pattern).Select(word => word.Text));

	/// <summary>
	/// Splits the first line of <paramref name="line" /> into words, as <see cref="Split" /> describes, leaving each
	/// here-document delimiter as typed.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="variables">The shell variables, or <see langword="null" /> to expand nothing.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">Runs a command substitution, as <see cref="Split" /> describes.</param>
	/// <param name="separators">
	/// The characters an unquoted expansion splits on, as <see cref="FieldSeparators" /> gives them, or
	/// <see langword="null" /> to split none.
	/// </param>
	/// <param name="bodyStart">
	/// Where the text after the first unquoted newline starts, or the line's length when there is none.
	/// </param>
	/// <param name="form">What each finished word becomes.</param>
	/// <returns>
	/// The words.
	/// </returns>
	private static Word[] SplitWords(string line, IReadOnlyDictionary<string, string>? variables, Action<Word>? unset, Func<string, string>? substitute, string? separators, out int bodyStart, WordForm form = WordForm.Expanded) =>
		new WordReader(line.ArgumentNotNull(), variables, unset, substitute, separators, form).Read(out bodyStart);

	/// <summary>
	/// Gets what an unquoted expansion read at this point splits on.
	/// </summary>
	/// <param name="quote">The quote that's open, or <see langword="null" />.</param>
	/// <param name="inCondition"><see langword="true" /> between <c>[[</c> and <c>]]</c>.</param>
	/// <param name="word">The word read so far.</param>
	/// <param name="words">The words before it.</param>
	/// <param name="separators">What expansions split on elsewhere, or <see langword="null" />.</param>
	/// <returns>
	/// <paramref name="separators" />, or <see langword="null" /> inside quotes, between <c>[[</c> and <c>]]</c>, in an
	/// assignment as <see cref="IsAssignmentWord" /> describes, and in a here-string or here-document delimiter.
	/// </returns>
	private static string? SeparatorsHere(char? quote, bool inCondition, string word, List<Word> words, string? separators) =>
		quote is null && !inCondition && !IsAssignmentWord(word, words) && TargetAfter(words) != TargetKind.Text ? separators : null;

	/// <summary>
	/// Works out what a separator in an unquoted expansion does to the word being read.
	/// </summary>
	/// <param name="blank"><see langword="true" /> for a space, tab or newline.</param>
	/// <param name="hasWord"><see langword="true" /> when a word is being read.</param>
	/// <param name="blankEnded"><see langword="true" /> when an expansion's blank ended the last word.</param>
	/// <returns>
	/// <c>Ends</c> is <see langword="true" /> when there's a word to end, or the separator isn't a blank and closes an
	/// empty field, as the second <c>:</c> in <c>a::b</c> does. A separator such as <c>:</c> right after a blank that
	/// ended a word goes with that blank instead, as in bash. <c>BlankEnded</c> is what the next separator sees.
	/// </returns>
	private static (bool Ends, bool BlankEnded) Separate(bool blank, bool hasWord, bool blankEnded)
	{
		var ends = hasWord || !(blank || blankEnded);

		return (ends, blank && (ends || blankEnded));
	}

	/// <summary>
	/// Cuts an expansion's text at each separator.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="separators">The characters that split it, or <see langword="null" /> to keep it whole.</param>
	/// <returns>
	/// In order, each run of text between separators as <c>Run</c>, and each separator as a <see langword="null" />
	/// <c>Run</c>, with <c>Blank</c> telling whether it's a space, tab or newline.
	/// </returns>
	private static IEnumerable<(string? Run, bool Blank)> SeparatorRuns(string text, string? separators)
	{
		for (var start = 0; start < text.Length;)
		{
			var length = text.AsSpan(start).IndexOfAny(separators) is var found and >= 0 ? found : text.Length - start;

			if (length > 0)
			{
				yield return (text.Substring(start, length), false);
				start += length;
			}
			else
			{
				yield return (null, text[start] is ' ' or '\t' or '\n');
				start++;
			}
		}
	}

	/// <summary>
	/// Cuts a <see cref="WordForm.Value" /> word into its quoted and unquoted runs.
	/// </summary>
	/// <param name="text">The word.</param>
	/// <param name="quoting">
	/// A <see cref="QuotedMark" /> or <see cref="UnquotedMark" /> for each character, and an
	/// <see cref="EmptyQuoteMark" /> where each quoted part starts.
	/// </param>
	/// <returns>
	/// In order, each longest run of characters that are all quoted or all unquoted, and an empty quoted run for each
	/// <see cref="EmptyQuoteMark" />.
	/// </returns>
	private static IEnumerable<(string Run, bool Quoted)> QuotedRuns(string text, string quoting)
	{
		for (int mark = 0, start = 0; mark < quoting.Length;)
		{
			var kind = quoting[mark];
			var length = kind == EmptyQuoteMark ? 0 : quoting.AsSpan(mark).IndexOfAnyExcept(kind) is var found and >= 0 ? found : quoting.Length - mark;

			yield return (text.Substring(start, length), kind != UnquotedMark);
			mark += Math.Max(length, 1);
			start += length;
		}
	}

	/// <summary>
	/// Gets what <c>$*</c> joins its parameters with where it gives one word.
	/// </summary>
	/// <param name="variables">The shell variables.</param>
	/// <returns>
	/// The first character of <c>IFS</c>, a space when <c>IFS</c> isn't set, or nothing when it's empty, as in bash.
	/// </returns>
	private static string ParameterJoiner(IReadOnlyDictionary<string, string> variables) =>
		FieldSeparators(variables) is [var first, ..] ? first.ToString() : string.Empty;

	/// <summary>
	/// Gets the words <c>$@</c> or <c>$*</c> gives, or the <c>${@...}</c> and <c>${*...}</c> forms.
	/// </summary>
	/// <param name="parameters">The positional parameters, each already changed by the form's operator.</param>
	/// <param name="star"><see langword="true" /> for <c>*</c>.</param>
	/// <param name="quoted"><see langword="true" /> inside double quotes or a here-document.</param>
	/// <param name="separators">What the words split on, or <see langword="null" /> where they aren't split.</param>
	/// <param name="joiner">What <c>*</c> joins the parameters with.</param>
	/// <returns>
	/// One word per parameter, except that <c>"$*"</c> joins them with <paramref name="joiner" />. Unquoted where
	/// nothing is split, as in <c>A=$*</c> or <c>[[ $@ ]]</c>, both give one word, <c>*</c> joined with
	/// <paramref name="joiner" /> and <c>@</c> with spaces, as in bash.
	/// </returns>
	private static string[] ListParts(IEnumerable<string> parameters, bool star, bool quoted, string? separators, string joiner) =>
		star && quoted ? [string.Join(joiner, parameters)]
		: quoted || separators is not null ? [.. parameters]
		: [string.Join(star ? joiner : " ", parameters)];

	/// <summary>
	/// Gets the words a finished word adds.
	/// </summary>
	/// <param name="condition">Where the words are between <c>[[</c> and <c>]]</c>.</param>
	/// <param name="text">The word with its quotes removed.</param>
	/// <param name="start">Where it starts in <paramref name="line" />.</param>
	/// <param name="end">Where it ends.</param>
	/// <param name="pattern">The word as a pattern.</param>
	/// <param name="line">The command line.</param>
	/// <param name="variables">The shell variables, or <see langword="null" /> to expand nothing.</param>
	/// <param name="words">The words before it.</param>
	/// <returns>
	/// What <see cref="ConditionWords.Read" /> gives for it, else the file names its wildcards match.
	/// </returns>
	private static IEnumerable<Word> FinishedWords(ConditionWords condition, string text, int start, int end, StringBuilder pattern, string line, IReadOnlyDictionary<string, string>? variables, List<Word> words) =>
		condition.Read(text, start, end, pattern, line, words.Count == 0)
		?? ExpandWildcards(text, pattern, variables, words).Select(match => new Word(match, IsOperator: false, start, end));

	/// <summary>
	/// Finds where the command substitution that starts at <paramref name="index" /> ends.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">The position of a <c>$(</c> or a backquote, outside single quotes and not escaped.</param>
	/// <returns>
	/// The position of the closing <c>)</c> or backquote, or -1 when there is none or nothing starts a substitution at
	/// <paramref name="index" />. For a <c>$((</c> it's where <see cref="ArithmeticEnd" /> says the arithmetic ends, or,
	/// when that's nowhere, where the substitution that starts with a subshell ends, as in bash.
	/// </returns>
	/// <remarks>
	/// Inside <c>$(...)</c>, parentheses outside quotes are counted, and quoted or escaped text is skipped, so the
	/// <c>)</c> in <c>$(echo ")")</c> doesn't end it. A <c>#</c> at the start of a word begins a comment that runs to
	/// the end of its line, so the <c>)</c> in <c>$(echo a # )</c> doesn't end it either, as in bash. A nested
	/// substitution inside double quotes or backquotes ends where its own call says. Inside backquotes the next
	/// backquote that isn't escaped ends it.
	/// </remarks>
	internal static int SubstitutionEnd(string line, int index) =>
		!IsSubstitutionStart(line.ArgumentNotNull(), index) ? -1
		: line[index] == '`' ? BackquoteEnd(line, index)
		: ArithmeticEnd(line, index) is var arithmetic and >= 0 ? arithmetic
		: ParenthesisEnd(line, index);

	/// <summary>
	/// Finds the <c>)</c> that ends the <c>$(</c> at <paramref name="index" />, as <see cref="SubstitutionEnd" />
	/// describes.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">The position of the <c>$(</c>.</param>
	/// <returns>
	/// The position of the <c>)</c>, or -1 when there is none.
	/// </returns>
	private static int ParenthesisEnd(string line, int index)
	{
		// The $( itself is the first parenthesis, and a word, or a comment, can start right after it.
		var depth = 1;
		char? quote = null;

		// True after a blank or an operator character outside quotes, where a word, or a comment, can start.
		var wordStart = true;
		var cases = new CaseTracker();

		for (var end = index + 2; end < line.Length; end++)
		{
			var character = line[end];

			if (quote is null && wordStart && cases.ReadReservedWord(line, end, depth) is > 0 and var length)
			{
				end += length - 1;
				wordStart = false;

				continue;
			}

			if (character == '\\')
			{
				end++;
			}
			else if (quote == '"')
			{
				quote = character == '"' ? null : quote;

				if (IsSubstitutionStart(line, end))
				{
					end = SubstitutionEnd(line, end);

					// Everything after a nested substitution that never closes is inside it, so this one can't close
					// either. Scanning on would also go over the same text again at every level of nesting.
					if (end < 0)
					{
						return -1;
					}
				}
			}
			else
			{
				switch (character)
				{
					case '#' when wordStart:
						end = line.IndexOf('\n', end) is var newline and >= 0 ? newline - 1 : line.Length;

						break;
					case '\'':
						end = QuoteEnd(line, end);

						if (end < 0)
						{
							return -1;
						}

						break;
					case '"':
						quote = character;

						break;
					case '`' when SubstitutionEnd(line, end) is var nested and >= 0:
						end = nested;

						break;
					case '`':
						return -1;
					case ')' when cases.EndsPattern(depth):
						break;
					case '(':
						depth++;

						break;
					case ')' when --depth == 0:
						return end;
					default:
						break;
				}
			}

			wordStart = quote is null && (character is ' ' or '\t' or '\n' || OperatorStarts.Contains(character));
			cases.Read(character, quote);
		}

		return -1;
	}

	/// <summary>
	/// Finds the backquote that ends the substitution starting at the backquote at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">The position of the opening backquote.</param>
	/// <returns>
	/// The position of the next backquote that isn't escaped, or -1 when there is none.
	/// </returns>
	private static int BackquoteEnd(string line, int index)
	{
		for (var end = index + 1; end < line.Length; end++)
		{
			if (line[end] == '\\')
			{
				end++;
			}
			else if (line[end] == '`')
			{
				return end;
			}
		}

		return -1;
	}

	/// <summary>
	/// Checks whether a command substitution starts at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">The position to look at.</param>
	/// <returns>
	/// <see langword="true" /> for a backquote or a <c>$(</c>, which includes <c>$((</c>.
	/// </returns>
	private static bool IsSubstitutionStart(string line, int index) =>
		line[index] == '`' || line.AsSpan(index).StartsWith("$(", StringComparison.Ordinal);

	/// <summary>
	/// Runs the substitution from <paramref name="start" /> to <paramref name="close" />.
	/// </summary>
	/// <param name="line">The command line being split.</param>
	/// <param name="start">The position of the <c>$(</c> or backquote.</param>
	/// <param name="close">The position of the closing <c>)</c> or backquote.</param>
	/// <param name="quote">The quote the substitution is inside, or <see langword="null" />.</param>
	/// <param name="substitute">Runs the command list, or <see langword="null" /> to run nothing.</param>
	/// <param name="separators">What the output splits on, as <see cref="SeparatorsHere" /> gives it.</param>
	/// <returns>
	/// The substitution as typed when <paramref name="substitute" /> is <see langword="null" />, which isn't split.
	/// Otherwise its output with NUL characters and trailing newlines removed, split on <c>Separators</c>.
	/// <c>Quoted</c> tells whether its wildcards only match themselves.
	/// </returns>
	private static (string[] Fields, bool Quoted, string? Separators) SubstitutionFields(string line, int start, int close, char? quote, Func<string, string>? substitute, string? separators) =>
		substitute is null ? ([line[start..(close + 1)]], true, null)
		: ([SubstitutionOutput(line, start, close, inDoubleQuotes: quote == '"', substitute)], quote is not null, separators);

	/// <summary>
	/// Runs the substitution from <paramref name="start" /> to <paramref name="close" />.
	/// </summary>
	/// <param name="line">The text holding it.</param>
	/// <param name="start">The position of the <c>$(</c> or backquote.</param>
	/// <param name="close">The position of the closing <c>)</c> or backquote.</param>
	/// <param name="inDoubleQuotes">
	/// <see langword="true" /> when it's inside double quotes, where a backslash before <c>"</c> inside backquotes is
	/// dropped too.
	/// </param>
	/// <param name="substitute">Runs the command list.</param>
	/// <returns>
	/// Its output with NUL characters and trailing newlines removed.
	/// </returns>
	private static string SubstitutionOutput(string line, int start, int close, bool inDoubleQuotes, Func<string, string> substitute)
	{
		var inner = line[start] == '`' ? Unescape(line[(start + 1)..close], inDoubleQuotes) : line[(start + 2)..close];

		// argv can't hold NUL, and bash drops it with a warning.
		return substitute(inner).Replace("\0", string.Empty, StringComparison.Ordinal).TrimEnd('\n');
	}

	/// <summary>
	/// Checks whether <paramref name="word" /> is a <c>NAME=value</c> that bash reads as an assignment, whose
	/// substitutions aren't split.
	/// </summary>
	/// <param name="word">The word read so far.</param>
	/// <param name="words">The words before it.</param>
	/// <returns>
	/// <see langword="true" /> when the word is shaped like an assignment and comes before the command's name, or is
	/// an argument of <c>export</c>, <c>declare</c> or <c>local</c>. So <c>export A=$(cmd)</c> keeps its spaces, and
	/// <c>printf A=$(cmd)</c> is split.
	/// </returns>
	private static bool IsAssignmentWord(string word, List<Word> words) =>
		DeclareCommand.IsAssignment(word) && CommandName(words) is null or "export" or "declare" or "local";

	/// <summary>
	/// Gets the name of the command the words being read belong to.
	/// </summary>
	/// <param name="words">The words read so far.</param>
	/// <returns>
	/// The first word after the last operator that isn't a <c>NAME=value</c>, or <see langword="null" /> when the
	/// name hasn't been read yet.
	/// </returns>
	private static string? CommandName(List<Word> words) =>
		words.Skip(words.FindLastIndex(typed => typed.IsOperator) + 1).FirstOrDefault(typed => !DeclareCommand.IsAssignment(typed.Text)).Text;

	/// <summary>
	/// Checks whether the unquoted <paramref name="character" /> at <paramref name="index" /> starts a comment.
	/// </summary>
	/// <param name="character">The character.</param>
	/// <param name="hasWord"><see langword="true" /> when a word is being read.</param>
	/// <param name="index">Its position.</param>
	/// <param name="afterExpansion">The position just past the last <c>$NAME</c> or substitution.</param>
	/// <returns>
	/// <see langword="true" /> for a <c>#</c> at the start of a word. POSIX starts a comment only there, so the
	/// <c>#</c> in <c>a#b</c>, <c>\#x</c> and <c>$(true)#x</c> is kept.
	/// </returns>
	private static bool StartsComment(char character, bool hasWord, int index, int afterExpansion) =>
		character == '#' && !hasWord && index != afterExpansion;

	/// <summary>
	/// Removes the backslashes that only escape a character inside backquotes, the way bash reads the command there.
	/// </summary>
	/// <param name="text">The text between the backquotes.</param>
	/// <param name="inDoubleQuotes"><see langword="true" /> when the backquotes are inside double quotes.</param>
	/// <returns>
	/// The command, with each backslash before <c>`</c>, <c>\</c> or <c>$</c> dropped, and before <c>"</c> too when
	/// <paramref name="inDoubleQuotes" />. Any other backslash stays.
	/// </returns>
	private static string Unescape(string text, bool inDoubleQuotes)
	{
		var command = new StringBuilder(text.Length);

		for (var index = 0; index < text.Length; index++)
		{
			// SubstitutionEnd never ends on an escaped backquote, so a backslash always has a character after it.
			if (text[index] == '\\' && (text[index + 1] is '`' or '\\' or '$' || (inDoubleQuotes && text[index + 1] == '"')))
			{
				index++;
			}

			_ = command.Append(text[index]);
		}

		return command.ToString();
	}

	/// <summary>
	/// Adds <paramref name="text" /> to <paramref name="pattern" />, behind backslashes when it was quoted, so a quoted
	/// <c>*</c> matches only a <c>*</c>.
	/// </summary>
	/// <param name="pattern">The word being built for <see cref="Glob.Expand" />.</param>
	/// <param name="text">The characters to add.</param>
	/// <param name="quoted">
	/// <see langword="true" /> when the characters were quoted, escaped, or came from a quoted expansion or <c>~</c>.
	/// </param>
	/// <param name="regex">
	/// <see langword="true" /> for the word after <c>=~</c>, a regular expression, where quoted text goes through
	/// <see cref="Regex.Escape" /> instead.
	/// </param>
	private static void AppendPattern(StringBuilder pattern, ReadOnlySpan<char> text, bool quoted, bool regex = false)
	{
		if (regex)
		{
			_ = pattern.Append(quoted ? Regex.Escape(text.ToString()) : text);

			return;
		}

		foreach (var character in text)
		{
			_ = quoted && character is '*' or '?' or '[' or ']' or '\\' ? pattern.Append('\\').Append(character) : pattern.Append(character);
		}
	}

	/// <summary>
	/// Gets the words that a finished word stands for once its wildcards are matched against file names.
	/// </summary>
	/// <param name="text">The word with its quotes removed.</param>
	/// <param name="pattern">The word as a pattern for <see cref="Glob.Expand" />.</param>
	/// <param name="variables">
	/// The shell variables, or <see langword="null" /> when <see cref="Split" /> expands nothing, wildcards included.
	/// </param>
	/// <param name="words">The words before it, which tell whether it's the text of a here-string or sets a variable.</param>
	/// <returns>
	/// The matching paths, or just <paramref name="text" /> when nothing matches or the word is a <c>NAME=value</c> in
	/// front of the command or the text of a here-document or here-string.
	/// </returns>
	/// <remarks>
	/// POSIX expands no wildcards in the <c>NAME=value</c> words in front of a command, so <c>A=*</c> keeps its
	/// <c>*</c>, and bash expands none in <c>&lt;&lt;&lt; *</c>. A redirect's file that matches more than one path is
	/// refused as ambiguous once the word ends, as <see cref="Word.Ambiguous" /> says.
	/// </remarks>
	private static string[] ExpandWildcards(string text, StringBuilder pattern, IReadOnlyDictionary<string, string>? variables, List<Word> words)
	{
		var previous = TargetAfter(words);

		// Operators are never assignments, so the command has no name yet when the last other word is one, or none is.
		var last = words.FindLastIndex(word => !DeclareCommand.IsAssignment(word.Text));
		var expand = variables is not null && previous != TargetKind.Text && !(DeclareCommand.IsAssignment(text) && (last < 0 || words[last].IsOperator));
		var matches = expand ? Glob.Expand(pattern.ToString()) : [];

		return matches.Length == 0 ? [text] : matches;
	}

	/// <summary>
	/// Gets what the word being read names when it comes right after a redirect operator.
	/// </summary>
	/// <param name="words">The words before it.</param>
	/// <returns>
	/// The kind of target, as <see cref="RedirectKind" /> gives it, or <see langword="null" /> when the last word isn't
	/// a redirect.
	/// </returns>
	private static TargetKind? TargetAfter(List<Word> words) =>
		words is [.., { IsOperator: true, Text: var typed }] ? RedirectKind(typed) : null;

	/// <summary>
	/// Reads the operator that starts at <paramref name="index" />, which the caller has checked is outside quotes and
	/// not escaped.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">The position to look at.</param>
	/// <returns>
	/// The longest operator in <see cref="Operators" /> that starts there, or <see langword="null" /> when none does.
	/// </returns>
	internal static string? OperatorAt(string line, int index)
	{
		if (!OperatorStarts.Contains(line.ArgumentNotNull()[index]))
		{
			return null;
		}

		foreach (var candidate in Operators)
		{
			if (line.AsSpan(index).StartsWith(candidate, StringComparison.Ordinal))
			{
				return candidate;
			}
		}

		return null;
	}

	/// <summary>
	/// Checks whether <paramref name="typed" />, the raw text of the word just before <paramref name="typedOperator" />,
	/// names the stream that the redirect works on, as the <c>2</c> in <c>2&gt;</c> does.
	/// </summary>
	/// <param name="typed">The word's characters as typed, quotes and backslashes included.</param>
	/// <param name="typedOperator">The operator right after the word.</param>
	/// <returns>
	/// <see langword="true" /> when <paramref name="typedOperator" /> starts with <c>&lt;</c> or <c>&gt;</c> and
	/// <paramref name="typed" /> is only digits, so the digits belong to the operator.
	/// </returns>
	/// <remarks>
	/// Bash reads such digits as an IO_NUMBER only when they are the whole word and unquoted, so <c>a2&gt;f</c>,
	/// <c>'2'&gt;f</c> and <c>\2&gt;f</c> keep <c>2</c> as part of an argument. Checking the raw text covers the
	/// quoted and escaped cases.
	/// </remarks>
	internal static bool IsStreamNumber(ReadOnlySpan<char> typed, string typedOperator) =>
		typed.Length > 0 && typedOperator.ArgumentNotNull()[0] is '<' or '>' && !typed.ContainsAnyExceptInRange('0', '9');

	/// <summary>
	/// Reads the expansion, if any, that starts at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The command line being split.</param>
	/// <param name="index">The position to look at, which expands only when it holds a <c>$</c>.</param>
	/// <param name="variables">
	/// The shell variables, positional parameters included, or <see langword="null" /> to leave every <c>$</c> as typed.
	/// </param>
	/// <param name="quoted"><see langword="true" /> inside double quotes, where <c>$*</c> joins its parameters.</param>
	/// <param name="separators">
	/// What the expansion splits on, which an unquoted <c>${X:-word}</c> needs for the expansions in its word, or
	/// <see langword="null" /> where it isn't split.
	/// </param>
	/// <param name="joiner">What <c>$*</c> joins its parameters with, as <see cref="ListParts" /> says.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">Runs a command substitution inside a <c>${...}</c>, as <see cref="Split" /> describes.</param>
	/// <param name="parts">
	/// The words the expansion gives: one for a variable, and for <c>$@</c> and <c>$*</c> what
	/// <see cref="ListParts" /> gives.
	/// </param>
	/// <param name="quoting">Which characters of the parts were quoted, as <see cref="TryExpandBraced" /> gives them, or <see langword="null" />.</param>
	/// <param name="end">The position of the last character the expansion used.</param>
	/// <returns>
	/// <see langword="false" /> when no form follows the <c>$</c>, so it stays as typed.
	/// </returns>
	/// <exception cref="ExpansionError">A <c>${...}</c> can't be expanded, as <see cref="TryExpandBraced" /> says.</exception>
	private static bool TryExpand(string line, int index, IReadOnlyDictionary<string, string>? variables, bool quoted, string? separators, string joiner, Action<Word>? unset, Func<string, string>? substitute, out string[] parts, out string[]? quoting, out int end)
	{
		parts = [];
		quoting = null;
		end = index;

		if (line[index] != '$' || variables is null)
		{
			return false;
		}

		if (TryReadParameterList(line, index, out var star, out end))
		{
			parts = ListParts(DeclareCommand.ReadPositional(variables), star, quoted, separators, joiner);

			return true;
		}

		if (index + 1 < line.Length && line[index + 1] == '{')
		{
			return TryExpandBraced(line, index, variables, quoted, separators, joiner, unset, substitute, out parts, out quoting, out end);
		}

		if (!TryReadVariableName(line, index, out var name, out end))
		{
			return false;
		}

		if (unset is not null && !IsSet(variables, name))
		{
			unset(new Word(name, IsOperator: false, index, end + 1));
		}

		parts = [Lookup(variables, name)];

		return true;
	}

	/// <summary>
	/// Reads the <c>$@</c>, <c>$*</c>, <c>${@}</c> or <c>${*}</c> that starts at the <c>$</c> at
	/// <paramref name="index" />, which stands for all the positional parameters.
	/// </summary>
	/// <param name="line">The command line being split.</param>
	/// <param name="index">The position of the <c>$</c>.</param>
	/// <param name="star"><see langword="true" /> for <c>*</c>, which joins the parameters inside double quotes.</param>
	/// <param name="end">The position of the last character the expansion used.</param>
	/// <returns>
	/// <see langword="false" /> when none of the four forms starts there.
	/// </returns>
	private static bool TryReadParameterList(string line, int index, out bool star, out int end)
	{
		var braced = index + 1 < line.Length && line[index + 1] == '{';
		var at = index + (braced ? 2 : 1);

		star = at < line.Length && line[at] == '*';
		end = braced ? at + 1 : at;

		return at < line.Length && line[at] is '@' or '*' && (!braced || (end < line.Length && line[end] == '}'));
	}

	/// <summary>
	/// Reads the variable name of the <c>$NAME</c>, <c>$?</c>, <c>$#</c>, <c>$$</c>, <c>$!</c>,
	/// <c>$-</c> or <c>$0</c> to <c>$9</c> that starts at the <c>$</c> at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The command line being split.</param>
	/// <param name="index">The position of the <c>$</c>.</param>
	/// <param name="name">The variable's name, or an empty string when no name follows.</param>
	/// <param name="end">The position of the last character the expansion used.</param>
	/// <returns>
	/// <see langword="false" /> when no form follows the <c>$</c>, so it stays as typed.
	/// </returns>
	private static bool TryReadVariableName(string line, int index, out string name, out int end)
	{
		// Only one digit counts, so $10 is $1 followed by 0, as in POSIX. ${10} goes through TryExpandBraced.
		if (index + 1 < line.Length && (line[index + 1] is '?' or '#' or '$' or '!' or '-' || char.IsAsciiDigit(line[index + 1])))
		{
			name = line[index + 1].ToString();
			end = index + 1;

			return true;
		}

		if (index + 1 < line.Length && (char.IsAsciiLetter(line[index + 1]) || line[index + 1] == '_'))
		{
			end = index + 1;

			while (end + 1 < line.Length && (char.IsAsciiLetterOrDigit(line[end + 1]) || line[end + 1] == '_'))
			{
				end++;
			}

			name = line[(index + 1)..(end + 1)];

			return true;
		}

		name = string.Empty;
		end = index;

		return false;
	}

	/// <summary>
	/// Checks whether the <c>~</c> at <paramref name="index" /> starts the value of a <c>NAME=value</c> word, or a new
	/// part of that value after a <c>:</c>.
	/// </summary>
	/// <param name="line">The command line being split.</param>
	/// <param name="index">The position of the <c>~</c>, which isn't at the start of its word.</param>
	/// <param name="word">The word read so far, quotes removed.</param>
	/// <returns>
	/// <see langword="true" /> when the character typed just before is the word's first <c>=</c> or a <c>:</c>, and
	/// the word so far is an assignment.
	/// </returns>
	/// <remarks>
	/// Bash does this for any word shaped like an assignment, arguments included, which is what makes
	/// <c>make PREFIX=~/x</c> work. Only the first <c>=</c> counts, so <c>A=b=~</c> keeps its <c>~</c>.
	/// </remarks>
	private static bool IsAssignmentValueStart(string line, int index, StringBuilder word)
	{
		if (line[index - 1] is not ('=' or ':'))
		{
			return false;
		}

		var text = word.ToString();

		return DeclareCommand.IsAssignment(text) && (line[index - 1] == ':' || text.IndexOf('=', StringComparison.Ordinal) == text.Length - 1);
	}

	/// <summary>
	/// Reads the <c>~</c> or <c>~user</c>, if one starts at the unquoted <paramref name="index" />, and finds the home
	/// folder it names.
	/// </summary>
	/// <param name="line">The command line being split.</param>
	/// <param name="index">The position of the <c>~</c>.</param>
	/// <param name="word">The word read so far, quotes removed.</param>
	/// <param name="hasWord">
	/// <see langword="true" /> when the <c>~</c> isn't at the start of its word, so it only expands in a
	/// <c>NAME=value</c> word, where a <c>:</c> also ends the user name.
	/// </param>
	/// <param name="variables">
	/// The shell variables, which <c>HOME</c> is read from first, or <see langword="null" /> to leave <c>~</c> as typed.
	/// </param>
	/// <param name="home">The home folder, or an empty string when there is none.</param>
	/// <param name="end">The position of the last character the user name used, or of the <c>~</c> itself.</param>
	/// <returns>
	/// <see langword="false" /> when there is no <c>~</c> or it should stay as typed: it's in the middle of a word,
	/// part of the name is quoted, escaped or a <c>$</c>, or no user has that name.
	/// </returns>
	/// <remarks>
	/// Windows has no lookup of another user's home folder, so there <c>~user</c> always stays as typed.
	/// </remarks>
	private static bool TryExpandTilde(string line, int index, StringBuilder word, bool hasWord, IReadOnlyDictionary<string, string>? variables, out string home, out int end)
	{
		home = string.Empty;
		end = index;

		if (line[index] != '~' || variables is null || (hasWord && !IsAssignmentValueStart(line, index, word)))
		{
			return false;
		}

		while (end + 1 < line.Length && line[end + 1] is not ('/' or ' ' or '\t') && !(hasWord && line[end + 1] == ':')
			&& !OperatorStarts.Contains(line[end + 1]))
		{
			end++;
		}

		// Quotes, backslashes and $ stay in the name, so ~'x' and ~$USER find no user and keep the ~, as in bash.
		var user = line[(index + 1)..(end + 1)];

		// .NET's UserProfile reads HOME on Unix and the profile folder on Windows, which usually has no HOME.
		home = user.Length == 0
			? Lookup(variables, "HOME") is { Length: > 0 } set ? set : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
			: (OperatingSystem.IsWindows() ? null : NativeMethods.HomeOf(user)) ?? string.Empty;

		return user.Length == 0 || home.Length > 0;
	}

	/// <summary>
	/// Quotes <paramref name="text" /> as one word for a POSIX shell, which <see cref="Split" /> also reads back as one
	/// word.
	/// </summary>
	/// <param name="text">The text, such as a path.</param>
	/// <returns>
	/// The text in single quotes, with each <c>'</c> inside written as <c>'\''</c>.
	/// </returns>
	internal static string Quote(string text) => $"'{text.ArgumentNotNull().Replace("'", @"'\''", StringComparison.Ordinal)}'";

	/// <summary>
	/// Quotes <paramref name="text" /> through <see cref="Quote" /> only when a shell would read it differently
	/// unquoted, the way bash writes values in <c>set</c> and <c>set -x</c> output.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <returns>
	/// The text as is when it's made only of letters, digits and <c>_-./:,+=@%~</c> and doesn't start with <c>~</c>.
	/// Otherwise, an empty text included, the quoted text.
	/// </returns>
	internal static string QuoteIfNeeded(string text) =>
		text.ArgumentNotNull().Length > 0 && !text.AsSpan().ContainsAnyExcept(PlainCharacters) && text[0] != '~' ? text : Quote(text);

	/// <summary>
	/// Gets the value of the variable <paramref name="name" />.
	/// </summary>
	/// <param name="variables">The shell variables, which win over the environment.</param>
	/// <param name="name">The variable name.</param>
	/// <returns>
	/// The shell variable, else the environment variable, else an empty string.
	/// </returns>
	/// <remarks>
	/// POSIX makes every environment variable a shell variable when the shell starts, so <c>$PATH</c> and <c>$HOME</c>
	/// expand without being declared first.
	/// </remarks>
	internal static string Lookup(IReadOnlyDictionary<string, string> variables, string name) =>
		variables.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name) ?? string.Empty;

	/// <summary>
	/// Gets the characters that split an unquoted expansion's value into words.
	/// </summary>
	/// <param name="variables">The shell variables, which win over the environment.</param>
	/// <returns>
	/// The value of <c>IFS</c>, or <see cref="DefaultSeparators" /> when it isn't set. An empty <c>IFS</c> splits
	/// nothing.
	/// </returns>
	internal static string FieldSeparators(IReadOnlyDictionary<string, string> variables) =>
		IsSet(variables, "IFS") ? Lookup(variables, "IFS") : DefaultSeparators;

	/// <summary>
	/// Gets what the word after a redirect operator names.
	/// </summary>
	/// <param name="typed">The operator, with or without the stream number typed before it.</param>
	/// <returns>
	/// The kind of target, or <see langword="null" /> when <paramref name="typed" /> isn't a redirect Sharpie runs.
	/// <c>&lt;&gt;</c> isn't one yet.
	/// </returns>
	internal static TargetKind? RedirectKind(string typed) => typed.ArgumentNotNull().TrimStart(Digits) switch
	{
		"<" or ">" or ">>" or ">|" or "&>" or "&>>" => TargetKind.File,
		"<&" or ">&" => TargetKind.Stream,
		"<<" or "<<-" or "<<<" => TargetKind.Text,
		_ => null,
	};

	/// <summary>
	/// Takes the redirects out of one command's words, in the order they were typed.
	/// </summary>
	/// <param name="stage">One command of a pipeline, from <see cref="SplitPipeline" />.</param>
	/// <returns>
	/// The words left for the command, and each redirect with the word after its operator, kept whole so an error can
	/// point at it. An operator with no word after it, another operator in its place, or a stream number above
	/// <see cref="MaxStream" /> stays in the words, so the command is refused rather than run without it.
	/// </returns>
	/// <remarks>
	/// <c>&gt;&amp;word</c> on stream 1 means <c>&amp;&gt;word</c> when the word isn't a stream number or <c>-</c>, as in
	/// bash, so <c>&gt;&amp;out.txt</c> writes both streams to the file.
	/// </remarks>
	public static (Word[] Words, Redirect[] Redirects) SplitRedirect(Word[] stage)
	{
		_ = stage.ArgumentNotNull();

		var words = new List<Word>();
		var redirects = new List<Redirect>();

		for (var index = 0; index < stage.Length; index++)
		{
			if (stage[index] is { IsOperator: true, Text: var typed } && index + 1 < stage.Length && !stage[index + 1].IsOperator
				&& StreamOf(typed) is { } stream)
			{
				var target = stage[++index];
				var operatorText = typed.TrimStart(Digits);

				redirects.Add(operatorText == ">&" && stream == 1 && target.Text != "-" && !IsNumber(target.Text)
					? new Redirect(stream, "&>", target)
					: new Redirect(stream, operatorText, target));
			}
			else
			{
				words.Add(stage[index]);
			}
		}

		return ([.. words], [.. redirects]);
	}

	/// <summary>
	/// Gets the stream a redirect operator works on.
	/// </summary>
	/// <param name="typed">The operator as typed, stream number included.</param>
	/// <returns>
	/// The typed number, or 0 for a <c>&lt;</c> operator and 1 for the others when none was typed.
	/// <see langword="null" /> when <paramref name="typed" /> isn't a redirect Sharpie runs or its number is above
	/// <see cref="MaxStream" />.
	/// </returns>
	internal static int? StreamOf(string typed)
	{
		var digits = typed.ArgumentNotNull().Length - typed.TrimStart(Digits).Length;

		return RedirectKind(typed) is null ? null
			: digits == 0 ? (typed[0] == '<' ? 0 : 1)
			: int.TryParse(typed.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var stream) && stream <= MaxStream ? stream
			: null;
	}

	/// <summary>
	/// Checks whether <paramref name="text" /> is a stream number, as in <c>2&gt;&amp;1</c>.
	/// </summary>
	/// <param name="text">The word after the operator.</param>
	/// <returns>
	/// <see langword="true" /> when it's one or more digits and nothing else.
	/// </returns>
	internal static bool IsNumber(string text) => text.Length > 0 && !text.AsSpan().ContainsAnyExceptInRange('0', '9');

	/// <summary>
	/// Splits <paramref name="words" /> at every <c>|</c> operator into the commands of a pipeline.
	/// </summary>
	/// <param name="words">The words from <see cref="Split" />.</param>
	/// <returns>
	/// Each command's words, redirects included, in order. With no <c>|</c> this is one command. A command is empty
	/// when a <c>|</c> has nothing on one side, as in <c>a |</c> or <c>a | | b</c>.
	/// </returns>
	public static Word[][] SplitPipeline(Word[] words)
	{
		_ = words.ArgumentNotNull();

		var stages = new List<Word[]>();
		var start = 0;

		for (var index = 0; index <= words.Length; index++)
		{
			if (index == words.Length || words[index] is { IsOperator: true, Text: "|" })
			{
				stages.Add(words[start..index]);
				start = index + 1;
			}
		}

		return [.. stages];
	}

	/// <summary>
	/// Gets the here-documents a command's first line starts, in order.
	/// </summary>
	/// <param name="command">The command as typed, or just its first line.</param>
	/// <returns>
	/// Each <c>&lt;&lt;</c> or <c>&lt;&lt;-</c>'s delimiter with its quotes removed, whether any part of it was quoted
	/// or escaped, which keeps the body literal, and whether it's <c>&lt;&lt;-</c>, which drops leading tabs.
	/// </returns>
	internal static (string Delimiter, bool Quoted, bool StripTabs)[] HereDocuments(string command)
	{
		var words = SplitWords(command, null, null, null, null, out _);

		return [.. HereDocumentTargets(words).Select(index => (words[index].Text, IsQuoted(command, words[index]), words[index - 1].Text.EndsWith('-')))];
	}

	/// <summary>
	/// Reads one here-document's body, line by line, up to the line that holds only its delimiter.
	/// </summary>
	/// <param name="text">The text holding the body.</param>
	/// <param name="start">Where the body's first line starts.</param>
	/// <param name="delimiter">The delimiter, quotes removed.</param>
	/// <param name="stripTabs">
	/// <see langword="true" /> for <c>&lt;&lt;-</c>, which drops the tabs at the start of each line, the delimiter
	/// line's included.
	/// </param>
	/// <returns>
	/// The body with every line ending in a newline and nothing expanded, where the text after the delimiter line
	/// starts, and whether the delimiter line was found. Without it the body runs to the end of
	/// <paramref name="text" />.
	/// </returns>
	internal static (string Body, int End, bool Closed) ReadHereDocument(string text, int start, string delimiter, bool stripTabs)
	{
		_ = text.ArgumentNotNull();

		var body = new StringBuilder();
		var position = start;

		while (position < text.Length)
		{
			var newline = text.IndexOf('\n', position);
			var lineEnd = newline < 0 ? text.Length : newline;
			var line = text[position..lineEnd];

			position = Math.Min(lineEnd + 1, text.Length);
			line = stripTabs ? line.TrimStart('\t') : line;

			if (line == delimiter)
			{
				return (body.ToString(), position, true);
			}

			_ = body.Append(line).Append('\n');
		}

		return (body.ToString(), position, false);
	}

	/// <summary>
	/// Expands the body of a here-document whose delimiter wasn't quoted, as bash does.
	/// </summary>
	/// <param name="body">The body from <see cref="ReadHereDocument" />.</param>
	/// <param name="variables">The shell variables.</param>
	/// <param name="unset">Called for a variable that isn't set, as <see cref="Split" /> describes.</param>
	/// <param name="substitute">
	/// Runs each <c>$(...)</c> and backquotes for its output, or <see langword="null" /> to keep them as typed.
	/// </param>
	/// <returns>
	/// The body with <c>$NAME</c>, <c>${NAME}</c>, <c>$?</c> and the positional parameters replaced as
	/// <see cref="Split" /> replaces them inside double quotes, except that <c>$@</c> joins its parameters too, and
	/// each command substitution replaced by its output and each <c>$((...))</c> by its number, never split. Inside
	/// backquotes a backslash before <c>"</c> stays, as in bash. A backslash is dropped only before <c>$</c>, <c>`</c>, <c>\</c> or a newline, and a backslash before a newline
	/// joins the two lines. Quotes are ordinary characters.
	/// </returns>
	internal static string ExpandBody(string body, IReadOnlyDictionary<string, string> variables, Action<Word>? unset = null, Func<string, string>? substitute = null)
	{
		var text = new StringBuilder();

		for (var index = 0; index < body.Length; index++)
		{
			if (body[index] == '\\' && index + 1 < body.Length && body[index + 1] is '$' or '`' or '\\' or '\n')
			{
				_ = body[++index] == '\n' ? text : text.Append(body[index]);
			}
			else if (body[index] == '$' && ArithmeticEnd(body, index) is var sum and >= 0)
			{
				_ = text.Append(ExpandArithmetic(body, index, sum, variables, unset, substitute));
				index = sum;
			}
			else if (SubstitutionEnd(body, index) is var close and >= 0)
			{
				_ = text.Append(substitute is null ? body[index..(close + 1)] : SubstitutionOutput(body, index, close, inDoubleQuotes: false, substitute));
				index = close;
			}
			else if (TryExpand(body, index, variables, quoted: true, separators: null, joiner: " ", unset, substitute, out var parts, out _, out var end))
			{
				_ = text.AppendJoin(' ', parts);
				index = end;
			}
			else
			{
				_ = text.Append(body[index]);
			}
		}

		return text.ToString();
	}

	/// <summary>
	/// Gets the backslash that stays in a word before an escaped character, if any.
	/// </summary>
	/// <param name="quote">The quote that's open, or <see langword="null" />.</param>
	/// <param name="character">The escaped character.</param>
	/// <returns>
	/// <c>\</c> inside double quotes before anything but <c>"</c>, <c>\</c>, <c>$</c>, <c>`</c> and a newline, which
	/// are the only characters bash escapes there; otherwise, an empty string.
	/// </returns>
	private static string KeptBackslash(char? quote, char character) =>
		quote == '"' && character is not ('"' or '\\' or '$' or '`' or '\n') ? "\\" : string.Empty;

	/// <summary>
	/// Finds where the line holding <paramref name="index" /> ends.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="index">A position in it.</param>
	/// <returns>
	/// The position of the next newline, or the text's length when there is none.
	/// </returns>
	internal static int LineEnd(string text, int index) => text.IndexOf('\n', index) is var newline and >= 0 ? newline : text.Length;

	/// <summary>
	/// Finds the words that are here-document delimiters.
	/// </summary>
	/// <param name="words">The words of a command's first line.</param>
	/// <returns>
	/// The index of each word right after a <c>&lt;&lt;</c> or <c>&lt;&lt;-</c>, in order. An operator in its place
	/// isn't one.
	/// </returns>
	private static IEnumerable<int> HereDocumentTargets(Word[] words) =>
		Enumerable.Range(1, Math.Max(words.Length - 1, 0)).Where(index => IsDelimiterOperator(words[index - 1]) && !words[index].IsOperator);

	/// <summary>
	/// Gets what the word being read right now expands with.
	/// </summary>
	/// <param name="words">The words read before it.</param>
	/// <param name="variables">The shell variables, or <see langword="null" /> to expand nothing.</param>
	/// <param name="substitute">Runs a command substitution, as <see cref="Split" /> describes.</param>
	/// <returns>
	/// <paramref name="variables" /> and <paramref name="substitute" />, or neither for a here-document's delimiter,
	/// which is matched as typed, so <c>&lt;&lt;$X</c> ends at a line holding <c>$X</c>, and <c>&lt;&lt;$(x)</c> at
	/// <c>$(x)</c>.
	/// </returns>
	private static (IReadOnlyDictionary<string, string>? Variables, Func<string, string>? Substitute) ExpansionsFor(List<Word> words, IReadOnlyDictionary<string, string>? variables, Func<string, string>? substitute) =>
		IsDelimiter(words) ? (null, null) : (variables, substitute);

	/// <summary>
	/// Checks whether the word being read right now is a here-document delimiter, which expands nothing.
	/// </summary>
	/// <param name="words">The words read before it.</param>
	/// <returns>
	/// <see langword="true" /> when the last of them is <c>&lt;&lt;</c> or <c>&lt;&lt;-</c>.
	/// </returns>
	private static bool IsDelimiter(List<Word> words) => words is [.., var previous] && IsDelimiterOperator(previous);

	/// <summary>
	/// Checks whether <paramref name="word" /> is <c>&lt;&lt;</c> or <c>&lt;&lt;-</c>, with or without a stream number.
	/// </summary>
	/// <param name="word">The word.</param>
	/// <returns>
	/// <see langword="true" /> for either operator.
	/// </returns>
	private static bool IsDelimiterOperator(Word word) => word is { IsOperator: true, Text: var typed } && typed.TrimStart(Digits) is "<<" or "<<-";

	/// <summary>
	/// Checks whether any part of a here-document's delimiter was quoted or escaped, which keeps the body literal.
	/// </summary>
	/// <param name="line">The command as typed.</param>
	/// <param name="delimiter">The delimiter word.</param>
	/// <returns>
	/// <see langword="true" /> when its typed text holds <c>'</c>, <c>"</c> or <c>\</c>.
	/// </returns>
	private static bool IsQuoted(string line, Word delimiter) => line.AsSpan(delimiter.Start, delimiter.End - delimiter.Start).ContainsAny('\'', '"', '\\');

	/// <summary>
	/// Gets the words of a line that only runs <c>man</c> or <c>dman</c>, which the terminal window shows in its own
	/// reader.
	/// </summary>
	/// <param name="line">The line the user entered.</param>
	/// <returns>
	/// The words, starting with <c>man</c> or <c>dman</c>, or <see langword="null" /> when the line is anything else,
	/// including either command with no page, with a redirect, pipe, other operator or command substitution, or with
	/// other commands after <c>;</c>, <c>&amp;&amp;</c> or <c>||</c>. The shell runs those itself, and the page prints
	/// as text.
	/// </returns>
	public static string[]? PagedCommand(string line)
	{
		if (CommandList.Split(line.ArgumentNotNull()) is not { Count: 1 })
		{
			return null;
		}

		// Only notes the substitution, so nothing runs before the shell runs the line itself.
		var substituted = false;
		var (words, redirects) = SplitRedirect(Split(line, substitute: _ =>
		{
			substituted = true;

			return string.Empty;
		}));

		return !substituted && words is [{ IsOperator: false, Text: "man" or "dman" }, _, ..] && redirects is []
			&& !Array.Exists(words, word => word.IsOperator)
			? [.. words.Select(word => word.Text)]
			: null;
	}

	/// <summary>
	/// Follows the <c>case</c> commands inside a <c>$(...)</c>, so the <c>)</c> that ends a pattern doesn't end the
	/// substitution.
	/// </summary>
	private sealed class CaseTracker
	{
		/// <summary>
		/// The characters that end a word outside quotes.
		/// </summary>
		private static readonly SearchValues<char> WordEnds = SearchValues.Create(" \t\n;&|()<>");

		/// <summary>
		/// The parenthesis depth each open <c>case</c> started at.
		/// </summary>
		private readonly List<int> open = [];

		/// <summary>
		/// <see langword="true" /> where a command's first word can start, so a reserved word there counts.
		/// </summary>
		private bool commandStart = true;

		/// <summary>
		/// Checks whether a <c>)</c> ends a pattern of the innermost <c>case</c>.
		/// </summary>
		/// <param name="depth">The parenthesis depth the <c>)</c> is at.</param>
		/// <returns>
		/// <see langword="true" /> when that <c>case</c> started at this depth.
		/// </returns>
		public bool EndsPattern(int depth) => this.open is [.., var start] && start == depth;

		/// <summary>
		/// Reads the reserved word that starts at <paramref name="index" />, if a command can start there.
		/// </summary>
		/// <param name="line">The text.</param>
		/// <param name="index">Where a word starts, outside quotes.</param>
		/// <param name="depth">The parenthesis depth there.</param>
		/// <returns>
		/// The reserved word's length, or 0 when the word there is any other or isn't in a command's place.
		/// </returns>
		public int ReadReservedWord(string line, int index, int depth)
		{
			var rest = line.AsSpan(index);
			var word = rest.IndexOfAny(WordEnds) is >= 0 and var length ? rest[..length] : rest;
			string? reserved = null;

			// A blank or operator character; Read notes those.
			if (word.IsEmpty)
			{
				return 0;
			}

			foreach (var keyword in this.commandStart ? CommandParser.ReservedWords : [])
			{
				reserved = word.SequenceEqual(keyword) ? keyword : reserved;
			}

			if (reserved == "case")
			{
				this.open.Add(depth);
			}
			else if (reserved == "esac" && this.EndsPattern(depth))
			{
				this.open.RemoveAt(this.open.Count - 1);
			}

			// The word after case is the one it matches, not a command; any other reserved word starts a command.
			this.commandStart = reserved is not null and not "case";

			return reserved?.Length ?? 0;
		}

		/// <summary>
		/// Notes a character read outside a reserved word.
		/// </summary>
		/// <param name="character">The character.</param>
		/// <param name="quote">The quote open after it, or <see langword="null" />.</param>
		public void Read(char character, char? quote) =>
			this.commandStart = quote is null && (character is ' ' or '\t' ? this.commandStart : character is ';' or '&' or '|' or '(' or ')' or '\n');
	}
}

/// <summary>
/// Tracks where <see cref="CommandLine.Split" /> is between <c>[[</c> and <c>]]</c>, where words read as bash reads a
/// condition.
/// </summary>
internal sealed class ConditionWords
{
	/// <summary>
	/// How deep the word after <c>=~</c> is in unquoted parentheses, where blanks are part of it.
	/// </summary>
	private int depth;

	/// <summary>
	/// Gets a value indicating whether the words being read are between <c>[[</c> and <c>]]</c>.
	/// </summary>
	/// <value>
	/// <see langword="true" /> inside a condition.
	/// </value>
	public bool Active { get; private set; }

	/// <summary>
	/// Gets a value indicating whether the word being read follows <c>=~</c>, so it's a regular expression.
	/// </summary>
	/// <value>
	/// <see langword="true" /> for that word.
	/// </value>
	public bool Regex { get; private set; }

	/// <summary>
	/// Reads the operator, if any, that starts at the unquoted <paramref name="index" />, and counts the parentheses
	/// of a regular expression.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">The position to look at.</param>
	/// <returns>
	/// What <see cref="CommandLine.OperatorAt" /> gives, or <see langword="null" /> for <c>(</c>, <c>)</c> and
	/// <c>|</c> in the word after <c>=~</c>, which are part of the expression.
	/// </returns>
	public string? OperatorAt(string line, int index)
	{
		if (!this.Regex || line[index] is not ('(' or ')' or '|'))
		{
			return CommandLine.OperatorAt(line, index);
		}

		this.depth += line[index] switch { '(' => 1, ')' => -1, _ => 0 };

		return null;
	}

	/// <summary>
	/// Checks whether what an expansion gave makes a word, even when it's empty.
	/// </summary>
	/// <param name="part">One part of the expansion.</param>
	/// <param name="quoted"><see langword="true" /> when the expansion was quoted.</param>
	/// <returns>
	/// <see langword="true" /> when the part isn't empty, was quoted, or is in a condition, where bash keeps an empty
	/// unquoted expansion so <c>[[ -n $x ]]</c> fails for an empty <c>x</c>.
	/// </returns>
	public bool KeepsPart(string part, bool quoted) => quoted || this.Active || part.Length > 0;

	/// <summary>
	/// Checks whether an unquoted character ends the word being read.
	/// </summary>
	/// <param name="character">The character.</param>
	/// <returns>
	/// <see langword="true" /> for a space, tab or newline, except a space or tab inside the parentheses of a regular
	/// expression.
	/// </returns>
	public bool EndsWord(char character) => character == '\n' || (character is ' ' or '\t' && this.depth <= 0);

	/// <summary>
	/// Checks whether digits before an operator name the stream it redirects, which they never do in a condition.
	/// </summary>
	/// <param name="typed">The word's characters as typed.</param>
	/// <param name="typedOperator">The operator right after it.</param>
	/// <returns>
	/// What <see cref="CommandLine.IsStreamNumber" /> gives, or <see langword="false" /> inside a condition.
	/// </returns>
	public bool IsStreamNumber(ReadOnlySpan<char> typed, string typedOperator) => !this.Active && CommandLine.IsStreamNumber(typed, typedOperator);

	/// <summary>
	/// Reads a finished word, which starts or ends a condition or belongs to one.
	/// </summary>
	/// <param name="text">The word with its quotes removed.</param>
	/// <param name="start">Where it starts in <paramref name="line" />.</param>
	/// <param name="end">Where it ends.</param>
	/// <param name="pattern">The word as a pattern.</param>
	/// <param name="line">The command line, for the word's text as typed.</param>
	/// <param name="first"><see langword="true" /> when it's the line's first word.</param>
	/// <returns>
	/// An unquoted <c>[[</c> first or the <c>]]</c> after it as an operator, any word between them with its
	/// <see cref="Word.Pattern" />, or <see langword="null" /> outside a condition, where the caller expands the word.
	/// </returns>
	public Word[]? Read(string text, int start, int end, StringBuilder pattern, string line, bool first)
	{
		var typed = line.AsSpan(start, end - start);

		if ((first && typed is "[[") || (this.Active && typed is "]]"))
		{
			this.Active = !this.Active;
			this.Regex = false;

			return [new Word(text, IsOperator: true, start, end)];
		}

		this.Regex = this.Active && typed is "=~";
		this.depth = 0;

		return this.Active ? [new Word(text, IsOperator: false, start, end, pattern.ToString())] : null;
	}
}
