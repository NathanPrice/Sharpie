using FluentAssertions;
using Sharpie.Features.Ansi;
using Sharpie.Features.Vt;

namespace Sharpie.Tests.Features.Vt;

/// <summary>
/// Tests for <see cref="VtScreen" /> and the <see cref="VtParser" /> that feeds it.
/// </summary>
public sealed class VtScreenTests
{
	private readonly List<string> replies = [];
	private readonly VtScreen screen;

	/// <summary>
	/// Initializes a new instance of the <see cref="VtScreenTests" /> class with a 10 by 4 screen.
	/// </summary>
	public VtScreenTests() => this.screen = new VtScreen(10, 4, this.replies.Add);

	/// <summary>
	/// Checks that plain text and line breaks land on the rows they should.
	/// </summary>
	[Fact]
	public void Feed_TextWithCrLf_FillsRows()
	{
		// Act
		this.screen.Feed("one\r\ntwo");

		// Assert
		_ = this.screen.GetText().Should().Be("one\ntwo");
		_ = (this.screen.CursorRow, this.screen.CursorColumn).Should().Be((1, 3));
	}

	/// <summary>
	/// Checks that a row filled exactly doesn't wrap until the next character, and that a wrapped row joins the next
	/// one when the text is read back.
	/// </summary>
	[Fact]
	public void Feed_PastLastColumn_WrapsLazily()
	{
		// Act
		this.screen.Feed("0123456789");
		var cursorAfterFill = (this.screen.CursorRow, this.screen.CursorColumn);
		this.screen.Feed("x");

		// Assert
		_ = cursorAfterFill.Should().Be((0, 9));
		_ = this.screen.GetText().Should().Be("0123456789x");
		_ = this.screen.GetLine(0).Wrapped.Should().BeTrue();
	}

	/// <summary>
	/// Checks that rows scrolled off the top go to the scrollback, oldest first.
	/// </summary>
	[Fact]
	public void Feed_MoreRowsThanScreen_ScrollsIntoScrollback()
	{
		// Act
		this.screen.Feed("a\r\nb\r\nc\r\nd\r\ne\r\nf");

		// Assert
		_ = this.screen.ScrollbackCount.Should().Be(2);
		_ = this.screen.GetText().Should().Be("a\nb\nc\nd\ne\nf");
	}

	/// <summary>
	/// Checks that a sequence split across two chunks still takes effect.
	/// </summary>
	[Fact]
	public void Feed_SequenceSplitAcrossChunks_IsApplied()
	{
		// Act
		this.screen.Feed("ab\e[");
		this.screen.Feed("1;5H*");

		// Assert
		_ = this.screen.GetLine(0).Text().Should().Be("ab  *");
	}

	/// <summary>
	/// Checks the cursor movement sequences, counted from 1 as programs send them.
	/// </summary>
	/// <param name="sequence">The sequence, sent from row 2, column 3.</param>
	/// <param name="row">The expected row, counted from 0.</param>
	/// <param name="column">The expected column, counted from 0.</param>
	[Theory]
	[InlineData("\e[A", 0, 2)]
	[InlineData("\e[B", 2, 2)]
	[InlineData("\e[2C", 1, 4)]
	[InlineData("\e[D", 1, 1)]
	[InlineData("\e[4;7H", 3, 6)]
	[InlineData("\e[H", 0, 0)]
	[InlineData("\e[9G", 1, 8)]
	[InlineData("\e[3d", 2, 2)]
	[InlineData("\e[99;99H", 3, 9)]
	public void Feed_CursorMove_LandsOnCell(string sequence, int row, int column)
	{
		// Arrange
		this.screen.Feed("\e[2;3H");

		// Act
		this.screen.Feed(sequence);

		// Assert
		_ = (this.screen.CursorRow, this.screen.CursorColumn).Should().Be((row, column));
	}

	/// <summary>
	/// Checks the erase sequences.
	/// </summary>
	/// <param name="sequence">The erase, sent with the cursor on row 1, column 2 of a full screen.</param>
	/// <param name="expected">The screen's text afterwards.</param>
	[Theory]
	[InlineData("\e[K", "aaaaaaaaaa\nbb\ncccccccccc\ndddddddddd")]
	[InlineData("\e[1K", "aaaaaaaaaa\n   bbbbbbb\ncccccccccc\ndddddddddd")]
	[InlineData("\e[2K", "aaaaaaaaaa\n\ncccccccccc\ndddddddddd")]
	[InlineData("\e[J", "aaaaaaaaaa\nbb")]
	[InlineData("\e[1J", "\n   bbbbbbb\ncccccccccc\ndddddddddd")]
	[InlineData("\e[2J", "")]
	[InlineData("\e[3X", "aaaaaaaaaa\nbb   bbbbb\ncccccccccc\ndddddddddd")]
	public void Feed_Erase_BlanksCells(string sequence, string expected)
	{
		// Arrange
		this.screen.Feed("aaaaaaaaaa\r\nbbbbbbbbbb\r\ncccccccccc\r\ndddddddddd\e[2;3H");

		// Act
		this.screen.Feed(sequence);

		// Assert
		_ = this.screen.GetText().Should().Be(expected);
	}

	/// <summary>
	/// Checks inserting and deleting characters and rows.
	/// </summary>
	/// <param name="sequence">The edit, sent with the cursor on row 0, column 1.</param>
	/// <param name="expected">The screen's text afterwards.</param>
	[Theory]
	[InlineData("\e[2@", "a  bcdefgh\nrow2")]
	[InlineData("\e[2P", "adefghij\nrow2")]
	[InlineData("\e[L", "\nabcdefghij\nrow2")]
	[InlineData("\e[M", "row2")]
	public void Feed_InsertOrDelete_ShiftsContent(string sequence, string expected)
	{
		// Arrange
		this.screen.Feed("abcdefghij\r\nrow2\e[1;2H");

		// Act
		this.screen.Feed(sequence);

		// Assert
		_ = this.screen.GetText().Should().Be(expected);
	}

	/// <summary>
	/// Checks that a scroll region keeps the rows outside it in place and out of the scrollback.
	/// </summary>
	[Fact]
	public void Feed_LineFeedAtRegionBottom_ScrollsOnlyRegion()
	{
		// Arrange
		this.screen.Feed("top\r\nr1\r\nr2\r\nbottom\e[2;3r\e[3;1H");

		// Act
		this.screen.Feed("\nnew");

		// Assert
		_ = this.screen.GetText().Should().Be("top\nr2\nnew\nbottom");
		_ = this.screen.ScrollbackCount.Should().Be(0);
	}

	/// <summary>
	/// Checks that the alternate screen starts blank and leaving it brings back the main screen and its cursor.
	/// </summary>
	[Fact]
	public void Feed_AlternateScreen_RestoresMainOnExit()
	{
		// Arrange
		this.screen.Feed("prompt");

		// Act
		this.screen.Feed("\e[?1049h");
		var whileAlternate = this.screen.GetText();
		this.screen.Feed("full screen\e[?1049l");

		// Assert
		_ = whileAlternate.Should().BeEmpty();
		_ = this.screen.AlternateScreen.Should().BeFalse();
		_ = this.screen.GetText().Should().Be("prompt");
		_ = (this.screen.CursorRow, this.screen.CursorColumn).Should().Be((0, 6));
	}

	/// <summary>
	/// Checks that SGR codes style the cells printed after them, including underline and inverse.
	/// </summary>
	[Fact]
	public void Feed_Sgr_StylesCells()
	{
		// Act
		this.screen.Feed("\e[1;31;4;7mX\e[0mY");

		// Assert
		var cells = this.screen.GetLine(0).Cells;

		_ = this.screen.GetStyle(cells[0].Style).Should().Be(new AnsiStyle(new AnsiColor(1, 0), null, Bold: true, Underline: true, Inverse: true));
		_ = this.screen.GetStyle(cells[1].Style).Should().Be(default(AnsiStyle));
	}

	/// <summary>
	/// Checks that a wide character takes two cells and wraps whole when it doesn't fit.
	/// </summary>
	[Fact]
	public void Feed_WideCharacter_TakesTwoCells()
	{
		// Act
		this.screen.Feed("123456789日");

		// Assert
		_ = this.screen.GetLine(1).Cells[0].Width.Should().Be(2);
		_ = this.screen.GetLine(1).Cells[1].Width.Should().Be(0);
		_ = this.screen.CursorColumn.Should().Be(2);
	}

	/// <summary>
	/// Checks that the DEC line drawing set turns letters into box drawing characters.
	/// </summary>
	[Fact]
	public void Feed_LineDrawingSet_DrawsBoxCharacters()
	{
		// Act
		this.screen.Feed("\e(0lqk\e(Bq");

		// Assert
		_ = this.screen.GetLine(0).Text().Should().Be("┌─┐q");
	}

	/// <summary>
	/// Checks the answers to the reports programs ask the terminal for.
	/// </summary>
	/// <param name="request">The request.</param>
	/// <param name="expected">The answer.</param>
	[Theory]
	[InlineData("\e[6n", "\e[2;3R")]
	[InlineData("\e[5n", "\e[0n")]
	[InlineData("\e[c", "\e[?62;22c")]
	[InlineData("\e[>c", "\e[>1;10;0c")]
	[InlineData("\e[18t", "\e[8;4;10t")]
	[InlineData("\e]11;?\a", "\e]11;rgb:1515/1717/1c1c\e\\")]
	public void Feed_Request_Replies(string request, string expected)
	{
		// Arrange
		this.screen.Feed("\e[2;3H");

		// Act
		this.screen.Feed(request);

		// Assert
		_ = this.replies.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that the modes programs set are reported to the view.
	/// </summary>
	[Fact]
	public void Feed_PrivateModes_AreTracked()
	{
		// Act
		this.screen.Feed("\e[?1h\e[?25l\e[?2004h\e[?1002h\e[?1006h\e[?1004h\e[5 q");

		// Assert
		_ = this.screen.ApplicationCursorKeys.Should().BeTrue();
		_ = this.screen.CursorVisible.Should().BeFalse();
		_ = this.screen.BracketedPaste.Should().BeTrue();
		_ = this.screen.MouseTracking.Should().Be(MouseTracking.Drag);
		_ = this.screen.MouseEncoding.Should().Be(MouseEncoding.Sgr);
		_ = this.screen.FocusEvents.Should().BeTrue();
		_ = this.screen.CursorShape.Should().Be(CursorShape.Bar);
	}

	/// <summary>
	/// Checks that OSC 7 sets the working directory and OSC 2 the title, ended by BEL or by ESC backslash.
	/// </summary>
	[Fact]
	public void Feed_Osc_SetsDirectoryAndTitle()
	{
		// Act
		this.screen.Feed("\e]7;file:///home/me/my%20docs\a\e]2;vim notes\e\\");

		// Assert
		_ = this.screen.WorkingDirectory.Should().Be("/home/me/my docs");
		_ = this.screen.Title.Should().Be("vim notes");
		_ = this.screen.GetText().Should().BeEmpty();
	}

	/// <summary>
	/// Checks that the shell's man page request is decoded and raised, and doesn't print anything.
	/// </summary>
	[Fact]
	public void Feed_ManPageOsc_RaisesRequest()
	{
		// Arrange
		string? requested = null;
		this.screen.ManPageRequested += (_, line) => requested = line;

		// Act
		this.screen.Feed($"\e]{Shell.Program.ManPageOsc}{Convert.ToBase64String("man ls"u8.ToArray())}\a");

		// Assert
		_ = requested.Should().Be("man ls");
	}

	/// <summary>
	/// Checks that making the screen shorter moves rows above the cursor into the scrollback, and making it taller
	/// brings them back.
	/// </summary>
	[Fact]
	public void Resize_ShorterThenTaller_KeepsRowsAndCursor()
	{
		// Arrange
		this.screen.Feed("a\r\nb\r\nc\r\nd");

		// Act
		this.screen.Resize(10, 2);
		var shorter = (this.screen.ScrollbackCount, this.screen.CursorRow);
		this.screen.Resize(10, 4);

		// Assert
		_ = shorter.Should().Be((2, 1));
		_ = this.screen.ScrollbackCount.Should().Be(0);
		_ = this.screen.GetText().Should().Be("a\nb\nc\nd");
		_ = this.screen.CursorRow.Should().Be(3);
	}

	/// <summary>
	/// Checks that tab moves to the next multiple of eight.
	/// </summary>
	[Fact]
	public void Feed_Tab_MovesToNextStop()
	{
		// Act
		this.screen.Feed("ab\tc");

		// Assert
		_ = this.screen.GetLine(0).Text().Should().Be("ab      c");
	}
}
