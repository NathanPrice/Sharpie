using FluentAssertions;
using Sharpie.Shell.Features.Completion;

namespace Sharpie.Shell.Tests.Features.Completion;

/// <summary>
/// Tests for <see cref="TabCompleter" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class TabCompleterTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly TabCompleter sut = new(["echo", "exit"]);

	/// <summary>
	/// Initializes a new instance of the <see cref="TabCompleterTests" /> class with <c>PATH</c> pointed at a folder
	/// holding only <c>mytool1</c>, <c>mytool2</c> and <c>mytool3</c>.
	/// </summary>
	public TabCompleterTests()
	{
		var folder = this.environment.CreateFolder();

		// Created out of order to check the matches come back sorted.
		_ = ShellEnvironment.CreateFile(folder, "mytool3", executable: true);
		_ = ShellEnvironment.CreateFile(folder, "mytool1", executable: true);
		_ = ShellEnvironment.CreateFile(folder, "mytool2", executable: true);
		ShellEnvironment.SetPath(folder);
	}

	/// <summary>
	/// Checks that a Tab that can add text does so without ringing the bell.
	/// </summary>
	/// <param name="line">The text typed before Tab.</param>
	/// <param name="expected">The line after Tab.</param>
	[Theory]
	[InlineData("ech", "echo ")]
	[InlineData("my", "mytool")]
	public void Press_WhenTextCanBeAdded_ReturnsLongerLine(string line, string expected)
	{
		// Act
		var result = this.sut.Press(line);

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult(expected, null, Bell: false));
	}

	/// <summary>
	/// Checks the full sequence on several matches: bell, then the list, then each match in order with its index for
	/// highlighting, wrapping back to the first.
	/// </summary>
	[Fact]
	public void Press_RepeatedOnSeveralMatches_RingsListsThenCyclesInOrder()
	{
		// Act
		var presses = Enumerable.Range(0, 6).Select(_ => this.sut.Press("mytool")).ToList();

		// Assert
		_ = presses.Select(press => press.Bell).Should().Equal(true, false, false, false, false, false);
		_ = presses[1].Listed.Should().Equal("mytool1", "mytool2", "mytool3");
		_ = presses[1].Selected.Should().BeNull();
		_ = presses.Skip(2).Select(press => press.Line).Should().Equal("mytool1", "mytool2", "mytool3", "mytool1");
		_ = presses.Skip(2).Select(press => press.Selected).Should().Equal(0, 1, 2, 0);
	}

	/// <summary>
	/// Checks that matches spread over several <c>PATH</c> folders ring the bell on the first Tab and are all listed,
	/// sorted, on the second.
	/// </summary>
	[Fact]
	public void Press_TwiceWithMatchesInSeveralPathFolders_ListsAllSorted()
	{
		// Arrange
		var first = this.environment.CreateFolder();
		var second = this.environment.CreateFolder();

		_ = ShellEnvironment.CreateFile(first, "xyz_quz", executable: true);
		_ = ShellEnvironment.CreateFile(second, "xyz_baz", executable: true);
		_ = ShellEnvironment.CreateFile(second, "xyz_bar", executable: true);
		ShellEnvironment.SetPath(first, second);

		// Act
		var bell = this.sut.Press("xyz_");
		var list = this.sut.Press("xyz_");

		// Assert
		_ = bell.Bell.Should().BeTrue();
		_ = list.Listed.Should().Equal("xyz_bar", "xyz_baz", "xyz_quz");
	}

	/// <summary>
	/// Checks that <see cref="TabCompleter.Reset" /> between two Tabs makes the second one ring the bell again
	/// instead of listing.
	/// </summary>
	[Fact]
	public void Press_AfterReset_RingsBellAgain()
	{
		// Arrange
		_ = this.sut.Press("mytool");
		this.sut.Reset();

		// Act
		var result = this.sut.Press("mytool");

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("mytool", null, Bell: true));
	}

	/// <summary>
	/// Checks that cycling through file names keeps the command typed before them.
	/// </summary>
	[Fact]
	public void Press_CyclingFileNames_KeepsCommand()
	{
		// Arrange
		var folder = this.environment.CreateFolder();

		_ = ShellEnvironment.CreateFile(folder, "notes1", executable: false);
		_ = ShellEnvironment.CreateFile(folder, "notes2", executable: false);
		Directory.SetCurrentDirectory(folder);

		// Act
		var presses = Enumerable.Range(0, 3).Select(_ => this.sut.Press("cat notes")).ToList();

		// Assert
		_ = presses[1].Listed.Should().Equal("notes1", "notes2");
		_ = presses[2].Line.Should().Be("cat notes1");
	}

	/// <summary>
	/// Checks that Tab after a command with a registered completer script completes to the line the script prints,
	/// followed by a space.
	/// </summary>
	[Fact]
	public void Press_AfterCommandWithCompleter_UsesScriptOutput()
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true, "#!/bin/sh\necho run\n");
		var sut = new TabCompleter(["echo"], command => command == "docker" ? script : null);

		// Act
		var result = sut.Press("docker ");

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("docker run ", null, Bell: false));
	}

	/// <summary>
	/// Checks that a completer script printing several lines rings the bell on the first Tab and lists the lines,
	/// sorted, on the second, leaving the typed line alone.
	/// </summary>
	[Fact]
	public void Press_TwiceWithCompleterPrintingSeveralLines_RingsThenListsSorted()
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true, "#!/bin/sh\nprintf 'push\\nadd\\ncommit\\n'\n");
		var sut = new TabCompleter(["echo"], command => command == "git" ? script : null);

		// Act
		var bell = sut.Press("git ");
		var list = sut.Press("git ");

		// Assert
		_ = bell.Should().Be(new TabCompleter.TabResult("git ", null, Bell: true));
		_ = list.Listed.Should().Equal("add", "commit", "push");
		_ = list.Selected.Should().BeNull();
		_ = list.Line.Should().Be("git ");
	}

	/// <summary>
	/// Checks that Tab fills in the start shared by every line the completer script prints when it goes past the typed
	/// word, with no bell and no trailing space.
	/// </summary>
	[Fact]
	public void Press_WithCompleterCandidatesSharingLongerPrefix_CompletesToPrefix()
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true, "#!/bin/sh\nprintf 'checkout\\ncherry-pick\\n'\n");
		var sut = new TabCompleter(["echo"], command => command == "git" ? script : null);

		// Act
		var result = sut.Press("git c");

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("git che", null, Bell: false));
	}

	/// <summary>
	/// Checks that when the completer lines share nothing past the typed word, Tab rings the bell and then lists them.
	/// </summary>
	[Fact]
	public void Press_WithCompleterCandidatesSharingOnlyTypedText_RingsThenLists()
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true, "#!/bin/sh\nprintf 'checkout\\ncherry-pick\\n'\n");
		var sut = new TabCompleter(["echo"], command => command == "git" ? script : null);

		// Act
		var bell = sut.Press("git che");
		var list = sut.Press("git che");

		// Assert
		_ = bell.Should().Be(new TabCompleter.TabResult("git che", null, Bell: true));
		_ = list.Listed.Should().Equal("checkout", "cherry-pick");
	}

	/// <summary>
	/// Checks that the completer script gets the command, the word being completed, and the word before it as its
	/// three arguments. The third is the command name itself when only the command comes before, as in bash.
	/// </summary>
	/// <param name="line">The text typed before Tab.</param>
	/// <param name="expected">The line after Tab, holding the arguments the script echoed back.</param>
	[Theory]
	[InlineData("git remote set", "git remote git|set|remote ")]
	[InlineData("git remote ", "git remote git||remote ")]
	[InlineData("git ", "git git||git ")]
	[InlineData("git re", "git git|re|git ")]
	public void Press_AfterCommandWithCompleter_PassesCompletionContext(string line, string expected)
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true, "#!/bin/sh\necho \"$1|$2|$3\"\n");
		var sut = new TabCompleter(["echo"], command => command == "git" ? script : null);

		// Act
		var result = sut.Press(line);

		// Assert
		_ = result.Line.Should().Be(expected);
	}

	/// <summary>
	/// Checks that the completer script gets the whole line in <c>COMP_LINE</c> and the cursor's byte offset in
	/// <c>COMP_POINT</c>, which is past the end of the line.
	/// </summary>
	/// <param name="line">The text typed before Tab.</param>
	/// <param name="expected">The line after Tab, holding the variables the script echoed back.</param>
	[Theory]
	[InlineData("git ad", "git git ad|6 ")]
	[InlineData("git é", "git git é|6 ")]
	public void Press_AfterCommandWithCompleter_SetsLineAndPointVariables(string line, string expected)
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true,
			"#!/bin/sh\necho \"$COMP_LINE|$COMP_POINT\"\n");
		var sut = new TabCompleter(["echo"], command => command == "git" ? script : null);

		// Act
		var result = sut.Press(line);

		// Assert
		_ = result.Line.Should().Be(expected);
		_ = Environment.GetEnvironmentVariable("COMP_LINE").Should().BeNull();
	}

	/// <summary>
	/// Checks that a registered script that can't be started falls back to normal completion instead of throwing.
	/// </summary>
	[Fact]
	public void Press_WhenCompleterScriptIsMissing_FallsBackToBell()
	{
		// Arrange
		Directory.SetCurrentDirectory(this.environment.CreateFolder());
		var sut = new TabCompleter(["echo"], _ => "/no/such/completer");

		// Act
		var result = sut.Press("docker ");

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("docker ", null, Bell: true));
	}

	/// <summary>
	/// Checks that after Tab completes something, by name, by a completer's only line or by the start its lines share,
	/// the next Tab starts over and rings the bell instead of listing.
	/// </summary>
	/// <param name="completing">The line Tab completes in between.</param>
	[Theory]
	[InlineData("ech")]
	[InlineData("docker ")]
	[InlineData("git c")]
	public void Press_AfterCompleting_StartsOver(string completing)
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		var docker = ShellEnvironment.CreateFile(folder, "docker-completer", executable: true, "#!/bin/sh\necho run\n");
		var git = ShellEnvironment.CreateFile(folder, "git-completer", executable: true, "#!/bin/sh\nprintf 'checkout\\ncherry-pick\\n'\n");
		var sut = new TabCompleter(["echo", "exit"], command => command switch { "docker" => docker, "git" => git, _ => null });
		_ = sut.Press("mytool");
		_ = sut.Press(completing);

		// Act
		var result = sut.Press("mytool");

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("mytool", null, Bell: true));
	}

	/// <summary>
	/// Checks that a completer script that reads its input gets an empty, closed input instead of waiting forever.
	/// </summary>
	[Fact]
	public void Press_CompleterReadingInput_GetsClosedInput()
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true, "#!/bin/sh\ncat > /dev/null\necho run\n");
		var sut = new TabCompleter(["echo"], command => command == "docker" ? script : null);

		// Act
		var result = sut.Press("docker ");

		// Assert
		_ = result.Line.Should().Be("docker run ");
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
