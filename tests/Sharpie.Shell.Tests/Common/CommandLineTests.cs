using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="CommandLine" />.
/// </summary>
public sealed class CommandLineTests
{
	/// <summary>
	/// Checks that <see cref="CommandLine.Split" /> splits on unquoted spaces and tabs, keeps quoted text
	/// literally, joins text that touches across quotes into one word, and treats an unquoted backslash as an
	/// escape for the next character. Inside double quotes a backslash escapes <c>"</c> and <c>\</c> but stays
	/// before anything else.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="expected">The words the line should produce.</param>
	[Theory]
	[InlineData("echo hello    world", new[] { "echo", "hello", "world" })]
	[InlineData("echo\thello", new[] { "echo", "hello" })]
	[InlineData("  echo hi  ", new[] { "echo", "hi" })]
	[InlineData("echo 'hello    world'", new[] { "echo", "hello    world" })]
	[InlineData("echo 'tab\there'", new[] { "echo", "tab\there" })]
	[InlineData("echo 'hello''world'", new[] { "echo", "helloworld" })]
	[InlineData("echo hello''world", new[] { "echo", "helloworld" })]
	[InlineData("echo ''", new[] { "echo", "" })]
	[InlineData("echo '$HOME * ~ \\n'", new[] { "echo", "$HOME * ~ \\n" })]
	[InlineData("cat '/tmp/file name' '/tmp/file name with spaces'", new[] { "cat", "/tmp/file name", "/tmp/file name with spaces" })]
	[InlineData("echo 'never closed  here", new[] { "echo", "never closed  here" })]
	[InlineData("echo \"hello    world\"", new[] { "echo", "hello    world" })]
	[InlineData("echo \"hello\"\"world\"", new[] { "echo", "helloworld" })]
	[InlineData("echo \"a\" \"b\"", new[] { "echo", "a", "b" })]
	[InlineData("echo \"shell's test\"", new[] { "echo", "shell's test" })]
	[InlineData("echo '\"hi\"'", new[] { "echo", "\"hi\"" })]
	[InlineData("echo \"\"", new[] { "echo", "" })]
	[InlineData("echo \"a\"'b'c", new[] { "echo", "abc" })]
	[InlineData("cat \"/tmp/file name\" \"/tmp/file name with spaces\"", new[] { "cat", "/tmp/file name", "/tmp/file name with spaces" })]
	[InlineData("echo \"never closed  here", new[] { "echo", "never closed  here" })]
	[InlineData(@"echo three\ \ \ spaces", new[] { "echo", "three   spaces" })]
	[InlineData(@"echo before\     after", new[] { "echo", "before ", "after" })]
	[InlineData(@"echo test\nexample", new[] { "echo", "testnexample" })]
	[InlineData(@"echo hello\\world", new[] { "echo", @"hello\world" })]
	[InlineData(@"echo \'hello\'", new[] { "echo", "'hello'" })]
	[InlineData(@"echo \'\""literal quotes\""\'", new[] { "echo", "'\"literal", "quotes\"'" })]
	[InlineData(@"cat /tmp/\_ignored_1 /tmp/ignore_\2 /tmp/just_one_\\_3", new[] { "cat", "/tmp/_ignored_1", "/tmp/ignore_2", @"/tmp/just_one_\_3" })]
	[InlineData(@"echo 'a\b'", new[] { "echo", @"a\b" })]
	[InlineData(@"echo ""a\b""", new[] { "echo", @"a\b" })]
	[InlineData(@"echo ""A \\ escapes itself""", new[] { "echo", @"A \ escapes itself" })]
	[InlineData(@"echo ""A \"" inside double quotes""", new[] { "echo", "A \" inside double quotes" })]
	[InlineData(@"echo ""just'one'\\n'backslash""", new[] { "echo", @"just'one'\n'backslash" })]
	[InlineData(@"echo ""inside\""literal_quote.""outside\""", new[] { "echo", "inside\"literal_quote.outside\"" })]
	[InlineData(@"cat /tmp/""number 1"" /tmp/""doublequote \"" 2"" /tmp/""backslash \\ 3""", new[] { "cat", "/tmp/number 1", "/tmp/doublequote \" 2", @"/tmp/backslash \ 3" })]
	[InlineData(@"echo trailing\", new[] { "echo", @"trailing\" })]
	[InlineData(@"echo \ ", new[] { "echo", " " })]
	public void Split_WithLine_ReturnsExpectedWords(string line, string[] expected)
	{
		// Act
		var words = CommandLine.Split(line);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.Split" /> replaces <c>$NAME</c> with the variable's value outside single
	/// quotes, drops an unquoted unset name, and leaves an escaped or nameless <c>$</c> as typed.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="expected">The words the line should produce.</param>
	[Theory]
	[InlineData("echo $A $B", new[] { "echo", "one", "two" })]
	[InlineData("echo x$A-y", new[] { "echo", "xone-y" })]
	[InlineData("echo \"$A b\"", new[] { "echo", "one b" })]
	[InlineData("echo '$A'", new[] { "echo", "$A" })]
	[InlineData(@"echo \$A", new[] { "echo", "$A" })]
	[InlineData("echo $MISSING z", new[] { "echo", "z" })]
	[InlineData("echo \"$MISSING\"", new[] { "echo", "" })]
	[InlineData("echo $ $1", new[] { "echo", "$", "$1" })]
	[InlineData("echo ${A}end", new[] { "echo", "oneend" })]
	[InlineData("echo ${A}and${B}", new[] { "echo", "oneandtwo" })]
	[InlineData("echo stock_${A}_id", new[] { "echo", "stock_one_id" })]
	[InlineData("echo \"${A} x\"", new[] { "echo", "one x" })]
	[InlineData("echo '${A}'", new[] { "echo", "${A}" })]
	[InlineData("echo ${MISSING} z", new[] { "echo", "z" })]
	[InlineData("echo ${A", new[] { "echo", "${A" })]
	[InlineData("echo a$", new[] { "echo", "a$" })]
	[InlineData("echo a$1", new[] { "echo", "a$1" })]
	[InlineData("echo _$1", new[] { "echo", "_$1" })]
	[InlineData("echo {$A}", new[] { "echo", "{one}" })]
	[InlineData("echo }${A}", new[] { "echo", "}one" })]
	[InlineData("echo $Ab", new[] { "echo" })]
	public void Split_WithVariables_ExpandsDollarNames(string line, string[] expected)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["A"] = "one", ["B"] = "two" };

		// Act
		var words = CommandLine.Split(line, variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that an unquoted <c>#</c> at the start of a word drops the rest of the line, and that a <c>#</c> inside a
	/// word, in quotes or after a backslash is kept.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="expected">The words the line should produce.</param>
	[Theory]
	[InlineData("# a whole comment", new string[0])]
	[InlineData("   #indented comment", new string[0])]
	[InlineData("echo hi # trailing words", new[] { "echo", "hi" })]
	[InlineData("echo hi\t#tab before", new[] { "echo", "hi" })]
	[InlineData("echo a#b", new[] { "echo", "a#b" })]
	[InlineData("echo '#' \"#\"", new[] { "echo", "#", "#" })]
	[InlineData(@"echo \#x", new[] { "echo", "#x" })]
	[InlineData("echo ''# x", new[] { "echo", "#", "x" })]
	public void Split_WithHash_DropsComment(string line, string[] expected)
	{
		// Act
		var words = CommandLine.Split(line);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <c>$NAME</c> falls back to the environment when no shell variable has the name, with either form.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	[Theory]
	[InlineData("echo $SHARPIE_SAMPLE_VALUE")]
	[InlineData("echo ${SHARPIE_SAMPLE_VALUE}")]
	public void Split_WithEnvironmentVariable_ExpandsIt(string line)
	{
		// Arrange
		Environment.SetEnvironmentVariable("SHARPIE_SAMPLE_VALUE", "from-env");

		try
		{
			// Act
			var words = CommandLine.Split(line, new Dictionary<string, string>());

			// Assert
			_ = words.Select(word => word.Text).Should().Equal("echo", "from-env");
		}
		finally
		{
			Environment.SetEnvironmentVariable("SHARPIE_SAMPLE_VALUE", null);
		}
	}

	/// <summary>
	/// Checks that a shell variable wins over an environment variable with the same name.
	/// </summary>
	[Fact]
	public void Split_WithShellAndEnvironmentVariable_PrefersShellVariable()
	{
		// Arrange
		Environment.SetEnvironmentVariable("SHARPIE_SAMPLE_SHADOWED", "from-env");

		try
		{
			// Act
			var words = CommandLine.Split("echo $SHARPIE_SAMPLE_SHADOWED", new Dictionary<string, string> { ["SHARPIE_SAMPLE_SHADOWED"] = "from-shell" });

			// Assert
			_ = words.Select(word => word.Text).Should().Equal("echo", "from-shell");
		}
		finally
		{
			Environment.SetEnvironmentVariable("SHARPIE_SAMPLE_SHADOWED", null);
		}
	}

	/// <summary>
	/// Checks that a line with nothing but whitespace produces no words, so the shell just prompts again.
	/// </summary>
	/// <param name="line">The blank line to split.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("\t \t")]
	public void Split_WithBlankLine_ReturnsEmpty(string line)
	{
		// Act
		var words = CommandLine.Split(line);

		// Assert
		_ = words.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.Split" /> gives each unquoted operator as its own token, even with no space
	/// around it, and that digits make up a redirect's stream number only when they are the whole word.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="expected">The words it should give, with each operator written as <c>[op]</c>.</param>
	[Theory]
	[InlineData("ls|wc -l", new[] { "ls", "[|]", "wc", "-l" })]
	[InlineData("echo hi>out", new[] { "echo", "hi", "[>]", "out" })]
	[InlineData("sleep 5&", new[] { "sleep", "5", "[&]" })]
	[InlineData("a&&b||c;d", new[] { "a", "[&&]", "b", "[||]", "c", "[;]", "d" })]
	[InlineData("cat<in>>out", new[] { "cat", "[<]", "in", "[>>]", "out" })]
	[InlineData("cmd 2>err", new[] { "cmd", "[2>]", "err" })]
	[InlineData("cmd 1>>log", new[] { "cmd", "[1>>]", "log" })]
	[InlineData("cmd 2>&1", new[] { "cmd", "[2>&]", "1" })]
	[InlineData("echo a2>f", new[] { "echo", "a2", "[>]", "f" })]
	[InlineData(@"echo \2>f", new[] { "echo", "2", "[>]", "f" })]
	[InlineData("echo '2'>f", new[] { "echo", "2", "[>]", "f" })]
	[InlineData("echo 2 >f", new[] { "echo", "2", "[>]", "f" })]
	[InlineData("a|&b", new[] { "a", "[|&]", "b" })]
	[InlineData("cmd &>all", new[] { "cmd", "[&>]", "all" })]
	[InlineData("echo a;#c", new[] { "echo", "a", "[;]" })]
	[InlineData("(echo)", new[] { "[(]", "echo", "[)]" })]
	public void Split_WithOperators_ReturnsOperatorTokens(string line, string[] expected)
	{
		// Act
		var words = CommandLine.Split(line);

		// Assert
		_ = words.Select(word => word.IsOperator ? $"[{word.Text}]" : word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that only a word of digits before a redirect is its stream number, and that no text at all isn't one.
	/// </summary>
	/// <param name="typed">The word's raw text.</param>
	/// <param name="typedOperator">The operator after it.</param>
	/// <param name="expected">Whether the digits belong to the operator.</param>
	[Theory]
	[InlineData("2", ">", true)]
	[InlineData("12", "<", true)]
	[InlineData("", ">", false)]
	[InlineData("2", "|", false)]
	[InlineData("2a", ">", false)]
	public void IsStreamNumber_WithWord_ReturnsWhetherDigitsJoinOperator(string typed, string typedOperator, bool expected)
	{
		// Act
		var result = CommandLine.IsStreamNumber(typed, typedOperator);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a quoted or escaped operator is a plain word, not an operator.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="expected">The words it should give, all plain.</param>
	[Theory]
	[InlineData("echo '|'", new[] { "echo", "|" })]
	[InlineData("echo \"&&\"", new[] { "echo", "&&" })]
	[InlineData(@"echo \>", new[] { "echo", ">" })]
	[InlineData("echo 'a|b'", new[] { "echo", "a|b" })]
	[InlineData("echo a'>'b", new[] { "echo", "a>b" })]
	public void Split_WithQuotedOperators_ReturnsPlainWords(string line, string[] expected)
	{
		// Act
		var words = CommandLine.Split(line);

		// Assert
		_ = words.Should().OnlyContain(word => !word.IsOperator);
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.SplitRedirect" /> takes <c>&gt;</c> or <c>1&gt;</c> and the file name after
	/// it out of the words, with or without spaces around the operator.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expectedWords">The words that should be left for the command.</param>
	/// <param name="expectedPath">The file name that should be returned.</param>
	[Theory]
	[InlineData("echo hi > out.txt", new[] { "echo", "hi" }, "out.txt")]
	[InlineData("echo hi 1> out.txt", new[] { "echo", "hi" }, "out.txt")]
	[InlineData("echo hi>out.txt", new[] { "echo", "hi" }, "out.txt")]
	[InlineData("ls /tmp > /tmp/foo/baz.md", new[] { "ls", "/tmp" }, "/tmp/foo/baz.md")]
	public void SplitRedirect_WithOperator_ReturnsWordsAndPath(string line, string[] expectedWords, string expectedPath)
	{
		// Act
		var (result, path, append, errorPath, _) = CommandLine.SplitRedirect(CommandLine.Split(line));

		// Assert
		_ = result.Select(word => word.Text).Should().Equal(expectedWords);
		_ = path!.Value.Text.Should().Be(expectedPath);
		_ = append.Should().BeFalse();
		_ = errorPath.Should().BeNull();
	}

	/// <summary>
	/// Checks that <c>&gt;&gt;</c> and <c>1&gt;&gt;</c> are taken out of the words with their file name and ask for
	/// standard output to be appended.
	/// </summary>
	/// <param name="line">The command line.</param>
	[Theory]
	[InlineData("echo hi >> out.md")]
	[InlineData("echo hi 1>> out.md")]
	[InlineData("echo hi 1>>out.md")]
	public void SplitRedirect_WithAppendOperator_ReturnsPathAndAppend(string line)
	{
		// Act
		var (result, path, append, errorPath, _) = CommandLine.SplitRedirect(CommandLine.Split(line));

		// Assert
		_ = result.Select(word => word.Text).Should().Equal("echo", "hi");
		_ = path!.Value.Text.Should().Be("out.md");
		_ = append.Should().BeTrue();
		_ = errorPath.Should().BeNull();
	}

	/// <summary>
	/// Checks that when <c>&gt;&gt;</c> is followed by <c>&gt;</c>, the later operator decides that the file is
	/// replaced, not appended to.
	/// </summary>
	[Fact]
	public void SplitRedirect_WithAppendThenWrite_LastOperatorWins()
	{
		// Act
		var (_, path, append, _, _) = CommandLine.SplitRedirect(CommandLine.Split("echo hi >> a.md > b.md"));

		// Assert
		_ = path!.Value.Text.Should().Be("b.md");
		_ = append.Should().BeFalse();
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.SplitRedirect" /> takes <c>2&gt;</c> and its file name out of the words
	/// and leaves standard output alone.
	/// </summary>
	/// <param name="line">The command line.</param>
	[Theory]
	[InlineData("cat a nonexistent 2> err.md")]
	[InlineData("cat a nonexistent 2>err.md")]
	public void SplitRedirect_WithErrorOperator_ReturnsErrorPath(string line)
	{
		// Act
		var (result, path, _, errorPath, errorAppend) = CommandLine.SplitRedirect(CommandLine.Split(line));

		// Assert
		_ = result.Select(word => word.Text).Should().Equal("cat", "a", "nonexistent");
		_ = path.Should().BeNull();
		_ = errorPath!.Value.Text.Should().Be("err.md");
		_ = errorAppend.Should().BeFalse();
	}

	/// <summary>
	/// Checks that <c>2&gt;&gt;</c> is taken out of the words with its file name and asks for standard error to be
	/// appended, leaving standard output alone.
	/// </summary>
	/// <param name="line">The command line.</param>
	[Theory]
	[InlineData("cat nonexistent 2>> err.md")]
	[InlineData("cat nonexistent 2>>err.md")]
	public void SplitRedirect_WithErrorAppendOperator_ReturnsErrorPathAndAppend(string line)
	{
		// Act
		var (result, path, append, errorPath, errorAppend) = CommandLine.SplitRedirect(CommandLine.Split(line));

		// Assert
		_ = result.Select(word => word.Text).Should().Equal("cat", "nonexistent");
		_ = path.Should().BeNull();
		_ = append.Should().BeFalse();
		_ = errorPath!.Value.Text.Should().Be("err.md");
		_ = errorAppend.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>&gt;</c> and <c>2&gt;</c> on the same line each get their own file name.
	/// </summary>
	[Fact]
	public void SplitRedirect_WithBothOperators_ReturnsBothPaths()
	{
		// Act
		var (result, path, _, errorPath, _) = CommandLine.SplitRedirect(CommandLine.Split("ls > out.md 2> err.md"));

		// Assert
		_ = result.Select(word => word.Text).Should().Equal("ls");
		_ = path!.Value.Text.Should().Be("out.md");
		_ = errorPath!.Value.Text.Should().Be("err.md");
	}

	/// <summary>
	/// Checks that words with no redirect come back unchanged: no operator, a quoted one, an operator with no file
	/// name after it, or one followed by another operator instead of a file name.
	/// </summary>
	/// <param name="line">The command line.</param>
	[Theory]
	[InlineData("echo hi")]
	[InlineData("echo hi >")]
	[InlineData("echo hi 2>")]
	[InlineData("echo hi >>")]
	[InlineData("echo hi 2>>")]
	[InlineData("echo hi '>' out.md")]
	[InlineData(@"echo hi \> out.md")]
	[InlineData("echo hi > | wc")]
	public void SplitRedirect_WithoutRedirect_ReturnsWordsUnchanged(string line)
	{
		// Arrange
		var words = CommandLine.Split(line);

		// Act
		var (result, path, append, errorPath, errorAppend) = CommandLine.SplitRedirect(words);

		// Assert
		_ = result.Should().Equal(words);
		_ = path.Should().BeNull();
		_ = append.Should().BeFalse();
		_ = errorPath.Should().BeNull();
		_ = errorAppend.Should().BeFalse();
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.SplitPipeline" /> splits at every <c>|</c> operator, and gives an empty
	/// array when a command between bars is missing or another operator is left over.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The stages, each one's words joined by spaces.</param>
	[Theory]
	[InlineData("ls -la", new[] { "ls -la" })]
	[InlineData("cat f | wc", new[] { "cat f", "wc" })]
	[InlineData("cat f|wc", new[] { "cat f", "wc" })]
	[InlineData("cat f | head -n 3 | wc", new[] { "cat f", "head -n 3", "wc" })]
	[InlineData("echo '|' x", new[] { "echo | x" })]
	[InlineData("cat f |", new string[0])]
	[InlineData("| wc", new string[0])]
	[InlineData("cat f | | wc", new string[0])]
	[InlineData("cat f < x", new string[0])]
	[InlineData("cat f | wc >", new string[0])]
	public void SplitPipeline_WithBars_ReturnsStages(string line, string[] expected)
	{
		// Act
		var result = CommandLine.SplitPipeline(CommandLine.Split(line));

		// Assert
		_ = result.Select(stage => string.Join(' ', stage)).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that the Spargine guard rejects a missing line instead of failing deeper in the parser.
	/// </summary>
	[Fact]
	public void Split_WithNullLine_ThrowsArgumentNullException()
	{
		// Act
		var act = () => CommandLine.Split(null!);

		// Assert
		_ = act.Should().Throw<ArgumentNullException>();
	}

	/// <summary>
	/// Checks that a line running only <c>man</c> or <c>dman</c> with a page name is handed to the window's reader with
	/// all its words, and anything else is left for the shell to run.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The words the window gets, or <see langword="null" /> when the shell runs the line.</param>
	[Theory]
	[InlineData("man ls", new[] { "man", "ls" })]
	[InlineData("dman -vb Select", new[] { "dman", "-vb", "Select" })]
	[InlineData("dman String.Split", new[] { "dman", "String.Split" })]
	[InlineData("dman", null)]
	[InlineData("dman List > out.txt", null)]
	[InlineData("dman List | less", null)]
	[InlineData("echo dman", null)]
	[InlineData("man ls; ls", null)]
	[InlineData("man ls && ls", null)]
	[InlineData("dman List>out.txt", null)]
	[InlineData("dman List|less", null)]
	[InlineData("man ls&", null)]
	[InlineData("man ls < x", null)]
	[InlineData("man '|'", new[] { "man", "|" })]
	[InlineData("man `ls`", null)]
	[InlineData("man ls\nls", null)]
	public void PagedCommand_Line_ReturnsWordsForReader(string line, string[]? expected)
	{
		// Act
		var result = CommandLine.PagedCommand(line);

		// Assert
		_ = result.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
	}

	/// <summary>
	/// Checks that <c>$?</c> expands to the last status outside single quotes, and stays as typed inside them.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The words it should give.</param>
	[Theory]
	[InlineData("echo $?", new[] { "echo", "3" })]
	[InlineData("echo \"$?\"", new[] { "echo", "3" })]
	[InlineData("echo x$?y", new[] { "echo", "x3y" })]
	[InlineData("echo '$?'", new[] { "echo", "$?" })]
	[InlineData(@"echo \$?", new[] { "echo", "$?" })]
	public void Split_WithStatusVariable_ExpandsOutsideSingleQuotes(string line, string[] expected)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["?"] = "3" };

		// Act
		var words = CommandLine.Split(line, variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.Quote" /> gives one word that <see cref="CommandLine.Split" /> reads back
	/// unchanged, whatever quotes, spaces or <c>$</c> it holds.
	/// </summary>
	/// <param name="text">The text to quote.</param>
	[Theory]
	[InlineData("plain")]
	[InlineData("it's here")]
	[InlineData("$HOME and \"quotes\" and \\")]
	[InlineData("")]
	public void Quote_WithText_SplitsBackToSameWord(string text)
	{
		// Act
		var words = CommandLine.Split(CommandLine.Quote(text), new Dictionary<string, string> { ["HOME"] = "expanded" });

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(text);
	}

	/// <summary>
	/// Checks that each word records where it was typed, quotes and backslashes included, so an error can point at it.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="expected">Each word's start and end, as <c>start-end</c>.</param>
	[Theory]
	[InlineData("ls -la", new[] { "0-2", "3-6" })]
	[InlineData("  cd  x  ", new[] { "2-4", "6-7" })]
	[InlineData("echo 'a b'", new[] { "0-4", "5-10" })]
	[InlineData(@"a\ b c", new[] { "0-4", "5-6" })]
	[InlineData("ls|wc", new[] { "0-2", "2-3", "3-5" })]
	[InlineData("echo a 2>err", new[] { "0-4", "5-6", "7-9", "9-12" })]
	[InlineData("echo a >> f", new[] { "0-4", "5-6", "7-9", "10-11" })]
	public void Split_WithLine_RecordsWordPositions(string line, string[] expected)
	{
		// Act
		var words = CommandLine.Split(line);

		// Assert
		_ = words.Select(word => $"{word.Start}-{word.End}").Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.SplitRedirect" /> keeps the file name's position, so a failed redirect can
	/// point at it.
	/// </summary>
	[Fact]
	public void SplitRedirect_WithPath_KeepsPathPosition()
	{
		// Act
		var (_, path, _, errorPath, _) = CommandLine.SplitRedirect(CommandLine.Split("ls > out 2> err"));

		// Assert
		_ = path.Should().Be(new Word("out", IsOperator: false, 5, 8));
		_ = errorPath.Should().Be(new Word("err", IsOperator: false, 12, 15));
	}
}
