using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="CommandList" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class CommandListTests : IDisposable
{
	private readonly ShellEnvironment environment = new();

	/// <summary>
	/// Checks that a line is cut at every unquoted <c>;</c>, newline, <c>&amp;&amp;</c> and <c>||</c>, each command
	/// keeping its own quotes and the operator before it.
	/// </summary>
	/// <param name="line">The line to split.</param>
	/// <param name="expected">The operator and command pairs, flattened.</param>
	[Theory]
	[InlineData("echo a", new[] { ";", "echo a" })]
	[InlineData("echo a; echo b", new[] { ";", "echo a", ";", "echo b" })]
	[InlineData("echo a;echo b", new[] { ";", "echo a", ";", "echo b" })]
	[InlineData("echo a\necho b", new[] { ";", "echo a", ";", "echo b" })]
	[InlineData("a && b || c", new[] { ";", "a", "&&", "b", "||", "c" })]
	[InlineData("a&&b||c", new[] { ";", "a", "&&", "b", "||", "c" })]
	[InlineData("a | b & ", new[] { ";", "a | b &" })]
	[InlineData("echo 'a; b' \"c && d\"", new[] { ";", "echo 'a; b' \"c && d\"" })]
	[InlineData(@"echo a\; b\&\& c", new[] { ";", @"echo a\; b\&\& c" })]
	[InlineData("a &&\nb", new[] { ";", "a", "&&", "b" })]
	[InlineData("a ||\n\nb", new[] { ";", "a", "||", "b" })]
	[InlineData(";; a ;;", new[] { ";", "a" })]
	[InlineData("echo a # b; c\necho d", new[] { ";", "echo a", ";", "echo d" })]
	[InlineData("echo a#b; c", new[] { ";", "echo a#b", ";", "c" })]
	[InlineData(@"echo \#b; c", new[] { ";", @"echo \#b", ";", "c" })]
	[InlineData("echo '#' ; c", new[] { ";", "echo '#'", ";", "c" })]
	[InlineData("echo \"a#\" #x", new[] { ";", "echo \"a#\"" })]
	[InlineData("# only a comment", new string[0])]
	[InlineData("   ", new string[0])]
	[InlineData("echo '$(x)' '`y`'", new[] { ";", "echo '$(x)' '`y`'" })]
	[InlineData(@"echo \$(x) \`y", new[] { ";", @"echo \$(x) \`y" })]
	[InlineData("echo \"it's\"; b", new[] { ";", "echo \"it's\"", ";", "b" })]
	[InlineData("a;#x\nb", new[] { ";", "a", ";", "b" })]
	[InlineData("sleep 1 &", new[] { ";", "sleep 1 &" })]
	[InlineData("a & b", new[] { ";", "a &", ";", "b" })]
	[InlineData("a&b", new[] { ";", "a&", ";", "b" })]
	[InlineData("a && b & c", new[] { ";", "a && b &", ";", "c" })]
	[InlineData("a; b || c &", new[] { ";", "a", ";", "b || c &" })]
	[InlineData("a &&\nb &", new[] { ";", "a && b &" })]
	[InlineData("a 2>&1 & b", new[] { ";", "a 2>&1 &", ";", "b" })]
	[InlineData("a &> f", new[] { ";", "a &> f" })]
	[InlineData("a |& b", new[] { ";", "a |& b" })]
	[InlineData("a |", new[] { ";", "a |" })]
	[InlineData("echo $", new[] { ";", "echo $" })]
	[InlineData("echo ($HOME) (x)", new[] { ";", "echo ($HOME) (x)" })]
	[InlineData("echo \"a #\" b", new[] { ";", "echo \"a #\" b" })]
	[InlineData("echo 'say \"hi\"'; b", new[] { ";", "echo 'say \"hi\"'", ";", "b" })]
	public void Split_WithLine_CutsAtUnquotedOperators(string line, string[] expected)
	{
		// Act
		var commands = CommandList.Split(line);

		// Assert
		_ = commands.Should().NotBeNull();
		_ = commands!.SelectMany(command => new[] { command.Operator, command.Command }).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a command substitution stays in its command whole, so the operators, quotes and <c>#</c> inside it
	/// neither cut nor end the command around it.
	/// </summary>
	/// <param name="line">The line to split.</param>
	/// <param name="expected">The operator and command pairs, flattened.</param>
	[Theory]
	[InlineData("echo $(a; b && c | d e#f)", new[] { ";", "echo $(a; b && c | d e#f)" })]
	[InlineData("echo `a; b || c`; d", new[] { ";", "echo `a; b || c`", ";", "d" })]
	[InlineData("echo \"$(a; \"b;c\")\"; d", new[] { ";", "echo \"$(a; \"b;c\")\"", ";", "d" })]
	[InlineData("echo $(a ')') && b", new[] { ";", "echo $(a ')')", "&&", "b" })]
	[InlineData("echo $(a)#b # c", new[] { ";", "echo $(a)#b" })]
	[InlineData("echo $(a # ;)\n); b", new[] { ";", "echo $(a # ;)\n)", ";", "b" })]
	public void Split_WithCommandSubstitution_KeepsItInItsCommand(string line, string[] expected)
	{
		// Act
		var commands = CommandList.Split(line);

		// Assert
		_ = commands.Should().NotBeNull();
		_ = commands!.SelectMany(command => new[] { command.Operator, command.Command }).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <see cref="CommandList.Unfinished" /> names the quote or substitution a line leaves open, and
	/// nothing for a complete line, arithmetic, or a quote inside a comment.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="expected">What's left open, or <c>'\0'</c> for nothing.</param>
	[Theory]
	[InlineData("echo 'a", '\'')]
	[InlineData("echo \"a", '"')]
	[InlineData("echo \"'a\" 'b", '\'')]
	[InlineData("echo $(a", '(')]
	[InlineData("echo \"$(a", '(')]
	[InlineData("echo `a", '`')]
	[InlineData("echo a", '\0')]
	[InlineData("echo $((1", '\0')]
	[InlineData("# don't", '\0')]
	[InlineData("echo a # it's", '\0')]
	[InlineData("echo $(a 'b) c')", '\0')]
	public void Unfinished_WithLine_NamesWhatIsOpen(string line, char expected)
	{
		// Act
		var open = CommandList.Unfinished(line);

		// Assert
		_ = open.Should().Be(expected == '\0' ? null : expected);
	}

	/// <summary>
	/// Checks that a substitution that never closes, and arithmetic expansion, are refused.
	/// </summary>
	/// <param name="line">The line.</param>
	[Theory]
	[InlineData("echo $(date")]
	[InlineData("echo \"$(date\"")]
	[InlineData("echo `date")]
	[InlineData("echo $((1 + 1))")]
	[InlineData("a; b $(c")]
	public void Split_WithUnclosedSubstitutionOrArithmetic_ReturnsNull(string line)
	{
		// Act
		var commands = CommandList.Split(line);

		// Assert
		_ = commands.Should().BeNull();
	}

	/// <summary>
	/// Checks that <c>&amp;&amp;</c> and <c>||</c> run or skip each command by the status so far, read from the left
	/// with equal precedence, and that a skipped command leaves the status alone.
	/// </summary>
	/// <param name="line">The list, where <c>t</c> succeeds and <c>f</c> fails.</param>
	/// <param name="expectedRan">The commands that ran, in order.</param>
	/// <param name="expectedStatus">The status the list ends with.</param>
	[Theory]
	[InlineData("t && a", new[] { "t", "a" }, 0)]
	[InlineData("f && a", new[] { "f" }, 1)]
	[InlineData("t || a", new[] { "t" }, 0)]
	[InlineData("f || a", new[] { "f", "a" }, 0)]
	[InlineData("f && a || b", new[] { "f", "b" }, 0)]
	[InlineData("t || a && b", new[] { "t", "b" }, 0)]
	[InlineData("f; a", new[] { "f", "a" }, 0)]
	[InlineData("t; f", new[] { "t", "f" }, 1)]
	[InlineData("f && a && b", new[] { "f" }, 1)]
	[InlineData("t && f || f && a", new[] { "t", "f", "f" }, 1)]
	[InlineData("f & a", new[] { "f &", "a" }, 0)]
	public void Run_WithList_RunsCommandsByStatus(string line, string[] expectedRan, int expectedStatus)
	{
		// Arrange
		var ran = new List<string>();

		// Act
		var status = CommandList.Run(line, 0, command =>
		{
			ran.Add(command);

			return command == "f" ? 1 : 0;
		});

		// Assert
		_ = ran.Should().Equal(expectedRan);
		_ = status.Should().Be(expectedStatus);
	}

	/// <summary>
	/// Checks that the status before the line decides whether a leading <c>&amp;&amp;</c> or <c>||</c> command runs,
	/// and comes back unchanged from a line with no commands.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="before">The status before the line.</param>
	/// <param name="expected">The status the line ends with.</param>
	[Theory]
	[InlineData("", 5, 5)]
	[InlineData("# comment", 3, 3)]
	[InlineData("&& a", 0, 7)]
	[InlineData("&& a", 1, 1)]
	[InlineData("|| a", 1, 7)]
	public void Run_WithStatusBefore_StartsFromIt(string line, int before, int expected)
	{
		// Act
		var status = CommandList.Run(line, before, _ => 7);

		// Assert
		_ = status.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a line with an unclosed substitution runs nothing, prints why, and gives
	/// <see cref="CommandList.UnsupportedStatus" />.
	/// </summary>
	[Fact]
	public void Run_WithUnclosedSubstitution_RunsNothingAndReports()
	{
		// Arrange
		var ran = new List<string>();

		// Act
		var status = CommandList.Run("echo a; echo $(date", 0, command =>
		{
			ran.Add(command);

			return 0;
		});

		// Assert
		_ = ran.Should().BeEmpty();
		_ = status.Should().Be(CommandList.UnsupportedStatus);
		_ = this.environment.Error.Should().Contain("sharpie::parser::unclosed_substitution");
	}

	/// <summary>
	/// Checks that an unclosed <c>$(</c> or backquote is reported with the whole line, its opening underlined, and how
	/// to close it.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="start">Where the substitution starts.</param>
	/// <param name="end">Where its opening ends.</param>
	/// <param name="summary">The report's summary.</param>
	/// <param name="help">The report's hint.</param>
	[Theory]
	[InlineData("echo a; echo $(date", 13, 15, "`$(` is never closed", "add the missing `)`")]
	[InlineData("echo `date", 5, 6, "The backquote is never closed", "add the closing backquote")]
	public void Run_WithUnclosedSubstitution_PointsAtIt(string line, int start, int end, string summary, string help)
	{
		// Act
		_ = CommandList.Run(line, 0, _ => 0);

		// Assert
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::parser::unclosed_substitution", summary)
		{
			Source = line,
			Start = start,
			End = end,
			Label = "runs a command for its output",
			Help = help,
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that arithmetic expansion is refused with its own report, pointing at the <c>$((</c>.
	/// </summary>
	[Fact]
	public void Run_WithArithmetic_PointsAtIt()
	{
		// Act
		var status = CommandList.Run("echo $((1+2))", 0, _ => 0);

		// Assert
		_ = status.Should().Be(CommandList.UnsupportedStatus);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::parser::arithmetic", "Arithmetic expansion isn't supported")
		{
			Source = "echo $((1+2))",
			Start = 5,
			End = 8,
			Label = "works out a number",
			Help = "run a program such as expr for the number, or quote it to pass it as text",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that a failure is reported only for a command that ran and ends its <c>&amp;&amp;</c> and <c>||</c>
	/// chain, the commands <c>set -e</c> acts on.
	/// </summary>
	/// <param name="line">The line, where each command's status is its length minus one.</param>
	/// <param name="expected">The statuses reported as failures.</param>
	[Theory]
	[InlineData("a; bb", new[] { 1 })]
	[InlineData("bb; a", new[] { 1 })]
	[InlineData("bb && a", new int[0])]
	[InlineData("a && bb", new[] { 1 })]
	[InlineData("bb || a", new int[0])]
	[InlineData("bb || ccc", new[] { 2 })]
	[InlineData("bb && ccc; a", new int[0])]
	[InlineData("a && bb; ccc", new[] { 1, 2 })]
	[InlineData("bb && ccc || dddd", new[] { 3 })]
	[InlineData("a", new int[0])]
	public void Run_WithFailedCommands_ReportsOnlyChainEnds(string line, int[] expected)
	{
		// Arrange
		var failures = new List<int>();

		// Act
		_ = CommandList.Run(line, 0, command => command.Length - 1, failures.Add);

		// Assert
		_ = failures.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that each command that runs is told whether <c>&amp;&amp;</c> or <c>||</c> follows it.
	/// </summary>
	[Fact]
	public void Run_WithTestedCommands_TellsRunWhichAreTested()
	{
		// Arrange
		var ran = new List<(string Command, bool Tested)>();

		// Act
		_ = CommandList.Run("a && b; c || d; e", 0, (command, tested) =>
		{
			ran.Add((command, tested));

			return 0;
		});

		// Assert
		_ = ran.Should().Equal(("a", true), ("b", false), ("c", true), ("e", false));
	}

	/// <summary>
	/// Checks that each here-document's body, up to its delimiter line, goes on the end of the command that starts it,
	/// with the operators around it kept, and that quotes, comments, substitutions and operators in a body are text.
	/// </summary>
	/// <param name="line">The text to split.</param>
	/// <param name="expected">The operator and command pairs, flattened.</param>
	[Theory]
	[InlineData("cat <<EOF\nhi\nEOF\necho next", new[] { ";", "cat <<EOF\nhi\nEOF", ";", "echo next" })]
	[InlineData("cat <<EOF; echo b\nhi\nEOF", new[] { ";", "cat <<EOF\nhi\nEOF", ";", "echo b" })]
	[InlineData("echo a; cat <<EOF\nhi\nEOF", new[] { ";", "echo a", ";", "cat <<EOF\nhi\nEOF" })]
	[InlineData("cat <<EOF &&\nhi\nEOF\necho next", new[] { ";", "cat <<EOF\nhi\nEOF", "&&", "echo next" })]
	[InlineData("cat <<A && cat <<B\none\nA\ntwo\nB", new[] { ";", "cat <<A\none\nA", "&&", "cat <<B\ntwo\nB" })]
	[InlineData("cat <<A <<B\none\nA\ntwo\nB", new[] { ";", "cat <<A <<B\none\nA\ntwo\nB" })]
	[InlineData("cat <<EOF\ndon't # stop; a && b || c\n'\nEOF", new[] { ";", "cat <<EOF\ndon't # stop; a && b || c\n'\nEOF" })]
	[InlineData("cat <<-EOF\n\thi\n\tEOF\necho next", new[] { ";", "cat <<-EOF\n\thi\n\tEOF", ";", "echo next" })]
	[InlineData("cat <<EOF # a note\nhi\nEOF", new[] { ";", "cat <<EOF\nhi\nEOF" })]
	[InlineData("cat <<EOF\nhi", new[] { ";", "cat <<EOF\nhi" })]
	[InlineData("cat <<EOF", new[] { ";", "cat <<EOF" })]
	[InlineData("cat <<EOF\n", new[] { ";", "cat <<EOF" })]
	[InlineData("cat '<<EOF'\nEOF", new[] { ";", "cat '<<EOF'", ";", "EOF" })]
	[InlineData("cat <<EOF\n\nEOF", new[] { ";", "cat <<EOF\n\nEOF" })]
	[InlineData("cat <<'EOF'\n$(date) `x`\nEOF", new[] { ";", "cat <<'EOF'\n$(date) `x`\nEOF" })]
	[InlineData("cat <<EOF\n\\$(date) \\`x\nEOF", new[] { ";", "cat <<EOF\n\\$(date) \\`x\nEOF" })]
	[InlineData("cat <<EOF\nnow $(date) `x`\nEOF", new[] { ";", "cat <<EOF\nnow $(date) `x`\nEOF" })]
	[InlineData("cat <<EOF\n$( ) `\nEOF\necho next", new[] { ";", "cat <<EOF\n$( ) `\nEOF", ";", "echo next" })]
	public void Split_WithHereDocument_AddsBodyToItsCommand(string line, string[] expected)
	{
		// Act
		var commands = CommandList.Split(line);

		// Assert
		_ = commands.Should().NotBeNull();
		_ = commands!.SelectMany(command => new[] { command.Operator, command.Command }).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a here-document that never gets its delimiter line still runs with the lines it had, after a
	/// warning.
	/// </summary>
	[Fact]
	public void Run_WithUnclosedHereDocument_WarnsAndRuns()
	{
		// Arrange
		var ran = new List<string>();

		// Act
		var status = CommandList.Run("cat <<EOF\nhi", 0, command =>
		{
			ran.Add(command);

			return 4;
		});

		// Assert
		_ = ran.Should().Equal("cat <<EOF\nhi");
		_ = status.Should().Be(4);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::parser::unclosed_here_document", "The here-document never got its `EOF` line")
		{
			Help = "end it with a line holding only EOF; it ran with the lines it had",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that <see cref="CommandList.IsHereDocumentOpen" /> says whether a reader still owes a here-document its
	/// delimiter line.
	/// </summary>
	/// <param name="text">The lines read so far.</param>
	/// <param name="expected">Whether another line is needed.</param>
	[Theory]
	[InlineData("cat <<EOF", true)]
	[InlineData("cat <<EOF\nhi", true)]
	[InlineData("cat <<EOF\nhi\nEOF", false)]
	[InlineData("cat <<EOF\nhi\nEOFX", true)]
	[InlineData("cat <<A <<B\nx\nA", true)]
	[InlineData("cat <<A <<B\nx\nA\nB", false)]
	[InlineData("cat <<-EOF\n\tEOF", false)]
	[InlineData("cat <<EOF\n\tEOF", true)]
	[InlineData("cat <<EOF\nEOF\ncat <<END", true)]
	[InlineData("cat <<< EOF", false)]
	[InlineData("cat '<<EOF'", false)]
	[InlineData("echo hi", false)]
	public void IsHereDocumentOpen_WithText_SaysWhetherBodyIsOpen(string text, bool expected)
	{
		// Act
		var open = CommandList.IsHereDocumentOpen(text);

		// Assert
		_ = open.Should().Be(expected);
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
