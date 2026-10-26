using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="CommandParser" />.
/// </summary>
public sealed class CommandParserTests
{
	/// <summary>
	/// Checks the tree each construct parses into, shown in a short notation: <c>'text'</c> for a leaf, and the
	/// construct's name with its parts in parentheses.
	/// </summary>
	/// <param name="text">The text to parse.</param>
	/// <param name="expected">The tree.</param>
	[Theory]
	[InlineData("a; b", "'a', 'b'")]
	[InlineData("a && b || c", "'a' && 'b' || 'c'")]
	[InlineData("a | b", "'a | b'")]
	[InlineData("a|b", "'a|b'")]
	[InlineData("${q/'}'/x} && b", "'${q/'}'/x}' && 'b'")]
	[InlineData("a |\n  b | # note\n c", "'a | b | c'")]
	[InlineData("a |\n b &", "'a | b' &")]
	[InlineData("! a | b", "!'a | b'")]
	[InlineData("a & b", "'a' &, 'b'")]
	[InlineData("{ a; b; }", "group('a', 'b')")]
	[InlineData("{ a; } > f 2>&1", "group('a') > '> f 2>&1'")]
	[InlineData("( a; b )", "subshell('a', 'b')")]
	[InlineData("(a)", "subshell('a')")]
	[InlineData("( a; )", "subshell('a')")]
	[InlineData("( (a) )", "subshell(subshell('a'))")]
	[InlineData("{ a; } > fi", "group('a') > '> fi'")]
	[InlineData("for x in a;\n\ndo b; done", "for(x in a; 'b')")]
	[InlineData("f()\n{ a; }", "function(f; group('a'))")]
	[InlineData("a#b", "'a#b'")]
	[InlineData("a#b;c", "'a#b', 'c'")]
	[InlineData("cat <<", "'cat <<'")]
	[InlineData("cat << ; b\nx", "'cat <<', 'b', 'x'")]
	[InlineData("<<EOF cat\nx\nEOF", "'<<EOF cat\nx\nEOF'")]
	[InlineData("if a; then b; fi", "if('a' ? 'b')")]
	[InlineData("if a; then b; elif c; then d; else e; fi", "if('a' ? 'b' : 'c' ? 'd' : 'e')")]
	[InlineData("while a; do b; done", "while('a'; 'b')")]
	[InlineData("until a; do b; done", "until('a'; 'b')")]
	[InlineData("for x in a b; do c; done", "for(x in a b; 'c')")]
	[InlineData("for x in; do c; done", "for(x in ; 'c')")]
	[InlineData("for x; do c; done", "for(x; 'c')")]
	[InlineData("for x do c; done", "for(x; 'c')")]
	[InlineData("case $w in a|b) c;; *) ;; esac", "case($w; a|b: 'c'; *: )")]
	[InlineData("case w in (a) b; esac", "case(w; a: 'b')")]
	[InlineData("f() { a; }", "function(f; group('a'))")]
	[InlineData("function f { a; }", "function(f; group('a'))")]
	[InlineData("function f() { a; }", "function(f; group('a'))")]
	[InlineData("function f\n{ a; }", "function(f; group('a'))")]
	[InlineData("[[ a && b ]] && c", "'[[ a && b ]]' && 'c'")]
	[InlineData("a\n[[ b || c ]]", "'a', '[[ b || c ]]'")]
	[InlineData("if [[ a < b ]]; then c; fi", "if('[[ a < b ]]' ? 'c')")]
	[InlineData("f () ( a ) > out", "function(f; subshell('a') > '> out')")]
	[InlineData("a | while b; do c; done | d", "'a' | while('b'; 'c') | 'd'")]
	[InlineData("{ a; } &", "group('a') &")]
	[InlineData("if a; then for x in 1; do while b; do c; done; done; fi", "if('a' ? for(x in 1; while('b'; 'c')))")]
	[InlineData("(( x ))", "arith( x )")]
	[InlineData("((a)) && b", "arith(a) && 'b'")]
	[InlineData("(( x )) > out", "arith( x ) > '> out'")]
	[InlineData("if (( a > 1 )); then b; fi", "if(arith( a > 1 ) ? 'b')")]
	[InlineData("! ((a))", "!arith(a)")]
	[InlineData("((a); (b))", "subshell(subshell('a'), subshell('b'))")]
	[InlineData("echo $((1+2)) $(( (x) ))", "'echo $((1+2)) $(( (x) ))'")]
	[InlineData("for ((;;)); do a; done", "for((;1;); 'a')")]
	[InlineData("for ((i=0; i<3; i++))\ndo a; done", "for((i=0; i<3; i++); 'a')")]
	[InlineData("for (( i=0 ; ';' ; \";\" )) do a; done", "for(( i=0 ; ';' ; \";\" ); 'a')")]
	public void Parse_WithConstruct_BuildsTree(string text, string expected)
	{
		// Act
		var parsed = CommandParser.Parse(text);

		// Assert
		_ = parsed.Error.Should().BeNull();
		_ = Show(parsed.List!).Should().Be(expected);
	}

	/// <summary>
	/// Checks that a compound command may span lines, with newlines wherever POSIX allows a line break.
	/// </summary>
	[Fact]
	public void Parse_WithLinesInsideCompound_BuildsTree()
	{
		// Act
		var parsed = CommandParser.Parse("if a\nthen\n  b\n  c\nelse\n  d\nfi\nfor x\nin 1 2\ndo\n  e\ndone\ncase w\nin\n  a)\n    f\n    ;;\nesac\ng() {\n  h &&\n  i\n}");

		// Assert
		_ = Show(parsed.List!).Should().Be("if('a' ? 'b', 'c' : 'd'), for(x in 1 2; 'e'), case(w; a: 'f'), function(g; group('h' && 'i'))");
	}

	/// <summary>
	/// Checks that reserved words count only unquoted and as the first word of a command, so the same words anywhere
	/// else are ordinary words.
	/// </summary>
	/// <param name="text">The text to parse.</param>
	/// <param name="expected">The tree.</param>
	[Theory]
	[InlineData("echo if fi then", "'echo if fi then'")]
	[InlineData("\"if\" a", "'\"if\" a'")]
	[InlineData("\\if a", "'\\if a'")]
	[InlineData("a}", "'a}'")]
	[InlineData("{a; b}", "'{a', 'b}'")]
	[InlineData("A=1 if", "'A=1 if'")]
	[InlineData("echo {", "'echo {'")]
	[InlineData("for in in in; do done=1; done", "for(in in in; 'done=1')")]
	[InlineData("case in in in) echo esac;; esac", "case(in; in: 'echo esac')")]
	public void Parse_WithReservedWordOutOfPlace_KeepsItAWord(string text, string expected)
	{
		// Act
		var parsed = CommandParser.Parse(text);

		// Assert
		_ = parsed.Error.Should().BeNull();
		_ = Show(parsed.List!).Should().Be(expected);
	}

	/// <summary>
	/// Checks that a here-document's body goes with the command that starts it, inside a compound command and after
	/// one.
	/// </summary>
	/// <param name="text">The text to parse.</param>
	/// <param name="expected">The tree.</param>
	[Theory]
	[InlineData("if a; then cat <<EOF\nhi\nEOF\nfi", "if('a' ? 'cat <<EOF\nhi\nEOF')")]
	[InlineData("while read l; do b; done <<EOF\nx\nEOF", "while('read l'; 'b') > '<<EOF\nx\nEOF'")]
	[InlineData("{ cat <<A; }\nx\nA", "group('cat <<A\nx\nA')")]
	[InlineData("cat <<A | cat <<B\none\nA\ntwo\nB", "'cat <<A | cat <<B\none\nA\ntwo\nB'")]
	public void Parse_WithHereDocument_GivesBodyToItsCommand(string text, string expected)
	{
		// Act
		var parsed = CommandParser.Parse(text);

		// Assert
		_ = Show(parsed.List!).Should().Be(expected);
	}

	/// <summary>
	/// Checks that a compound command's text holds the here-document bodies that come after it, so a copy of the
	/// shell can run the text alone.
	/// </summary>
	[Fact]
	public void Parse_WithBodyAfterCompound_PutsBodyInItsText()
	{
		// Act
		var parsed = CommandParser.Parse("{ cat <<A; } & echo b\nx\nA");

		// Assert
		_ = parsed.List!.Items[0].Command.Text.Should().Be("{ cat <<A; }\nx\nA");
	}

	/// <summary>
	/// Checks which texts end too early, so a reader should add the next line, and which are whole.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="expected">Whether it's incomplete.</param>
	[Theory]
	[InlineData("if a; then", true)]
	[InlineData("if a; then b", true)]
	[InlineData("if a; then b; fi", false)]
	[InlineData("while a", true)]
	[InlineData("for x in a b", true)]
	[InlineData("case x in", true)]
	[InlineData("case x in a) b;;", true)]
	[InlineData("{ a", true)]
	[InlineData("{ a }", true)]
	[InlineData("( a", true)]
	[InlineData("a &&", true)]
	[InlineData("a ||\n", true)]
	[InlineData("a |", true)]
	[InlineData("f()", true)]
	[InlineData("for x in a b do c", true)]
	[InlineData("!", true)]
	[InlineData("a", false)]
	[InlineData("", false)]
	[InlineData("# if", false)]
	[InlineData("echo 'if", false)]
	public void Parse_WithText_SaysWhetherIncomplete(string text, bool expected)
	{
		// Act
		var parsed = CommandParser.Parse(text);

		// Assert
		_ = parsed.Incomplete.Should().Be(expected);
		_ = (parsed.List is null).Should().Be(expected);
	}

	/// <summary>
	/// Checks that a token the grammar doesn't allow is reported with where it is and what was expected.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="start">Where the token starts.</param>
	/// <param name="end">Where it ends.</param>
	/// <param name="summary">The report's summary.</param>
	/// <param name="label">The report's label.</param>
	[Theory]
	[InlineData("fi", 0, 2, "Unexpected `fi`", "not allowed here")]
	[InlineData("if true; fi", 9, 11, "Unexpected `fi`", "expected `then`")]
	[InlineData("if then fi", 3, 7, "Unexpected `then`", "expected a command")]
	[InlineData("for 1x in a; do b; done", 4, 6, "Unexpected `1x`", "expected a variable name")]
	[InlineData("a ( b", 4, 5, "Unexpected `b`", "expected `)`")]
	[InlineData("a (\n)", 3, 4, "Unexpected `newline`", "expected `)`")]
	[InlineData("echo ($HOME)", 6, 11, "Unexpected `$HOME`", "expected `)`")]
	[InlineData("echo a (b)", 7, 8, "Unexpected `(`", "not allowed here")]
	[InlineData("( a ) b", 6, 7, "Unexpected `b`", "not allowed here")]
	[InlineData("a; ; b", 3, 4, "Unexpected `;`", "expected a command")]
	[InlineData("&& a", 0, 2, "Unexpected `&&`", "expected a command")]
	[InlineData(";; a", 0, 2, "Unexpected `;;`", "not allowed here")]
	[InlineData("for x in a b | c", 13, 14, "Unexpected `|`", "expected `;` or a newline")]
	[InlineData("case x in a b) c;; esac", 12, 13, "Unexpected `b`", "expected `)`")]
	[InlineData("case x in a) b; c) d;; esac", 17, 18, "Unexpected `)`", "expected `;;` or `esac`")]
	[InlineData("f() a", 4, 5, "Unexpected `a`", "expected `{`")]
	[InlineData("function 'f' { a; }", 9, 12, "Unexpected `'f'`", "expected a function name")]
	[InlineData("function ; a", 9, 10, "Unexpected `;`", "expected a function name")]
	[InlineData("a && fi", 5, 7, "Unexpected `fi`", "expected a command")]
	[InlineData("a=b() { :; }", 3, 4, "Unexpected `(`", "not allowed here")]
	[InlineData("'f'() { :; }", 3, 4, "Unexpected `(`", "not allowed here")]
	[InlineData("case ; esac", 5, 6, "Unexpected `;`", "expected a word")]
	[InlineData("case x in ) a;; esac", 10, 11, "Unexpected `)`", "expected a pattern")]
	public void Parse_WithSyntaxError_ReportsToken(string text, int start, int end, string summary, string label)
	{
		// Act
		var parsed = CommandParser.Parse(text);

		// Assert
		_ = parsed.Incomplete.Should().BeFalse();
		_ = parsed.Error.Should().Be(new ShellError("sharpie::parser::syntax", summary)
		{
			Source = text,
			Start = start,
			End = end,
			Label = label,
		});
	}

	/// <summary>
	/// Checks that a text that ends too early says what would finish it.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="help">The report's hint.</param>
	[Theory]
	[InlineData("if a; then b", "end it with `fi`")]
	[InlineData("if", "end it with `then`")]
	[InlineData("if a; then", "end it with `fi`")]
	[InlineData("if a; then b; elif", "end it with `then`")]
	[InlineData("if a; then b; elif c; then", "end it with `fi`")]
	[InlineData("if a; then b; else", "end it with `fi`")]
	[InlineData("while a; do", "end it with `done`")]
	[InlineData("for x in a; do", "end it with `done`")]
	[InlineData("(", "end it with `)`")]
	[InlineData("{", "end it with `}`")]
	[InlineData("while a", "end it with `do`")]
	[InlineData("{ a", "end it with `}`")]
	[InlineData("( a", "end it with `)`")]
	[InlineData("a &&", "add the rest of the command")]
	public void Parse_WithIncompleteText_SaysWhatIsMissing(string text, string help)
	{
		// Act
		var parsed = CommandParser.Parse(text);

		// Assert
		_ = parsed.Error.Should().Be(new ShellError("sharpie::parser::syntax", "The command isn't finished") { Help = help });
	}

	/// <summary>
	/// Checks that a function name and <c>(</c> at the end of the text is an error rather than incomplete, since POSIX
	/// allows no line break before the <c>)</c>.
	/// </summary>
	[Fact]
	public void Parse_WithOpenFunctionParenthesisAtEnd_IsAnError()
	{
		// Act
		var parsed = CommandParser.Parse("f(");

		// Assert
		_ = parsed.Incomplete.Should().BeFalse();
		_ = parsed.Error.Should().Be(new ShellError("sharpie::parser::syntax", "The command isn't finished") { Help = "end it with `)`" });
	}

	/// <summary>
	/// Checks that the <c>case</c> fall-through operators and an unclosed substitution are refused with reports of their
	/// own.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="code">The report's code.</param>
	/// <param name="start">Where the refused part starts.</param>
	/// <param name="end">Where its opening ends.</param>
	[Theory]
	[InlineData("echo $((1", "sharpie::parser::unclosed_substitution", 5, 7)]
	[InlineData("case x in a) b;& c) d;; esac", "sharpie::parser::unsupported", 14, 16)]
	[InlineData("case x in a) b;;& esac", "sharpie::parser::unsupported", 14, 17)]
	[InlineData("case x in a) ;& esac", "sharpie::parser::unsupported", 13, 15)]
	[InlineData("$(a", "sharpie::parser::unclosed_substitution", 0, 2)]
	public void Parse_WithUnsupportedSyntax_RefusesIt(string text, string code, int start, int end)
	{
		// Act
		var parsed = CommandParser.Parse(text);

		// Assert
		_ = parsed.List.Should().BeNull();
		_ = parsed.Error!.Code.Should().Be(code);
		_ = (parsed.Error.Start, parsed.Error.End).Should().Be((start, end));
	}

	/// <summary>
	/// Checks that <c>for ((...))</c> needs exactly three expressions, pointing at the <c>((...))</c>.
	/// </summary>
	/// <param name="text">The text.</param>
	[Theory]
	[InlineData("for ((1;2)); do a; done")]
	[InlineData("for ((1;2;3;4)); do a; done")]
	public void Parse_WithArithmeticForOfWrongShape_ReportsIt(string text)
	{
		// Act
		var parsed = CommandParser.Parse(text);

		// Assert
		_ = parsed.Error!.Code.Should().Be("sharpie::parser::syntax");
		_ = parsed.Error.Label.Should().Be("expected `((init; test; step))`");
		_ = (parsed.Error.Start, parsed.Error.End).Should().Be((4, text.IndexOf("; do", StringComparison.Ordinal)));
	}

	/// <summary>
	/// Checks that <c>((</c> is arithmetic only where a command starts, so after a word, where <c>x(</c> starts a
	/// function, or after another <c>((...))</c>, it's a <c>(</c> the grammar doesn't allow there, as in bash.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="start">Where the unexpected token starts.</param>
	[Theory]
	[InlineData("x((1))", 2)]
	[InlineData("((1)) ((2))", 6)]
	public void Parse_WithArithmeticAfterWord_ReportsTheParenthesis(string text, int start)
	{
		// Act
		var parsed = CommandParser.Parse(text);

		// Assert
		_ = (parsed.Error!.Summary, parsed.Error.Start, parsed.Error.End).Should().Be(("Unexpected `(`", start, start + 1));
	}

	/// <summary>
	/// Checks the full report for <c>;&amp;</c>, and that a syntax error leaves nothing marked as refused.
	/// </summary>
	[Fact]
	public void Parse_WithFallThrough_ExplainsTheRefusal()
	{
		// Act
		var parsed = CommandParser.Parse("case x in a) b;& esac");

		// Assert
		_ = parsed.Incomplete.Should().BeFalse();
		_ = parsed.Refused.Should().Be(-1);
		_ = parsed.Error.Should().Be(new ShellError("sharpie::parser::unsupported", "`;&` isn't supported")
		{
			Source = "case x in a) b;& esac",
			Start = 14,
			End = 16,
			Label = "runs the next item too",
			Help = "end the item with ;; and repeat the commands in the next one",
		});
	}

	/// <summary>
	/// Checks that a compound command's text holds the here-document bodies inside it once, as typed.
	/// </summary>
	[Fact]
	public void Parse_WithBodyInsideCompound_KeepsTextAsTyped()
	{
		// Arrange
		const string Text = "{ cat <<A\nx\nA\n}";

		// Act
		var parsed = CommandParser.Parse(Text);

		// Assert
		_ = parsed.List!.Items[0].Command.Text.Should().Be(Text);
	}

	/// <summary>
	/// Checks that the <c>)</c> ending a <c>case</c> pattern inside <c>$(...)</c> doesn't end the substitution.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="expected">The tree.</param>
	[Theory]
	[InlineData("echo $(case x in a) b;; esac)", "'echo $(case x in a) b;; esac)'")]
	[InlineData("echo $(case x in (a) b;; esac) c", "'echo $(case x in (a) b;; esac) c'")]
	[InlineData("echo $(if t; then case x in a) b;; esac; fi)", "'echo $(if t; then case x in a) b;; esac; fi)'")]
	[InlineData("echo $(echo case x) y", "'echo $(echo case x) y'")]
	[InlineData("echo $(case x in a) $(b);; esac)", "'echo $(case x in a) $(b);; esac)'")]
	[InlineData("echo $(case esac in esac) b;; esac)", "'echo $(case esac in esac) b;; esac)'")]
	public void Parse_WithCaseInsideSubstitution_KeepsSubstitutionWhole(string text, string expected)
	{
		// Act
		var parsed = CommandParser.Parse(text);

		// Assert
		_ = parsed.Error.Should().BeNull();
		_ = Show(parsed.List!).Should().Be(expected);
	}

	/// <summary>
	/// Checks that the reserved words <c>type</c> reports are exactly the grammar's.
	/// </summary>
	[Fact]
	public void ReservedWords_AreTheGrammars()
	{
		// Assert
		_ = CommandParser.ReservedWords.Should().BeEquivalentTo(["!", "{", "}", "case", "do", "done", "elif", "else", "esac", "fi", "for", "function", "if", "in", "then", "until", "while"]);
	}

	/// <summary>
	/// Shows a list in the notation the tests compare.
	/// </summary>
	/// <param name="list">The list.</param>
	/// <returns>
	/// The notation.
	/// </returns>
	private static string Show(ListNode list) =>
		string.Join(", ", list.Items.Select(item => Show(item.Command) + (item.Background ? " &" : string.Empty)));

	/// <summary>
	/// Shows a command in the notation the tests compare.
	/// </summary>
	/// <param name="node">The command.</param>
	/// <returns>
	/// The notation.
	/// </returns>
	private static string Show(CommandNode node) => node switch
	{
		Leaf leaf => $"'{leaf.Text}'",
		AndOr andOr => Show(andOr.First) + string.Concat(andOr.Rest.Select(next => $" {next.Operator} {Show(next.Pipeline)}")),
		Pipeline pipeline => (pipeline.Negated ? "!" : string.Empty) + string.Join(" | ", pipeline.Stages.Select(Show)),
		FunctionDefinition function => $"function({function.Name}; {Show(function.Body)})",
		Compound compound => ShowCompound(compound) + (compound.Redirects is { } redirects ? $" > '{redirects.Text}'" : string.Empty),
		_ => throw new ArgumentException("Unknown node", nameof(node)),
	};

	/// <summary>
	/// Shows a compound command without its redirects.
	/// </summary>
	/// <param name="compound">The command.</param>
	/// <returns>
	/// The notation.
	/// </returns>
	private static string ShowCompound(Compound compound) => compound switch
	{
		Group group => $"group({Show(group.Body)})",
		Subshell subshell => $"subshell({Show(subshell.Body)})",
		If conditional => "if(" + string.Join(" : ", conditional.Branches.Select(branch => $"{Show(branch.Condition)} ? {Show(branch.Body)}")) + (conditional.Else is { } otherwise ? $" : {Show(otherwise)}" : string.Empty) + ")",
		Loop loop => $"{(loop.Until ? "until" : "while")}({Show(loop.Condition)}; {Show(loop.Body)})",
		For loop => $"for({loop.Name}{(loop.Words is { } words ? $" in {words}" : string.Empty)}; {Show(loop.Body)})",
		ArithmeticCommand arithmetic => $"arith({arithmetic.Expression})",
		ArithmeticFor loop => $"for(({loop.Init};{loop.Condition};{loop.Step}); {Show(loop.Body)})",
		Case match => $"case({match.Word}; " + string.Join("; ", match.Items.Select(item => $"{string.Join('|', item.Patterns)}: {Show(item.Body)}")) + ")",
		_ => throw new ArgumentException("Unknown node", nameof(compound)),
	};
}
