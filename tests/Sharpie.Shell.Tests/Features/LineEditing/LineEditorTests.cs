using FluentAssertions;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Shell.Tests.Features.LineEditing;

/// <summary>
/// Tests for <see cref="LineEditor" />, fed keys the way <see cref="Console.ReadKey(bool)" /> returns them.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class LineEditorTests : IDisposable
{
	private const string Prompt = PromptHeader.Symbol;

	private readonly Queue<ConsoleKeyInfo> keys = new();
	private readonly StringWriter output = new();
	private readonly ShellEnvironment environment = new();
	private readonly List<string> history = ["first", "second"];
	private readonly List<int> reads = [];
	private readonly LineEditor sut;
	private int columns = 80;

	/// <summary>
	/// Initializes a new instance of the <see cref="LineEditorTests" /> class with two history lines, an 80-column
	/// terminal, and <c>PATH</c> and the working directory pointed at a folder holding only <c>xfile</c>, so the
	/// programs on this machine can't add a suggestion.
	/// </summary>
	public LineEditorTests()
	{
		var folder = this.environment.CreateFolder();

		// Also the working directory, so the file a test creates there is the only one a later word could match.
		ShellEnvironment.SetPath(folder);
		Directory.SetCurrentDirectory(folder);
		_ = ShellEnvironment.CreateFile(folder, "xfile", executable: false);
		this.sut = new LineEditor(this.ReadKey, this.output, () => this.columns, new TabCompleter(["echo", "exit"]), this.history, ["echo", "exit"]);
	}

	/// <summary>
	/// Checks that typed characters come back as the line when Enter is pressed.
	/// </summary>
	[Fact]
	public void ReadLine_TypedTextThenEnter_ReturnsLine()
	{
		// Arrange
		this.Type("echo hi");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine("header\r\n");

		// Assert
		_ = line.Should().Be("echo hi");
		_ = this.output.ToString().Should().StartWith("header\r\n").And.EndWith("\r\n");
	}

	/// <summary>
	/// Checks that a command that only exists once the first line is done, like a new alias, is colored as a command
	/// on the next line rather than keeping the first line's answer.
	/// </summary>
	[Fact]
	public void ReadLine_CommandAddedBetweenLines_IsColoredOnNextLine()
	{
		// Arrange
		List<string> builtins = ["echo"];
		var editor = new LineEditor(this.ReadKey, this.output, () => this.columns, new TabCompleter(builtins), this.history, builtins);
		this.Type("sampletool");
		this.Press(ConsoleKey.Enter);
		_ = editor.ReadLine(string.Empty);
		var firstLine = this.output.ToString();
		builtins.Add("sampletool");
		this.Type("sampletool");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = editor.ReadLine(string.Empty);

		// Assert
		_ = firstLine.Should().EndWith("\e[31msampletool\e[0m\r\e[12C\r\n");
		_ = this.output.ToString()[firstLine.Length..].Should().EndWith("\e[32msampletool\e[0m\r\e[12C\r\n");
	}

	/// <summary>
	/// Checks the editing keys: moving with the arrows, deleting with Backspace and Delete, and inserting mid-line.
	/// </summary>
	[Fact]
	public void ReadLine_EditingKeys_ChangeLineAtCursor()
	{
		// Arrange
		this.Type("ecxo");
		this.Press(ConsoleKey.LeftArrow);
		this.Press(ConsoleKey.Backspace);
		this.Type("h");
		this.Press(ConsoleKey.Home);
		this.Press(ConsoleKey.Delete);
		this.Type("E");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("Echo");
	}

	/// <summary>
	/// Checks that with nothing typed, Up steps through every history line and stops at the oldest, and Down steps back.
	/// </summary>
	[Fact]
	public void ReadLine_UpAndDown_RecallHistory()
	{
		// Arrange
		this.Press(ConsoleKey.UpArrow);
		this.Press(ConsoleKey.UpArrow);
		this.Press(ConsoleKey.UpArrow);
		this.Press(ConsoleKey.DownArrow);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("second");
	}

	/// <summary>
	/// Checks that Up and Down after typing skip history lines that don't start with the typed text, and Up stops at
	/// the oldest one that does.
	/// </summary>
	[Fact]
	public void ReadLine_UpWithTypedText_SkipsLinesThatDontMatch()
	{
		// Arrange
		this.history.Add("first again");
		this.Type("fi");
		this.Press(ConsoleKey.UpArrow);
		this.Press(ConsoleKey.UpArrow);
		this.Press(ConsoleKey.UpArrow);
		this.Press(ConsoleKey.DownArrow);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("first again");
	}

	/// <summary>
	/// Checks that Down past the newest matching history line brings back what was typed.
	/// </summary>
	[Fact]
	public void ReadLine_DownPastNewestMatch_BringsBackTypedText()
	{
		// Arrange
		this.history.Add("first again");
		this.Type("fi");
		this.Press(ConsoleKey.UpArrow);
		this.Press(ConsoleKey.DownArrow);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("fi");
	}

	/// <summary>
	/// Checks that Ctrl+C drops the line and shows <c>^C</c>, as in bash.
	/// </summary>
	[Fact]
	public void ReadLine_CtrlC_ReturnsEmptyLineAndShowsMarker()
	{
		// Arrange
		this.Type("rm -rf");
		this.Press(ConsoleKey.C, control: true);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().BeEmpty();
		_ = this.output.ToString().Should().EndWith("^C\r\n");
	}

	/// <summary>
	/// Checks that a here-document's body line is drawn after <c>&gt; </c> with no coloring or suggestion, even when
	/// it starts like a command or a history line.
	/// </summary>
	[Fact]
	public void ReadLine_Continuation_DrawsPlainLineAfterPrompt()
	{
		// Arrange
		this.Type("echo fi");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty, continuation: true);

		// Assert
		_ = line.Should().Be("echo fi");
		_ = this.output.ToString().Should().EndWith("\r\e[J> echo fi\r\e[9C\r\n").And.NotContainAny("\e[32m", "\e[30m", "\e[0m");
	}

	/// <summary>
	/// Checks that Ctrl+C while reading a here-document's body gives no line, so the whole command is dropped.
	/// </summary>
	[Fact]
	public void ReadLine_CtrlCInContinuation_ReturnsNull()
	{
		// Arrange
		this.Type("half a line");
		this.Press(ConsoleKey.C, control: true);

		// Act
		var line = this.sut.ReadLine(string.Empty, continuation: true);

		// Assert
		_ = line.Should().BeNull();
		_ = this.output.ToString().Should().EndWith("^C\r\n");
	}

	/// <summary>
	/// Checks that Ctrl+D on an empty line ends input, and on a non-empty line deletes instead.
	/// </summary>
	[Fact]
	public void ReadLine_CtrlDOnEmptyLine_ReturnsNull()
	{
		// Arrange
		this.Press(ConsoleKey.D, control: true);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().BeNull();
		_ = this.output.ToString().Should().EndWith($"{PromptHeader.Symbol}\r\e[2C\r\n");
	}

	/// <summary>
	/// Checks that Ctrl+W removes the word before the cursor and Ctrl+U the rest of the line before it.
	/// </summary>
	[Fact]
	public void ReadLine_CtrlWAndCtrlU_DeleteBackwards()
	{
		// Arrange
		this.Type("echo one two");
		this.Press(ConsoleKey.W, control: true);
		this.Type("three");
		this.Press(ConsoleKey.U, control: true);
		this.Type("pwd");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("pwd");
	}

	/// <summary>
	/// Checks that Tab completes a builtin name, colored as a command.
	/// </summary>
	[Fact]
	public void ReadLine_Tab_CompletesAndColorsCommand()
	{
		// Arrange
		this.Type("ec");
		this.Press(ConsoleKey.Tab);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("echo ");
		_ = this.output.ToString().Should().Contain("\e[32mecho\e[0m");
	}

	/// <summary>
	/// Checks that a line longer than the terminal wraps, and the redraw moves back up to the prompt's row first.
	/// </summary>
	[Fact]
	public void ReadLine_LineWiderThanTerminal_RedrawsFromPromptRow()
	{
		// Arrange
		this.Type(new string('x', 100));
		this.Type("y");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.output.ToString().Should().Contain("\e[1A\r\e[J");
	}

	/// <summary>
	/// Checks that typing the start of a history line shows the rest of it dimmed after the cursor.
	/// </summary>
	[Fact]
	public void ReadLine_PrefixOfHistory_ShowsDimSuggestion()
	{
		// Arrange
		this.Type("fi");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(1).Should().Be($"\r\e[J{PromptHeader.Symbol}\e[31mfi\e[0m\e[30mrst\e[0m\r\e[4C");
	}

	/// <summary>
	/// Checks that Right, End and Ctrl+E at the end of the line take the suggestion into it.
	/// </summary>
	/// <param name="key">The key that accepts.</param>
	/// <param name="control">Whether Ctrl is held.</param>
	[Theory]
	[InlineData(ConsoleKey.RightArrow, false)]
	[InlineData(ConsoleKey.End, false)]
	[InlineData(ConsoleKey.E, true)]
	public void ReadLine_AcceptKeyAtEnd_TakesSuggestion(ConsoleKey key, bool control)
	{
		// Arrange
		this.Type("fi");
		this.Press(key, control);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("first");
	}

	/// <summary>
	/// Checks that Right in the middle of the line only moves the cursor, even when a history line matches.
	/// </summary>
	[Fact]
	public void ReadLine_RightArrowMidLine_MovesWithoutAccepting()
	{
		// Arrange
		this.Type("fi");
		this.Press(ConsoleKey.LeftArrow);
		this.Press(ConsoleKey.RightArrow);
		this.Type("x");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("fix");
	}

	/// <summary>
	/// Checks that Enter runs only what was typed and draws the finished line without the suggestion.
	/// </summary>
	[Fact]
	public void ReadLine_EnterWithoutAccepting_ReturnsTypedTextOnly()
	{
		// Arrange
		this.Type("fi");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("fi");
		_ = this.Frame(2).Should().Be($"\r\e[J{PromptHeader.Symbol}\e[31mfi\e[0m\r\e[4C\r\n");
	}

	/// <summary>
	/// Checks that when several history lines match, the newest one is suggested.
	/// </summary>
	[Fact]
	public void ReadLine_SeveralMatches_SuggestsNewest()
	{
		// Arrange
		this.history.Add("first again");
		this.Type("f");
		this.Press(ConsoleKey.RightArrow);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("first again");
	}

	/// <summary>
	/// Checks that editing a line Up brought back makes the next Up search by the edited text instead of carrying on
	/// from where it was.
	/// </summary>
	[Fact]
	public void ReadLine_UpAfterEditingRecalledLine_SearchesEditedText()
	{
		// Arrange
		this.history.Add("sixth");
		this.Press(ConsoleKey.UpArrow);
		this.Press(ConsoleKey.U, control: true);
		this.Type("f");
		this.Press(ConsoleKey.UpArrow);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("first");
	}

	/// <summary>
	/// Checks that no suggestion shows while Up is stepping through history, so Right can't take a line the next Up
	/// wouldn't give.
	/// </summary>
	[Fact]
	public void ReadLine_WhileBrowsingHistory_HidesSuggestion()
	{
		// Arrange
		this.history.Clear();
		this.history.AddRange(["first again", "second", "first"]);
		this.Type("f");
		this.Press(ConsoleKey.UpArrow);
		this.Press(ConsoleKey.RightArrow);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("first");
	}

	/// <summary>
	/// Checks that Enter on an empty line draws the prompt once on start and once more when done, then ends the line.
	/// </summary>
	[Fact]
	public void ReadLine_EnterOnEmptyLine_DrawsPromptThenEndsLine()
	{
		// Arrange
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine("h\r\n");

		// Assert
		_ = this.output.ToString().Should().Be($"h\r\n\r\e[J{Prompt}\r\e[2C\r\e[J{Prompt}\r\e[2C\r\n");
	}

	/// <summary>
	/// Checks that a line that exactly fills its row prints a space to make the terminal wrap, so the next redraw can
	/// count on the cursor being on the next row.
	/// </summary>
	[Fact]
	public void ReadLine_LineFillsRow_ForcesWrap()
	{
		// Arrange
		this.columns = 10;
		this.Type("abcdefgh");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(7).Should().Be($"\r\e[J{Prompt}\e[31mabcdefgh\e[0m \r\r");
		_ = this.Frame(8).Should().Be($"\e[1A\r\e[J{Prompt}\e[31mabcdefgh\e[0m \r\r\r\n");
	}

	/// <summary>
	/// Checks that with the cursor on the first row of a wrapped line, the redraw moves up from the line's end to the
	/// cursor's row and column.
	/// </summary>
	[Fact]
	public void ReadLine_HomeOnWrappedLine_MovesUpToCursor()
	{
		// Arrange
		this.columns = 10;
		this.Type("abcdefghij");
		this.Press(ConsoleKey.Home);
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(10).Should().Be($"\e[1A\r\e[J{Prompt}\e[31mabcdefghij\e[0m\e[1A\r\e[2C");
		_ = this.Frame(11).Should().Be($"\r\e[J{Prompt}\e[31mabcdefghij\e[0m\r\e[2C\r\n");
	}

	/// <summary>
	/// Checks that a terminal that reports no width is drawn as 80 columns wide.
	/// </summary>
	[Fact]
	public void ReadLine_NoTerminalWidth_Uses80Columns()
	{
		// Arrange
		this.columns = 0;
		this.Type("abcdefgh");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(7).Should().Be($"\r\e[J{Prompt}\e[31mabcdefgh\e[0m\r\e[10C");
	}

	/// <summary>
	/// Checks the colors: unknown commands red, quoted text yellow, redirects cyan and missing paths red and
	/// underlined, with the text between them left plain.
	/// </summary>
	[Fact]
	public void ReadLine_MixedLine_ColorsEachPart()
	{
		// Arrange
		this.Type("xx  \"q\" > /nonexistent/f");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.output.ToString().Should().EndWith($"\r\e[J{Prompt}\e[31mxx\e[0m  \e[33m\"q\"\e[0m \e[36m>\e[0m \e[31;4m/nonexistent/f\e[0m\r\e[26C\r\n");
	}

	/// <summary>
	/// Checks that the first Tab with several matches rings the bell, the second lists them under the line, and the
	/// third puts the first one in and highlights it, then Enter clears the list.
	/// </summary>
	[Fact]
	public void ReadLine_TabWithSeveralMatches_RingsThenListsThenCycles()
	{
		// Arrange
		var sut = this.ListEditor();
		this.Type("zq");
		this.Press(ConsoleKey.Tab);
		this.Press(ConsoleKey.Tab);
		this.Press(ConsoleKey.Tab);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("zqa");
		_ = this.Frame(2).Should().Be($"\a\r\e[J{Prompt}\e[31mzq\e[0m\r\e[4C");
		_ = this.Frame(3).Should().Be($"\r\e[J{Prompt}\e[31mzq\e[0m\r\nzqa  zqbcd \r\e[2A\r\e[4C\r\e[4C");
		_ = this.Frame(4).Should().Be($"\r\e[J{Prompt}\e[32mzqa\e[0m\r\n\e[48;2;56;74;102mzqa\e[49m  zqbcd \r\e[2A\r\e[5C\r\e[5C");
		_ = this.Frame(5).Should().Be($"\r\e[J{Prompt}\e[32mzqa\e[0m\r\e[5C\r\n");
	}

	/// <summary>
	/// Checks that typing after a list of matches starts completion over, so the next Tab completes the new text
	/// instead of stepping through the old list.
	/// </summary>
	[Fact]
	public void ReadLine_TypingAfterList_StartsCompletionOver()
	{
		// Arrange
		var sut = this.ListEditor();
		this.Type("zq");
		this.Press(ConsoleKey.Tab);
		this.Press(ConsoleKey.Tab);
		this.Type("b");
		this.Press(ConsoleKey.Tab);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("zqbcd ");
	}

	/// <summary>
	/// Checks that Ctrl+L clears the screen, draws the header at the top and the line under it.
	/// </summary>
	[Fact]
	public void ReadLine_CtrlL_ClearsScreenAndRedraws()
	{
		// Arrange
		this.Type("ab");
		this.Press(ConsoleKey.L, control: true);
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine("head\r\n");

		// Assert
		_ = this.Frame(2).Should().Be($"\e[H\e[2Jhead\r\n\r\e[J{Prompt}\e[31mab\e[0m\r\e[4C");
	}

	/// <summary>
	/// Checks the editing keys at the edges: at the start or end of the line, across runs of spaces, and with no
	/// suggestion to take.
	/// </summary>
	/// <param name="script">
	/// The keys, with special keys in braces, Ctrl shown as <c>^</c>, like <c>{^W}</c>, and Alt as <c>%</c>, like
	/// <c>{%B}</c>.
	/// </param>
	/// <param name="expected">The line Enter returns.</param>
	[Theory]
	[InlineData("fi{LeftArrow}{LeftArrow}{RightArrow}x", "fxi")]
	[InlineData("{Backspace}ab", "ab")]
	[InlineData("abc{^W}", "")]
	[InlineData("echo one  {^W}", "echo ")]
	[InlineData("  {^W}x", "x")]
	[InlineData("abc{Home}{^D}", "bc")]
	[InlineData("abc{LeftArrow}{^K}", "ab")]
	[InlineData("abc{^A}x", "xabc")]
	[InlineData("abc{LeftArrow}{LeftArrow}{End}x", "abcx")]
	[InlineData("abc{LeftArrow}{^E}x", "abcx")]
	[InlineData("abc{LeftArrow}{Delete}", "ab")]
	[InlineData("zz{RightArrow}", "zz")]
	[InlineData("echo one two{%B}{%B}x", "echo xone two")]
	[InlineData("one  two{%B}x", "one  xtwo")]
	[InlineData("one  two{Home}{%F}x", "onex  two")]
	[InlineData("one  two{Home}{%F}{%F}x", "one  twox")]
	[InlineData("ab{%Q}", "ab")]
	[InlineData("ab{%F}x", "abx")]
	[InlineData("echo one{Escape}bx", "echo xone")]
	[InlineData("ab{Escape}{%B}x", "xab")]
	public void ReadLine_EditingKeyScript_GivesLine(string script, string expected)
	{
		// Arrange
		this.Script(script);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a history line that is exactly what was typed isn't suggested, so an older, longer one is.
	/// </summary>
	[Fact]
	public void ReadLine_HistoryLineSameAsTyped_SuggestsLongerOne()
	{
		// Arrange
		this.history.Clear();
		this.history.AddRange(["first again", "first"]);
		this.Type("first");
		this.Press(ConsoleKey.RightArrow);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("first again");
	}

	/// <summary>
	/// Checks that Up skips a history line that is exactly what was typed, and stays put at an oldest line that doesn't
	/// match.
	/// </summary>
	/// <param name="presses">How many times Up is pressed.</param>
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public void ReadLine_UpWithTypedText_SkipsSameLineAndOldestNonMatch(int presses)
	{
		// Arrange
		this.history.Clear();
		this.history.AddRange(["nope", "first", "fi"]);
		this.Type("fi");

		for (var press = 0; press < presses; press++)
		{
			this.Press(ConsoleKey.UpArrow);
		}

		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("first");
	}

	/// <summary>
	/// Checks that typing the start of a command name with no history line to match shows the rest of the name dimmed,
	/// and Right takes it.
	/// </summary>
	[Fact]
	public void ReadLine_PrefixOfCommandName_ShowsAndAcceptsSuggestion()
	{
		// Arrange
		var sut = this.CommandEditor("zqtopgrade");
		this.Type("zqtopg");
		this.Press(ConsoleKey.RightArrow);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(5).Should().Contain("\e[30mrade\e[0m");
		_ = line.Should().Be("zqtopgrade");
	}

	/// <summary>
	/// Checks that the suggestion keeps up as the name narrows and backs up, and stops once the name is whole, the
	/// names share nothing more, or a later word is being typed, even one a file name starts with.
	/// </summary>
	/// <param name="typed">The text typed.</param>
	/// <param name="expected">The dimmed text after the last key, or an empty string for none.</param>
	[Theory]
	[InlineData("zqtop", "")]
	[InlineData("zqt", "op")]
	[InlineData("zqtopm", "em")]
	[InlineData("zqtopg{Backspace}m", "em")]
	[InlineData("zqtopmem", "")]
	[InlineData("zqtopg{Backspace}", "")]
	[InlineData("zqtopg x", "")]
	[InlineData(" ", "")]
	public void ReadLine_TypingCommandName_SuggestsWhatTabWouldAdd(string typed, string expected)
	{
		// Arrange
		var sut = this.CommandEditor("zqtop", "zqtopgrade", "zqtopmem");
		this.Script(typed);
		this.Press(ConsoleKey.Enter);

		// Act
		_ = sut.ReadLine(string.Empty);

		// Assert
		var last = this.Frame(this.reads.Count - 2);
		_ = expected.Length > 0
			? last.Should().Contain($"\e[30m{expected}\e[0m")
			: last.Should().NotContain("\e[30m");
	}

	/// <summary>
	/// Checks that a history line beats a command name for the suggestion.
	/// </summary>
	[Fact]
	public void ReadLine_HistoryAndCommandMatch_SuggestsHistory()
	{
		// Arrange
		this.history.Add("zqtopgrade --yes");
		var sut = this.CommandEditor("zqtopgrade");
		this.Type("zqtopg");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(5).Should().Contain("\e[30mrade --yes\e[0m");
	}

	/// <summary>
	/// Makes an editor on an 80-column terminal whose command names are <paramref name="names" />.
	/// </summary>
	/// <param name="names">The command names.</param>
	/// <returns>
	/// The editor, reading keys from <see cref="keys" /> and writing to <see cref="output" />.
	/// </returns>
	private LineEditor CommandEditor(params string[] names) =>
		new(this.ReadKey, this.output, () => 80, new TabCompleter(names), this.history, names);

	/// <summary>
	/// Reads the next queued key, first noting how much output there is, so <see cref="Frame" /> can tell what each key drew.
	/// </summary>
	/// <returns>
	/// The key.
	/// </returns>
	private ConsoleKeyInfo ReadKey()
	{
		this.reads.Add(this.output.GetStringBuilder().Length);

		return this.keys.Dequeue();
	}

	/// <summary>
	/// Checks that a dimmed suggestion that runs onto the next row counts toward the rows to move back up.
	/// </summary>
	[Fact]
	public void ReadLine_SuggestionWrapsRow_MovesBackUpToCursor()
	{
		// Arrange
		this.columns = 10;
		this.history.Add("abcdefghijk");
		this.Type("ab");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(1).Should().Be($"\r\e[J{Prompt}\e[31mab\e[0m\e[30mcdefghijk\e[0m\e[1A\r\e[4C");
	}

	/// <summary>
	/// Checks that with the cursor on a middle row of a line three rows long, the redraw moves up only from the last
	/// row to the cursor's.
	/// </summary>
	[Fact]
	public void ReadLine_CursorOnMiddleRow_MovesUpOnlyToIt()
	{
		// Arrange
		this.columns = 10;
		this.Type(new string('x', 25));
		this.Press(ConsoleKey.Home);

		for (var step = 0; step < 10; step++)
		{
			this.Press(ConsoleKey.RightArrow);
		}

		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(35).Should().EndWith("\e[1A\r\e[2C");
	}

	/// <summary>
	/// Checks that a list of matches under a line that ends exactly at a row's end moves back to the start of the
	/// row, without a move right of zero columns.
	/// </summary>
	[Fact]
	public void ReadLine_ListAfterLineEndingAtRowEnd_MovesToRowStart()
	{
		// Arrange
		string[] names = ["zqabcdefX", "zqabcdefY"];
		var sut = new LineEditor(this.ReadKey, this.output, () => 10, new TabCompleter(names), this.history, names);
		this.Type("zqabcdef");
		this.Press(ConsoleKey.Tab);
		this.Press(ConsoleKey.Tab);
		this.Press(ConsoleKey.Enter);

		// Act
		_ = sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(9).Should().Contain("zqabcdefX  zqabcdefY").And.NotContain("\e[0C");
	}

	/// <summary>
	/// Makes an editor on a 10-column terminal that completes two names no program on <c>PATH</c> starts like, so
	/// its list of matches is the same on every machine.
	/// </summary>
	/// <returns>
	/// The editor, reading keys from <see cref="keys" /> and writing to <see cref="output" />.
	/// </returns>
	private LineEditor ListEditor()
	{
		string[] names = ["zqa", "zqbcd"];

		return new LineEditor(this.ReadKey, this.output, () => 10, new TabCompleter(names), this.history, names);
	}

	/// <summary>
	/// Queues keys written as text, with special keys in braces like <c>{Home}</c>, Ctrl keys like <c>{^W}</c> and Alt
	/// keys like <c>{%B}</c>.
	/// </summary>
	/// <param name="script">The keys.</param>
	private void Script(string script)
	{
		for (var index = 0; index < script.Length; index++)
		{
			if (script[index] != '{')
			{
				this.Type(script[index].ToString());

				continue;
			}

			var end = script.IndexOf('}', index);
			var name = script[(index + 1)..end];

			if (name.StartsWith('^'))
			{
				this.Press(Enum.Parse<ConsoleKey>(name[1..]), control: true);
			}
			else if (name.StartsWith('%'))
			{
				this.PressAlt(Enum.Parse<ConsoleKey>(name[1..]));
			}
			else
			{
				this.Press(Enum.Parse<ConsoleKey>(name));
			}

			index = end;
		}
	}

	/// <summary>
	/// Gets what the editor wrote in response to one key.
	/// </summary>
	/// <param name="key">The key's position among the keys read, or <c>-1</c> for what was written before the first one.</param>
	/// <returns>
	/// The output from reading that key up to reading the next, or to the end for the last key.
	/// </returns>
	private string Frame(int key)
	{
		var text = this.output.ToString();
		var start = key < 0 ? 0 : this.reads[key];
		var end = key + 1 < this.reads.Count ? this.reads[key + 1] : text.Length;

		return text[start..end];
	}

	/// <inheritdoc />
	public void Dispose()
	{
		this.output.Dispose();
		this.environment.Dispose();
	}

	private void Type(string text)
	{
		foreach (var character in text)
		{
			this.keys.Enqueue(new ConsoleKeyInfo(character, (ConsoleKey)char.ToUpperInvariant(character), shift: char.IsUpper(character), alt: false, control: false));
		}
	}

	private void Press(ConsoleKey key, bool control = false) =>
		this.keys.Enqueue(new ConsoleKeyInfo(control ? (char)(key - ConsoleKey.A + 1) : '\0', key, shift: false, alt: false, control));

	/// <summary>
	/// Queues a letter key with Alt held, as <see cref="Console.ReadKey(bool)" /> returns ESC followed by that letter.
	/// </summary>
	/// <param name="key">The letter key.</param>
	private void PressAlt(ConsoleKey key) =>
		this.keys.Enqueue(new ConsoleKeyInfo(char.ToLowerInvariant((char)key), key, shift: false, alt: true, control: false));
}
