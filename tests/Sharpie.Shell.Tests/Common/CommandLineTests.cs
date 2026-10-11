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
		_ = words.Should().Equal(expected);
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
		_ = words.Should().Equal(expected);
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
	/// Checks that <see cref="CommandLine.SplitRedirect" /> takes <c>&gt;</c> or <c>1&gt;</c> and the file name after
	/// it out of the words.
	/// </summary>
	/// <param name="words">The split command line.</param>
	/// <param name="expectedWords">The words that should be left for the command.</param>
	/// <param name="expectedPath">The file name that should be returned.</param>
	[Theory]
	[InlineData(new[] { "echo", "hi", ">", "out.txt" }, new[] { "echo", "hi" }, "out.txt")]
	[InlineData(new[] { "echo", "hi", "1>", "out.txt" }, new[] { "echo", "hi" }, "out.txt")]
	[InlineData(new[] { "ls", "/tmp", ">", "/tmp/foo/baz.md" }, new[] { "ls", "/tmp" }, "/tmp/foo/baz.md")]
	public void SplitRedirect_WithOperator_ReturnsWordsAndPath(string[] words, string[] expectedWords, string expectedPath)
	{
		// Act
		var (result, path, append, errorPath, _) = CommandLine.SplitRedirect(words);

		// Assert
		_ = result.Should().Equal(expectedWords);
		_ = path.Should().Be(expectedPath);
		_ = append.Should().BeFalse();
		_ = errorPath.Should().BeNull();
	}

	/// <summary>
	/// Checks that <c>&gt;&gt;</c> and <c>1&gt;&gt;</c> are taken out of the words with their file name and ask for
	/// standard output to be appended.
	/// </summary>
	/// <param name="operatorText">The append operator to use.</param>
	[Theory]
	[InlineData(">>")]
	[InlineData("1>>")]
	public void SplitRedirect_WithAppendOperator_ReturnsPathAndAppend(string operatorText)
	{
		// Act
		var (result, path, append, errorPath, _) = CommandLine.SplitRedirect(["echo", "hi", operatorText, "out.md"]);

		// Assert
		_ = result.Should().Equal("echo", "hi");
		_ = path.Should().Be("out.md");
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
		var (_, path, append, _, _) = CommandLine.SplitRedirect(["echo", "hi", ">>", "a.md", ">", "b.md"]);

		// Assert
		_ = path.Should().Be("b.md");
		_ = append.Should().BeFalse();
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.SplitRedirect" /> takes <c>2&gt;</c> and its file name out of the words
	/// and leaves standard output alone.
	/// </summary>
	[Fact]
	public void SplitRedirect_WithErrorOperator_ReturnsErrorPath()
	{
		// Act
		var (result, path, _, errorPath, errorAppend) = CommandLine.SplitRedirect(["cat", "a", "nonexistent", "2>", "err.md"]);

		// Assert
		_ = result.Should().Equal("cat", "a", "nonexistent");
		_ = path.Should().BeNull();
		_ = errorPath.Should().Be("err.md");
		_ = errorAppend.Should().BeFalse();
	}

	/// <summary>
	/// Checks that <c>2&gt;&gt;</c> is taken out of the words with its file name and asks for standard error to be
	/// appended, leaving standard output alone.
	/// </summary>
	[Fact]
	public void SplitRedirect_WithErrorAppendOperator_ReturnsErrorPathAndAppend()
	{
		// Act
		var (result, path, append, errorPath, errorAppend) =
			CommandLine.SplitRedirect(["cat", "nonexistent", "2>>", "err.md"]);

		// Assert
		_ = result.Should().Equal("cat", "nonexistent");
		_ = path.Should().BeNull();
		_ = append.Should().BeFalse();
		_ = errorPath.Should().Be("err.md");
		_ = errorAppend.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>&gt;</c> and <c>2&gt;</c> on the same line each get their own file name.
	/// </summary>
	[Fact]
	public void SplitRedirect_WithBothOperators_ReturnsBothPaths()
	{
		// Act
		var (result, path, _, errorPath, _) = CommandLine.SplitRedirect(["ls", ">", "out.md", "2>", "err.md"]);

		// Assert
		_ = result.Should().Equal("ls");
		_ = path.Should().Be("out.md");
		_ = errorPath.Should().Be("err.md");
	}

	/// <summary>
	/// Checks that words with no operator, or an operator with no file name after it, come back unchanged.
	/// </summary>
	/// <param name="words">The split command line.</param>
	[Theory]
	[InlineData([new[] { "echo", "hi" }])]
	[InlineData([new[] { "echo", "hi", ">" }])]
	[InlineData([new[] { "echo", "hi", "2>" }])]
	[InlineData([new[] { "echo", "hi", ">>" }])]
	[InlineData([new[] { "echo", "hi", "2>>" }])]
	public void SplitRedirect_WithoutRedirect_ReturnsWordsUnchanged(string[] words)
	{
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
	/// Checks that <see cref="CommandLine.SplitPipeline" /> splits at every <c>|</c>, and gives an empty array when a
	/// command between bars is missing.
	/// </summary>
	/// <param name="line">The words joined by spaces.</param>
	/// <param name="expected">The stages, each one's words joined by spaces.</param>
	[Theory]
	[InlineData("ls -la", new[] { "ls -la" })]
	[InlineData("cat f | wc", new[] { "cat f", "wc" })]
	[InlineData("cat f | head -n 3 | wc", new[] { "cat f", "head -n 3", "wc" })]
	[InlineData("cat f |", new string[0])]
	[InlineData("| wc", new string[0])]
	[InlineData("cat f | | wc", new string[0])]
	public void SplitPipeline_WithBars_ReturnsStages(string line, string[] expected)
	{
		// Act
		var result = CommandLine.SplitPipeline(line.Split(' '));

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
	public void PagedCommand_Line_ReturnsWordsForReader(string line, string[]? expected)
	{
		// Act
		var result = CommandLine.PagedCommand(line);

		// Assert
		_ = result.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
	}
}
