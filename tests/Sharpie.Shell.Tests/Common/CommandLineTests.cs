using System.Globalization;
using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="CommandLine" />.
/// </summary>
[Collection(GlobalState.Name)]
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
	[InlineData("echo $ $1", new[] { "echo", "$" })]
	[InlineData("echo ${A}end", new[] { "echo", "oneend" })]
	[InlineData("echo ${A}and${B}", new[] { "echo", "oneandtwo" })]
	[InlineData("echo stock_${A}_id", new[] { "echo", "stock_one_id" })]
	[InlineData("echo \"${A} x\"", new[] { "echo", "one x" })]
	[InlineData("echo '${A}'", new[] { "echo", "${A}" })]
	[InlineData("echo ${MISSING} z", new[] { "echo", "z" })]
	[InlineData("echo ${A", new[] { "echo", "${A" })]
	[InlineData("echo a$", new[] { "echo", "a$" })]
	[InlineData("echo a$1", new[] { "echo", "a" })]
	[InlineData("echo _$1", new[] { "echo", "_" })]
	[InlineData("echo $%", new[] { "echo", "$%" })]
	[InlineData("echo $-", new[] { "echo" })]
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
	/// Checks that <c>$0</c> to <c>$9</c>, <c>${10}</c> and <c>$#</c> expand to the positional parameters, and that
	/// <c>$@</c> and <c>$*</c> give one word per parameter, joined to the text that touches them, as bash does.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="parameters">The positional parameters.</param>
	/// <param name="expected">The words the line should produce.</param>
	[Theory]
	[InlineData("echo $0 $1 $2 $#", new[] { "a", "b" }, new[] { "echo", "name", "a", "b", "2" })]
	[InlineData("echo $10 ${10}", new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "ten" }, new[] { "echo", "10", "ten" })]
	[InlineData("echo \"$1\" $3", new[] { "a b" }, new[] { "echo", "a b" })]
	[InlineData("echo $# $#x", new string[0], new[] { "echo", "0", "0x" })]
	[InlineData("echo $@", new[] { "a", "b c" }, new[] { "echo", "a", "b", "c" })]
	[InlineData("echo $*", new[] { "a b", "c" }, new[] { "echo", "a", "b", "c" })]
	[InlineData("echo ${@} ${*}", new[] { "a", "b" }, new[] { "echo", "a", "b", "a", "b" })]
	[InlineData("echo \"$@\"", new[] { "a", "", "b" }, new[] { "echo", "a", "", "b" })]
	[InlineData("echo $@", new[] { "a", "", "b" }, new[] { "echo", "a", "b" })]
	[InlineData("echo \"x$@y\"", new[] { "a", "b" }, new[] { "echo", "xa", "by" })]
	[InlineData("echo x$@y", new[] { "a" }, new[] { "echo", "xay" })]
	[InlineData("echo \"$*\"", new[] { "a", "b" }, new[] { "echo", "a b" })]
	[InlineData("echo $*", new[] { "a", "b" }, new[] { "echo", "a", "b" })]
	[InlineData("echo \"$@\" end", new string[0], new[] { "echo", "end" })]
	[InlineData("echo \"x$@y\"", new string[0], new[] { "echo", "xy" })]
	[InlineData("echo \"$@\"y", new string[0], new[] { "echo", "y" })]
	[InlineData("echo \"x$@\"", new string[0], new[] { "echo", "x" })]
	[InlineData("echo \"$*\"", new string[0], new[] { "echo", "" })]
	[InlineData("echo '$@' \\$@ ${@", new[] { "a" }, new[] { "echo", "$@", "$@", "${@" })]
	[InlineData("echo \"$@\" | wc", new[] { "a", "b" }, new[] { "echo", "a", "b", "|", "wc" })]
	[InlineData("echo ${N:-\"$@\"}", new[] { "a b", "c" }, new[] { "echo", "a b", "c" })]
	[InlineData("echo ${N:-$@}", new[] { "a b", "c" }, new[] { "echo", "a", "b", "c" })]
	public void Split_WithPositionalParameters_ExpandsThem(string line, string[] parameters, string[] expected)
	{
		// Arrange
		var declare = new DeclareCommand(new ShellSettings());

		declare.Set("0", "name");
		declare.SetPositional(parameters);

		// Act
		var words = CommandLine.Split(line, declare.Variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that an unquoted variable splits on space, tab and newline when <c>IFS</c> isn't set, except where bash
	/// keeps it whole: inside double quotes, in an assignment and in a here-string.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="expected">The words the line should produce.</param>
	[Theory]
	[InlineData("ls $F", new[] { "ls", "-l", "-a" })]
	[InlineData("echo \"$F\"", new[] { "echo", "-l -a" })]
	[InlineData("echo x${S}y", new[] { "echo", "x", "a", "b", "y" })]
	[InlineData("A=$F", new[] { "A=-l -a" })]
	[InlineData("export A=$F", new[] { "export", "A=-l -a" })]
	[InlineData("local A=$F", new[] { "local", "A=-l -a" })]
	[InlineData("echo A=$F", new[] { "echo", "A=-l", "-a" })]
	[InlineData("cat <<< $S", new[] { "cat", "<<<", "  a  b  " })]
	[InlineData("echo > $O", new[] { "echo", ">", "out" })]
	[InlineData("echo ${N:-$G}", new[] { "echo", "a", "b", "c" })]
	[InlineData("A=${N:-a  b}", new[] { "A=a  b" })]
	[InlineData("echo ${F#-l}", new[] { "echo", "-a" })]
	[InlineData("echo $E x", new[] { "echo", "x" })]
	public void Split_WithUnquotedVariable_SplitsOnIfs(string line, string[] expected)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["F"] = "-l -a", ["S"] = "  a  b  ", ["G"] = "a b\tc", ["E"] = string.Empty, ["O"] = "  out  " };

		// Act
		var words = CommandLine.Split(line, variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that unquoted expansions split on <c>IFS</c> as bash splits them, where blanks run together and any
	/// other separator ends a field by itself.
	/// </summary>
	/// <param name="ifs">The value of <c>IFS</c>, or <see langword="null" /> to leave it unset.</param>
	/// <param name="line">The command line to split.</param>
	/// <param name="expected">The words the line should produce.</param>
	[Theory]
	[InlineData(":", "p $V", new[] { "p", "a", "", "b" })]
	[InlineData(":", "p $L", new[] { "p", "", "a" })]
	[InlineData(":", "p x$L", new[] { "p", "x", "a" })]
	[InlineData(":", "$L x", new[] { "", "a", "x" })]
	[InlineData(":", "p \"\"$L", new[] { "p", "", "a" })]
	[InlineData(":", "p $B", new[] { "p", "a b" })]
	[InlineData(" :", "p $C", new[] { "p", "a", "b" })]
	[InlineData(" :", "p $D", new[] { "p", "", "a" })]
	[InlineData(" :", "p $H", new[] { "p", "a", "", "b" })]
	[InlineData(" :", "p $X$Y", new[] { "p", "a", "b" })]
	[InlineData(" :", "p $X $Y", new[] { "p", "a", "", "b" })]
	[InlineData("", "p $B", new[] { "p", "a b" })]
	[InlineData("", "p ${N:-a b}", new[] { "p", "a b" })]
	[InlineData(null, "p $T", new[] { "p", "a", "b", "c" })]
	[InlineData(":", "p $(x)", new[] { "p", "a", "b" })]
	[InlineData(":", "p ${N:-a b}", new[] { "p", "a b" })]
	[InlineData(":", "p ${N:-a:b}", new[] { "p", "a", "b" })]
	[InlineData(":", "p ${N:-a::b}", new[] { "p", "a", "", "b" })]
	[InlineData(":", "p ${N:-\"a:b\":c}", new[] { "p", "a:b", "c" })]
	[InlineData(":", "p ${N:-a\\:b}", new[] { "p", "a:b" })]
	[InlineData(":", "p ${N:-:a}", new[] { "p", "", "a" })]
	[InlineData(":", "p x${N:-:a}", new[] { "p", "x", "a" })]
	[InlineData(":", "A=${N:-a:b}", new[] { "A=a:b" })]
	[InlineData(" :", "p $X${N:-:b}", new[] { "p", "a", "b" })]
	[InlineData(" :", "p ${N:-a :b}", new[] { "p", "a", "b" })]
	[InlineData(null, "p x${N:- a}", new[] { "p", "x", "a" })]
	[InlineData(null, "p ${N:-a }y", new[] { "p", "a", "y" })]
	[InlineData(null, "p ${N:-\"$B\"}", new[] { "p", "a b" })]
	[InlineData(null, "p ${N:-a \"b c\"  d}", new[] { "p", "a", "b c", "d" })]
	[InlineData(null, "p ${N:-\"\"}", new[] { "p", "" })]
	[InlineData(null, "p ${N:- }", new[] { "p" })]
	[InlineData(null, "p ${N:-\"\" x}", new[] { "p", "", "x" })]
	[InlineData(null, "p ${N:-$X}y", new[] { "p", "a", "y" })]
	[InlineData(null, "p ${N:-${M:-\"\"} x}", new[] { "p", "", "x" })]
	public void Split_WithIfs_SplitsAsBash(string? ifs, string line, string[] expected)
	{
		// Arrange
		var variables = new Dictionary<string, string>
		{
			["V"] = "a::b:",
			["L"] = ":a",
			["B"] = "a b",
			["C"] = "a : b",
			["D"] = " :a",
			["H"] = "a: :b",
			["X"] = "a ",
			["Y"] = ":b",
			["T"] = "a\tb\nc",
		};

		if (ifs is not null)
		{
			variables["IFS"] = ifs;
		}

		// Act
		var words = CommandLine.Split(line, variables, substitute: _ => "a:b");

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <c>"$*"</c> joins the parameters with the first character of <c>IFS</c>, as unquoted <c>$*</c> does
	/// where nothing splits, and that a here-document joins them with spaces, as bash does.
	/// </summary>
	/// <param name="ifs">The value of <c>IFS</c>, or <see langword="null" /> to leave it unset.</param>
	/// <param name="line">The command line to split, with <c>a</c>, <c>b</c> and <c>c</c> as the parameters.</param>
	/// <param name="expected">The words the line should produce.</param>
	[Theory]
	[InlineData(":", "echo \"$*\"", new[] { "echo", "a:b:c" })]
	[InlineData(":", "echo \"${*}\"", new[] { "echo", "a:b:c" })]
	[InlineData(":", "echo \"${*#a}\"", new[] { "echo", ":b:c" })]
	[InlineData(":", "echo \"${*:-z}\"", new[] { "echo", "a:b:c" })]
	[InlineData(":", "echo \"$@\"", new[] { "echo", "a", "b", "c" })]
	[InlineData(":", "echo $*", new[] { "echo", "a", "b", "c" })]
	[InlineData(", ", "echo \"$*\"", new[] { "echo", "a,b,c" })]
	[InlineData("", "echo \"$*\"", new[] { "echo", "abc" })]
	[InlineData(null, "echo \"$*\"", new[] { "echo", "a b c" })]
	[InlineData(":", "A=$*", new[] { "A=a:b:c" })]
	[InlineData(":", "A=$@", new[] { "A=a b c" })]
	[InlineData(":", "[[ $* ]]", new[] { "[[", "a:b:c", "]]" })]
	[InlineData(":", "cat <<< $*", new[] { "cat", "<<<", "a:b:c" })]
	[InlineData(":", "cat <<EOF\n$* ${*#a} $@\nEOF", new[] { "cat", "<<", "a b c  b c a b c\n" })]
	public void Split_WithStarParameter_JoinsOnIfs(string? ifs, string line, string[] expected)
	{
		// Arrange
		var declare = new DeclareCommand(new ShellSettings());

		declare.SetPositional(["a", "b", "c"]);

		if (ifs is not null)
		{
			declare.Set("IFS", ifs);
		}

		// Act
		var words = CommandLine.Split(line, declare.Variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a redirect's file name or stream that splits into more than one word or into none comes back as one
	/// ambiguous word holding it as typed, and one that gives exactly one word doesn't.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="target">The text the word after the operator should have.</param>
	/// <param name="ambiguous">Whether it should be ambiguous.</param>
	[Theory]
	[InlineData("echo > $F", "$F", true)]
	[InlineData("echo > ${F}", "${F}", true)]
	[InlineData("echo > x$F", "x$F", true)]
	[InlineData("echo > $E", "$E", true)]
	[InlineData("echo 2>&$X", "$X", true)]
	[InlineData("echo > $O", "out", false)]
	[InlineData("echo > \"$F\"", "a b", false)]
	[InlineData("echo > ''", "", false)]
	[InlineData("cat <<< $F", "a b", false)]
	public void Split_WithSplitRedirectTarget_MarksItAmbiguous(string line, string target, bool ambiguous)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["F"] = "a b", ["E"] = string.Empty, ["O"] = "  out  ", ["X"] = "1 2" };

		// Act
		var words = CommandLine.Split(line, variables);

		// Assert
		_ = words.Should().HaveCount(3);
		_ = (words[2].Text, words[2].Ambiguous).Should().Be((target, ambiguous));
	}

	/// <summary>
	/// Checks that an ambiguous redirect target covers only the word typed after the operator, so the words after it
	/// stay arguments, and that it points at that word.
	/// </summary>
	[Fact]
	public void Split_WithAmbiguousRedirectBeforeArgument_KeepsArgument()
	{
		// Act
		var words = CommandLine.Split("echo >$F x", new Dictionary<string, string> { ["F"] = "a b" });

		// Assert
		_ = words.Should().Equal(
			new Word("echo", IsOperator: false, 0, 4),
			new Word(">", IsOperator: true, 5, 6),
			new Word("$F", IsOperator: false, 6, 8, Ambiguous: true),
			new Word("x", IsOperator: false, 9, 10));
	}

	/// <summary>
	/// Checks that a redirect to a wildcard that matches more than one file is ambiguous, and one that matches a single
	/// file names it.
	/// </summary>
	[Fact]
	public void Split_WithRedirectToWildcard_IsAmbiguousOnlyForSeveralFiles()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		try
		{
			File.WriteAllText(Path.Combine(folder, "x1.tmp"), string.Empty);
			File.WriteAllText(Path.Combine(folder, "x2.tmp"), string.Empty);

			// Act
			var several = CommandLine.Split($"echo > {folder}/*.tmp", new Dictionary<string, string>());
			var one = CommandLine.Split($"echo > {folder}/x1*", new Dictionary<string, string>());

			// Assert
			_ = (several[2].Text, several[2].Ambiguous).Should().Be(($"{folder}/*.tmp", true));
			_ = (one[2].Text, one[2].Ambiguous).Should().Be((Path.Combine(folder, "x1.tmp"), false));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that the unset callback gets every variable that's set in neither the shell nor the environment, with
	/// where it was typed, and none of the ones that always count as set.
	/// </summary>
	[Fact]
	public void Split_WithUnsetCallback_ReportsUnsetNames()
	{
		// Arrange
		var declare = new DeclareCommand(new ShellSettings());
		var unset = new List<Word>();

		declare.Set("A", "one");

		// Act
		var words = CommandLine.Split("echo $A $nope \"${gone}\" $1 $? $# $@ \"$*\" $PATH '$quoted'", declare.Variables, unset.Add);

		// Assert
		_ = unset.Should().Equal(new Word("nope", false, 8, 13), new Word("gone", false, 15, 22), new Word("1", false, 24, 26));
		_ = words.Select(word => word.Text).Should().StartWith(["echo", "one", ""]);
	}

	/// <summary>
	/// Checks that <c>$$</c>, <c>${$}</c> and <c>$-</c> expand to the shell's process id and option letters, and that
	/// <c>$!</c> counts as unset before any background program.
	/// </summary>
	[Fact]
	public void Split_WithSpecialParameters_ExpandsThem()
	{
		// Arrange
		var settings = new ShellSettings();
		var declare = new DeclareCommand(settings);
		var unset = new List<Word>();
		var saved = ExternalProgram.LastBackground;
		var id = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

		_ = settings.Set("nounset", true);
		ExternalProgram.LastBackground = null;

		try
		{
			// Act
			var words = CommandLine.Split("echo $$ ${$} $-x \"$$\" $!", declare.Variables, unset.Add);

			// Assert
			_ = words.Select(word => word.Text).Should().Equal("echo", id, id, "ux", id);
			_ = unset.Should().Equal(new Word("!", false, 22, 24));
		}
		finally
		{
			ExternalProgram.LastBackground = saved;
		}
	}

	/// <summary>
	/// Checks that the words <c>$@</c> gives all point at the expansion, so an error can show where they came from.
	/// </summary>
	[Fact]
	public void Split_WithParameterList_PointsEachWordAtIt()
	{
		// Arrange
		var declare = new DeclareCommand(new ShellSettings());

		declare.SetPositional(["a", "b"]);

		// Act
		var words = CommandLine.Split("echo x$@", declare.Variables);

		// Assert
		_ = words.Should().Equal(new Word("echo", false, 0, 4), new Word("xa", false, 5, 8), new Word("b", false, 5, 8));
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.QuoteIfNeeded" /> leaves plain words alone and quotes the rest the way bash's
	/// <c>set</c> and <c>set -x</c> do.
	/// </summary>
	/// <param name="text">The text to quote.</param>
	/// <param name="expected">The text as it should be written.</param>
	[Theory]
	[InlineData("plain", "plain")]
	[InlineData("a=b:c,d@e%f+g~h/i.j-k_0", "a=b:c,d@e%f+g~h/i.j-k_0")]
	[InlineData("", "''")]
	[InlineData("a b", "'a b'")]
	[InlineData("it's", @"'it'\''s'")]
	[InlineData("~", "'~'")]
	[InlineData("$x", "'$x'")]
	[InlineData("*", "'*'")]
	public void QuoteIfNeeded_WithText_QuotesOnlyWhenNeeded(string text, string expected)
	{
		// Act
		var quoted = CommandLine.QuoteIfNeeded(text);

		// Assert
		_ = quoted.Should().Be(expected);
	}

	/// <summary>
	/// Checks that an unquoted <c>#</c> at the start of a word drops the rest of the line, and that a <c>#</c> inside a
	/// word, in quotes or after a backslash is kept.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="expected">The words the line should produce.</param>
	[Theory]
	[InlineData("# a whole comment", new string[0])]
	[InlineData(" #x", new string[0])]
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
	/// Checks that <see cref="CommandLine.SplitRedirect" /> takes each redirect and the word after it out of the
	/// words, in typed order, with its stream number or the operator's default stream.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expectedWords">The words that should be left for the command.</param>
	/// <param name="expectedRedirects">Each redirect as its stream, operator and target word, joined by spaces.</param>
	[Theory]
	[InlineData("echo hi > out.txt", new[] { "echo", "hi" }, new[] { "1 > out.txt" })]
	[InlineData("echo hi 1> out.txt", new[] { "echo", "hi" }, new[] { "1 > out.txt" })]
	[InlineData("echo hi>out.txt", new[] { "echo", "hi" }, new[] { "1 > out.txt" })]
	[InlineData("ls /tmp > /tmp/foo/baz.md", new[] { "ls", "/tmp" }, new[] { "1 > /tmp/foo/baz.md" })]
	[InlineData("echo hi 1>>out.md", new[] { "echo", "hi" }, new[] { "1 >> out.md" })]
	[InlineData("cat a nonexistent 2>err.md", new[] { "cat", "a", "nonexistent" }, new[] { "2 > err.md" })]
	[InlineData("cat nonexistent 2>> err.md", new[] { "cat", "nonexistent" }, new[] { "2 >> err.md" })]
	[InlineData("cat < in.txt", new[] { "cat" }, new[] { "0 < in.txt" })]
	[InlineData("cat 3< in.txt", new[] { "cat" }, new[] { "3 < in.txt" })]
	[InlineData("ls >| f", new[] { "ls" }, new[] { "1 >| f" })]
	[InlineData("ls &> f", new[] { "ls" }, new[] { "1 &> f" })]
	[InlineData("ls &>> f", new[] { "ls" }, new[] { "1 &>> f" })]
	[InlineData("ls 2>&1", new[] { "ls" }, new[] { "2 >& 1" })]
	[InlineData("ls >&2", new[] { "ls" }, new[] { "1 >& 2" })]
	[InlineData("ls >&f", new[] { "ls" }, new[] { "1 &> f" })]
	[InlineData("ls 1>&f", new[] { "ls" }, new[] { "1 &> f" })]
	[InlineData("ls 2>&f", new[] { "ls" }, new[] { "2 >& f" })]
	[InlineData("ls >&-", new[] { "ls" }, new[] { "1 >& -" })]
	[InlineData("ls >& ''", new[] { "ls" }, new[] { "1 &> " })]
	[InlineData("cat <&3", new[] { "cat" }, new[] { "0 <& 3" })]
	[InlineData("cat 0<&-", new[] { "cat" }, new[] { "0 <& -" })]
	[InlineData("cat <<< word", new[] { "cat" }, new[] { "0 <<< word" })]
	[InlineData("cat 4<<< word", new[] { "cat" }, new[] { "4 <<< word" })]
	[InlineData("ls 255> f", new[] { "ls" }, new[] { "255 > f" })]
	[InlineData("ls > a 2>&1 < b x", new[] { "ls", "x" }, new[] { "1 > a", "2 >& 1", "0 < b" })]
	[InlineData("echo hi >> a.md > b.md", new[] { "echo", "hi" }, new[] { "1 >> a.md", "1 > b.md" })]
	[InlineData("> f", new string[0], new[] { "1 > f" })]
	public void SplitRedirect_WithRedirects_ReturnsWordsAndRedirects(string line, string[] expectedWords, string[] expectedRedirects)
	{
		// Act
		var (words, redirects) = CommandLine.SplitRedirect(CommandLine.Split(line));

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expectedWords);
		_ = redirects.Select(redirect => $"{redirect.Stream} {redirect.Operator} {redirect.Target.Text}").Should().Equal(expectedRedirects);
	}

	/// <summary>
	/// Checks that words with no usable redirect come back unchanged: no operator, a quoted one, an operator with no
	/// word after it or another operator in its place, a stream number above 255, and <c>&lt;&gt;</c>, which Sharpie
	/// doesn't run yet.
	/// </summary>
	/// <param name="line">The command line.</param>
	[Theory]
	[InlineData("echo hi")]
	[InlineData("echo hi >")]
	[InlineData("echo hi 2>")]
	[InlineData("echo hi >>")]
	[InlineData("echo hi 2>&")]
	[InlineData("cat <<")]
	[InlineData("echo hi '>' out.md")]
	[InlineData(@"echo hi \> out.md")]
	[InlineData("echo hi > | wc")]
	[InlineData("echo hi 256> f")]
	[InlineData("echo hi 99999999999> f")]
	[InlineData("cat <> f")]
	public void SplitRedirect_WithoutUsableRedirect_ReturnsWordsUnchanged(string line)
	{
		// Arrange
		var words = CommandLine.Split(line);

		// Act
		var (result, redirects) = CommandLine.SplitRedirect(words);

		// Assert
		_ = result.Should().Equal(words);
		_ = redirects.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.StreamOf" /> reads a typed stream number, gives 0 or 1 when none is typed,
	/// and gives nothing for a number above 255 or an operator that isn't a redirect Sharpie runs.
	/// </summary>
	/// <param name="typed">The operator as typed.</param>
	/// <param name="expected">The stream, or <see langword="null" />.</param>
	[Theory]
	[InlineData(">", 1)]
	[InlineData(">>", 1)]
	[InlineData("&>", 1)]
	[InlineData("<", 0)]
	[InlineData("<<<", 0)]
	[InlineData("<&", 0)]
	[InlineData("2>", 2)]
	[InlineData("0>", 0)]
	[InlineData("255>&", 255)]
	[InlineData("256>", null)]
	[InlineData("99999999999>", null)]
	[InlineData("<>", null)]
	[InlineData("|", null)]
	public void StreamOf_WithOperator_ReturnsStream(string typed, int? expected)
	{
		// Act
		var stream = CommandLine.StreamOf(typed);

		// Assert
		_ = stream.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.RedirectKind" /> tells file, stream and text targets apart, with or
	/// without a stream number, and gives nothing for other operators.
	/// </summary>
	/// <param name="typed">The operator as typed.</param>
	/// <param name="expected">The kind, or <see langword="null" />.</param>
	[Theory]
	[InlineData("<", "File")]
	[InlineData(">", "File")]
	[InlineData("2>>", "File")]
	[InlineData(">|", "File")]
	[InlineData("&>", "File")]
	[InlineData("&>>", "File")]
	[InlineData("2>&", "Stream")]
	[InlineData("<&", "Stream")]
	[InlineData("<<", "Text")]
	[InlineData("<<-", "Text")]
	[InlineData("<<<", "Text")]
	[InlineData("<>", null)]
	[InlineData("|&", null)]
	[InlineData("|", null)]
	[InlineData("&&", null)]
	public void RedirectKind_WithOperator_ReturnsKind(string typed, string? expected)
	{
		// Act
		var kind = CommandLine.RedirectKind(typed);

		// Assert
		_ = (kind?.ToString()).Should().Be(expected);
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.SplitPipeline" /> splits at every <c>|</c> operator, keeping redirects in
	/// their stage and an empty stage where a <c>|</c> has nothing on one side.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The stages, each one's words joined by spaces.</param>
	[Theory]
	[InlineData("ls -la", new[] { "ls -la" })]
	[InlineData("cat f | wc", new[] { "cat f", "wc" })]
	[InlineData("cat f|wc", new[] { "cat f", "wc" })]
	[InlineData("cat f | head -n 3 | wc", new[] { "cat f", "head -n 3", "wc" })]
	[InlineData("echo '|' x", new[] { "echo | x" })]
	[InlineData("cat f 2>&1 | wc > out", new[] { "cat f 2>& 1", "wc > out" })]
	[InlineData("cat f |", new[] { "cat f", "" })]
	[InlineData("| wc", new[] { "", "wc" })]
	[InlineData("cat f | | wc", new[] { "cat f", "", "wc" })]
	public void SplitPipeline_WithBars_ReturnsStages(string line, string[] expected)
	{
		// Act
		var result = CommandLine.SplitPipeline(CommandLine.Split(line));

		// Assert
		_ = result.Select(stage => string.Join(' ', stage.Select(word => word.Text))).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a here-document's body takes the place of its delimiter word, expanded when no part of the delimiter
	/// was quoted, and kept literal when one was.
	/// </summary>
	/// <param name="line">The command, its body and its delimiter line.</param>
	/// <param name="expected">The text the command should read.</param>
	[Theory]
	[InlineData("cat <<EOF\nhello $X\nEOF", "hello val\n")]
	[InlineData("cat <<EOF\n${X}y $? $ $1 $@ $*\nEOF", "valy 3 $ a a b a b\n")]
	[InlineData("cat <<EOF\n${N:-d} ${X:+\"q r\"} ${#X} ${X%l}\nEOF", "d q r 3 va\n")]
	[InlineData("cat <<'EOF'\nhello $X\nEOF", "hello $X\n")]
	[InlineData("cat <<\"EOF\"\nhello $X\nEOF", "hello $X\n")]
	[InlineData("cat <<E\\OF\nhello $X\nEOF", "hello $X\n")]
	[InlineData("cat <<E'O'F\nhello $X\nEOF", "hello $X\n")]
	[InlineData("cat <<EOF\na\\$b \\x \\\\ \\`c end\\\njoined\nEOF", "a$b \\x \\ `c endjoined\n")]
	[InlineData("cat <<'EOF'\na\\$b \\\\ end\\\nEOF", "a\\$b \\\\ end\\\n")]
	[InlineData("cat <<EOF\ndon't \"stop\" # here; ok\nEOF", "don't \"stop\" # here; ok\n")]
	[InlineData("cat <<EOF\n\tkept\n\tEOF\nEOF", "\tkept\n\tEOF\n")]
	[InlineData("cat <<-EOF\n\t\tstripped\n\tEOF", "stripped\n")]
	[InlineData("cat <<EOF\n\nEOF", "\n")]
	[InlineData("cat <<EOF\nEOF", "")]
	[InlineData("cat <<EOF\nnever closed", "never closed\n")]
	[InlineData("cat <<EOF", "")]
	[InlineData("cat <<EOF # note\nbody\nEOF", "body\n")]
	[InlineData("cat <<$X\nbody $X\nval\n$X", "body val\nval\n")]
	[InlineData("cat <<~\nbody\n~", "body\n")]
	public void Split_WithHereDocument_GivesBodyInPlaceOfDelimiter(string line, string expected)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["X"] = "val", ["?"] = "3", ["#"] = "2", ["1"] = "a", ["2"] = "b" };

		// Act
		var words = CommandLine.Split(line, variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("cat", words[1].Text, expected);
		_ = words[1].Text.Should().StartWith("<<");
	}

	/// <summary>
	/// Checks that command substitution in a here-document's body runs only when the delimiter isn't quoted, that its
	/// output isn't split, and that the delimiter itself runs nothing.
	/// </summary>
	/// <param name="line">The command, its body and its delimiter line.</param>
	/// <param name="expected">The text the command should read.</param>
	[Theory]
	[InlineData("cat <<EOF\na $(echo x) `b` \\$(no) \\`no`\nEOF", "a [echo x] [b] $(no) `no`\n")]
	[InlineData("cat <<EOF\n`echo \\\"q\\\" \\$X`\nEOF", "[echo \\\"q\\\" $X]\n")]
	[InlineData("cat <<EOF\n$(echo \")\")\nEOF", "[echo \")\"]\n")]
	[InlineData("cat <<EOF\n$(lines)\nEOF", "one  two\n\nthree\n")]
	[InlineData("cat <<EOF\n$(never closed\nEOF", "$(never closed\n")]
	[InlineData("cat <<'EOF'\n$(echo x) `b`\nEOF", "$(echo x) `b`\n")]
	[InlineData("cat <<$(x)\nbody\n$(x)", "body\n")]
	public void Split_WithSubstitutionInHereDocument_RunsItWhenUnquoted(string line, string expected)
	{
		// Arrange
		var variables = new Dictionary<string, string>();

		// Act
		var words = CommandLine.Split(line, variables, substitute: list => list == "lines" ? "one  two\n\nthree\n\n" : $"[{list}]");

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("cat", words[1].Text, expected);
	}

	/// <summary>
	/// Checks that a variable in a here-document's body that isn't set is reported at the delimiter, and that with no
	/// one to report to it just expands to nothing.
	/// </summary>
	[Fact]
	public void Split_WithUnsetVariableInHereDocument_ReportsItAtDelimiter()
	{
		// Arrange
		var reported = new List<Word>();
		var variables = new Dictionary<string, string>();

		// Act
		var words = CommandLine.Split("cat <<EOF\na $SHARPIE_SAMPLE_UNSET b\nEOF", variables, reported.Add);
		var unreported = CommandLine.Split("cat <<EOF\n$SHARPIE_SAMPLE_UNSET\nEOF", variables);

		// Assert
		_ = words[2].Text.Should().Be("a  b\n");
		_ = reported.Should().Equal(new Word("SHARPIE_SAMPLE_UNSET", IsOperator: false, 6, 9));
		_ = unreported[2].Text.Should().Be("\n");
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.ExpandBody" /> with nothing to run substitutions keeps each one as typed,
	/// variables inside included, while still expanding the variables around it.
	/// </summary>
	[Fact]
	public void ExpandBody_WithoutSubstitute_KeepsSubstitutionsAsTyped()
	{
		// Act
		var body = CommandLine.ExpandBody("a $(x $Y) `b $Y` $Y", new Dictionary<string, string> { ["Y"] = "v" });

		// Assert
		_ = body.Should().Be("a $(x $Y) `b $Y` v");
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.ExpandBody" /> keeps a backslash at the very end of a body as it is.
	/// </summary>
	[Fact]
	public void ExpandBody_WithTrailingBackslash_KeepsIt()
	{
		// Act
		var body = CommandLine.ExpandBody(@"a\", new Dictionary<string, string>());

		// Assert
		_ = body.Should().Be(@"a\");
	}

	/// <summary>
	/// Checks that several here-documents on one line take their bodies in order, and that the delimiter word keeps
	/// its position in the first line.
	/// </summary>
	[Fact]
	public void Split_WithTwoHereDocuments_GivesEachItsOwnBody()
	{
		// Act
		var words = CommandLine.Split("cat <<A - <<-B\none\nA\n\ttwo\n\tB", new Dictionary<string, string>());

		// Assert
		_ = words.Should().Equal(
			new Word("cat", IsOperator: false, 0, 3),
			new Word("<<", IsOperator: true, 4, 6),
			new Word("one\n", IsOperator: false, 6, 7),
			new Word("-", IsOperator: false, 8, 9),
			new Word("<<-", IsOperator: true, 10, 13),
			new Word("two\n", IsOperator: false, 13, 14));
	}

	/// <summary>
	/// Checks that a here-string's word expands variables but no wildcards, as in bash.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The here-string's text.</param>
	[Theory]
	[InlineData("cat <<< *", "*")]
	[InlineData("cat <<< $X", "val")]
	[InlineData("cat <<< \"a  b\"", "a  b")]
	public void Split_WithHereString_ExpandsVariablesButNoWildcards(string line, string expected)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["X"] = "val" };

		// Act
		var words = CommandLine.Split(line, variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("cat", "<<<", expected);
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.HereDocuments" /> finds each here-document a line starts, with its delimiter
	/// unquoted, whether it was quoted, and whether it strips tabs.
	/// </summary>
	/// <param name="line">The command's first line.</param>
	/// <param name="expected">Each here-document as its delimiter, then q when quoted and t when it strips tabs.</param>
	[Theory]
	[InlineData("cat <<EOF", new[] { "EOF" })]
	[InlineData("cat << EOF | wc", new[] { "EOF" })]
	[InlineData("cat <<'EOF'", new[] { "EOF q" })]
	[InlineData("cat <<\"E\"OF", new[] { "EOF q" })]
	[InlineData("cat <<\\EOF", new[] { "EOF q" })]
	[InlineData("cat <<-EOF", new[] { "EOF t" })]
	[InlineData("cat 3<<-'EOF'", new[] { "EOF qt" })]
	[InlineData("cat <<$X", new[] { "$X" })]
	[InlineData("cat <<A; cat <<-B", new[] { "A", "B t" })]
	[InlineData("cat <<< EOF", new string[0])]
	[InlineData("cat <<", new string[0])]
	[InlineData("cat '<<EOF'", new string[0])]
	[InlineData("echo hi", new string[0])]
	public void HereDocuments_WithLine_ReturnsDelimiters(string line, string[] expected)
	{
		// Act
		var documents = CommandLine.HereDocuments(line);

		// Assert
		_ = documents.Select(document => $"{document.Delimiter} {(document.Quoted ? "q" : string.Empty)}{(document.StripTabs ? "t" : string.Empty)}".TrimEnd())
			.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.ReadHereDocument" /> says where the text after the delimiter line starts and
	/// whether the delimiter line was there.
	/// </summary>
	/// <param name="text">The text, with the body starting at 0.</param>
	/// <param name="expectedEnd">Where the text after the body starts.</param>
	/// <param name="expectedClosed">Whether the delimiter line was found.</param>
	[Theory]
	[InlineData("a\nEOF\nrest", 6, true)]
	[InlineData("a\nEOF", 5, true)]
	[InlineData("EOF\n", 4, true)]
	[InlineData("\nEOF", 4, true)]
	[InlineData("a\nb", 3, false)]
	[InlineData("a\nEOFx\n", 7, false)]
	[InlineData("", 0, false)]
	public void ReadHereDocument_WithText_FindsDelimiterLine(string text, int expectedEnd, bool expectedClosed)
	{
		// Act
		var (_, end, closed) = CommandLine.ReadHereDocument(text, 0, "EOF", stripTabs: false);

		// Assert
		_ = end.Should().Be(expectedEnd);
		_ = closed.Should().Be(expectedClosed);
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
	[InlineData("man $(echo ls)", null)]
	[InlineData("man \"$(echo ls)\"", null)]
	[InlineData("man '$(x)'", new[] { "man", "$(x)" })]
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
	/// Checks that an unquoted <c>~</c> at the start of a word, or after the <c>=</c> or a <c>:</c> of an assignment,
	/// becomes <c>HOME</c>.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The words it should give.</param>
	[Theory]
	[InlineData("ls ~", new[] { "ls", "/h" })]
	[InlineData("ls ~/Downloads", new[] { "ls", "/h/Downloads" })]
	[InlineData("cp x ~/", new[] { "cp", "x", "/h/" })]
	[InlineData("ls ~|wc", new[] { "ls", "/h", "|", "wc" })]
	[InlineData("echo x > ~/out", new[] { "echo", "x", ">", "/h/out" })]
	[InlineData("A=~/bin", new[] { "A=/h/bin" })]
	[InlineData("PATH=~/a:~/b", new[] { "PATH=/h/a:/h/b" })]
	[InlineData("make PREFIX=~", new[] { "make", "PREFIX=/h" })]
	public void Split_WithTilde_ExpandsHome(string line, string[] expected)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["HOME"] = "/h" };

		// Act
		var words = CommandLine.Split(line, variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a <c>~</c> that is quoted, escaped, in the middle of a word, or names no user stays as typed.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The words it should give.</param>
	[Theory]
	[InlineData("echo '~' \"~/x\" \\~", new[] { "echo", "~", "~/x", "~" })]
	[InlineData("echo a~ a/~", new[] { "echo", "a~", "a/~" })]
	[InlineData("A=b=~ A=b~:~", new[] { "A=b=~", "A=b~:/h" })]
	[InlineData("--opt=~ x:~", new[] { "--opt=~", "x:~" })]
	[InlineData("echo ~$NOSUCHSAMPLEVAR ~'u' ~\\u", new[] { "echo", "~", "~u", "~u" })]
	[InlineData("echo ~nosuchsampleuser/x ~:x", new[] { "echo", "~nosuchsampleuser/x", "~:x" })]
	[InlineData("A=~nosuchsampleuser:~", new[] { "A=~nosuchsampleuser:/h" })]
	public void Split_WithTildeNotExpandable_KeepsTilde(string line, string[] expected)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["HOME"] = "/h" };

		// Act
		var words = CommandLine.Split(line, variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that with no variables, as the highlighter splits, <c>~</c> stays as typed.
	/// </summary>
	[Fact]
	public void Split_WithoutVariables_KeepsTilde()
	{
		// Act
		var words = CommandLine.Split("ls ~/x");

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("ls", "~/x");
	}

	/// <summary>
	/// Checks that an empty <c>HOME</c> falls back to the profile folder .NET reports.
	/// </summary>
	[Fact]
	public void Split_WithEmptyHome_UsesProfileFolder()
	{
		// Act
		var words = CommandLine.Split("ls ~", new Dictionary<string, string> { ["HOME"] = string.Empty });

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("ls", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
	}

	/// <summary>
	/// Checks that <c>~user</c> gives that user's home folder from the system's user database, not <c>HOME</c>.
	/// </summary>
	[Fact]
	public void Split_WithCurrentUserTilde_ExpandsToTheirHome()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var words = CommandLine.Split($"ls ~{Environment.UserName}/x A=~{Environment.UserName}:x", new Dictionary<string, string> { ["HOME"] = "/h" });

		// Assert
		_ = words[1].Text.Should().EndWith("/x").And.NotStartWith("~").And.NotStartWith("/h/");
		_ = Directory.Exists(words[1].Text[..^2]).Should().BeTrue();
		_ = words[2].Text.Should().Be($"A={words[1].Text[..^2]}:x");
	}

	/// <summary>
	/// Checks that <c>~user</c> stays as typed when any of the user name is quoted, even for a user that exists.
	/// </summary>
	[Fact]
	public void Split_WithQuotedUserName_KeepsTilde()
	{
		// Arrange
		var user = Environment.UserName;

		// Act
		var words = CommandLine.Split($"ls ~'{user}'", new Dictionary<string, string> { ["HOME"] = "/h" });

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("ls", $"~{user}");
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
		var (_, redirects) = CommandLine.SplitRedirect(CommandLine.Split("ls > out 2> err"));

		// Assert
		_ = redirects.Select(redirect => redirect.Target).Should().Equal(new Word("out", IsOperator: false, 5, 8), new Word("err", IsOperator: false, 12, 15));
	}

	/// <summary>
	/// Checks that with variables given, an unquoted <c>*</c>, <c>?</c> or <c>[...]</c> becomes the sorted file names
	/// it matches, and that quoted or escaped wildcards, patterns that match nothing, <c>NAME=value</c> words in front
	/// of a command and a redirect to more than one file all stay as typed.
	/// </summary>
	/// <param name="line">The command line, with <c>{0}</c> for a folder holding <c>a.tmp</c>, <c>b.tmp</c> and <c>c.txt</c>.</param>
	/// <param name="expected">The words it should give, with <c>{0}</c> for the same folder.</param>
	[Theory]
	[InlineData("rm {0}/*.tmp", new[] { "rm", "{0}/a.tmp", "{0}/b.tmp" })]
	[InlineData("ls \"{0}\"/?.t*", new[] { "ls", "{0}/a.tmp", "{0}/b.tmp", "{0}/c.txt" })]
	[InlineData("ls {0}/[bc].*|wc", new[] { "ls", "{0}/b.tmp", "{0}/c.txt", "|", "wc" })]
	[InlineData("echo \"{0}/*.tmp\" '{0}/*.tmp'", new[] { "echo", "{0}/*.tmp", "{0}/*.tmp" })]
	[InlineData(@"echo {0}/\*.tmp {0}/a.tm\?", new[] { "echo", "{0}/*.tmp", "{0}/a.tm?" })]
	[InlineData("echo {0}/*.none", new[] { "echo", "{0}/*.none" })]
	[InlineData("echo $P", new[] { "echo", "{0}/a.tmp", "{0}/b.tmp" })]
	[InlineData("echo \"$P\"", new[] { "echo", "{0}/*.tmp" })]
	[InlineData("A={0}/* B={0}/* sampletool {0}/c.*", new[] { "A={0}/*", "B={0}/*", "sampletool", "{0}/c.txt" })]
	[InlineData("true | A={0}/* sampletool", new[] { "true", "|", "A={0}/*", "sampletool" })]
	[InlineData("echo x > {0}/*.tmp", new[] { "echo", "x", ">", "{0}/*.tmp" })]
	[InlineData("echo x > {0}/a.t?p", new[] { "echo", "x", ">", "{0}/a.tmp" })]
	[InlineData("echo ~", new[] { "echo", "{0}/*.tmp" })]
	[InlineData(@"echo ""{0}/q\r""*", new[] { "echo", @"{0}/q\r.log" })]
	public void Split_WithWildcards_ExpandsMatchingFiles(string line, string[] expected)
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		foreach (var name in new[] { "a.tmp", "b.tmp", "c.txt", @"q\r.log" })
		{
			File.WriteAllText(Path.Combine(folder, name), string.Empty);
		}

		try
		{
			// Act
			var words = CommandLine.Split(line.Replace("{0}", folder, StringComparison.Ordinal), new Dictionary<string, string> { ["P"] = $"{folder}/*.tmp", ["HOME"] = $"{folder}/*.tmp" });

			// Assert
			_ = words.Select(word => word.Text).Should().Equal(expected.Select(word => word.Replace("{0}", folder, StringComparison.Ordinal)));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that a wildcard in a <c>NAME=value</c> word stays as typed while the command has no name yet, here at the
	/// start and after a <c>|</c>, and expands once the word is an argument.
	/// </summary>
	/// <param name="line">The command line, run in a folder holding a file named <c>A=b</c>.</param>
	/// <param name="expected">The words it should give.</param>
	[Theory]
	[InlineData("A=* B=* sampletool A=*", new[] { "A=*", "B=*", "sampletool", "A=b" })]
	[InlineData("sampletool B=1 A=*", new[] { "sampletool", "B=1", "A=b" })]
	[InlineData("true B=1 | C=2 A=* sampletool", new[] { "true", "B=1", "|", "C=2", "A=*", "sampletool" })]
	public void Split_WithWildcardInAssignment_ExpandsOnlyArguments(string line, string[] expected)
	{
		// Arrange
		var original = Directory.GetCurrentDirectory();
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		File.WriteAllText(Path.Combine(folder, "A=b"), string.Empty);
		Directory.SetCurrentDirectory(folder);

		try
		{
			// Act
			var words = CommandLine.Split(line, new Dictionary<string, string>());

			// Assert
			_ = words.Select(word => word.Text).Should().Equal(expected);
		}
		finally
		{
			Directory.SetCurrentDirectory(original);
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that every file name a pattern gives keeps the pattern's position, and that without variables the
	/// pattern isn't expanded at all.
	/// </summary>
	[Fact]
	public void Split_WithWildcard_KeepsPatternPositionAndNeedsVariables()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;
		var line = $"rm {folder}/*.tmp";

		File.WriteAllText(Path.Combine(folder, "a.tmp"), string.Empty);
		File.WriteAllText(Path.Combine(folder, "b.tmp"), string.Empty);

		try
		{
			// Act
			var expanded = CommandLine.Split(line, new Dictionary<string, string>());
			var plain = CommandLine.Split(line);

			// Assert
			_ = expanded[1..].Should().Equal(
				new Word($"{folder}/a.tmp", IsOperator: false, 3, line.Length),
				new Word($"{folder}/b.tmp", IsOperator: false, 3, line.Length));
			_ = plain.Select(word => word.Text).Should().Equal("rm", $"{folder}/*.tmp");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.SubstitutionEnd" /> finds the <c>)</c> or backquote that closes a
	/// substitution past nesting, quotes and escapes, and gives -1 for one that never closes or for <c>$((</c>.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="index">Where the substitution starts.</param>
	/// <param name="expected">Where it closes, or -1.</param>
	[Theory]
	[InlineData("$(a)", 0, 3)]
	[InlineData("x $(a (b) c) d", 2, 11)]
	[InlineData("$(echo ')')", 0, 10)]
	[InlineData("$(echo \")\")", 0, 10)]
	[InlineData("$(echo \\))", 0, 9)]
	[InlineData("$(echo \"$(echo ')')\")", 0, 20)]
	[InlineData("$(echo `echo )`)", 0, 15)]
	[InlineData("$(echo $((1 + 2)))", 0, 17)]
	[InlineData("`a \\` b`", 0, 7)]
	[InlineData("a `b` `c`", 2, 4)]
	[InlineData("$(echo", 0, -1)]
	[InlineData("$(echo `x)", 0, -1)]
	[InlineData("`echo", 0, -1)]
	[InlineData("`a \\`", 0, -1)]
	[InlineData("$((1+2))", 0, 7)]
	[InlineData("$((echo hi) )", 0, 12)]
	[InlineData("$((1", 0, -1)]
	[InlineData("$x", 0, -1)]
	[InlineData("$(echo a # )\n)", 0, 13)]
	[InlineData("$(echo a # )", 0, -1)]
	[InlineData("$(#c\necho)", 0, 9)]
	[InlineData("$(#)\necho)", 0, 9)]
	[InlineData("$(echo a#b)", 0, 10)]
	[InlineData("$(echo a\\ #b)", 0, 12)]
	[InlineData("$(echo '#' \\#)", 0, 13)]
	[InlineData("$(echo \"$(a\")", 0, -1)]
	[InlineData("$(echo \"`a\")", 0, -1)]
	[InlineData("$(echo \"$((1+2))\")", 0, 17)]
	public void SubstitutionEnd_WithLine_FindsClose(string line, int index, int expected)
	{
		// Act
		var close = CommandLine.SubstitutionEnd(line, index);

		// Assert
		_ = close.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.ArithmeticEnd" /> finds the closing <c>))</c>, skipping parentheses, quotes
	/// and nested expansions, and gives -1 when the first parenthesis closes alone or nothing closes.
	/// </summary>
	/// <param name="line">The text.</param>
	/// <param name="index">Where the <c>$((</c> or <c>((</c> starts.</param>
	/// <param name="expected">The position of the last <c>)</c>, or -1.</param>
	[Theory]
	[InlineData("$((1+2))", 0, 7)]
	[InlineData("((1+2))", 0, 6)]
	[InlineData("x $(( (1+2) * (3) )) y", 2, 19)]
	[InlineData("$(( $(echo ')') + 1 ))", 0, 21)]
	[InlineData("$(( `echo 1` ))", 0, 14)]
	[InlineData("$(( ${X:-)} ))", 0, 13)]
	[InlineData("$(( \\) \")\" ))", 0, 12)]
	[InlineData("$(( ')' ))", 0, 9)]
	[InlineData("$(( \"${X:-'}\" ))", 0, 15)]
	[InlineData("$(( $((1)) ))", 0, 12)]
	[InlineData("$((echo hi) )", 0, -1)]
	[InlineData("((a); (b))", 0, -1)]
	[InlineData("$((1", 0, -1)]
	[InlineData("$((1)", 0, -1)]
	[InlineData("$(( $(echo ) )", 0, -1)]
	[InlineData("$(( ${X ))", 0, -1)]
	[InlineData("$(1)", 0, -1)]
	[InlineData("(1)", 0, -1)]
	public void ArithmeticEnd_WithLine_FindsClose(string line, int index, int expected)
	{
		// Act
		var close = CommandLine.ArithmeticEnd(line, index);

		// Assert
		_ = close.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <c>$((...))</c> becomes the number its expression gives: its text expanded as if in double quotes,
	/// assignments kept, the number split as a substitution's output is, a here-document body expanded too, and the
	/// text kept as typed where nothing expands.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The words.</param>
	[Theory]
	[InlineData("echo $((1 + 2)) \"$((2*3))\" x$((N))y", new[] { "echo", "3", "6", "x10y" })]
	[InlineData("echo $(( \"1\" + $N )) $(( $(n) * 2 )) $(( ${N} / 3 ))", new[] { "echo", "11", "14", "3" })]
	[InlineData("echo $((M = N + 1)) $M $((M++)) $M", new[] { "echo", "11", "11", "11", "12" })]
	[InlineData("echo $((E)) $((-1)) $((2#101)) $(( $U + 1 ))", new[] { "echo", "0", "-1", "5", "1" })]
	[InlineData("echo $((1 < 2)) $((1<<2)) $((2>1))", new[] { "echo", "1", "4", "1" })]
	[InlineData("echo $((101)) x$((1))y $((1))#c", new[] { "echo", "", "0", "x", "y", "", "#c" })]
	[InlineData("echo \"$((101))\"", new[] { "echo", "101" })]
	[InlineData("echo '$((1))' $((echo a) )", new[] { "echo", "$((1))", "sub" })]
	public void Split_WithArithmetic_GivesTheNumber(string line, string[] expected)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["N"] = "10", ["E"] = string.Empty, ["IFS"] = line.Contains("$((101))", StringComparison.Ordinal) ? "1" : " " };

		// Act
		var words = CommandLine.Split(line, variables, substitute: list => list == "n" ? "7" : "sub");

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <c>$((...))</c> stays as typed with no variables, and in a here-document's delimiter.
	/// </summary>
	[Fact]
	public void Split_WithArithmeticAndNothingToExpand_KeepsItAsTyped()
	{
		// Act
		var plain = CommandLine.Split("echo $((1 + 2))");
		var delimiter = CommandLine.Split("cat <<$((1))\n3\n$((1))", new Dictionary<string, string>());

		// Assert
		_ = plain.Select(word => word.Text).Should().Equal("echo", "$((1 + 2))");
		_ = delimiter[^1].Text.Should().Be("3\n");
	}

	/// <summary>
	/// Checks that an unquoted here-document body works out <c>$((...))</c>.
	/// </summary>
	[Fact]
	public void ExpandBody_WithArithmetic_GivesTheNumber()
	{
		// Act
		var body = CommandLine.ExpandBody("sum $((1 + N)) \"$((2))\"\n", new Dictionary<string, string> { ["N"] = "2" });

		// Assert
		_ = body.Should().Be("sum 3 \"2\"\n");
	}

	/// <summary>
	/// Checks that an expression that can't be worked out throws an error that isn't fatal, pointing into the
	/// expression, and that an unset variable inside is reported where it was typed.
	/// </summary>
	[Fact]
	public void Split_WithBadArithmetic_ThrowsAndReportsUnset()
	{
		// Arrange
		var unset = new List<Word>();

		// Act
		var act = () => CommandLine.Split("echo $(( $U + 1/0 ))", new Dictionary<string, string>(), unset.Add);
		var offset = () => CommandLine.Split("echo ${S:1/0}", new Dictionary<string, string> { ["S"] = "abc" });

		// Assert
		var thrown = act.Should().Throw<CommandLine.ExpansionError>().Which;

		_ = (thrown.Fatal, thrown.Error.Code, thrown.Error.Source, thrown.Error.Start, thrown.Error.End).Should().Be((false, "sharpie::arithmetic::division_by_zero", "  + 1/0 ", 6, 7));
		_ = unset.Select(word => (word.Text, word.Start, word.End)).Should().Equal(("U", 9, 11));
		_ = offset.Should().Throw<CommandLine.ExpansionError>().Which.Should().Match<CommandLine.ExpansionError>(error => !error.Fatal && error.Error.Code == "sharpie::arithmetic::division_by_zero");
	}

	/// <summary>
	/// Checks that under <c>set -u</c> an unset name read inside <c>$((...))</c> or a <c>${X:offset}</c> throws a
	/// fatal error, as bash's "unbound variable" is.
	/// </summary>
	/// <param name="line">The command as typed.</param>
	[Theory]
	[InlineData("echo $((nope + 1))")]
	[InlineData("echo ${S:nope}")]
	public void Split_WithNoUnsetArithmetic_ThrowsFatal(string line)
	{
		// Act
		var act = () => CommandLine.Split(line, new Dictionary<string, string> { ["S"] = "abc" }, _ => { });

		// Assert
		_ = act.Should().Throw<CommandLine.ExpansionError>().Which.Should().Match<CommandLine.ExpansionError>(error => error.Fatal && error.Error.Code == "sharpie::shell::unset_variable");
	}

	/// <summary>
	/// Checks that unclosed substitutions nested in double quotes are found unclosed without scanning the line again
	/// at every level.
	/// </summary>
	[Fact]
	public void SubstitutionEnd_WithDeeplyNestedUnclosed_IsQuick()
	{
		// Arrange
		var line = string.Concat(Enumerable.Repeat("\"$(", 40));
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();

		// Act
		var close = CommandLine.SubstitutionEnd(line, 1);

		// Assert
		_ = close.Should().Be(-1);
		_ = stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
	}

	/// <summary>
	/// Checks that the command list inside <c>$(...)</c> or backquotes reaches the substitute, with the backslashes
	/// that only escape inside backquotes removed.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="expected">The list the substitute should get.</param>
	[Theory]
	[InlineData("echo $(list one)", "list one")]
	[InlineData("echo `list one`", "list one")]
	[InlineData("echo \"$(a \"b c\")\"", "a \"b c\"")]
	[InlineData("echo $(echo $(inner))", "echo $(inner)")]
	[InlineData("echo `a \\` \\\\ \\$ \\\" \\x`", "a ` \\ $ \\\" \\x")]
	[InlineData("echo \"`a \\\" \\x`\"", "a \" \\x")]
	public void Split_WithSubstitution_RunsInnerList(string line, string expected)
	{
		// Arrange
		var lists = new List<string>();

		// Act
		_ = CommandLine.Split(line, new Dictionary<string, string>(), substitute: list =>
		{
			lists.Add(list);

			return string.Empty;
		});

		// Assert
		_ = lists.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a substitution's output is split on spaces, tabs and newlines outside quotes, kept whole inside
	/// double quotes and in a <c>NAME=value</c> word, and loses its trailing newlines and NUL characters.
	/// </summary>
	/// <param name="line">The line, whose substitutions all give <paramref name="output" />.</param>
	/// <param name="output">What the substitution prints.</param>
	/// <param name="expected">The words.</param>
	[Theory]
	[InlineData("echo $(x)", "a b", new[] { "echo", "a", "b" })]
	[InlineData("echo $(x)", "a\tb\nc\n", new[] { "echo", "a", "b", "c" })]
	[InlineData("echo `x`", "a b", new[] { "echo", "a", "b" })]
	[InlineData("echo $(x)", "", new[] { "echo" })]
	[InlineData("echo $(x) y", " \n", new[] { "echo", "y" })]
	[InlineData("echo \"$(x)\"", "", new[] { "echo", "" })]
	[InlineData("echo \"$(x)\"", "a  b\n\n", new[] { "echo", "a  b" })]
	[InlineData("echo \"<$(x)>\"", "a\nb\n", new[] { "echo", "<a\nb>" })]
	[InlineData("echo p$(x)q", "a b", new[] { "echo", "pa", "bq" })]
	[InlineData("echo p$(x)q", " a ", new[] { "echo", "p", "a", "q" })]
	[InlineData("echo $(x)", "a\0b", new[] { "echo", "ab" })]
	[InlineData("A=$(x)", "a  b", new[] { "A=a  b" })]
	[InlineData("export A=$(x) B", "a b", new[] { "export", "A=a b", "B" })]
	[InlineData("declare A=$(x)", "a b", new[] { "declare", "A=a b" })]
	[InlineData("local A=$(x)", "a b", new[] { "local", "A=a b" })]
	[InlineData("A=$(x) B=$(x) cmd", "a b", new[] { "A=a b", "B=a b", "cmd" })]
	[InlineData("printf A=$(x)", "a b", new[] { "printf", "A=a", "b" })]
	[InlineData("export A | B=$(x)", "a b", new[] { "export", "A", "|", "B=a b" })]
	[InlineData("echo $(x)#y $(x) #z", "", new[] { "echo", "#y" })]
	[InlineData("echo $UNSET_SAMPLE#y", "", new[] { "echo", "#y" })]
	[InlineData("echo '$(x)' \"\\$(x)\"", "no", new[] { "echo", "$(x)", "$(x)" })]
	public void Split_WithSubstitution_SplitsOutputOutsideQuotes(string line, string output, string[] expected)
	{
		// Act
		var words = CommandLine.Split(line, new Dictionary<string, string>(), substitute: _ => output);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that each field of a split output points at the whole substitution.
	/// </summary>
	[Fact]
	public void Split_WithSplitSubstitution_PointsEachFieldAtIt()
	{
		// Act
		var words = CommandLine.Split("echo $(x)", new Dictionary<string, string>(), substitute: _ => "a b");

		// Assert
		_ = words[1..].Should().Equal(new Word("a", IsOperator: false, 5, 9), new Word("b", IsOperator: false, 5, 9));
	}

	/// <summary>
	/// Checks that a wildcard in an unquoted substitution's output matches file names, and one in a quoted
	/// substitution's output only matches itself.
	/// </summary>
	[Fact]
	public void Split_WithWildcardInSubstitution_MatchesUnquotedOnly()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		File.WriteAllText(Path.Combine(folder, "a.tmp"), string.Empty);
		File.WriteAllText(Path.Combine(folder, "b.tmp"), string.Empty);

		try
		{
			// Act
			var words = CommandLine.Split("ls $(x) \"$(x)\"", new Dictionary<string, string>(), substitute: _ => $"{folder}/*.tmp");

			// Assert
			_ = words.Select(word => word.Text).Should().Equal("ls", $"{folder}/a.tmp", $"{folder}/b.tmp", $"{folder}/*.tmp");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that with no substitute a wildcard typed inside a substitution only matches itself.
	/// </summary>
	[Fact]
	public void Split_WithWildcardInUnrunSubstitution_MatchesNoFile()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		File.WriteAllText(Path.Combine(folder, "$(yes)"), string.Empty);

		try
		{
			// Act
			var words = CommandLine.Split($"ls {folder}/$(y*)", new Dictionary<string, string>());

			// Assert
			_ = words.Select(word => word.Text).Should().Equal("ls", $"{folder}/$(y*)");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Checks that with no substitute a substitution stays in its word as typed, wildcards included, and nothing runs.
	/// </summary>
	[Fact]
	public void Split_WithSubstitutionAndNoSubstitute_KeepsItAsTyped()
	{
		// Act
		var words = CommandLine.Split("echo a$(b *; c)d `e f`", new Dictionary<string, string>());

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("echo", "a$(b *; c)d", "`e f`");
	}

	/// <summary>
	/// Checks that the words from an unquoted first <c>[[</c> to its <c>]]</c> read as a condition: the brackets and
	/// <c>&lt;</c> are operators, wildcards stay as typed with a pattern that escapes the quoted ones, a variable or
	/// substitution isn't split, an empty one is still a word, and the word after <c>=~</c> keeps <c>(</c>, <c>|</c>
	/// and the blanks in its parentheses, with its quoted text escaped as a regular expression. Each word is shown as
	/// <c>op:TEXT</c> for an operator or <c>TEXT|PATTERN</c> for a word.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The words.</param>
	[Theory]
	[InlineData("[[ * == a* ]]", new[] { "op:[[", "*|*", "==|==", "a*|a*", "op:]]" })]
	[InlineData("[[ $x == \"a*\"b ]]", new[] { "op:[[", "a b|a b", "==|==", @"a*b|a\*b", "op:]]" })]
	[InlineData("[[ $star ]]", new[] { "op:[[", "a*|a*", "op:]]" })]
	[InlineData("[[ a < b ]]", new[] { "op:[[", "a|a", "op:<", "b|b", "op:]]" })]
	[InlineData("[[ 2 >1 ]]", new[] { "op:[[", "2|2", "op:>", "1|1", "op:]]" })]
	[InlineData("[[ -n $empty ]]", new[] { "op:[[", "-n|-n", "|", "op:]]" })]
	[InlineData("[[ a && $(out) ]]", new[] { "op:[[", "a|a", "op:&&", "x  y|x  y", "op:]]" })]
	[InlineData("[[ a =~ ^(a| b)$ ]]", new[] { "op:[[", "a|a", "=~|=~", "^(a| b)$|^(a| b)$", "op:]]" })]
	[InlineData("[[ a =~ \"a.b\"x|y ]]", new[] { "op:[[", "a|a", "=~|=~", @"a.bx|y|a\.bx|y", "op:]]" })]
	[InlineData("[[ a =~ b ]] && (c)", new[] { "op:[[", "a|a", "=~|=~", "b|b", "op:]]", "op:&&", "op:(", "c|", "op:)" })]
	[InlineData("'[[' a ]]", new[] { "[[|", "a|", "]]|" })]
	[InlineData("echo [[ a ]]", new[] { "echo|", "[[|", "a|", "]]|" })]
	public void Split_WithCondition_ReadsWordsAsBashDoes(string line, string[] expected)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["x"] = "a b", ["star"] = "a*", ["empty"] = string.Empty };

		// Act
		var words = CommandLine.Split(line, variables, substitute: _ => "x  y\n");

		// Assert
		_ = words.Select(word => word.IsOperator ? $"op:{word.Text}" : $"{word.Text}|{word.Pattern}").Should().Equal(expected);
	}

	/// <summary>
	/// Checks the <c>${...}</c> forms against what bash gives for the same line: defaults, lengths, removing and
	/// replacing a pattern, and substrings.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="expected">The words the line should produce.</param>
	[Theory]
	[InlineData("echo ${S:-d} ${E:-d} ${N:-d}", new[] { "echo", "aXbXc", "d", "d" })]
	[InlineData("echo ${S-d} \"${E-d}\" ${N-d}", new[] { "echo", "aXbXc", "", "d" })]
	[InlineData("echo ${S:+alt} \"${E:+alt}\" ${N:+alt} ${S+alt} ${E+alt} ${N+alt}", new[] { "echo", "alt", "", "alt", "alt" })]
	[InlineData("echo ${N:=v} $N ${E=w} ${E:=w}", new[] { "echo", "v", "v", "w" })]
	[InlineData("echo ${#W} ${#S} ${#E} ${#} ${#@} ${##}", new[] { "echo", "2", "5", "0", "3", "3", "1" })]
	[InlineData("echo ${S#*X} ${S##*X} ${S%X*} ${S%%X*} ${S#Z} ${S#}", new[] { "echo", "bXc", "c", "aXb", "a", "aXbXc", "aXbXc" })]
	[InlineData("echo ${P#\"*\"} \"${P#*}\" ${S#$P} ${S#\"$P\"} \"${S#*X}\"", new[] { "echo", "X", "*X", "bXc", "aXbXc", "bXc" })]
	[InlineData("echo ${S/X/-} ${S//X/-} ${S//X} ${S//} ${S/[ab]*/-} ${S/\"*\"/-} ${S/X*/-} ${S/X*b/-}", new[] { "echo", "a-bXc", "a-b-c", "abc", "aXbXc", "-", "aXbXc", "a-", "a-Xc" })]
	[InlineData("echo \"${G/ /_}\" ${G// } ${E/*/-}", new[] { "echo", "a_b c", "abc", "-" })]
	[InlineData("echo ${S:1} ${S:1:2} ${S: -2} ${S:1:-1} ${S:$I} ${S:I} ${S: 1 : 2 } ${S:9} ${S:1:-4} ${W:1}", new[] { "echo", "XbXc", "Xb", "Xc", "XbX", "XbXc", "XbXc", "Xb", "😀" })]
	[InlineData("echo ${S:1+1:2} ${S:I*2} ${S:(I+1)%5:I} ${S:E}", new[] { "echo", "bX", "bXc", "b", "aXbXc" })]
	[InlineData("echo ${N:-${S}} ${N:-\"}\"} \"${N:-a  b}\" ${N:-a  b} ${N:-${M:-z}}", new[] { "echo", "aXbXc", "}", "a  b", "a", "b", "z" })]
	[InlineData("echo ${N:-~/x} \"${N:-~}\" ${N:-a|b;c#d} ${N:-'q  r'}", new[] { "echo", "/h/x", "~", "a|b;c#d", "q  r" })]
	[InlineData("echo ${*:-none} ${10:-ten} \"${N:-$S}\" ${N:-}", new[] { "echo", "1", "2", "3", "ten", "aXbXc" })]
	[InlineData("echo ${S}end ${S:-x}y ${A", new[] { "echo", "aXbXcend", "aXbXcy", "${A" })]
	[InlineData("echo ${N:-a\\}b} ${N:-'a}'} \"${N:-'}'}\" ${N:-'a|b'x;'c;d'}", new[] { "echo", "a}b", "a}", "''}", "a|bx;c;d" })]
	[InlineData("echo \"${@:-x}\" \"${*:-x}\" ${N:=~/x}", new[] { "echo", "1", "2", "3", "1 2 3", "/h/x" })]
	[InlineData("echo ${D/'/'/-} ${D///b} \"${E///x}\" \"${E/a/-}\"", new[] { "echo", "a-b", "a/b", "", "" })]
	[InlineData("echo ${S::2} ${S:0:1} ${S:3:9} \"${S:1:0}\" ${S:$N:2} \"${S#a*c}\"", new[] { "echo", "aX", "a", "Xc", "", "aX", "" })]
	[InlineData("echo ${@#1} ${@/2/X} \"${*%3}\" \"${@%3}\"", new[] { "echo", "2", "3", "1", "X", "3", "1 2 ", "1", "2", "" })]
	[InlineData("echo ${N:-\"*\"} ${N:-''} ${N:-\\*} ${N:+x} ${S:+\"*\"}", new[] { "echo", "*", "", "*", "*" })]
	public void Split_WithBracedForms_ExpandsAsBash(string line, string[] expected)
	{
		// Arrange
		var variables = new Dictionary<string, string>
		{
			["S"] = "aXbXc",
			["E"] = string.Empty,
			["P"] = "*X",
			["I"] = "1",
			["W"] = "é😀",
			["G"] = "a b c",
			["D"] = "a/b",
			["HOME"] = "/h",
			["#"] = "3",
			["1"] = "1",
			["2"] = "2",
			["3"] = "3",
		};

		// Act
		var words = CommandLine.Split(line, variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <c>${X:=value}</c> stores the value in the shell's variables whole, while the unquoted expansion
	/// splits, as in bash.
	/// </summary>
	[Fact]
	public void Split_WithAssigningDefault_StoresVariable()
	{
		// Arrange
		var declare = new DeclareCommand(new ShellSettings());

		// Act
		var words = CommandLine.Split("echo ${SHARPIE_SAMPLE_UNSET:=v w}", declare.Variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("echo", "v", "w");
		_ = declare.Variables["SHARPIE_SAMPLE_UNSET"].Should().Be("v w");
	}

	/// <summary>
	/// Checks that a <c>${...}</c> that can't expand throws an error that points at it, with bash's message, and that
	/// only <c>?</c> is fatal, ending a script.
	/// </summary>
	/// <param name="line">The command line to split.</param>
	/// <param name="code">The error's code.</param>
	/// <param name="summary">The error's message.</param>
	/// <param name="label">The note under the <c>${...}</c>.</param>
	/// <param name="fatal">Whether the error ends a shell that isn't interactive.</param>
	[Theory]
	[InlineData("echo ${N:?gone away}", "sharpie::shell::parameter_unset", "N: gone away", "empty or not set", true)]
	[InlineData("echo ${E:?}", "sharpie::shell::parameter_unset", "E: parameter null or not set", "empty or not set", true)]
	[InlineData("echo ${N?}", "sharpie::shell::parameter_unset", "N: parameter not set", "not set", true)]
	[InlineData("echo ${N:-${M:?inner}}", "sharpie::shell::parameter_unset", "M: inner", "empty or not set", true)]
	[InlineData("echo ${1:=a}", "sharpie::shell::bad_substitution", "$1: cannot assign in this way", "not a variable name", false)]
	[InlineData("echo ${S:1:-5}", "sharpie::shell::bad_substitution", "-5: substring expression < 0", "ends before its offset", false)]
	[InlineData("echo ${S:5:-1}", "sharpie::shell::bad_substitution", "-1: substring expression < 0", "ends before its offset", false)]
	[InlineData("echo ${S^^}", "sharpie::shell::bad_substitution", "Sharpie can't expand `${S^^}`", "not a form Sharpie reads", false)]
	[InlineData("echo ${!S}", "sharpie::shell::bad_substitution", "Sharpie can't expand `${!S}`", "not a form Sharpie reads", false)]
	[InlineData("echo ${%S}", "sharpie::shell::bad_substitution", "Sharpie can't expand `${%S}`", "not a form Sharpie reads", false)]
	[InlineData("echo ${S/#a/b}", "sharpie::shell::bad_substitution", "Sharpie can't expand `${S/#a/b}`", "not a form Sharpie reads", false)]
	[InlineData("echo ${@:2}", "sharpie::shell::bad_substitution", "Sharpie can't expand `${@:2}`", "not a form Sharpie reads", false)]
	[InlineData("echo ${*:2}", "sharpie::shell::bad_substitution", "Sharpie can't expand `${*:2}`", "not a form Sharpie reads", false)]
	[InlineData("echo ${}", "sharpie::shell::bad_substitution", "Sharpie can't expand `${}`", "not a form Sharpie reads", false)]
	public void Split_WithBadBracedForm_Throws(string line, string code, string summary, string label, bool fatal)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["S"] = "aXbXc", ["E"] = string.Empty };

		// Act
		var act = () => CommandLine.Split(line, variables);

		// Assert
		var thrown = act.Should().Throw<CommandLine.ExpansionError>().Which;

		_ = (thrown.Error.Code, thrown.Error.Summary, thrown.Error.Label, thrown.Fatal).Should().Be((code, summary, label, fatal));
		_ = (thrown.Error.Source, thrown.Error.Start, thrown.Error.End).Should().Be((line, 5, line.Length));
	}

	/// <summary>
	/// Checks that <c>set -u</c> reports an unset name in <c>${#X}</c>, a pattern, a substring or a default's word,
	/// pointing at the whole <c>${...}</c>, but not the name of a default form, which decides what an unset name gives.
	/// </summary>
	[Fact]
	public void Split_WithUnsetCallbackAndBracedForms_ReportsOnlyNonDefaultForms()
	{
		// Arrange
		var unset = new List<Word>();

		// Act
		var words = CommandLine.Split("echo ${N:-d} ${N-d} ${N+x} ${N:+x} ${#@} ${#M} ${O#a} ${Q:1} ${*:-none} ${A:-$B} ${@-none}", new Dictionary<string, string>(), unset.Add);

		// Assert
		_ = unset.Select(word => (word.Text, word.Start, word.End)).Should().Equal(("M", 41, 46), ("O", 47, 53), ("Q", 54, 60), ("B", 72, 80));
		_ = words.Select(word => word.Text).Should().Equal("echo", "d", "d", "0", "0", "none", "none");
	}
}
