using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Conditions;

/// <summary>
/// Runs <c>test</c>, <c>[</c> and <c>[[ ... ]]</c> through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class ConditionProcessTests
{
	/// <summary>
	/// Checks that conditions run inside lists the way bash runs them, with <c>&amp;&amp;</c> and <c>||</c> inside
	/// <c>[[ ]]</c> joining tests instead of commands, and <c>&lt;</c> there comparing rather than redirecting.
	/// </summary>
	/// <param name="command">The <c>-c</c> command.</param>
	/// <param name="expected">What it prints.</param>
	[Theory]
	[InlineData("[ -d / ] && echo y", "y\n")]
	[InlineData("test -f / || echo n", "n\n")]
	[InlineData("x=abc; [[ $x == a* && -n $x ]] && echo ok", "ok\n")]
	[InlineData("x='a b'; [[ $x == 'a b' ]]; echo $?", "0\n")]
	[InlineData("[[ abc =~ ^a(b|c)+$ ]]; echo $?", "0\n")]
	[InlineData("[[ a < b ]] && echo less", "less\n")]
	[InlineData("[[ $(echo 'a  b') == 'a  b' ]] && echo kept", "kept\n")]
	[InlineData("[[ '' || a ]] || echo no; echo done", "done\n")]
	[InlineData("type [", "[ is a shell builtin\n")]
	[InlineData("type test", "test is a shell builtin\n")]
	public async Task Shell_WithCondition_RunsItAsBashDoes(string command, string expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", command]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>set -x</c> shows a condition before it runs with its words unquoted, as bash does.
	/// </summary>
	[Fact]
	public async Task Shell_WithTracedCondition_PrintsIt()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "set -x; [[ 'a b' == a* ]]"]).ConfigureAwait(true);

		// Assert
		_ = run.Error.Should().Be("+ [[ a b == a* ]]\n");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that a condition in the background runs in a copy of the shell and gives its status to <c>wait</c>.
	/// </summary>
	[Fact]
	public async Task Shell_WithBackgroundCondition_RunsItInCopy()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "[[ a && '' ]] &\nwait %1; echo $?"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().MatchRegex(@"^\[1\] \d+\n1\n$");
	}

	/// <summary>
	/// Checks that a bad condition ends with status 2.
	/// </summary>
	[Fact]
	public async Task Shell_WithUnclosedCondition_ExitsTwo()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "[[ a"]).ConfigureAwait(true);

		// Assert
		_ = run.Error.Should().StartWith("Error: sharpie::condition::missing_bracket\n");
		_ = run.ExitCode.Should().Be(2);
	}
}
