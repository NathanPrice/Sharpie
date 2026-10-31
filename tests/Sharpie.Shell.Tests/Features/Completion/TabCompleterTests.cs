using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.Variables;

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
		var result = this.sut.Press(line, line.Length);

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult(expected, expected.Length, null, Bell: false));
	}

	/// <summary>
	/// Checks the full sequence on several matches: bell, then the list, then each match in order with its index for
	/// highlighting, wrapping back to the first.
	/// </summary>
	[Fact]
	public void Press_RepeatedOnSeveralMatches_RingsListsThenCyclesInOrder()
	{
		// Act
		var presses = Enumerable.Range(0, 6).Select(_ => this.sut.Press("mytool", 6)).ToList();

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
		var bell = this.sut.Press("xyz_", 4);
		var list = this.sut.Press("xyz_", 4);

		// Assert
		_ = bell.Bell.Should().BeTrue();
		_ = list.Listed.Should().Equal("xyz_bar", "xyz_baz", "xyz_quz");
	}

	/// <summary>
	/// Checks that Tab on a whole program name that longer names also start with finishes it with a space instead of
	/// ringing the bell.
	/// </summary>
	[Fact]
	public void Press_OnExactNameWithLongerMatches_AcceptsItWithSpace()
	{
		// Arrange
		var folder = this.environment.CreateFolder();

		_ = ShellEnvironment.CreateFile(folder, "mytool", executable: true);
		_ = ShellEnvironment.CreateFile(folder, "mytool1", executable: true);
		ShellEnvironment.SetPath(folder);

		// Act
		var result = this.sut.Press("mytool", 6);

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("mytool ", 7, null, Bell: false));
	}

	/// <summary>
	/// Checks that <see cref="TabCompleter.Reset" /> between two Tabs makes the second one ring the bell again
	/// instead of listing.
	/// </summary>
	[Fact]
	public void Press_AfterReset_RingsBellAgain()
	{
		// Arrange
		_ = this.sut.Press("mytool", 6);
		this.sut.Reset();

		// Act
		var result = this.sut.Press("mytool", 6);

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("mytool", 6, null, Bell: true));
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
		var presses = Enumerable.Range(0, 3).Select(_ => this.sut.Press("cat notes", 9)).ToList();

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
		var sut = WithCompleters(["echo"], ("docker", script));

		// Act
		var result = sut.Press("docker ", 7);

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("docker run ", 11, null, Bell: false));
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
		var sut = WithCompleters(["echo"], ("git", script));

		// Act
		var bell = sut.Press("git ", 4);
		var list = sut.Press("git ", 4);

		// Assert
		_ = bell.Should().Be(new TabCompleter.TabResult("git ", 4, null, Bell: true));
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
		var sut = WithCompleters(["echo"], ("git", script));

		// Act
		var result = sut.Press("git c", 5);

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("git che", 7, null, Bell: false));
	}

	/// <summary>
	/// Checks that when the completer lines share nothing past the typed word, Tab rings the bell and then lists them.
	/// </summary>
	[Fact]
	public void Press_WithCompleterCandidatesSharingOnlyTypedText_RingsThenLists()
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true, "#!/bin/sh\nprintf 'checkout\\ncherry-pick\\n'\n");
		var sut = WithCompleters(["echo"], ("git", script));

		// Act
		var bell = sut.Press("git che", 7);
		var list = sut.Press("git che", 7);

		// Assert
		_ = bell.Should().Be(new TabCompleter.TabResult("git che", 7, null, Bell: true));
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
		var sut = WithCompleters(["echo"], ("git", script));

		// Act
		var result = sut.Press(line, line.Length);

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
	[InlineData("ls | git ad", "ls | git git ad|6 ")]
	public void Press_AfterCommandWithCompleter_SetsLineAndPointVariables(string line, string expected)
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true,
			"#!/bin/sh\necho \"$COMP_LINE|$COMP_POINT\"\n");
		var sut = WithCompleters(["echo"], ("git", script));

		// Act
		var result = sut.Press(line, line.Length);

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
		var sut = WithCompleters(["echo"], ("", "/no/such/completer"));

		// Act
		var result = sut.Press("docker ", 7);

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("docker ", 7, null, Bell: true));
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
		var sut = WithCompleters(["echo", "exit"], ("docker", docker), ("git", git));
		_ = sut.Press("mytool", 6);
		_ = sut.Press(completing, completing.Length);

		// Act
		var result = sut.Press("mytool", 6);

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("mytool", 6, null, Bell: true));
	}

	/// <summary>
	/// Checks that a completer script that reads its input gets an empty, closed input instead of waiting forever.
	/// </summary>
	[Fact]
	public void Press_CompleterReadingInput_GetsClosedInput()
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true, "#!/bin/sh\ncat > /dev/null\necho run\n");
		var sut = WithCompleters(["echo"], ("docker", script));

		// Act
		var result = sut.Press("docker ", 7);

		// Assert
		_ = result.Line.Should().Be("docker run ");
	}

	/// <summary>
	/// Checks that a word starting <c>$</c> or <c>${</c> completes to a variable name, with a <c>/</c> and no space
	/// when the variable holds a directory, as bash does.
	/// </summary>
	/// <param name="line">The text typed before Tab.</param>
	/// <param name="expected">The line after Tab.</param>
	[Theory]
	[InlineData("echo $SAMPLEV", "echo $SAMPLEVAR ")]
	[InlineData("echo ${SAMPLEV", "echo ${SAMPLEVAR} ")]
	[InlineData("echo \"$SAMPLEV", "echo \"$SAMPLEVAR\" ")]
	[InlineData("echo $SAMPLED", "echo $SAMPLEDIR/")]
	[InlineData("echo '$SAMPLEV", "echo '$SAMPLEV")]
	public void Press_OnVariableName_CompletesName(string line, string expected)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["SAMPLEVAR"] = "x", ["SAMPLEDIR"] = this.environment.CreateFolder() };
		var sut = new TabCompleter(["echo"], variables: variables);

		// Act
		var result = sut.Press(line, line.Length);

		// Assert
		_ = result.Line.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a file name completes inside a folder written as <c>~/</c> or <c>$NAME/</c>, which is looked up
	/// expanded but kept as typed.
	/// </summary>
	/// <param name="line">The text typed before Tab.</param>
	/// <param name="expected">The line after Tab.</param>
	[Theory]
	[InlineData("cat ~/sam", "cat ~/sample.txt ")]
	[InlineData("cat $SAMPLEDIR/sam", "cat $SAMPLEDIR/sample.txt ")]
	[InlineData("cat ${SAMPLEDIR}/sam", "cat ${SAMPLEDIR}/sample.txt ")]
	public void Press_InExpandedFolder_KeepsFolderAsTyped(string line, string expected)
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(folder, "sample.txt", executable: false);
		var sut = new TabCompleter(["cat"], variables: new Dictionary<string, string> { ["HOME"] = folder, ["SAMPLEDIR"] = folder });

		// Act
		var result = sut.Press(line, line.Length);

		// Assert
		_ = result.Line.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <c>~</c> and part of a user name completes to <c>~name/</c>, ready to go on into the home folder.
	/// </summary>
	[Fact]
	public void Press_OnTildeUser_CompletesUserName()
	{
		// Windows has no ~user.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var result = this.sut.Press("ls ~roo", 7);

		// Assert
		_ = result.Line.Should().Be("ls ~root/");
	}

	/// <summary>
	/// Checks that file names with spaces and quotes come back quoted the way the word was typed: backslashes when
	/// unquoted, and inside the quote that's open otherwise, closed once the name is finished.
	/// </summary>
	/// <param name="line">The text typed before Tab.</param>
	/// <param name="expected">The line after Tab.</param>
	[Theory]
	[InlineData("cat my", "cat my\\ file.txt ")]
	[InlineData("cat my\\ f", "cat my\\ file.txt ")]
	[InlineData("cat \"my f", "cat \"my file.txt\" ")]
	[InlineData("cat 'my f", "cat 'my file.txt' ")]
	[InlineData("cat 'it", "cat 'it'\\''s.txt' ")]
	[InlineData("cat it", "cat it\\'s.txt ")]
	public void Press_OnNameNeedingQuotes_QuotesItAsTyped(string line, string expected)
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(folder, "my file.txt", executable: false);
		_ = ShellEnvironment.CreateFile(folder, "it's.txt", executable: false);
		Directory.SetCurrentDirectory(folder);

		// Act
		var result = this.sut.Press(line, line.Length);

		// Assert
		_ = result.Line.Should().Be(expected);
	}

	/// <summary>
	/// Checks that Tab in the middle of the line completes the word ending at the cursor, keeps the text after it,
	/// leaves the cursor after the completed word, and adds no space there, as readline does.
	/// </summary>
	[Fact]
	public void Press_MidLine_CompletesWordAtCursorAndKeepsRest()
	{
		// Act
		var result = this.sut.Press("ech hello", 3);

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult("echo hello", 4, null, Bell: false));
	}

	/// <summary>
	/// Checks that cycling through matches in the middle of the line swaps only the word at the cursor.
	/// </summary>
	[Fact]
	public void Press_CyclingMidLine_KeepsRest()
	{
		// Act
		var presses = Enumerable.Range(0, 3).Select(_ => this.sut.Press("mytool x", 6)).ToList();

		// Assert
		_ = presses[2].Should().Be(new TabCompleter.TabResult("mytool1 x", 7, presses[1].Listed, Bell: false, 0));
	}

	/// <summary>
	/// Checks that a word after <c>=</c> or <c>:</c> completes on its own, since bash's <c>COMP_WORDBREAKS</c> ends a
	/// word there, and that a <c>COMP_WORDBREAKS</c> without <c>=</c> keeps the word whole.
	/// </summary>
	/// <param name="breaks">The <c>COMP_WORDBREAKS</c> value, or <see langword="null" /> for bash's default.</param>
	/// <param name="expected">The line after Tab.</param>
	[Theory]
	[InlineData(null, "cat --file=sample.txt ")]
	[InlineData(" ", "cat --file=sam")]
	public void Press_AfterWordBreak_CompletesTheRest(string? breaks, string expected)
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(folder, "sample.txt", executable: false);
		Directory.SetCurrentDirectory(folder);
		var variables = breaks is null ? [] : new Dictionary<string, string> { ["COMP_WORDBREAKS"] = breaks };
		var sut = new TabCompleter(["cat"], variables: variables);

		// Act
		var result = sut.Press("cat --file=sam", 14);

		// Assert
		_ = result.Line.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a <c>complete -C</c> script gets <c>COMP_POINT</c> at the cursor rather than at the end of the line.
	/// </summary>
	[Fact]
	public void Press_CompleterMidLine_GetsCursorAsPoint()
	{
		// Windows can't run a #! script.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true, "#!/bin/sh\necho \"p$COMP_POINT\"\n");
		var sut = WithCompleters(["echo"], ("sampletool", script));

		// Act
		var result = sut.Press("sampletool  tail", 11);

		// Assert
		_ = result.Line.Should().Be("sampletool p11 tail");
	}

	/// <summary>
	/// Checks the quoting of names with characters the shell treats specially: <c>"</c> and <c>$</c> get a backslash
	/// inside double quotes, and a leading <c>~</c> or <c>#</c> gets one only unquoted at the start of the word.
	/// </summary>
	/// <param name="line">The text typed before Tab.</param>
	/// <param name="expected">The line after Tab.</param>
	[Theory]
	[InlineData("cat \"a", "cat \"a\\$b\\\"c\" ")]
	[InlineData("cat \\~", "cat \\~tilde ")]
	[InlineData("cat \"~", "cat \"~tilde\" ")]
	[InlineData("cat #", "cat \\#hash ")]
	[InlineData("cat x", "cat x~y ")]
	public void Press_OnSpecialName_QuotesOnlyWhatNeedsIt(string line, string expected)
	{
		// Windows doesn't allow " in a file name.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Arrange
		var folder = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(folder, "a$b\"c", executable: false);
		_ = ShellEnvironment.CreateFile(folder, "~tilde", executable: false);
		_ = ShellEnvironment.CreateFile(folder, "#hash", executable: false);
		_ = ShellEnvironment.CreateFile(folder, "x~y", executable: false);
		Directory.SetCurrentDirectory(folder);

		// Act
		var result = this.sut.Press(line, line.Length);

		// Assert
		_ = result.Line.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a folder typed with backslashes is kept as typed while only the new name is quoted.
	/// </summary>
	[Fact]
	public void Press_InEscapedFolder_KeepsFolderAsTyped()
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(Directory.CreateDirectory(Path.Combine(folder, "my dir")).FullName, "my file", executable: false);
		Directory.SetCurrentDirectory(folder);

		// Act
		var result = this.sut.Press("cat my\\ dir/my", 14);

		// Assert
		_ = result.Line.Should().Be("cat my\\ dir/my\\ file ");
	}

	/// <summary>
	/// Checks that a folder Tab can't look in has no matches: one the user can't read, an unknown <c>~user</c>, and an
	/// unclosed <c>${</c>.
	/// </summary>
	/// <param name="line">The text typed before Tab.</param>
	[Theory]
	[InlineData("ls /root/")]
	[InlineData("cat ~nosuchsampleuser/u")]
	[InlineData("cat ${SAMPLEDIR/sam")]
	public void Press_InFolderThatCantBeRead_RingsBell(string line)
	{
		// Windows has no /root, and runs as root would read it.
		if (OperatingSystem.IsWindows() || Environment.UserName == "root")
		{
			return;
		}

		// Arrange
		var folder = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(folder, "sample.txt", executable: false);
		var sut = new TabCompleter(["cat"], variables: new Dictionary<string, string> { ["SAMPLEDIR"] = folder });

		// Act
		var result = sut.Press(line, line.Length);

		// Assert
		_ = result.Should().Be(new TabCompleter.TabResult(line, line.Length, null, Bell: true));
	}

	/// <summary>
	/// Checks that <c>~name/</c> looks in that user's own home folder, not in <c>HOME</c>.
	/// </summary>
	[Fact]
	public void Press_InOwnTildeUserFolder_LooksInUsersHome()
	{
		// Windows has no ~user.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Arrange
		var folder = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(folder, "sharpie-sample-only-here.txt", executable: false);
		var sut = new TabCompleter(["cat"], variables: new Dictionary<string, string> { ["HOME"] = folder });
		var line = $"cat ~{Environment.UserName}/sharpie-sample-only-h";

		// Act
		var result = sut.Press(line, line.Length);

		// Assert
		_ = result.Bell.Should().BeTrue();
	}

	/// <summary>
	/// Checks that environment variables complete too, and several names list in order.
	/// </summary>
	[Fact]
	public void Press_OnVariableName_FindsEnvironmentAndListsInOrder()
	{
		// Arrange
		Environment.SetEnvironmentVariable("OLDPWD", this.environment.CreateFolder());
		var sut = new TabCompleter(["echo"], variables: new Dictionary<string, string> { ["SAMPLEB"] = "x", ["SAMPLEA"] = "x" });

		// Act
		var single = sut.Press("echo $OLDP", 10);
		_ = sut.Press("echo $SAMPLE", 12);
		var listed = sut.Press("echo $SAMPLE", 12);

		// Assert
		_ = single.Line.Should().Be("echo $OLDPWD/");
		_ = listed.Listed.Should().Equal("$SAMPLEA", "$SAMPLEB");
	}

	/// <summary>
	/// Checks that user names list in order.
	/// </summary>
	[Fact]
	public void Press_OnTildeTwice_ListsUsersInOrder()
	{
		// Windows has no ~user.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		_ = this.sut.Press("ls ~", 4);
		var listed = this.sut.Press("ls ~", 4);

		// Assert
		_ = listed.Listed.Should().NotBeNullOrEmpty().And.BeInAscendingOrder(StringComparer.Ordinal);
	}

	/// <summary>
	/// Checks that the command word itself is completed as a command even when it has a <c>complete -C</c> script.
	/// </summary>
	[Fact]
	public void Press_OnCommandWordWithCompleter_DoesNotRunScript()
	{
		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true, "#!/bin/sh\necho run\n");
		var sut = WithCompleters(["echo"], ("sampletool", script));

		// Act
		var result = sut.Press("sampletool", 10);

		// Assert
		_ = result.Line.Should().Be("sampletool");
	}

	/// <summary>
	/// Checks that cycling through a completer script's matches puts each one in as the script wrote it, unquoted.
	/// </summary>
	[Fact]
	public void Press_CyclingCompleterMatches_InsertsThemAsWritten()
	{
		// Windows can't run a #! script.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Arrange
		var script = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "completer", executable: true, "#!/bin/sh\necho 'a b'\necho 'c d'\n");
		var sut = WithCompleters(["echo"], ("sampletool", script));

		// Act
		var presses = Enumerable.Range(0, 3).Select(_ => sut.Press("sampletool ", 11)).ToList();

		// Assert
		_ = presses[2].Line.Should().Be("sampletool a b");
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Creates a completer with a <c>complete -C</c> program set for each command, as <c>complete</c> would set it.
	/// </summary>
	/// <param name="names">The builtin names.</param>
	/// <param name="completers">Each command and its program; an empty command sets the program for every command, as <c>-D</c> does.</param>
	/// <returns>
	/// The completer.
	/// </returns>
	private static TabCompleter WithCompleters(string[] names, params (string Command, string Program)[] completers)
	{
		var declare = new DeclareCommand(new ShellSettings());
		var engine = new CompletionEngine(declare, new Dictionary<string, ICommand>(), new Dictionary<string, string>(), _ => 0);

		foreach (var (command, program) in completers)
		{
			if (command.Length == 0)
			{
				engine.Default = new CompletionSpec { Command = program };
			}
			else
			{
				engine.Set(command, new CompletionSpec { Command = program });
			}
		}

		return new TabCompleter(names, engine, declare.Variables);
	}
}
