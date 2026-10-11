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
	/// Checks that command substitution outside single quotes is refused rather than passed on as text.
	/// </summary>
	/// <param name="line">A line with command substitution in it.</param>
	[Theory]
	[InlineData("echo $(date)")]
	[InlineData("echo \"$(date)\"")]
	[InlineData("echo `date`")]
	[InlineData("echo \"`date`\"")]
	[InlineData("echo $((1 + 1))")]
	[InlineData("a; b $(c)")]
	public void Split_WithCommandSubstitution_ReturnsNull(string line)
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
	/// Checks that a line with command substitution runs nothing, prints why, and gives
	/// <see cref="CommandList.UnsupportedStatus" />.
	/// </summary>
	[Fact]
	public void Run_WithCommandSubstitution_RunsNothingAndReports()
	{
		// Arrange
		var ran = new List<string>();

		// Act
		var status = CommandList.Run("echo a; echo $(date)", 0, command =>
		{
			ran.Add(command);

			return 0;
		});

		// Assert
		_ = ran.Should().BeEmpty();
		_ = status.Should().Be(CommandList.UnsupportedStatus);
		_ = this.environment.Error.Should().Be($"sharpie: $(...) and `...` aren't supported{Environment.NewLine}");
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
