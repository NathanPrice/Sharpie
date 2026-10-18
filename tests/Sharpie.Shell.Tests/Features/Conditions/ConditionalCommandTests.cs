using System.Globalization;
using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Conditions;

namespace Sharpie.Shell.Tests.Features.Conditions;

/// <summary>
/// Tests for <see cref="ConditionalCommand" />, given the words <see cref="CommandLine.Split" /> reads from a typed
/// <c>[[ ... ]]</c>.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ConditionalCommandTests : IDisposable
{
	private const string MissingLabel = "expected a word before this";
	private const string TestHelp = "add a test, like -f file or $x == y";
	private const string UnexpectedLabel = "expected && or || before this";
	private const string UnexpectedHelp = "join the tests with && or ||, or quote a word that has spaces";
	private const string PatternLabel = "a POSIX extended regular expression";
	private const string PatternHelp = "check its brackets, classes and parentheses, or quote it to match it as text";
	private const string NumberLabel = "compares whole numbers";
	private const string NumberHelp = "-eq, -ne, -lt, -le, -gt and -ge compare whole numbers; use = or != to compare text";

	private readonly ShellEnvironment environment = new();
	private readonly Dictionary<string, string> variables = new()
	{
		["x"] = "a b",
		["star"] = "a*",
		["empty"] = string.Empty,
		["five"] = "5",
		["lines"] = "a\nb",
		["line"] = "a\n",
	};

	/// <inheritdoc />
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Checks that a condition gives the status bash gives it: <c>||</c> looser than <c>&amp;&amp;</c>, looser than
	/// <c>!</c>, groups in parentheses, patterns after <c>==</c> and <c>!=</c> that a quote turns into plain text,
	/// regular expressions after <c>=~</c>, and no word splitting. Each row was checked against bash 5.3.
	/// </summary>
	/// <param name="line">The command as typed.</param>
	/// <param name="expected">The status.</param>
	[Theory]
	[InlineData("[[ a ]]", 0)]
	[InlineData("[[ \"\" ]]", 1)]
	[InlineData("[[ $empty ]]", 1)]
	[InlineData("[[ -n $empty ]]", 1)]
	[InlineData("[[ -z $empty ]]", 0)]
	[InlineData("[[ -n $x ]]", 0)]
	[InlineData("[[ $x == 'a b' ]]", 0)]
	[InlineData("[[ $x = a* ]]", 0)]
	[InlineData("[[ $x == \"a*\" ]]", 1)]
	[InlineData("[[ 'a*' == \"a*\" ]]", 0)]
	[InlineData("[[ abc == $star ]]", 0)]
	[InlineData("[[ abc == \"$star\" ]]", 1)]
	[InlineData("[[ abc != a?c ]]", 1)]
	[InlineData("[[ abc != b* ]]", 0)]
	[InlineData("[[ abc != \"a*\" ]]", 0)]
	[InlineData("[[ / -nt /sample_missing ]]", 0)]
	[InlineData("[[ /sample_missing -ot / ]]", 0)]
	[InlineData("[[ / -ef / ]]", 0)]
	[InlineData("[[ a/b == * ]]", 0)]
	[InlineData("[[ a1 == a[[:digit:]] ]]", 0)]
	[InlineData("[[ a && b ]]", 0)]
	[InlineData("[[ a && \"\" ]]", 1)]
	[InlineData("[[ \"\" || b ]]", 0)]
	[InlineData("[[ \"\" || \"\" ]]", 1)]
	[InlineData("[[ a || \"\" && \"\" ]]", 0)]
	[InlineData("[[ ( a || \"\" ) && \"\" ]]", 1)]
	[InlineData("[[ ! a && \"\" ]]", 1)]
	[InlineData("[[ ! \"\" && a ]]", 0)]
	[InlineData("[[ ! ( a && b ) ]]", 1)]
	[InlineData("[[ ! ! a ]]", 0)]
	[InlineData("[[ abc =~ ^a(b|c)+$ ]]", 0)]
	[InlineData("[[ abd =~ ^a(b|c)+$ ]]", 1)]
	[InlineData("[[ abc =~ b ]]", 0)]
	[InlineData("[[ a1 =~ [[:digit:]] ]]", 0)]
	[InlineData("[[ ab =~ ^[[:alpha:]]+$ ]]", 0)]
	[InlineData("[[ a1 =~ ^[[:alpha:]]+$ ]]", 1)]
	[InlineData("[[ a.c =~ \"a.c\" ]]", 0)]
	[InlineData("[[ abc =~ \"a.c\" ]]", 1)]
	[InlineData("[[ abc =~ a.c ]]", 0)]
	[InlineData("[[ \"a b\" =~ ^(a b)$ ]]", 0)]
	[InlineData("[[ ab =~ ^(a b)$ ]]", 1)]
	[InlineData("[[ 5 -eq $five ]]", 0)]
	[InlineData("[[ 5 -eq five ]]", 0)]
	[InlineData("[[ 0 -eq $empty ]]", 0)]
	[InlineData("[[ 0 -eq unset_name ]]", 0)]
	[InlineData("[[ 2 -lt 10 ]]", 0)]
	[InlineData("[[ 2 < 10 ]]", 1)]
	[InlineData("[[ a < b ]]", 0)]
	[InlineData("[[ b > a ]]", 0)]
	[InlineData("[[ -d / && -e / ]]", 0)]
	[InlineData("[[ -v x ]]", 0)]
	[InlineData("[[ -v unset_name ]]", 1)]
	[InlineData("[[ 1 -eq 1 || 1 -eq bad! ]]", 0)]
	[InlineData("[[ 1 -eq 2 && 1 -eq bad! ]]", 1)]
	[InlineData("[[ \"\" && a =~ [a ]]", 1)]
	[InlineData("[[ a =~ ^(a)$ ]]", 0)]
	[InlineData("[[ $lines =~ a.b ]]", 0)]
	[InlineData("[[ $line =~ a$ ]]", 1)]
	[InlineData("[[ ab =~ (a$|b) ]]", 0)]
	[InlineData("[[ a$ =~ a\\$ ]]", 0)]
	[InlineData("[[ a$ =~ \"a$\" ]]", 0)]
	[InlineData("[[ axb =~ a\\.b ]]", 1)]
	[InlineData("[[ \"a b\" =~ a\\sb ]]", 1)]
	public void Run_WithCondition_GivesBashStatus(string line, int expected)
	{
		// Act
		var status = this.Run(line);

		// Assert
		_ = status.Should().Be(expected);
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a condition that can't be read gives status 2 and points at the word that's wrong.
	/// </summary>
	/// <param name="line">The command as typed.</param>
	/// <param name="code">The error's code.</param>
	/// <param name="underlined">The text the error underlines.</param>
	/// <param name="summary">What the error says is wrong.</param>
	/// <param name="label">The text under the underline.</param>
	/// <param name="help">Its hint.</param>
	[Theory]
	[InlineData("[[ a", "sharpie::condition::missing_bracket", "[[", "`[[` is missing its closing `]]`", "starts a condition", "end the condition with ]]")]
	[InlineData("[[ a ]] b", "sharpie::condition::after_bracket", "b", "`b` comes after the condition's end", "after `]]`", "put ; or && between the condition and the next command")]
	[InlineData("[[ ]]", "sharpie::condition::missing_word", "]]", "The condition is missing a test", MissingLabel, TestHelp)]
	[InlineData("[[ ! ]]", "sharpie::condition::missing_word", "]]", "The condition is missing a test", MissingLabel, TestHelp)]
	[InlineData("[[ a && ]]", "sharpie::condition::missing_word", "]]", "The condition is missing a test", MissingLabel, TestHelp)]
	[InlineData("[[ -n ]]", "sharpie::condition::missing_word", "]]", "The condition is missing the word `-n` checks", MissingLabel, "put a word after -n")]
	[InlineData("[[ -f && a ]]", "sharpie::condition::missing_word", "&&", "The condition is missing the word `-f` checks", MissingLabel, "put a word after -f")]
	[InlineData("[[ a == ]]", "sharpie::condition::missing_word", "]]", "The condition is missing the word `==` compares with", MissingLabel, "put a word after ==, or quote it to compare it as text")]
	[InlineData("[[ a b ]]", "sharpie::condition::unexpected_word", "b", "`b` doesn't belong to any test", UnexpectedLabel, UnexpectedHelp)]
	[InlineData("[[ -n = -n ]]", "sharpie::condition::unexpected_word", "-n", "`-n` doesn't belong to any test", UnexpectedLabel, UnexpectedHelp)]
	[InlineData("[[ a '<' b ]]", "sharpie::condition::unexpected_word", "'<'", "`<` doesn't belong to any test", UnexpectedLabel, UnexpectedHelp)]
	[InlineData("[[ a \">\" b ]]", "sharpie::condition::unexpected_word", "\">\"", "`>` doesn't belong to any test", UnexpectedLabel, UnexpectedHelp)]
	[InlineData("[[ && a ]]", "sharpie::condition::unexpected_operator", "&&", "`&&` can't start a test", "expected a word here", "quote it to compare it as text")]
	[InlineData("[[ ( a ]]", "sharpie::condition::unclosed_group", "(", "`(` is never closed", "opens a group", "add a ) after the tests it groups")]
	[InlineData("[[ a =~ [[:nope:]] ]]", "sharpie::condition::invalid_pattern", "[[:nope:]]", "`[[:nope:]]` isn't a valid regular expression", PatternLabel, PatternHelp)]
	[InlineData("[[ a =~ a) ]]", "sharpie::condition::invalid_pattern", "a)", "`a)` isn't a valid regular expression", PatternLabel, PatternHelp)]
	[InlineData("[[ a =~ [a ]]", "sharpie::condition::invalid_pattern", "[a", "`[a` isn't a valid regular expression", PatternLabel, PatternHelp)]
	[InlineData("[[ 1 -eq 1.5 ]]", "sharpie::test::not_a_number", "1 -eq 1.5", "`1.5` isn't a whole number", NumberLabel, NumberHelp)]
	[InlineData("[[ 0 -eq a-b ]]", "sharpie::test::not_a_number", "0 -eq a-b", "`a-b` isn't a whole number", NumberLabel, NumberHelp)]
	public void Run_WithBadCondition_ReportsAndGivesTwo(string line, string code, string underlined, string summary, string label, string help)
	{
		// Act
		var status = this.Run(line);

		// Assert
		_ = status.Should().Be(TestCommand.ErrorStatus);
		_ = this.environment.Error.Should().StartWith($"Error: {code}\n\n  × {summary}\n")
			.And.Contain($"╰── {label}\n")
			.And.EndWith($"  help: {help}\n");

		var lines = this.environment.Error.Split('\n');
		var source = Array.FindIndex(lines, text => text.Contains(" │ ", StringComparison.Ordinal));
		var column = lines[source].IndexOf('│', StringComparison.Ordinal) + 2;
		var marks = lines[source + 1][column..].TrimEnd();

		_ = lines[source][(column + marks.Length - marks.TrimStart().Length)..].Should().StartWith(underlined);
		_ = marks.Trim().Length.Should().Be(underlined.Length);
	}

	/// <summary>
	/// Checks that the file operators read a word shaped like a variable name as a file, not as the variable's value
	/// the number operators would read.
	/// </summary>
	/// <param name="line">The command as typed, in a folder that holds only the file <c>sample</c>.</param>
	[Theory]
	[InlineData("[[ sample -nt sample_missing ]]")]
	[InlineData("[[ sample_missing -ot sample ]]")]
	[InlineData("[[ sample -ef sample ]]")]
	public void Run_WithFileOperatorOnNameLikeFile_ReadsItAsFile(string line)
	{
		// Arrange
		Directory.SetCurrentDirectory(this.environment.CreateFolder());
		File.WriteAllText("sample", string.Empty);

		// Act
		var status = this.Run(line);

		// Assert
		_ = status.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>&lt;</c> and <c>&gt;</c> sort in the locale's order, where a lower-case letter comes before the
	/// next upper-case one, unlike in <c>test</c>.
	/// </summary>
	[Fact]
	public void Run_WithLessThanInCulture_SortsInCultureOrder()
	{
		// Arrange
		var culture = CultureInfo.CurrentCulture;
		CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

		try
		{
			// Act
			var less = this.Run("[[ a < B ]]");
			var greater = this.Run("[[ a > B ]]");

			// Assert
			_ = less.Should().Be(0);
			_ = greater.Should().Be(1);
		}
		finally
		{
			CultureInfo.CurrentCulture = culture;
		}
	}

	/// <summary>
	/// Checks that a POSIX regular expression's bracket expressions become .NET sets that match the same characters,
	/// and the text outside them is kept as is.
	/// </summary>
	/// <param name="pattern">The POSIX expression.</param>
	/// <param name="expected">The .NET expression, or <see langword="null" /> for a class that doesn't exist.</param>
	[Theory]
	[InlineData("abc", "abc")]
	[InlineData("^a$", @"^a\z")]
	[InlineData(@"a\$", @"a\$")]
	[InlineData("[$]", "[$]")]
	[InlineData("[[:digit:]]+", "[0-9]+")]
	[InlineData("[^[:alpha:]_]", @"[^\p{L}_]")]
	[InlineData("[[:upper:][:lower:]]", @"[\p{Lu}\p{Ll}]")]
	[InlineData("[[:space:][:blank:]]", @"[\s \t]")]
	[InlineData("[[:alnum:][:xdigit:]]", @"[\p{L}\p{Nd}0-9A-Fa-f]")]
	[InlineData("[[:punct:][:cntrl:]]", @"[\p{P}\p{S}\p{Cc}]")]
	[InlineData("[[:graph:]][[:print:]]", @"[\p{L}\p{M}\p{N}\p{P}\p{S}][\P{Cc}]")]
	[InlineData(@"[\d]", @"[\\d]")]
	[InlineData("[]a]", @"[\]a]")]
	[InlineData("[^]a]", @"[^\]a]")]
	[InlineData("[a[b]", @"[a\[b]")]
	[InlineData(@"\[a]", @"\[a]")]
	[InlineData("a[b", "a[b")]
	[InlineData("a[", "a[")]
	[InlineData("[^", "[^")]
	[InlineData(@"a\", @"a\")]
	[InlineData("[a-z]x[0-9]", "[a-z]x[0-9]")]
	[InlineData("[[:nope:]]", null)]
	public void ToDotNetPattern_WithPosixPattern_RewritesBrackets(string pattern, string? expected)
	{
		// Act
		var converted = ConditionalCommand.ToDotNetPattern(pattern);

		// Assert
		_ = converted.Should().Be(expected);
	}

	private int Run(string line) => ConditionalCommand.Run(CommandLine.Split(line, this.variables), line, this.variables);
}
