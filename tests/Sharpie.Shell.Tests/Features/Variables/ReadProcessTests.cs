using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Runs <c>read</c> through the built shell as a real process, reading from the shell's own standard input.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class ReadProcessTests
{
	/// <summary>
	/// Checks that <c>read</c> takes the line after its own from the shell's input and splits it.
	/// </summary>
	[Fact]
	public async Task Shell_WithReadInInput_ReadsNextLine()
	{
		// Act
		var run = await ShellProcess.RunAsync("read a b\nx y z\necho \"[$a][$b]\"\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ [x][y z]\n$ ");
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>IFS= read -r</c> keeps the line whole, spaces and backslashes included, over an <c>IFS</c> set
	/// earlier, which comes back after the <c>read</c>.
	/// </summary>
	[Fact]
	public async Task Shell_WithEmptyIfsAndRaw_KeepsLineWhole()
	{
		// Act
		var run = await ShellProcess.RunAsync("  x:\\y  \n", arguments: ["-c", "IFS=:; IFS= read -r a b; echo \"[$a][$b][$IFS]\""]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("[  x:\\y  ][][:]\n");
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that input ending before a newline gives 1 and still sets the name, as bash does.
	/// </summary>
	[Fact]
	public async Task Shell_WithInputEndingEarly_GivesOne()
	{
		// Act
		var run = await ShellProcess.RunAsync("abc", arguments: ["-c", "read a; echo $? \"[$a]\""]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("1 [abc]\n");
	}

	/// <summary>
	/// Checks the loop bash completion scripts use to read space-separated items, where the last item has no space
	/// after it, as bash gives.
	/// </summary>
	[Fact]
	public async Task Shell_WithReadDelimiterLoop_ReadsEveryItem()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "printf 'one two three' | while read -r -d ' ' item || [[ -n $item ]]; do echo \"<$item>\"; done"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("<one>\n<two>\n<three>\n");
		_ = run.Error.Should().BeEmpty();
	}
}
