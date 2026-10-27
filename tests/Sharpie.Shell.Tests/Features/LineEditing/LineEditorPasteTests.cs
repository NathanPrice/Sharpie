using FluentAssertions;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Shell.Tests.Features.LineEditing;

/// <summary>
/// Tests for pasting into the <see cref="LineEditor" /> while the terminal marks pasted text with
/// <c>ESC [200~</c> and <c>ESC [201~</c>, and for drawing the line breaks a paste leaves in the line.
/// </summary>
public sealed partial class LineEditorTests
{
	/// <summary>
	/// Checks that the line breaks in a paste go into the line instead of running each pasted line, and Enter runs the
	/// whole block.
	/// </summary>
	[Fact]
	public void ReadLine_PastedLines_WaitForEnter()
	{
		// Arrange
		this.Paste("echo one\recho two");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("echo one\necho two");
		_ = this.keys.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a paste's line breaks all become <c>\n</c>, tabs stay, and other control characters are left out,
	/// since they would act on the terminal when drawn.
	/// </summary>
	/// <param name="pasted">The pasted text.</param>
	/// <param name="expected">The line Enter returns.</param>
	[Theory]
	[InlineData("a\r\nb\nc\rd", "a\nb\nc\nd")]
	[InlineData("a\tb", "a\tb")]
	[InlineData("a\u0001b\u007fc\ed", "abcd")]
	public void ReadLine_Paste_KeepsOnlyTextAndLineBreaks(string pasted, string expected)
	{
		// Arrange
		this.Paste(pasted);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be(expected);
	}

	/// <summary>
	/// Checks that one Ctrl+_ takes back a whole paste.
	/// </summary>
	[Fact]
	public void ReadLine_CtrlUnderscoreAfterPaste_RemovesPaste()
	{
		// Arrange
		this.Type("ab");
		this.Paste("cd\ref");
		this.Press(ConsoleKey.D7, control: true);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("ab");
	}

	/// <summary>
	/// Checks that pasting during a Ctrl+R search ends the search and puts the text into the line, without the
	/// markers' characters.
	/// </summary>
	[Fact]
	public void ReadLine_PasteDuringSearch_GoesIntoLine()
	{
		// Arrange
		this.Script("{^R}");
		this.Paste("echo x");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("echo x");
	}

	/// <summary>
	/// Checks that a paste is still found when .NET returns the marker's ESC and <c>[</c> as one Alt+[ key, as it does
	/// when they are the only two bytes of a read.
	/// </summary>
	[Fact]
	public void ReadLine_PasteStartingWithAltBracket_IsPasted()
	{
		// Arrange
		this.keys.Enqueue(new ConsoleKeyInfo('[', ConsoleKey.Oem4, shift: false, alt: true, control: false));
		this.Type("200~echo x\e[201~");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("echo x");
	}

	/// <summary>
	/// Checks that an escape sequence .NET doesn't know, like a focus report, is dropped instead of typed into the line.
	/// </summary>
	/// <param name="typed">The keys, with the sequence in the middle.</param>
	[Theory]
	[InlineData("ab\e[Ic")]
	[InlineData("ab\e[12;3Xc")]
	[InlineData("ab\e[?1hc")]
	[InlineData("ab\e[2 qc")]
	[InlineData("ab\e[200Xc")]
	public void ReadLine_UnknownSequence_IsDropped(string typed)
	{
		// Arrange
		this.Type(typed);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("abc");
	}

	/// <summary>
	/// Checks that <c>[</c> typed without Escape before it stays in the line, as at the start of <c>[[ ... ]]</c>.
	/// </summary>
	[Fact]
	public void ReadLine_BracketWithoutEscape_IsTyped()
	{
		// Arrange
		this.Type("[[ a ]]");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("[[ a ]]");
	}

	/// <summary>
	/// Checks that an empty paste changes nothing and the keys after it are typed as usual.
	/// </summary>
	[Fact]
	public void ReadLine_EmptyPaste_LeavesLine()
	{
		// Arrange
		this.Type("ab");
		this.Paste(string.Empty);
		this.Type("c");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("abc");
	}

	/// <summary>
	/// Checks that the paste still ends when .NET returns the end marker's ESC and <c>[</c> as one Alt+[ key.
	/// </summary>
	[Fact]
	public void ReadLine_PasteEndingWithAltBracket_EndsPaste()
	{
		// Arrange
		this.Type("\e[200~echo x");
		this.keys.Enqueue(new ConsoleKeyInfo('[', ConsoleKey.Oem4, shift: false, alt: true, control: false));
		this.Type("201~!");
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("echo x!");
	}

	/// <summary>
	/// Checks that a line holding a line break is drawn with a carriage return before it, and that the next redraw
	/// moves back up over every row the line takes: one for a row that's exactly full, two for one that wraps.
	/// </summary>
	/// <param name="first">The first pasted line, after the 2-column prompt on a 10-column terminal.</param>
	/// <param name="rowsUp">How many rows the line takes above the last one.</param>
	[Theory]
	[InlineData("ab", 1)]
	[InlineData("abcdefgh", 1)]
	[InlineData("abcdefghijk", 2)]
	public void ReadLine_PastedLineBreak_DrawsAndCountsRows(string first, int rowsUp)
	{
		// Arrange
		this.columns = 10;
		this.Paste($"{first}\rx");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(this.reads.Count - 1).Should().Be($"\e[{rowsUp}A\r\e[J{Prompt}\e[31m{first}\e[0m\r\n\e[31mx\e[0m\r\e[1C\r\n");
	}

	/// <summary>
	/// Checks that with the cursor on the first of two lines, the redraw moves up from the end to the cursor's row, and
	/// a pasted tab is drawn as one space, the column the cursor maths counts it as.
	/// </summary>
	[Fact]
	public void ReadLine_HomeAfterPastedLines_MovesUpToCursor()
	{
		// Arrange
		this.Paste("echo\tx\rab");
		this.Press(ConsoleKey.Home);
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(this.reads.Count - 2).Should().Be($"\e[1A\r\e[J{Prompt}\e[32mecho\e[0m x\r\n\e[31mab\e[0m\e[1A\r\e[2C");
	}

	/// <summary>
	/// Queues <paramref name="text" /> between the paste markers, a key per character, as
	/// <see cref="Console.ReadKey(bool)" /> returns a paste.
	/// </summary>
	/// <param name="text">The pasted text, with line breaks as the terminal sends them.</param>
	private void Paste(string text) => this.Type($"\e[200~{text}\e[201~");
}
