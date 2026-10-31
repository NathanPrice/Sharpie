using FluentAssertions;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <c>&lt;(...)</c> and <c>&gt;(...)</c>, run by the real shell. Each output was checked against bash 5.3.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class ProcessSubstitutionProcessTests
{
	/// <summary>
	/// Checks what each form prints, from <c>-c</c>.
	/// </summary>
	/// <param name="script">The commands.</param>
	/// <param name="expected">What bash prints.</param>
	/// <returns>A task that completes when the test is done.</returns>
	[Theory]
	[InlineData("while read l; do echo \"got $l\"; done < <(printf '1\\n2\\n')", "got 1\ngot 2\n")]
	[InlineData("diff <(echo a) <(echo b); echo \"s=$?\"", "1c1\n< a\n---\n> b\ns=1\n")]
	[InlineData("for f in <(echo q); do cat $f; done; [[ -e <(true) ]] && echo exists", "q\nexists\n")]
	[InlineData("echo $(cat <(echo inner)); cat <(cat <(echo nested))", "inner\nnested\n")]
	[InlineData("f() { echo fn; }; a=(1 2); cat <(f) <(echo \"${a[1]}\")", "fn\n2\n")]
	[InlineData("cat <(echo a) <(echo b) 2>/dev/null 3>/dev/null", "a\nb\n")]
	[InlineData("head -c 3 <(yes) > /dev/null; echo \"s=$?\"", "s=0\n")]
	[InlineData("echo \"<(x)\" '>(y)'; [[ $(echo --x=<(true)) == --x=/dev/fd/* ]] && echo joined", "<(x) >(y)\njoined\n")]
	[InlineData("cat <(\n  echo multi\n)", "multi\n")]
	[InlineData("source <(echo 'echo sourced')", "sourced\n")]
	public async Task Shell_WithProcessSubstitution_PrintsAsBash(string script, string expected)
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be(expected);
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a <c>&gt;(...)</c> gets what the command writes. It runs alongside, so the script waits for its file.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithOutputSubstitution_FeedsTheList()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		const string Script = """
			t=$(mktemp)
			echo hi > >(tr a-z A-Z > "$t")
			for i in $(seq 100); do [[ -s $t ]] && break; sleep 0.1; done
			cat "$t"; rm -f "$t"
			""";

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", Script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("HI\n");
	}

	/// <summary>
	/// Checks that a substitution left open at the end of a typed line asks for the next line, as an open <c>$(</c>
	/// does.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithOpenSubstitutionTyped_ReadsNextLine()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var run = await ShellProcess.RunAsync("cat <(\necho multi\n)\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ > > multi\n$ ");
		_ = run.Error.Should().BeEmpty();
	}
}
