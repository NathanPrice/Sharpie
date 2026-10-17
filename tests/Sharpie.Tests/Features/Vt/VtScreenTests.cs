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
	/// Checks that the blank row a scroll adds shows nothing of the row it reuses, starting from a screen whose first
	/// row wraps.
	/// </summary>
	/// <param name="prefix">Sent before the screen is filled, to switch screens.</param>
	/// <param name="sequence">The scroll, sent with the cursor on the last row.</param>
	/// <param name="newRow">The screen row the blank row lands on.</param>
	/// <param name="expected">The screen's text afterwards.</param>
	[Theory]
	[InlineData("", "\e[H\e[M", 3, "x\nr2\nr3")]
	[InlineData("\e[?1049h", "\e[S", 3, "x\nr2\nr3")]
	[InlineData("", "\e[2;4r\e[S", 3, "0123456789r2\nr3")]
	[InlineData("", "\e[T", 0, "\n0123456789x\nr2")]
	public void Feed_ScrollDroppingRow_AddsCleanBlankRow(string prefix, string sequence, int newRow, string expected)
	{
		// Arrange
		this.screen.Feed(prefix + "0123456789x\r\nr2\r\nr3");

		// Act
		this.screen.Feed(sequence);

		// Assert
		_ = this.screen.GetText().Should().Be(expected);
		_ = this.screen.GetLine(this.screen.ScrollbackCount + newRow).Wrapped.Should().BeFalse();
	}

	/// <summary>
	/// Checks that once the scrollback is full it keeps the newest rows, and the blank rows made from the rows it drops
	/// come out clean.
	/// </summary>
	[Fact]
	public void Feed_PastFullScrollback_KeepsNewestRowsAndAddsCleanBlankRow()
	{
		// Arrange
		// Each line fills one row and wraps onto the next, so half the dropped rows are wrapped ones.
		var lines = string.Concat(Enumerable.Range(0, VtScreen.ScrollbackRows).Select(i => $"{i:D10}x\r\n"));

		// Act
		this.screen.Feed(lines);

		// Assert
		_ = this.screen.ScrollbackCount.Should().Be(VtScreen.ScrollbackRows);
		_ = this.screen.GetLine(1).Text().Should().Be("0000004999");
		_ = this.screen.GetText(this.screen.ScrollbackCount, 0, this.screen.LineCount - 1, 10).Should().Be("x\n0000009999x");
		_ = this.screen.GetLine(this.screen.LineCount - 1).Wrapped.Should().BeFalse();
	}

	/// <summary>
	/// Checks that a blank row a scroll adds takes the current background color, as erasing does.
	/// </summary>
	[Fact]
	public void Feed_ScrollWithBackground_FillsBlankRowWithBackground()
	{
		// Arrange
		this.screen.Feed("r0\r\nr1\r\nr2\r\nr3\e[44m");

		// Act
		this.screen.Feed("\e[T");

		// Assert
		_ = this.screen.GetLine(0).Cells.Select(cell => this.screen.GetStyle(cell.Style).Background)
			.Should().AllBeEquivalentTo(new AnsiColor(4, 0));
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
	/// Checks the kitty keyboard flags after programs push, pop and change them.
	/// </summary>
	/// <param name="sequences">What the programs send.</param>
	/// <param name="expected">The flags in effect.</param>
	[Theory]
	[InlineData("", 0)]
	[InlineData("\e[>1u", 1)]
	[InlineData("\e[>5u", 1)]
	[InlineData("\e[>u", 0)]
	[InlineData("\e[>1u\e[<u", 0)]
	[InlineData("\e[>1u\e[>0u", 0)]
	[InlineData("\e[>1u\e[>0u\e[<u", 1)]
	[InlineData("\e[>1u\e[>1u\e[<2u", 0)]
	[InlineData("\e[<u", 0)]
	[InlineData("\e[>1u\e[<5u", 0)]
	[InlineData("\e[=1u", 1)]
	[InlineData("\e[>1u\e[=1;3u", 0)]
	[InlineData("\e[>0u\e[=1;2u", 1)]
	[InlineData("\e[>1u\e[=0;1u", 0)]
	[InlineData("\e[>1u\ec", 0)]
	[InlineData("\e[?1049h\e[>1u\ec\e[?1049h", 0)]
	[InlineData("\e[>1u\e[u", 1)]
	public void Feed_KeyboardFlags_TracksStack(string sequences, int expected)
	{
		// Act
		this.screen.Feed(sequences);

		// Assert
		_ = this.screen.KeyboardFlags.Should().Be(expected);
	}

	/// <summary>
	/// Checks that the main and alternate screens keep their own keyboard flags.
	/// </summary>
	[Fact]
	public void Feed_KeyboardFlags_KeptApartPerScreen()
	{
		// Arrange
		this.screen.Feed("\e[>1u\e[?1049h");

		// Act
		var alternate = this.screen.KeyboardFlags;
		this.screen.Feed("\e[>1u\e[?1049l\e[<u");

		// Assert
		_ = alternate.Should().Be(0);
		_ = this.screen.KeyboardFlags.Should().Be(0);
	}

	/// <summary>
	/// Checks that pushing onto a full keyboard flag stack drops the oldest entry and keeps the newest.
	/// </summary>
	[Fact]
	public void Feed_KeyboardFlagsPastLimit_DropsOldest()
	{
		// Arrange
		this.screen.Feed("\e[>1u" + string.Concat(Enumerable.Repeat("\e[>0u", 15)));

		// Act
		this.screen.Feed("\e[>0u\e[<16u");

		// Assert
		_ = this.screen.KeyboardFlags.Should().Be(0);
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
	[InlineData("\e[?u", "\e[?0u")]
	[InlineData("\e[>1u\e[?u", "\e[?1u")]
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

	/// <summary>
	/// Checks that a reverse index on the top row scrolls the screen down, as pagers do to scroll back a line.
	/// </summary>
	[Fact]
	public void Feed_ReverseIndexOnTopRow_ScrollsDown()
	{
		// Arrange
		this.screen.Feed("a\r\nb\e[H");

		// Act
		this.screen.Feed("\eM");

		// Assert
		_ = new[] { this.screen.GetLine(0).Text(), this.screen.GetLine(1).Text(), this.screen.GetLine(2).Text() }
			.Should().Equal(string.Empty, "a", "b");
		_ = (this.screen.CursorRow, this.screen.CursorColumn).Should().Be((0, 0));
	}

	/// <summary>
	/// Checks that each way of saving the cursor brings it back to where it was saved.
	/// </summary>
	/// <param name="save">The sequence that saves the cursor.</param>
	/// <param name="restore">The sequence that restores it.</param>
	[Theory]
	[InlineData("\e7", "\e8")]
	[InlineData("\e[s", "\e[u")]
	[InlineData("\e[?1048h", "\e[?1048l")]
	public void Feed_SaveThenRestoreCursor_ReturnsToSavedCell(string save, string restore)
	{
		// Arrange
		this.screen.Feed($"\e[2;3H{save}\e[4;9H");

		// Act
		this.screen.Feed(restore);

		// Assert
		_ = (this.screen.CursorRow, this.screen.CursorColumn).Should().Be((1, 2));
	}

	/// <summary>
	/// Checks that in origin mode cursor moves count from the scroll region's top and stop at its bottom.
	/// </summary>
	/// <param name="move">The cursor move.</param>
	/// <param name="row">The screen row the cursor should land on.</param>
	[Theory]
	[InlineData("\e[H", 1)]
	[InlineData("\e[2;1H", 2)]
	[InlineData("\e[9;1H", 2)]
	public void Feed_OriginMode_MovesWithinScrollRegion(string move, int row)
	{
		// Arrange
		this.screen.Feed("\e[2;3r\e[?6h");

		// Act
		this.screen.Feed(move);

		// Assert
		_ = this.screen.CursorRow.Should().Be(row);
	}

	/// <summary>
	/// Checks that with autowrap off, text past the last column keeps overwriting the last cell instead of wrapping.
	/// </summary>
	[Fact]
	public void Feed_AutoWrapOff_OverwritesLastColumn()
	{
		// Act
		this.screen.Feed("\e[?7l0123456789xy");

		// Assert
		_ = this.screen.GetText().Should().Be("012345678y");
		_ = (this.screen.CursorRow, this.screen.CursorColumn).Should().Be((0, 9));
	}

	/// <summary>
	/// Checks that the repeat sequence prints the last character again the given number of times.
	/// </summary>
	[Fact]
	public void Feed_Repeat_PrintsLastCharacterAgain()
	{
		// Act
		this.screen.Feed("x\e[3b");

		// Assert
		_ = this.screen.GetLine(0).Text().Should().Be("xxxx");
	}

	/// <summary>
	/// Checks that with every tab stop cleared, a tab goes to the last column.
	/// </summary>
	[Fact]
	public void Feed_TabAfterClearingAllStops_MovesToLastColumn()
	{
		// Act
		this.screen.Feed("\e[3ga\t");

		// Assert
		_ = this.screen.CursorColumn.Should().Be(9);
	}

	/// <summary>
	/// Checks that once the style table passes its limit and is renumbered, every cell on screen and in the scrollback
	/// keeps its color.
	/// </summary>
	[Fact]
	public void Feed_PastStyleLimit_KeepsEveryCellsColor()
	{
		// Arrange
		var renumbered = 0;
		this.screen.StylesRenumbered += (_, _) => renumbered++;

		// Act
		for (var color = 0; color < 5000; color++)
		{
			this.screen.Feed(Shade(color));
		}

		// Assert
		var colors = Enumerable.Range(0, this.screen.LineCount)
			.SelectMany(line => this.screen.GetLine(line).Cells)
			.Where(cell => cell.Character == 'X')
			.Select(cell => this.screen.GetStyle(cell.Style).Foreground?.Rgb);
		_ = renumbered.Should().Be(1);
		_ = colors.Should().Equal(Enumerable.Range(0, 5000).Select(color => (uint?)(color << 8)));
	}

	/// <summary>
	/// Checks that styles no cell uses anymore are dropped once the table passes its limit, while the text still
	/// showing keeps its color and new text gets the style it asked for.
	/// </summary>
	[Fact]
	public void Feed_PastStyleLimit_DropsStylesNoLongerShown()
	{
		// Act
		for (var color = 0; color < 5000; color++)
		{
			this.screen.Feed($"\r{Shade(color)}");
		}

		this.screen.Feed("\e[1mB");

		// Assert
		var cells = this.screen.GetLine(0).Cells;
		_ = this.screen.StyleCount.Should().BeLessThan(1000);
		_ = this.screen.GetStyle(cells[0].Style).Foreground.Should().Be(new AnsiColor(null, 4999u << 8));
		_ = this.screen.GetStyle(cells[1].Style).Should().Be(new AnsiStyle(new AnsiColor(null, 4999u << 8), null, Bold: true));
		_ = cells[2].Style.Should().Be(0);
	}

	/// <summary>
	/// Checks that the style table is renumbered when a new style arrives with the table at its limit, not one style
	/// later.
	/// </summary>
	[Fact]
	public void Feed_NewStyleAtStyleLimit_RenumbersFirst()
	{
		// Arrange
		var renumbered = 0;
		this.screen.StylesRenumbered += (_, _) => renumbered++;

		// The default style plus 4095 shades fill the table to its limit of 4096.
		for (var color = 0; color < 4095; color++)
		{
			this.screen.Feed(Shade(color));
		}

		var beforeLimit = renumbered;

		// Act
		this.screen.Feed(Shade(4095));

		// Assert
		_ = beforeLimit.Should().Be(0);
		_ = renumbered.Should().Be(1);
	}

	/// <summary>
	/// Checks that after renumbering, a style that was dropped and one that was kept both come back as the right
	/// colors, and asking for a kept style again doesn't add it twice.
	/// </summary>
	[Fact]
	public void Feed_StylesAfterRenumbering_MapToTheirColors()
	{
		// Arrange
		// The renumbering runs when shade 4095 arrives, while shade 4094 is the only one on screen, so 4094 is kept and
		// shade 0 is dropped.
		for (var color = 0; color < 4096; color++)
		{
			this.screen.Feed($"\r{Shade(color)}");
		}

		// Act
		this.screen.Feed($"\r{Shade(4094)}");
		var countAfterKept = this.screen.StyleCount;
		this.screen.Feed(Shade(0));

		// Assert
		var cells = this.screen.GetLine(0).Cells;
		_ = countAfterKept.Should().Be(3);
		_ = this.screen.GetStyle(cells[0].Style).Foreground.Should().Be(new AnsiColor(null, 4094u << 8));
		_ = this.screen.GetStyle(cells[1].Style).Foreground.Should().Be(new AnsiColor(null, 0));
	}

	/// <summary>
	/// Checks that renumbering while the alternate screen shows keeps the colors on both screens.
	/// </summary>
	[Fact]
	public void Feed_PastStyleLimitOnAlternateScreen_KeepsBothScreensColors()
	{
		// Arrange
		this.screen.Feed("\e[31mR\e[0m\e[?1049h\e[H");

		// Act
		// 4100 shades on a 40-cell screen leave shades 4060 to 4099 showing, and the renumbering runs at shade 4095, so
		// most of them were printed before it.
		for (var color = 0; color < 4100; color++)
		{
			this.screen.Feed(Shade(color));
		}

		var alternate = Enumerable.Range(0, this.screen.Rows)
			.SelectMany(row => this.screen.GetLine(row).Cells)
			.Select(cell => this.screen.GetStyle(cell.Style).Foreground?.Rgb)
			.ToList();
		this.screen.Feed("\e[?1049l");

		// Assert
		_ = alternate.Should().Equal(Enumerable.Range(4060, 40).Select(color => (uint?)(color << 8)));
		_ = this.screen.GetStyle(this.screen.GetLine(0).Cells[0].Style).Foreground.Should().Be(new AnsiColor(1, 0));
	}

	/// <summary>
	/// Checks that overwriting or erasing one half of a wide character blanks its other half too, so no half is left
	/// on screen without the rest of its character.
	/// </summary>
	/// <param name="input">The output that writes a wide character, then overwrites or erases part of it.</param>
	/// <param name="widths">Each cell's width on the first row, as digits.</param>
	[Theory]
	[InlineData("日\e[2Gx", "1111111111")]
	[InlineData("日\e[1Gx", "1111111111")]
	[InlineData("日本\e[2G中", "1201111111")]
	[InlineData("\e[3G日\e[1G\e[3X", "1111111111")]
	[InlineData("\e[?7l\e[10G日\e[10Gx", "1111111111")]
	[InlineData("日\e[1G\e[P\e[1Gx", "1111111111")]
	public void Feed_OverwritingHalfOfWideCharacter_BlanksOtherHalf(string input, string widths)
	{
		// Act
		this.screen.Feed(input);

		// Assert
		_ = string.Concat(this.screen.GetLine(0).Cells.Select(cell => cell.Width)).Should().Be(widths);
	}

	/// <summary>
	/// Checks that the half of a wide character left blank keeps the character's background color, as erased cells do.
	/// </summary>
	[Fact]
	public void Feed_OverwritingSecondHalfOfWideCharacter_KeepsItsBackground()
	{
		// Act
		this.screen.Feed("\e[41m日\e[0m\e[2Gx");

		// Assert
		_ = this.screen.GetStyle(this.screen.GetLine(0).Cells[0].Style).Background.Should().Be(new AnsiColor(1, 0));
	}

	/// <summary>
	/// Checks that after the screen gets wider, rows coming back from a full scrollback to be reused are made the new
	/// width, so text can be printed across all of it.
	/// </summary>
	[Fact]
	public void Feed_AfterWideningWithFullScrollback_PrintsAcrossNewWidth()
	{
		// Arrange
		this.screen.Feed(string.Concat(Enumerable.Repeat("\r\n", VtScreen.ScrollbackRows + this.screen.Rows)));
		this.screen.Resize(20, 4);

		// Act
		this.screen.Feed("\r\n01234567890123456789");

		// Assert
		_ = this.screen.GetLine(this.screen.LineCount - 1).Text().Should().Be("01234567890123456789");
	}

	/// <summary>
	/// Makes the output for one <c>X</c> in its own truecolor shade.
	/// </summary>
	/// <param name="color">The shade's number, from 0 to 65535.</param>
	/// <returns>
	/// The SGR sequence for the shade, then the <c>X</c>.
	/// </returns>
	private static string Shade(int color) => $"\e[38;2;{color >> 8};{color & 255};0mX";
}
