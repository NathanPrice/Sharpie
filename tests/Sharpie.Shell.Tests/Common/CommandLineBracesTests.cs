using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for brace expansion in <see cref="CommandLine" />, each expected value taken from bash 5.3.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class CommandLineBracesTests
{
	/// <summary>
	/// Variables for <see cref="CommandLine.Split" />, which expands braces only when it's given some.
	/// </summary>
	private static readonly Dictionary<string, string> Variables = new() { ["HOME"] = "/home/u", ["X"] = "q", ["S"] = "1 2" };

	/// <summary>
	/// Checks that <see cref="CommandLine.ExpandBraces" /> expands lists, nested lists and sequences in order, keeps
	/// the quotes in each word, and gives <see langword="null" /> for a word with no brace expression.
	/// </summary>
	/// <param name="word">The word as typed.</param>
	/// <param name="expected">The words bash gives, or <see langword="null" />.</param>
	[Theory]
	[InlineData("{a,b}{1,2}", new[] { "a1", "a2", "b1", "b2" })]
	[InlineData("a{b,c{d,e}}f", new[] { "abf", "acdf", "acef" })]
	[InlineData("x{,.bak}", new[] { "x", "x.bak" })]
	[InlineData("{,}", new[] { "", "" })]
	[InlineData("{a,b,}", new[] { "a", "b", "" })]
	[InlineData("{,,}x", new[] { "x", "x", "x" })]
	[InlineData("{,a}{,b}", new[] { "", "b", "a", "ab" })]
	[InlineData("{a,b}}", new[] { "a}", "b}" })]
	[InlineData("{{a,b}", new[] { "{a", "{b" })]
	[InlineData("{a,{b}}", new[] { "a", "{b}" })]
	[InlineData("{a{b,c}}", new[] { "{ab}", "{ac}" })]
	[InlineData("{a}b,c}", new[] { "a}b", "c" })]
	[InlineData("{a,b}{", new[] { "a{", "b{" })]
	[InlineData("x{},a}", new[] { "x}", "xa" })]
	[InlineData("{}{a,b}", new[] { "{}a", "{}b" })]
	[InlineData(@"x\ {},a}", null)]
	[InlineData("{\"a b\",c}", new[] { "\"a b\"", "c" })]
	[InlineData("{'a,b',c}", new[] { "'a,b'", "c" })]
	[InlineData(@"{a\,b,c}", new[] { @"a\,b", "c" })]
	[InlineData("{a\"}\"b,c}", new[] { "a\"}\"b", "c" })]
	[InlineData("${X}{a,b}", new[] { "${X}a", "${X}b" })]
	[InlineData("{a,$(echo x,y)}", new[] { "a", "$(echo x,y)" })]
	[InlineData("{a,b}$", new[] { "a$", "b$" })]
	[InlineData("{1..3}", new[] { "1", "2", "3" })]
	[InlineData("{3..1}", new[] { "3", "2", "1" })]
	[InlineData("{1..1}", new[] { "1" })]
	[InlineData("{1..10..3}", new[] { "1", "4", "7", "10" })]
	[InlineData("{10..1..3}", new[] { "10", "7", "4", "1" })]
	[InlineData("{1..10..-3}", new[] { "1", "4", "7", "10" })]
	[InlineData("{1..3..0}", new[] { "1", "2", "3" })]
	[InlineData("{0..3..2}", new[] { "0", "2" })]
	[InlineData("{1..+3}", new[] { "1", "2", "3" })]
	[InlineData("{+1..3}", new[] { "1", "2", "3" })]
	[InlineData("{-1..-3}", new[] { "-1", "-2", "-3" })]
	[InlineData("{1..-2..1}", new[] { "1", "0", "-1", "-2" })]
	[InlineData("{01..03}", new[] { "01", "02", "03" })]
	[InlineData("{8..010}", new[] { "008", "009", "010" })]
	[InlineData("{00..3}", new[] { "00", "01", "02", "03" })]
	[InlineData("{05..1..2}", new[] { "05", "03", "01" })]
	[InlineData("{-05..1}", new[] { "-05", "-04", "-03", "-02", "-01", "000", "001" })]
	[InlineData("{-3..03}", new[] { "-3", "-2", "-1", "00", "01", "02", "03" })]
	[InlineData("{-02..-1}", new[] { "-02", "-01" })]
	[InlineData("{-0..2}", new[] { "0", "1", "2" })]
	[InlineData("{a..e}", new[] { "a", "b", "c", "d", "e" })]
	[InlineData("{e..a..2}", new[] { "e", "c", "a" })]
	[InlineData("{a..e..-2}", new[] { "a", "c", "e" })]
	[InlineData("{a..c..0}", new[] { "a", "b", "c" })]
	[InlineData("{Z..a}", new[] { "Z", "[", "\\", "]", "^", "_", "`", "a" })]
	[InlineData("{a..c}{1..2}", new[] { "a1", "a2", "b1", "b2", "c1", "c2" })]
	[InlineData("{a..c}..{d,e}", new[] { "a..d", "a..e", "b..d", "b..e", "c..d", "c..e" })]
	[InlineData("{a..b}..c}", new[] { "a..c}", "b..c}" })]
	[InlineData("{a..}x,y}", new[] { "a..}x", "y" })]
	[InlineData("{a..b,c}", new[] { "a..b", "c" })]
	[InlineData("{1..3,x}", new[] { "1..3", "x" })]
	[InlineData("{a.,b}", new[] { "a.", "b" })]
	[InlineData("{1..{a,b}}", new[] { "{1..a}", "{1..b}" })]
	[InlineData("{1..a{b,c}}", new[] { "{1..ab}", "{1..ac}" })]
	[InlineData("{x..{a,b}y}", new[] { "{x..ay}", "{x..by}" })]
	[InlineData("{{a,b}..1}", new[] { "{a..1}", "{b..1}" })]
	[InlineData("{1..{2..3}}", new[] { "{1..2}", "{1..3}" })]
	[InlineData("{a.{.b,c}}", new[] { "{a..b}", "{a.c}" })]
	[InlineData("{1.{2..3}x}", new[] { "{1.2x}", "{1.3x}" })]
	[InlineData("{aa..c{d,e}}", new[] { "{aa..cd}", "{aa..ce}" })]
	[InlineData("{1..b.{c,d}}", new[] { "{1..b.c}", "{1..b.d}" })]
	[InlineData("{0..10..5}", new[] { "0", "5", "10" })]
	[InlineData("{-9223372036854775807..0..9223372036854775807}", new[] { "-9223372036854775807", "0" })]
	[InlineData("{1..2}{3..1..x}{e,f}", new[] { "1{3..1..x}e", "1{3..1..x}f", "2{3..1..x}e", "2{3..1..x}f" })]
	[InlineData("{a,b}{1..3..0}{c,d}", new[] { "a1c", "a1d", "a2c", "a2d", "a3c", "a3d", "b1c", "b1d", "b2c", "b2d", "b3c", "b3d" })]
	[InlineData("{9223372036854775806..9223372036854775807}", new[] { "9223372036854775806", "9223372036854775807" })]
	[InlineData("{-9223372036854775808..-9223372036854775807}", new[] { "-9223372036854775808", "-9223372036854775807" })]
	[InlineData("plain", null)]
	[InlineData("", null)]
	[InlineData("{a}", null)]
	[InlineData("{}", null)]
	[InlineData("{},a}", null)]
	[InlineData("{a,b", null)]
	[InlineData(@"\{a,b}", null)]
	[InlineData(@"{a,b\}", null)]
	[InlineData("\"{a,b}\"", null)]
	[InlineData("'{a,b}'", null)]
	[InlineData("{'a,b'}", null)]
	[InlineData("{a,\"b}\"", null)]
	[InlineData("${a,b}", null)]
	[InlineData("${X:-{a,b}}", null)]
	[InlineData("$(echo {a,b})", null)]
	[InlineData("`echo {a,b}`", null)]
	[InlineData("{a..5}", null)]
	[InlineData("{1..a}", null)]
	[InlineData("{1.5..3}", null)]
	[InlineData("{aa..c}", null)]
	[InlineData("{a..c..x}", null)]
	[InlineData("{1..2..}", null)]
	[InlineData("{1..3..2..4}", null)]
	[InlineData("{1..3.}", null)]
	[InlineData("{1..3.x5}", null)]
	[InlineData("{a.b..c}", null)]
	[InlineData("{1..}", null)]
	[InlineData("{..}", null)]
	[InlineData("{..1}", null)]
	[InlineData("{1...3}", null)]
	[InlineData("{[..]}", null)]
	[InlineData("{1..2{a,b}}", null)]
	[InlineData("{1..3..{a,b}}", null)]
	[InlineData("a{1..2{a,b}}", null)]
	[InlineData("{9223372036854775807..9223372036854775808}", null)]
	[InlineData("{-9223372036854775808..9223372036854775807}", null)]
	[InlineData("{1..3..9223372036854775808}", null)]
	[InlineData("{1..3..-9223372036854775808}", null)]
	[InlineData("{1..3000000000}", null)]
	public void ExpandBraces_WithWord_ReturnsBashWords(string word, string[]? expected)
	{
		// Act
		var words = CommandLine.ExpandBraces(word);

		// Assert
		_ = words.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a sequence that isn't valid stays as typed while the braces after it still expand.
	/// </summary>
	[Fact]
	public void ExpandBraces_WithBadSequenceBeforeList_ExpandsList()
	{
		// Act
		var words = CommandLine.ExpandBraces("x{a..c..x}{1,2}");

		// Assert
		_ = words.Should().Equal("x{a..c..x}1", "x{a..c..x}2");
	}

	/// <summary>
	/// Checks that <see cref="CommandLine.Split" /> expands braces in each typed word before anything else, so each
	/// word then gets its variables, splitting, quote removal and <c>~</c>, and that braces stay as typed where bash
	/// leaves them: in a <c>NAME=value</c> before the command, in a here-string, between <c>[[</c> and <c>]]</c>, and
	/// in a comment.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The words it should give.</param>
	[Theory]
	[InlineData("echo {a,b}", new[] { "echo", "a", "b" })]
	[InlineData("cp x{,.bak}", new[] { "cp", "x", "x.bak" })]
	[InlineData("mkdir -p src/{a,b,c}", new[] { "mkdir", "-p", "src/a", "src/b", "src/c" })]
	[InlineData("echo {1..3}x", new[] { "echo", "1x", "2x", "3x" })]
	[InlineData("echo {,}", new[] { "echo" })]
	[InlineData("echo ''{a,b}", new[] { "echo", "a", "b" })]
	[InlineData(@"echo \x{a,b}", new[] { "echo", "xa", "xb" })]
	[InlineData("echo ${X}{a,b}", new[] { "echo", "qa", "qb" })]
	[InlineData("echo a{$X,b}", new[] { "echo", "aq", "ab" })]
	[InlineData("echo {$S,b}", new[] { "echo", "1", "2", "b" })]
	[InlineData("echo {a,$nope}", new[] { "echo", "a" })]
	[InlineData("echo {\"a b\",c}", new[] { "echo", "a b", "c" })]
	[InlineData("echo \"{a,b}\"{c,d}", new[] { "echo", "{a,b}c", "{a,b}d" })]
	[InlineData("echo {a,b}\" \"c", new[] { "echo", "a c", "b c" })]
	[InlineData("echo ${N:-{a,b}}", new[] { "echo", "{a,b}" })]
	[InlineData("echo {#a,b}", new[] { "echo", "#a", "b" })]
	[InlineData("echo {a,b}#c", new[] { "echo", "a#c", "b#c" })]
	[InlineData("echo #{a,b}", new[] { "echo" })]
	[InlineData("echo {a,b} # {c,d}", new[] { "echo", "a", "b" })]
	[InlineData("echo ~{,}", new[] { "echo", "/home/u", "/home/u" })]
	[InlineData("echo {~,x}", new[] { "echo", "/home/u", "x" })]
	[InlineData("echo ~/{a,b}", new[] { "echo", "/home/u/a", "/home/u/b" })]
	[InlineData("echo a=~/{x,y}", new[] { "echo", "a=~/x", "a=~/y" })]
	[InlineData("A={a,b} env", new[] { "A={a,b}", "env" })]
	[InlineData("A=~/{a,b}", new[] { "A=/home/u/{a,b}" })]
	[InlineData("export A={a,b}", new[] { "export", "A=a", "A=b" })]
	[InlineData("local A={a,b}", new[] { "local", "A=a", "A=b" })]
	[InlineData("echo A={a,b}", new[] { "echo", "A=a", "A=b" })]
	[InlineData("{a,b}=x", new[] { "a=x", "b=x" })]
	[InlineData("cat <<< {a,b}", new[] { "cat", "<<<", "{a,b}" })]
	[InlineData("for i in {1..3}; do", new[] { "for", "i", "in", "1", "2", "3", ";", "do" })]
	[InlineData("{ echo {a,b}; }", new[] { "{", "echo", "a", "b", ";", "}" })]
	[InlineData("echo {a,b}>x", new[] { "echo", "a", "b", ">", "x" })]
	[InlineData("echo {a,b}|cat", new[] { "echo", "a", "b", "|", "cat" })]
	[InlineData("(echo {a,b})", new[] { "(", "echo", "a", "b", ")" })]
	[InlineData("echo {a,b};echo {c..d}", new[] { "echo", "a", "b", ";", "echo", "c", "d" })]
	[InlineData("echo  {a,b}  {c,d}", new[] { "echo", "a", "b", "c", "d" })]
	public void Split_WithBraces_ExpandsThemFirst(string line, string[] expected)
	{
		// Act
		var words = CommandLine.Split(line, Variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that braces between <c>[[</c> and <c>]]</c> stay as typed, as in bash.
	/// </summary>
	[Fact]
	public void Split_WithBracesInCondition_KeepsThem()
	{
		// Act
		var words = CommandLine.Split("[[ {a,b} == x ]]", Variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("[[", "{a,b}", "==", "x", "]]");
	}

	/// <summary>
	/// Checks that braces stay as typed where nothing expands: with no variables, as the highlighter splits, in a
	/// <c>case</c> word or pattern, and in a here-document's delimiter.
	/// </summary>
	[Fact]
	public void Split_WhereNothingExpands_KeepsBraces()
	{
		// Act
		var plain = CommandLine.Split("echo {a,b}");
		var text = CommandLine.ExpandText("{a,b}", Variables);
		var pattern = CommandLine.Pattern("{a,b}", Variables);
		var delimiter = CommandLine.Split("cat <<{a,b}\nhi\n{a,b}\n", Variables);

		// Assert
		_ = plain.Select(word => word.Text).Should().Equal("echo", "{a,b}");
		_ = (text, pattern).Should().Be(("{a,b}", "{a,b}"));
		_ = delimiter.Select(word => word.Text).Should().Equal("cat", "<<", "hi\n");
	}

	/// <summary>
	/// Checks that each word braces give spans the whole typed word, as an error about it should point there.
	/// </summary>
	[Fact]
	public void Split_WithBraces_GivesWordsTheTypedSpan()
	{
		// Act
		var words = CommandLine.Split("echo x{a,b} y", Variables);

		// Assert
		_ = words.Should().Equal(
			new Word("echo", IsOperator: false, 0, 4),
			new Word("xa", IsOperator: false, 5, 11),
			new Word("xb", IsOperator: false, 5, 11),
			new Word("y", IsOperator: false, 12, 13));
	}

	/// <summary>
	/// Checks that a redirect target that braces turn into more than one word is one ambiguous word holding the text
	/// as typed, which bash refuses as an "ambiguous redirect", while one that gives a single word is the target.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="target">The text the word after the operator should have.</param>
	/// <param name="ambiguous">Whether it should be ambiguous.</param>
	[Theory]
	[InlineData("echo > {a,b}", "{a,b}", true)]
	[InlineData("echo > x{a,}", "x{a,}", true)]
	[InlineData("echo 2>{a,b}", "{a,b}", true)]
	[InlineData("echo > x{a,}y", "x{a,}y", true)]
	[InlineData("echo > {a,}", "a", false)]
	[InlineData("echo > {a..a}", "a", false)]
	public void Split_WithBracesAfterRedirect_MarksSeveralAmbiguous(string line, string target, bool ambiguous)
	{
		// Act
		var words = CommandLine.Split(line, Variables);

		// Assert
		_ = words.Should().HaveCount(3);
		_ = (words[2].Text, words[2].Ambiguous, words[2].Start, words[2].End).Should().Be((target, ambiguous, 7, line.Length));
	}

	/// <summary>
	/// Checks that an unset variable in a word braces give is reported at the whole typed word.
	/// </summary>
	[Fact]
	public void Split_WithUnsetInBraces_ReportsTypedWord()
	{
		// Arrange
		var unset = new List<Word>();

		// Act
		var words = CommandLine.Split("echo {a,$nope}", Variables, unset.Add);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("echo", "a");
		_ = unset.Should().Equal(new Word("nope", false, 5, 14));
	}

	/// <summary>
	/// Checks that an expansion that fails in a word braces give points at the whole typed word in the line.
	/// </summary>
	[Fact]
	public void Split_WithErrorInBraces_PointsAtTypedWord()
	{
		// Arrange
		const string line = "echo {a,${U:?gone}} z";
		var act = () => CommandLine.Split(line, Variables);

		// Assert
		var thrown = act.Should().Throw<CommandLine.ExpansionError>().Which;

		_ = (thrown.Error.Source, thrown.Error.Start, thrown.Error.End).Should().Be((line, 5, 19));
	}

	/// <summary>
	/// Checks that each word braces give then matches file names, so the wildcards in it expand.
	/// </summary>
	[Fact]
	public void Split_WithWildcardAfterBraces_MatchesFiles()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-tests-").FullName;

		try
		{
			File.WriteAllText(Path.Combine(folder, "a1"), string.Empty);
			File.WriteAllText(Path.Combine(folder, "b1"), string.Empty);

			// Act
			var words = CommandLine.Split($"ls {folder}/{{a,b}}* {folder}/{{c,d}}*", Variables);

			// Assert
			_ = words.Select(word => word.Text).Should().Equal("ls", Path.Combine(folder, "a1"), Path.Combine(folder, "b1"), $"{folder}/c*", $"{folder}/d*");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}
}
