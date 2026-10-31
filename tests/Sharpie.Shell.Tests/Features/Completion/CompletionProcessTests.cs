using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Completion;

/// <summary>
/// Tests for the completion builtins, run by the real shell. Each output was checked against bash 5.3.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class CompletionProcessTests
{
	/// <summary>
	/// Checks <c>COMP_WORDBREAKS</c>'s default and a completion function written as scripts write them.
	/// </summary>
	/// <returns>A task that completes when the test is done.</returns>
	[Fact]
	public async Task Shell_WithCompletionFunction_RunsAsBash()
	{
		// Arrange
		const string Script = """
			printf '%q\n' "$COMP_WORDBREAKS"
			_sampletool() { COMPREPLY=($(compgen -W "add commit" -- "$2")); }
			complete -o nospace -F _sampletool sampletool; complete -p sampletool
			_sampletool sampletool c sampletool; echo "${COMPREPLY[@]}"
			compgen -A function _sample; compopt -o filenames 2>/dev/null; echo "s=$?"
			""";

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", Script]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$' \\t\\n\"\\'@><=;|&(:'\ncomplete -o nospace -F _sampletool sampletool\ncommit\n_sampletool\ns=1\n");
	}
}
