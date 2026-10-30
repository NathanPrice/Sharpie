using FluentAssertions;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for indexed arrays, run by the real shell. Each output was checked against bash 5.3.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class ArraysProcessTests
{
	/// <summary>
	/// Checks what each array form prints, from <c>-c</c>.
	/// </summary>
	/// <param name="script">The commands.</param>
	/// <param name="expected">What bash prints.</param>
	/// <returns>A task that completes when the test is done.</returns>
	[Theory]
	[InlineData("a=(x y z); echo $a ${a[1]} ${a[-1]} ${#a[@]} \"${a[@]}\"", "x y z 3 x y z\n")]
	[InlineData("a=(x \"y z\" w); for e in \"${a[@]}\"; do echo \"[$e]\"; done; for e in ${a[@]}; do echo \"<$e>\"; done", "[x]\n[y z]\n[w]\n<x>\n<y>\n<z>\n<w>\n")]
	[InlineData("a=(x \"y z\" w); for e in \"${a[*]}\"; do echo \"[$e]\"; done", "[x y z w]\n")]
	[InlineData("a=(x y); a+=(z w); a[5]=q; echo \"${!a[@]}\" \"${a[@]}\" ${#a[@]}", "0 1 2 3 5 x y z w q 5\n")]
	[InlineData("a=(x y z); unset 'a[1]'; echo \"${!a[@]}\" \"${a[@]}\"; unset a; echo \"[${a[@]}]\" ${#a[@]}", "0 2 x z\n[] 0\n")]
	[InlineData("i=0; b=(); b[i++]=x; b[i++]=y; echo $i \"${b[@]}\"", "2 x y\n")]
	[InlineData("a=(1 2 3); (( a[1] += 5 )); echo ${a[1]} $(( a[0] + a[2] ))", "7 4\n")]
	[InlineData("a=(p q r); echo ${a[@]:1} ${a[@]:0:2} \"${a[@]/q/Q}\" ${#a[1]} \"${a[@]: -2}\"", "q r p q p Q r 1 q r\n")]
	[InlineData("a=(abc abd); echo \"${a[@]#ab}\" \"${a[@]^^}\"", "c d ABC ABD\n")]
	[InlineData("a=(x 'y\"$z'); declare -p a; e=(); declare -p e", "declare -a a=([0]=\"x\" [1]=\"y\\\"\\$z\")\ndeclare -a e=()\n")]
	[InlineData("f() { local a=(in side); echo \"${a[@]}\"; }; a=(out); f; echo \"${a[@]}\"", "in side\nout\n")]
	[InlineData("f() { local -a w; w=(1 2); echo ${#w[@]}; }; f; echo \"[${w[@]}]\"", "2\n[]\n")]
	[InlineData("a=(x y); (a=(changed); echo \"${a[@]}\"); echo \"${a[@]}\"", "changed\nx y\n")]
	[InlineData("a=(x y); echo \"$(echo \"${a[@]}\")\"; echo \"${a[1]}\" | cat", "x y\ny\n")]
	[InlineData("a=(\n  one   # first\n  two\n); echo \"${a[@]}\"", "one two\n")]
	[InlineData("IFS=: ; a=($(echo p:q:r)); echo ${#a[@]} ${a[2]}", "3 r\n")]
	[InlineData("a=([2]=c [0]=a b); echo \"${!a[@]}\" \"${a[@]}\"", "0 1 2 a b c\n")]
	[InlineData("w=(a b c); cw=2; echo \"${w[cw]}\" \"${w[$cw-1]}\"; a=(x y); a=z; echo \"${a[@]}\"", "c b\nz y\n")]
	[InlineData("s=one; s+=\" two\"; echo \"$s\" ${s[0]} ${#s[@]}; export P=1; P+=2; Q=a; Q+=b env | grep ^Q=; echo $P", "one two one two 1\nQ=ab\n12\n")]
	[InlineData("a='(x y)'; echo \"$a\"; a=(1 2); set | grep '^a='", "(x y)\na=([0]=\"1\" [1]=\"2\")\n")]
	[InlineData("e=(); printf \"[%s]\\n\" \"${e[@]:+set}\" \"${e[*]:+s}\" \"${e[@]-unset}\"", "[]\n[unset]\n")]
	[InlineData("[[ abc =~ (b)(x)?c ]]; echo $? \"${#BASH_REMATCH[@]}\" \"[${BASH_REMATCH[0]}]\" \"[${BASH_REMATCH[1]}]\" \"[${BASH_REMATCH[2]}]\"; [[ abc =~ z ]]; echo ${#BASH_REMATCH[@]}", "0 3 [bc] [b] []\n0\n")]
	[InlineData("f() { words_=(\"$@\"); }; f \"a b\" c; echo ${#words_[@]} \"${words_[0]}\"", "2 a b\n")]
	[InlineData("a=(x y); a[-1]=Y; echo \"${a[@]}\"; unset 'a[-5]'; echo $?", "x Y\n1\n")]
	public async Task Shell_WithArrays_PrintsWhatBashPrints(string script, string expected)
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a list left open at the end of a line asks for the next one, and that one never closed in
	/// <c>-c</c> text runs nothing and exits with 2, as bash does.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithListLeftOpen_PromptsForRest()
	{
		// Act
		var typed = await ShellProcess.RunAsync("a=(x\ny)\necho ${#a[@]} ${a[1]}\nexit\n").ConfigureAwait(true);
		var unclosed = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "a=(x; echo no"]).ConfigureAwait(true);

		// Assert
		_ = typed.Output.Should().Be("$ > $ 2 y\n$ ");
		_ = unclosed.Output.Should().BeEmpty();
		_ = unclosed.ExitCode.Should().Be(2);
		_ = unclosed.Error.Should().Contain("The `(` of an array's list is never closed").And.Contain("starts the list of elements").And.Contain("add the missing `)`");
	}

	/// <summary>
	/// Checks that a subscript that counts back past the first element is reported and fails the command.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithSubscriptBeforeFirstElement_ReportsIt()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "a=(x); a[-3]=y; echo $? ${#a[@]}"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("1 1\n");
		_ = run.Error.Should().Contain("a[-3]: bad array subscript");
	}
}
