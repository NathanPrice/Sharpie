using FluentAssertions;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Shell.Tests.Features.LineEditing;

/// <summary>
/// Tests for the <see cref="LineEditor" /> keys that reach past the line being edited: the kill buffer, Alt+. and
/// Ctrl+X Ctrl+E.
/// </summary>
public sealed partial class LineEditorTests
{
	/// <summary>
	/// Checks that what Ctrl+U cut on one line is still there for Ctrl+Y on the next, as readline's kill ring is.
	/// </summary>
	[Fact]
	public void ReadLine_CtrlYOnNextLine_PastesLastKill()
	{
		// Arrange
		this.Script("one two{^U}");
		this.Press(ConsoleKey.Enter);
		_ = this.sut.ReadLine(string.Empty);
		this.Press(ConsoleKey.Y, control: true);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("one two");
	}

	/// <summary>
	/// Checks that Ctrl+_ on a new line doesn't bring back a change made to the line before.
	/// </summary>
	[Fact]
	public void ReadLine_CtrlUnderscoreOnNextLine_DoesNothing()
	{
		// Arrange
		this.Script("ab{^U}");
		this.Press(ConsoleKey.Enter);
		_ = this.sut.ReadLine(string.Empty);
		this.Press(ConsoleKey.D7, control: true);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that Alt+. puts nothing in when there's no history.
	/// </summary>
	[Fact]
	public void ReadLine_AltPeriodWithNoHistory_DoesNothing()
	{
		// Arrange
		this.history.Clear();
		this.Script("{%OemPeriod}x");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("x");
	}

	/// <summary>
	/// Checks that a second Alt+. skips a history line with no words and takes the last word of the one before it,
	/// or keeps the word it has when no older line has any.
	/// </summary>
	/// <param name="oldest">The oldest history line, before a line of spaces and <c>second</c>.</param>
	/// <param name="expected">The line Enter returns.</param>
	[Theory]
	[InlineData("first", "first")]
	[InlineData("", "second")]
	public void ReadLine_AltPeriodTwice_SkipsLinesWithNoWords(string oldest, string expected)
	{
		// Arrange
		this.history[0] = oldest;
		this.history.Insert(1, "  ");
		this.Script("{%OemPeriod}{%OemPeriod}");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be(expected);
	}

	/// <summary>
	/// Checks that Alt+. takes the last word as typed, quotes and all, counts an operator as a word, and that a second
	/// Alt+. swaps out the whole word the first one put in.
	/// </summary>
	/// <param name="newest">The newest history line.</param>
	/// <param name="script">The keys typed.</param>
	/// <param name="expected">The line Enter returns.</param>
	[Theory]
	[InlineData("echo 'a b' \"$HOME\"", "{%OemPeriod}", "\"$HOME\"")]
	[InlineData("sampletool > out", "{%OemPeriod}", "out")]
	[InlineData("sampletool &", "{%OemPeriod}", "&")]
	[InlineData("sampletool one", "{%OemPeriod}{%OemPeriod}", "second")]
	public void ReadLine_AltPeriod_TakesRawLastWord(string newest, string script, string expected)
	{
		// Arrange
		this.history.Add(newest);
		this.Script(script);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be(expected);
	}

	/// <summary>
	/// Checks that Ctrl+X Ctrl+E hands the line to the editor and runs what comes back right away, without the line
	/// breaks the editor added at the end and with CRLF turned into LF.
	/// </summary>
	[Fact]
	public void ReadLine_CtrlXCtrlE_SubmitsEditedText()
	{
		// Arrange
		var edited = new List<string>();
		var sut = this.EditorWith(line =>
		{
			edited.Add(line);

			return "echo one\r\necho two\r\n\n";
		});
		this.Script("ab{^X}{^E}");

		// Act
		var line = sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("echo one\necho two");
		_ = edited.Should().Equal("ab");
	}

	/// <summary>
	/// Checks that when the editor fails, the line is left as it was for more editing.
	/// </summary>
	[Fact]
	public void ReadLine_CtrlXCtrlEEditorFails_KeepsLine()
	{
		// Arrange
		var sut = this.EditorWith(_ => null);
		this.Script("ab{^X}{^E}c");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("abc");
	}

	/// <summary>
	/// Checks that the editor isn't opened when Ctrl+E doesn't come right after Ctrl+X, or on a line of a
	/// here-document's body.
	/// </summary>
	/// <param name="script">The keys typed.</param>
	/// <param name="continuation">Whether the line is a here-document's body line.</param>
	/// <param name="expected">The line Enter returns.</param>
	[Theory]
	[InlineData("ab{^X}c{^E}", false, "ab")]
	[InlineData("ab{^X}{^X}{^E}", false, "ab")]
	[InlineData("ab{^X}{^E}", true, "ab")]
	public void ReadLine_CtrlEWithoutCtrlXFirst_DoesNotOpenEditor(string script, bool continuation, string expected)
	{
		// Arrange
		var opened = false;
		var sut = this.EditorWith(_ =>
		{
			opened = true;

			return "sampletool";
		});
		this.Script(script);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = sut.ReadLine(string.Empty, continuation);

		// Assert
		_ = line.Should().Be(expected);
		_ = opened.Should().BeFalse();
	}

	/// <summary>
	/// Makes an editor like <see cref="sut" /> whose Ctrl+X Ctrl+E calls <paramref name="editInEditor" />.
	/// </summary>
	/// <param name="editInEditor">Stands in for the user's editor.</param>
	/// <returns>
	/// The editor, reading keys from <see cref="keys" /> and writing to <see cref="output" />.
	/// </returns>
	private LineEditor EditorWith(Func<string, string?> editInEditor) =>
		new(this.ReadKey, this.output, () => this.columns, new TabCompleter(["echo", "exit"]), this.history, ["echo", "exit"], editInEditor);
}
