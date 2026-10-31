using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <c>printf</c>, run by the real shell. Each output was checked against bash 5.3.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class PrintfProcessTests
{
	/// <summary>
	/// Checks <c>printf</c> as scripts use it, from <c>-c</c>.
	/// </summary>
	/// <param name="script">The commands.</param>
	/// <param name="expected">What bash prints.</param>
	/// <returns>A task that completes when the test is done.</returns>
	[Theory]
	[InlineData("f() { local out; printf -v out '%s=%d' n 3; echo \"$out\"; }; f; echo \"[${out-unset}]\"", "n=3\n[unset]\n")]
	[InlineData("s=$'it\\'s a\\ttab'; eval \"t=$(printf '%q' \"$s\")\"; [[ $s == \"$t\" ]] && echo same", "same\n")]
	[InlineData("printf '%s\\n' b a c | sort | tr -d '\\n'; echo", "abc\n")]
	[InlineData("x=$(printf '%05.1f' 3.14159); echo \"[$x]\"; printf -v 'a[1]' '%x' 255; echo \"${a[1]}\"", "[003.1]\nff\n")]
	[InlineData("printf '%s %s\\n' k1 v1 k2; printf '%d\\n' nope; echo \"s=$?\"", "k1 v1\nk2 \n0\ns=1\n")]
	public async Task Shell_WithPrintf_PrintsAsBash(string script, string expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
	}
}
