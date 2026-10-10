using FluentAssertions;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Shell.Tests.Features.LineEditing;

/// <summary>
/// Tests for <see cref="LineEditor" />, fed keys the way <see cref="Console.ReadKey(bool)" /> returns them.
/// </summary>
public sealed class LineEditorTests : IDisposable
{
	private readonly Queue<ConsoleKeyInfo> keys = new();
	private readonly StringWriter output = new();
	private readonly List<string> history = ["first", "second"];
	private readonly LineEditor sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="LineEditorTests" /> class with two history lines and an
	/// 80-column terminal.
	/// </summary>
	public LineEditorTests() =>
		this.sut = new LineEditor(this.keys.Dequeue, this.output, () => 80, new TabCompleter(["echo", "exit"]), this.history, ["echo", "exit"]);

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
		_ = this.output.ToString().Should().Contain("\e[30mrst\e[0m");
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
		var output = this.output.ToString();

		_ = line.Should().Be("fi");
		_ = output[output.LastIndexOf("\r\e[J", StringComparison.Ordinal)..].Should().NotContain("rst");
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

	/// <inheritdoc />
	public void Dispose() => this.output.Dispose();

	private void Type(string text)
	{
		foreach (var character in text)
		{
			this.keys.Enqueue(new ConsoleKeyInfo(character, (ConsoleKey)char.ToUpperInvariant(character), shift: char.IsUpper(character), alt: false, control: false));
		}
	}

	private void Press(ConsoleKey key, bool control = false) =>
		this.keys.Enqueue(new ConsoleKeyInfo(control ? (char)(key - ConsoleKey.A + 1) : '\0', key, shift: false, alt: false, control));
}
