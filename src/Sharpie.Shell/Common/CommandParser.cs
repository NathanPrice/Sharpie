using System.Diagnostics.CodeAnalysis;
using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Common;

/// <summary>
/// What <see cref="CommandParser.Parse" /> made of a text.
/// </summary>
/// <param name="List">The commands, or <see langword="null" /> when <paramref name="Error" /> is set.</param>
/// <param name="Error">Why the text can't run, or <see langword="null" />.</param>
/// <param name="Incomplete">
/// <see langword="true" /> when the text ends inside a compound command or right after <c>&amp;&amp;</c>, <c>||</c>
/// or <c>|</c>, so a reader with more lines should add the next one.
/// </param>
/// <param name="OpenQuote">The quote the text ends inside, or <see langword="null" />.</param>
/// <param name="Refused">
/// Where an unclosed <c>$(</c> or backquote, or a <c>$((</c>, starts; otherwise, -1.
/// </param>
/// <param name="Unclosed">The delimiter of a here-document whose body runs to the end, or <see langword="null" />.</param>
internal sealed record ParseResult(ListNode? List, ShellError? Error, bool Incomplete, char? OpenQuote, int Refused, string? Unclosed);

/// <summary>
/// Turns a text into a tree of commands by the POSIX shell grammar, as bash's <c>parse.y</c> does.
/// </summary>
/// <remarks>
/// The grammar is list, and-or list, pipeline with an optional <c>!</c>, then a command: a simple command, a compound
/// command with its redirects, or a function definition. Simple commands stay as typed, for
/// <see cref="CommandLine.Split" /> to expand when they run. Reserved words count only unquoted and as the first word
/// of a command, so <c>echo if fi</c> is one simple command and <c>"if"</c> is a word; <c>in</c> counts after
/// <c>for name</c> and <c>case word</c>, and <c>do</c> after a <c>for</c>'s words. <c>{</c> and <c>}</c> must be whole
/// words, so <c>}</c> closes a group only after <c>;</c> or a newline.
/// <para>
/// Quotes, backslashes, comments and command substitutions are read as <c>CommandList</c> always read them. At each
/// newline the here-documents started since the last one take the lines up to their delimiter lines.
/// </para>
/// </remarks>
internal sealed class CommandParser
{
	/// <summary>
	/// The words the grammar reserves, which <c>type</c> calls shell keywords.
	/// </summary>
	internal static readonly string[] ReservedWords = ["!", "{", "}", "case", "do", "done", "elif", "else", "esac", "fi", "for", "function", "if", "in", "then", "until", "while"];

	/// <summary>
	/// The reserved words that end a list rather than start a command.
	/// </summary>
	private static readonly string[] Closers = ["}", "do", "done", "elif", "else", "esac", "fi", "then"];

	private readonly string source;

	private readonly List<Token> tokens;

	private int position;

	private CommandParser(string source, List<Token> tokens)
	{
		this.source = source;
		this.tokens = tokens;
	}

	/// <summary>
	/// The kinds of token the scanner reads.
	/// </summary>
	private enum TokenKind
	{
		/// <summary>A word, quotes and substitutions included.</summary>
		Word,

		/// <summary>Digits right before a redirect operator, like the <c>2</c> in <c>2&gt;</c>.</summary>
		Number,

		/// <summary>An operator, like <c>;</c>, <c>|</c>, <c>(</c> or <c>&gt;</c>.</summary>
		Operator,

		/// <summary>An unquoted newline.</summary>
		Newline,

		/// <summary>The end of the text.</summary>
		End,
	}

	/// <summary>
	/// Parses <paramref name="text" />.
	/// </summary>
	/// <param name="text">The text, which may hold many lines.</param>
	/// <returns>
	/// The commands, or the first error. A text that ends too early is <see cref="ParseResult.Incomplete" />, with an
	/// error that says what's missing.
	/// </returns>
	public static ParseResult Parse(string text)
	{
		_ = text.ArgumentNotNull();

		var (tokens, refused, quote, unclosed) = new Scanner(text).Scan();

		if (refused >= 0)
		{
			return new ParseResult(null, RefusalAt(text, refused), Incomplete: false, null, refused, null);
		}

		var parser = new CommandParser(text, tokens);

		try
		{
			var list = parser.ParseList();

			return parser.Peek is { Kind: not TokenKind.End } extra
				? throw parser.Unexpected(extra, null)
				: new ParseResult(list, null, Incomplete: false, quote, -1, unclosed);
		}
		catch (SyntaxException exception)
		{
			return new ParseResult(null, exception.Error, exception.Incomplete, quote, -1, unclosed);
		}
	}

	/// <summary>
	/// Builds the report for an unclosed substitution or arithmetic.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="refused">Where the refused part starts.</param>
	/// <returns>
	/// The report, pointing at the opening.
	/// </returns>
	private static ShellError RefusalAt(string text, int refused)
	{
		var backquote = text[refused] == '`';

		var report = text.AsSpan(refused).StartsWith("$((", StringComparison.Ordinal)
			? ArithmeticError(refused + 3)
			: new ShellError("sharpie::parser::unclosed_substitution", backquote ? "The backquote is never closed" : "`$(` is never closed")
			{
				End = refused + (backquote ? 1 : 2),
				Label = "runs a command for its output",
				Help = backquote ? "add the closing backquote" : "add the missing `)`",
			};

		return report with { Source = text, Start = refused };
	}

	/// <summary>
	/// Builds the report for arithmetic, which the shell can't work out yet.
	/// </summary>
	/// <param name="end">Where the opening ends.</param>
	/// <returns>
	/// The report, without its source.
	/// </returns>
	private static ShellError ArithmeticError(int end) =>
		new("sharpie::parser::arithmetic", "Arithmetic expansion isn't supported")
		{
			End = end,
			Label = "works out a number",
			Help = "run a program such as expr for the number, or quote it to pass it as text",
		};

	/// <summary>
	/// Gets the token at the current position.
	/// </summary>
	/// <value>
	/// The token, which is <see cref="TokenKind.End" /> once every other one is read.
	/// </value>
	private Token Peek => this.tokens[this.position];

	/// <summary>
	/// Checks whether <paramref name="token" /> is the operator <paramref name="typed" />.
	/// </summary>
	/// <param name="token">The token.</param>
	/// <param name="typed">The operator.</param>
	/// <returns>
	/// <see langword="true" /> when it is.
	/// </returns>
	private static bool IsOperator(Token token, string typed) => token.Kind == TokenKind.Operator && token.Text == typed;

	/// <summary>
	/// Checks whether <paramref name="token" /> is the reserved word <paramref name="word" />, typed unquoted.
	/// </summary>
	/// <param name="token">The token.</param>
	/// <param name="word">The reserved word.</param>
	/// <returns>
	/// <see langword="true" /> when it is.
	/// </returns>
	private static bool IsWord(Token token, string word) => token.Kind == TokenKind.Word && token.Text == word;

	/// <summary>
	/// Checks whether a token starts a compound command.
	/// </summary>
	/// <param name="token">The token.</param>
	/// <returns>
	/// <see langword="true" /> for <c>(</c>, <c>{</c>, <c>if</c>, <c>while</c>, <c>until</c>, <c>for</c> and
	/// <c>case</c>.
	/// </returns>
	private static bool IsCompoundStart(Token token) =>
		IsOperator(token, "(") || (token.Kind == TokenKind.Word && token.Text is "{" or "if" or "while" or "until" or "for" or "case");

	/// <summary>
	/// Checks whether a token is a redirect operator.
	/// </summary>
	/// <param name="token">The token.</param>
	/// <returns>
	/// <see langword="true" /> for any operator that starts with <c>&lt;</c> or <c>&gt;</c>, and <c>&amp;&gt;</c>.
	/// </returns>
	private static bool IsRedirect(Token token) => token.Kind == TokenKind.Operator && (token.Text[0] is '<' or '>' || token.Text.StartsWith("&>", StringComparison.Ordinal));

	/// <summary>
	/// Checks whether a word can name a function.
	/// </summary>
	/// <param name="name">The word as typed.</param>
	/// <returns>
	/// <see langword="true" /> when it needs no quoting, holds no <c>=</c> and isn't a reserved word.
	/// </returns>
	private static bool IsFunctionName(string name) =>
		CommandLine.QuoteIfNeeded(name) == name && !name.Contains('=', StringComparison.Ordinal) && !ReservedWords.Contains(name);

	/// <summary>
	/// Moves past the current token.
	/// </summary>
	/// <returns>
	/// The token moved past.
	/// </returns>
	private Token Next() => this.tokens[this.position++];

	/// <summary>
	/// Moves past newlines, which may come between the parts of a compound command.
	/// </summary>
	private void SkipNewlines()
	{
		while (this.Peek.Kind == TokenKind.Newline)
		{
			this.position++;
		}
	}

	/// <summary>
	/// Checks whether the current token ends a list rather than starting a command.
	/// </summary>
	/// <returns>
	/// <see langword="true" /> at the end, at <c>)</c>, at a <c>case</c> item's end, or at a reserved word that closes
	/// a compound command.
	/// </returns>
	private bool AtListEnd() =>
		this.Peek is { Kind: TokenKind.End }
		|| (this.Peek.Kind == TokenKind.Operator && this.Peek.Text is ")" or ";;" or ";&" or ";;&")
		|| (this.Peek.Kind == TokenKind.Word && Closers.Contains(this.Peek.Text));

	/// <summary>
	/// Reads commands joined by <c>;</c>, <c>&amp;</c> and newlines, up to a token that can't start one.
	/// </summary>
	/// <returns>
	/// The list, which is empty for a text of only blanks, comments and newlines.
	/// </returns>
	private ListNode ParseList()
	{
		var items = new List<ListItem>();

		while (true)
		{
			this.SkipNewlines();

			if (this.AtListEnd())
			{
				return new ListNode(items);
			}

			var command = this.ParseAndOr();

			if (IsOperator(this.Peek, "&"))
			{
				var ampersand = this.Next();

				// One simple pipeline runs as a job the way it always has, with the & left in its text.
				var text = command is { Rest: [], First: { Negated: false, Stages: [Leaf leaf] } }
					? leaf.Line + this.source[leaf.End..ampersand.End] + leaf.Bodies
					: command.Text;

				items.Add(new ListItem(command, Background: true, text));

				continue;
			}

			items.Add(new ListItem(command, Background: false, command.Text));

			if (IsOperator(this.Peek, ";"))
			{
				_ = this.Next();
			}
			else if (this.Peek.Kind != TokenKind.Newline)
			{
				return new ListNode(items);
			}
		}
	}

	/// <summary>
	/// Reads the list inside a compound command, which needs at least one command.
	/// </summary>
	/// <param name="closer">The word or operator that should come after it, for the report when it's empty.</param>
	/// <returns>
	/// The list.
	/// </returns>
	private ListNode ParseCompoundList(string closer)
	{
		var list = this.ParseList();

		return list.Items.Count > 0 ? list : throw this.Unexpected(this.Peek, this.Peek.Kind == TokenKind.End ? closer : "a command");
	}

	/// <summary>
	/// Reads pipelines joined by <c>&amp;&amp;</c> and <c>||</c>, each of which may be followed by newlines.
	/// </summary>
	/// <returns>
	/// The and-or list.
	/// </returns>
	private AndOr ParseAndOr()
	{
		var start = this.position;
		var first = this.ParsePipeline();
		var rest = new List<(string Operator, Pipeline Pipeline)>();

		while (IsOperator(this.Peek, CommandList.And) || IsOperator(this.Peek, CommandList.Or))
		{
			var typed = this.Next().Text;

			this.SkipNewlines();
			rest.Add((typed, this.ParsePipeline()));
		}

		var end = rest is [.., (_, var last)] ? last.End : first.End;

		return new AndOr(first.Start, end, first, rest) { Text = this.TextOf(start, first.Start, end) };
	}

	/// <summary>
	/// Reads commands joined by <c>|</c>, after an optional <c>!</c>.
	/// </summary>
	/// <returns>
	/// The pipeline. When every stage is a simple command it holds one <see cref="Leaf" /> for the whole pipeline.
	/// </returns>
	private Pipeline ParsePipeline()
	{
		var start = this.position;
		var negated = IsWord(this.Peek, "!");
		var bang = negated ? this.Next().Start : -1;
		var stages = new List<CommandNode> { this.ParseCommand() };
		var pipes = new List<string>();

		while (IsOperator(this.Peek, "|") || IsOperator(this.Peek, "|&"))
		{
			pipes.Add(this.Next().Text);
			this.SkipNewlines();
			stages.Add(this.ParseCommand());
		}

		if (stages.Count > 1 && stages.TrueForAll(stage => stage is Leaf))
		{
			stages = [this.Joined([.. stages.Cast<Leaf>()], pipes)];
		}

		var begin = negated ? bang : stages[0].Start;

		return new Pipeline(begin, stages[^1].End, negated, stages, pipes) { Text = this.TextOf(start, begin, stages[^1].End) };
	}

	/// <summary>
	/// Reads one command: a compound command with its redirects, a function definition, or a simple command.
	/// </summary>
	/// <returns>
	/// The command.
	/// </returns>
	private CommandNode ParseCommand()
	{
		var token = this.Peek;

		return IsCompoundStart(token) ? this.ParseCompound()
			: token.Kind == TokenKind.Word && Closers.Contains(token.Text) ? throw this.Unexpected(token, "a command")
			: token.Kind == TokenKind.Word && IsOperator(this.tokens[this.position + 1], "(") && IsFunctionName(token.Text) ? this.ParseFunction()
			: IsWord(token, "function") ? this.ParseFunction()
			: token.Kind is TokenKind.Word or TokenKind.Number || IsRedirect(token) ? this.ParseSimple()
			: throw this.Unexpected(token, "a command");
	}

	/// <summary>
	/// Reads a simple command: words and redirects, up to an operator that ends it.
	/// </summary>
	/// <returns>
	/// The command as a leaf.
	/// </returns>
	/// <remarks>
	/// A redirect with no word after it stays in the command, for <c>Program.RunCommand</c> to report as it always has.
	/// </remarks>
	private Leaf ParseSimple()
	{
		var first = this.position;
		var start = this.Peek.Start;
		var end = start;

		while (this.Peek.Kind is TokenKind.Word or TokenKind.Number || IsRedirect(this.Peek))
		{
			var token = this.Next();

			end = token.End;

			if (IsRedirect(token) && this.Peek.Kind == TokenKind.Word)
			{
				end = this.Next().End;
			}
		}

		return IsOperator(this.Peek, "(") ? throw this.Unexpected(this.Peek, null) : this.LeafOf(first, start, end);
	}

	/// <summary>
	/// Reads the redirects after a compound command.
	/// </summary>
	/// <returns>
	/// The redirects as a leaf, or <see langword="null" /> when there are none.
	/// </returns>
	private Leaf? ParseRedirects()
	{
		var first = this.position;
		var start = this.Peek.Start;
		var end = -1;

		while (this.Peek.Kind == TokenKind.Number || IsRedirect(this.Peek))
		{
			var token = this.Next();

			end = token.End;

			if (IsRedirect(token) && this.Peek.Kind == TokenKind.Word)
			{
				end = this.Next().End;
			}
		}

		return end < 0 ? null : this.LeafOf(first, start, end);
	}

	/// <summary>
	/// Reads a compound command and the redirects after it.
	/// </summary>
	/// <returns>
	/// The command.
	/// </returns>
	private Compound ParseCompound()
	{
		var first = this.position;
		var token = this.Next();
		var start = token.Start;

		Compound command = token.Text switch
		{
			"(" when IsOperator(this.Peek, "(") && this.Peek.Start == token.End => throw new SyntaxException(ArithmeticError(token.End + 1) with { Source = this.source, Start = start }, incomplete: false),
			"(" => new Subshell(start, 0, this.ParseClosed(")", ")")),
			"{" => new Group(start, 0, this.ParseClosed("}", "}")),
			"if" => this.ParseIf(start),
			"while" or "until" => this.ParseLoop(start, until: token.Text == "until"),
			"for" => this.ParseFor(start),
			_ => this.ParseCase(start),
		};

		var redirects = this.ParseRedirects();
		var end = redirects?.End ?? this.tokens[this.position - 1].End;

		return command with { End = end, Redirects = redirects, Text = this.TextOf(first, start, end) };
	}

	/// <summary>
	/// Reads a compound list and the word or operator that closes it.
	/// </summary>
	/// <param name="closer">The closing word, or <c>)</c>.</param>
	/// <param name="expected">How the report names the closer.</param>
	/// <returns>
	/// The list.
	/// </returns>
	private ListNode ParseClosed(string closer, string expected)
	{
		var list = this.ParseCompoundList(expected);

		this.Expect(closer);

		return list;
	}

	/// <summary>
	/// Moves past the reserved word or operator <paramref name="closer" />.
	/// </summary>
	/// <param name="closer">What must come next.</param>
	/// <exception cref="SyntaxException">Something else comes next.</exception>
	private void Expect(string closer)
	{
		if (!(closer == ")" ? IsOperator(this.Peek, closer) : IsWord(this.Peek, closer)))
		{
			throw this.Unexpected(this.Peek, closer);
		}

		_ = this.Next();
	}

	/// <summary>
	/// Reads the rest of an <c>if</c>, after the word itself.
	/// </summary>
	/// <param name="start">Where the <c>if</c> starts.</param>
	/// <returns>
	/// The command, its end not yet set.
	/// </returns>
	private If ParseIf(int start)
	{
		var branches = new List<(ListNode Condition, ListNode Body)> { (this.ParseClosed("then", "then"), this.ParseCompoundList("fi")) };
		ListNode? otherwise = null;

		while (IsWord(this.Peek, "elif"))
		{
			_ = this.Next();
			branches.Add((this.ParseClosed("then", "then"), this.ParseCompoundList("fi")));
		}

		if (IsWord(this.Peek, "else"))
		{
			_ = this.Next();
			otherwise = this.ParseCompoundList("fi");
		}

		this.Expect("fi");

		return new If(start, 0, branches, otherwise);
	}

	/// <summary>
	/// Reads the rest of a <c>while</c> or <c>until</c>.
	/// </summary>
	/// <param name="start">Where it starts.</param>
	/// <param name="until"><see langword="true" /> for <c>until</c>.</param>
	/// <returns>
	/// The command, its end not yet set.
	/// </returns>
	private Loop ParseLoop(int start, bool until) => new(start, 0, this.ParseClosed("do", "do"), this.ParseClosed("done", "done"), until);

	/// <summary>
	/// Reads the rest of a <c>for</c>: the name, the words after <c>in</c> when there are any, and the body.
	/// </summary>
	/// <param name="start">Where it starts.</param>
	/// <returns>
	/// The command, its end not yet set.
	/// </returns>
	private For ParseFor(int start)
	{
		var name = this.Peek;

		if (name.Kind != TokenKind.Word || !DeclareCommand.IsIdentifier(name.Text))
		{
			throw this.Unexpected(name, "a variable name");
		}

		_ = this.Next();
		this.SkipNewlines();

		string? words = null;

		if (IsWord(this.Peek, "in"))
		{
			_ = this.Next();

			var first = this.Peek;
			var last = first;

			while (this.Peek.Kind is TokenKind.Word or TokenKind.Number)
			{
				last = this.Next();
			}

			words = first.Kind is TokenKind.Word or TokenKind.Number ? this.source[first.Start..last.End] : string.Empty;
			this.ExpectSeparator();
		}
		else if (IsOperator(this.Peek, ";"))
		{
			_ = this.Next();
		}

		this.SkipNewlines();
		this.Expect("do");

		return new For(start, 0, name.Text, words, this.ParseClosed("done", "done"));
	}

	/// <summary>
	/// Moves past the <c>;</c> or newline that ends a <c>for</c>'s words.
	/// </summary>
	private void ExpectSeparator()
	{
		if (!IsOperator(this.Peek, ";") && this.Peek.Kind != TokenKind.Newline)
		{
			throw this.Unexpected(this.Peek, "`;` or a newline");
		}

		_ = this.Next();
	}

	/// <summary>
	/// Reads the rest of a <c>case</c>: the word, <c>in</c>, and each item up to <c>esac</c>.
	/// </summary>
	/// <param name="start">Where it starts.</param>
	/// <returns>
	/// The command, its end not yet set.
	/// </returns>
	private Case ParseCase(int start)
	{
		var word = this.Peek;

		if (word.Kind is not (TokenKind.Word or TokenKind.Number))
		{
			throw this.Unexpected(word, "a word");
		}

		_ = this.Next();
		this.SkipNewlines();
		this.Expect("in");

		var items = new List<(IReadOnlyList<string> Patterns, ListNode Body)>();

		while (true)
		{
			this.SkipNewlines();

			if (IsWord(this.Peek, "esac"))
			{
				_ = this.Next();

				return new Case(start, 0, word.Text, items);
			}

			if (IsOperator(this.Peek, "("))
			{
				_ = this.Next();
			}

			var patterns = new List<string> { this.ParsePattern() };

			while (IsOperator(this.Peek, "|"))
			{
				_ = this.Next();
				patterns.Add(this.ParsePattern());
			}

			this.Expect(")");
			items.Add((patterns, this.ParseList()));

			if (IsOperator(this.Peek, ";&") || IsOperator(this.Peek, ";;&"))
			{
				throw new SyntaxException(new ShellError("sharpie::parser::unsupported", $"`{this.Peek.Text}` isn't supported")
				{
					Source = this.source,
					Start = this.Peek.Start,
					End = this.Peek.End,
					Label = "runs the next item too",
					Help = "end the item with ;; and repeat the commands in the next one",
				}, incomplete: false);
			}

			if (!IsOperator(this.Peek, ";;") && !IsWord(this.Peek, "esac"))
			{
				throw this.Unexpected(this.Peek, "`;;` or `esac`");
			}

			if (IsOperator(this.Peek, ";;"))
			{
				_ = this.Next();
			}
		}
	}

	/// <summary>
	/// Reads one pattern of a <c>case</c> item.
	/// </summary>
	/// <returns>
	/// The pattern as typed.
	/// </returns>
	private string ParsePattern() =>
		this.Peek.Kind is TokenKind.Word or TokenKind.Number ? this.Next().Text : throw this.Unexpected(this.Peek, "a pattern");

	/// <summary>
	/// Reads a function definition: <c>name()</c>, or bash's <c>function name</c> with or without the
	/// <c>()</c>, and the compound command a call runs.
	/// </summary>
	/// <returns>
	/// The definition.
	/// </returns>
	private FunctionDefinition ParseFunction()
	{
		var first = this.position;
		var keyword = IsWord(this.Peek, "function");
		var start = keyword ? this.Next().Start : this.Peek.Start;
		var name = this.Peek;

		if (name.Kind != TokenKind.Word || !IsFunctionName(name.Text))
		{
			throw this.Unexpected(name, "a function name");
		}

		_ = this.Next();

		if (!keyword || IsOperator(this.Peek, "("))
		{
			_ = this.Next();

			// POSIX allows no line break between the parentheses, so the end of the text there is an error too.
			if (!IsOperator(this.Peek, ")"))
			{
				throw this.Unexpected(this.Peek, ")", canContinue: false);
			}

			_ = this.Next();
		}

		this.SkipNewlines();

		var body = IsCompoundStart(this.Peek) ? this.ParseCompound() : throw this.Unexpected(this.Peek, "`{`");

		return new FunctionDefinition(start, body.End, name.Text, body) { Text = this.TextOf(first, start, body.End) };
	}

	/// <summary>
	/// Builds the leaf for the tokens from <paramref name="first" /> to the current position.
	/// </summary>
	/// <param name="first">The index of its first token.</param>
	/// <param name="start">Where it starts in the text.</param>
	/// <param name="end">Where it ends.</param>
	/// <returns>
	/// The leaf, with the bodies of the here-documents among its tokens.
	/// </returns>
	private Leaf LeafOf(int first, int start, int end)
	{
		var bodies = string.Concat(this.tokens[first..this.position].Select(token => token.Body));
		var line = this.source[start..end];

		return new Leaf(start, end, line, bodies) { Text = line + bodies };
	}

	/// <summary>
	/// Builds the leaf for a pipeline of simple commands.
	/// </summary>
	/// <param name="stages">The stages.</param>
	/// <param name="pipes">The operator between each two stages.</param>
	/// <returns>
	/// The leaf, as typed on one line. A line break after a <c>|</c>, and the comment or here-document bodies before
	/// it, becomes a single <c>|</c> with blanks around it, since <see cref="CommandLine.Split" /> reads a command's
	/// words up to its first line break.
	/// </returns>
	private Leaf Joined(Leaf[] stages, List<string> pipes)
	{
		var line = new StringBuilder(stages[0].Line);

		for (var index = 1; index < stages.Length; index++)
		{
			var gap = this.source[stages[index - 1].End..stages[index].Start];

			_ = line.Append(gap.Contains('\n', StringComparison.Ordinal) ? $" {pipes[index - 1]} " : gap).Append(stages[index].Line);
		}

		var bodies = string.Concat(stages.Select(stage => stage.Bodies));
		var text = line.ToString();

		return new Leaf(stages[0].Start, stages[^1].End, text, bodies) { Text = text + bodies };
	}

	/// <summary>
	/// Gets the text of a command whose tokens run from <paramref name="first" /> to the current position.
	/// </summary>
	/// <param name="first">The index of its first token.</param>
	/// <param name="start">Where it starts in the text.</param>
	/// <param name="end">Where it ends.</param>
	/// <returns>
	/// The text as typed, followed by the here-document bodies among its tokens that come after
	/// <paramref name="end" />.
	/// </returns>
	private string TextOf(int first, int start, int end) =>
		this.source[start..end] + string.Concat(this.tokens[first..this.position].Where(token => token.Body.Length > 0 && token.BodyStart >= end).Select(token => token.Body));

	/// <summary>
	/// Builds the report for a token the grammar doesn't allow where it is.
	/// </summary>
	/// <param name="token">The token.</param>
	/// <param name="expected">What would be allowed there, or <see langword="null" />.</param>
	/// <param name="canContinue">
	/// <see langword="false" /> where the next line couldn't finish the command, so the end of the text isn't
	/// incomplete there.
	/// </param>
	/// <returns>
	/// The exception to throw. At the end of the text it's incomplete, since more lines could finish the command.
	/// </returns>
	private SyntaxException Unexpected(Token token, string? expected, bool canContinue = true)
	{
		if (token.Kind == TokenKind.End)
		{
			return new SyntaxException(new ShellError("sharpie::parser::syntax", "The command isn't finished")
			{
				Help = expected is null or "a command" ? "add the rest of the command" : $"end it with {Quoted(expected)}",
			}, incomplete: canContinue);
		}

		var shown = token.Kind == TokenKind.Newline ? "newline" : token.Text;

		return new SyntaxException(new ShellError("sharpie::parser::syntax", $"Unexpected `{shown}`")
		{
			Source = this.source,
			Start = token.Start,
			End = token.End,
			Label = expected is null ? "not allowed here" : $"expected {Quoted(expected)}",
		}, incomplete: false);

		static string Quoted(string expected) => expected.StartsWith('`') || expected.Contains(' ', StringComparison.Ordinal) ? expected : $"`{expected}`";
	}

	/// <summary>
	/// One token of the text.
	/// </summary>
	/// <param name="Kind">What it is.</param>
	/// <param name="Start">Where it starts.</param>
	/// <param name="End">Where it ends.</param>
	/// <param name="Text">The token as typed.</param>
	private readonly record struct Token(TokenKind Kind, int Start, int End, string Text)
	{
		/// <summary>
		/// Gets the body of the here-document a <c>&lt;&lt;</c> starts.
		/// </summary>
		/// <value>
		/// The body after a newline, delimiter line included, or empty.
		/// </value>
		public string Body { get; init; } = string.Empty;

		/// <summary>
		/// Gets where <see cref="Body" /> starts in the text.
		/// </summary>
		/// <value>
		/// The position of its first line.
		/// </value>
		public int BodyStart { get; init; }
	}

	/// <summary>
	/// Ends the parse at a syntax error.
	/// </summary>
	/// <param name="error">The report.</param>
	/// <param name="incomplete"><see langword="true" /> when the text only ended too early.</param>
	[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only the parser throws it, and always with a report.")]
	[SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "It never leaves the parser.")]
	private sealed class SyntaxException(ShellError error, bool incomplete) : Exception(error.Summary)
	{
		/// <summary>
		/// Gets the report.
		/// </summary>
		/// <value>
		/// What's wrong and where.
		/// </value>
		public ShellError Error => error;

		/// <summary>
		/// Gets a value indicating whether the text only ended too early.
		/// </summary>
		/// <value>
		/// <see langword="true" /> when more lines could finish the command.
		/// </value>
		public bool Incomplete => incomplete;
	}

	/// <summary>
	/// Cuts a text into tokens and reads the here-document bodies at each newline.
	/// </summary>
	/// <param name="text">The text.</param>
	private sealed class Scanner(string text)
	{
		/// <summary>
		/// The tokens so far.
		/// </summary>
		private readonly List<Token> tokens = [];

		/// <summary>
		/// The <c>&lt;&lt;</c> operators since the last newline, whose bodies start after the next one.
		/// </summary>
		private readonly List<int> hereDocuments = [];

		/// <summary>
		/// Where the word being read starts, or -1 between words.
		/// </summary>
		private int wordStart = -1;

		/// <summary>
		/// The quote that's open, or <see langword="null" />.
		/// </summary>
		private char? quote;

		/// <summary>
		/// <see langword="true" /> right after a backslash outside single quotes.
		/// </summary>
		private bool escaped;

		/// <summary>
		/// The delimiter of a here-document whose body never closed, or <see langword="null" />.
		/// </summary>
		private string? unclosed;

		/// <summary>
		/// <see langword="true" /> from a <c>[[</c> that starts a command to its <c>]]</c>, where <c>&amp;&amp;</c>,
		/// <c>||</c>, <c>&lt;</c>, <c>&gt;</c> and parentheses are part of the test, for <c>[[ ... ]]</c> to read.
		/// </summary>
		private bool inCondition;

		/// <summary>
		/// Reads the whole text.
		/// </summary>
		/// <returns>
		/// The tokens, ending with <see cref="TokenKind.End" />; where an unclosed substitution or <c>$((</c> starts,
		/// or -1; the quote left open at the end; and the delimiter of a here-document that never closed.
		/// </returns>
		public (List<Token> Tokens, int Refused, char? Quote, string? Unclosed) Scan()
		{
			for (var index = 0; index < text.Length; index++)
			{
				var character = text[index];

				if (this.escaped || this.quote == '\'')
				{
					this.escaped = false;
					this.quote = this.quote == '\'' && character == '\'' ? null : this.quote;
					this.StartWord(index);
				}
				else if (character == '`' || text.AsSpan(index).StartsWith("$(", StringComparison.Ordinal))
				{
					if (CommandLine.SubstitutionEnd(text, index) is not (var close and >= 0))
					{
						return (this.tokens, index, null, null);
					}

					this.StartWord(index);
					index = close;
				}
				else if (this.quote is null)
				{
					index = this.ReadUnquoted(index);
				}
				else
				{
					this.ReadWordCharacter(index);
				}
			}

			this.EndWord(text.Length, null);

			// At the end there are no lines left, so this only marks the here-documents of the last line as unclosed.
			_ = this.ReadBodies(text.Length);
			this.tokens.Add(new Token(TokenKind.End, text.Length, text.Length, string.Empty));

			return (this.tokens, -1, this.quote, this.unclosed);
		}

		/// <summary>
		/// Reads the character at <paramref name="index" />, outside quotes.
		/// </summary>
		/// <param name="index">Its position.</param>
		/// <returns>
		/// The position of the last character used.
		/// </returns>
		private int ReadUnquoted(int index)
		{
			var character = text[index];

			// A comment starts only at the start of a word, as in CommandLine.Split, so the # in a#b is kept.
			if (character == '#' && this.wordStart < 0)
			{
				return CommandLine.LineEnd(text, index) - 1;
			}

			if (this.wordStart < 0 && !this.inCondition && this.StartsCondition(index))
			{
				this.inCondition = true;
			}

			// A ]] typed right before an operator ends the test there, as in [[ a ]]&&b.
			if (this.inCondition && character is not ('\n' or ';') && CommandLine.OperatorAt(text, index) is not null
				&& !(this.wordStart >= 0 && text.AsSpan(this.wordStart, index - this.wordStart) is "]]"))
			{
				this.ReadWordCharacter(index);

				return index;
			}

			if (character == '\n' || CommandLine.OperatorAt(text, index) is not null)
			{
				this.inCondition = false;

				return this.ReadOperator(index);
			}

			if (character is ' ' or '\t')
			{
				this.EndWord(index, null);
			}
			else
			{
				this.ReadWordCharacter(index);
			}

			return index;
		}

		/// <summary>
		/// Checks whether a <c>[[</c> that starts a command starts at <paramref name="index" />.
		/// </summary>
		/// <param name="index">Where a word starts.</param>
		/// <returns>
		/// <see langword="true" /> for a whole <c>[[</c> word after an operator, a newline or a reserved word, or at the
		/// start.
		/// </returns>
		private bool StartsCondition(int index) =>
			text.AsSpan(index).StartsWith("[[", StringComparison.Ordinal)
			&& (index + 2 == text.Length || text[index + 2] is ' ' or '\t' or '\n' or ';')
			&& this.tokens switch
			{
				[] => true,
				[.., { Kind: TokenKind.Newline }] => true,
				[.., { Kind: TokenKind.Operator } previous] => !IsRedirect(previous),
				[.., { Kind: TokenKind.Word, Text: var word }] => ReservedWords.Contains(word),
				_ => false,
			};

		/// <summary>
		/// Adds the character at <paramref name="index" /> to the word, noting a backslash or quote.
		/// </summary>
		/// <param name="index">Its position.</param>
		private void ReadWordCharacter(int index)
		{
			var character = text[index];

			this.StartWord(index);

			if (character == '\\')
			{
				this.escaped = true;
			}
			else if (this.quote is null && character is '\'' or '"')
			{
				this.quote = character;
			}
			else if (character == this.quote)
			{
				this.quote = null;
			}
		}

		/// <summary>
		/// Starts a word at <paramref name="index" /> unless one is being read.
		/// </summary>
		/// <param name="index">The position.</param>
		private void StartWord(int index) => this.wordStart = this.wordStart < 0 ? index : this.wordStart;

		/// <summary>
		/// Adds the word read so far, which is a stream number when it's only digits right before a redirect.
		/// </summary>
		/// <param name="end">Where it ends.</param>
		/// <param name="next">The operator right after it, or <see langword="null" />.</param>
		private void EndWord(int end, string? next)
		{
			if (this.wordStart >= 0)
			{
				var number = next is not null && CommandLine.IsStreamNumber(text.AsSpan(this.wordStart, end - this.wordStart), next);

				this.tokens.Add(new Token(number ? TokenKind.Number : TokenKind.Word, this.wordStart, end, text[this.wordStart..end]));
				this.inCondition &= this.tokens[^1].Text != "]]";
				this.wordStart = -1;
			}
		}

		/// <summary>
		/// Adds the operator or newline at <paramref name="index" />, reading the here-document bodies after a
		/// newline.
		/// </summary>
		/// <param name="index">Its position.</param>
		/// <returns>
		/// The position of the last character used.
		/// </returns>
		private int ReadOperator(int index)
		{
			var newline = text[index] == '\n';
			var typed = newline ? "\n" : CommandLine.OperatorAt(text, index)!;

			this.EndWord(index, typed);
			this.tokens.Add(new Token(newline ? TokenKind.Newline : TokenKind.Operator, index, index + typed.Length, typed));

			if (typed is "<<" or "<<-")
			{
				this.hereDocuments.Add(this.tokens.Count - 1);
			}

			return newline ? this.ReadBodies(index + 1) - 1 : index + typed.Length - 1;
		}

		/// <summary>
		/// Gives each here-document since the last newline its body. An operator in place of the delimiter isn't one.
		/// </summary>
		/// <param name="start">Where the first body starts.</param>
		/// <returns>
		/// Where the text after the last body starts.
		/// </returns>
		private int ReadBodies(int start)
		{
			foreach (var index in this.hereDocuments.Where(index => this.tokens.Count > index + 1 && this.tokens[index + 1].Kind == TokenKind.Word))
			{
				var (delimiter, _, stripTabs) = CommandLine.HereDocuments(text[this.tokens[index].Start..this.tokens[index + 1].End])[0];
				var (_, end, closed) = CommandLine.ReadHereDocument(text, start, delimiter, stripTabs);
				var body = text[start..end];

				// The newline after the delimiter line belongs to the list, not the body.
				this.tokens[index] = this.tokens[index] with
				{
					Body = body.Length > 0 ? "\n" + (body.EndsWith('\n') ? body[..^1] : body) : string.Empty,
					BodyStart = start,
				};
				this.unclosed = closed ? this.unclosed : delimiter;
				start = end;
			}

			this.hereDocuments.Clear();

			return start;
		}
	}
}
