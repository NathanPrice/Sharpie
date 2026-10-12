using FluentAssertions;
using Sharpie.Features.Ansi;
using Sharpie.Features.Vt;

namespace Sharpie.Tests.Features.Vt;

/// <summary>
/// Tests for the edges of the control characters and escape sequences <see cref="VtScreen" /> handles: scroll
/// regions, waiting wraps, tab stops, modes, resets and reports.
/// </summary>
public sealed class VtSequenceTests
{
	private readonly List<string> replies = [];
	private readonly VtScreen screen;

	/// <summary>
	/// Initializes a new instance of the <see cref="VtSequenceTests" /> class with a 10 by 4 screen.
	/// </summary>
	public VtSequenceTests() => this.screen = new VtScreen(10, 4, this.replies.Add);

	/// <summary>
	/// Checks the text and cursor each input leaves.
	/// </summary>
	/// <param name="input">What the program writes.</param>
	/// <param name="text">The screen's text afterwards.</param>
	/// <param name="row">The cursor's row afterwards.</param>
	/// <param name="column">The cursor's column afterwards.</param>
	[Theory]
	[InlineData("\bx", "x", 0, 1)]
	[InlineData("0123456789\bx", "01234567x9", 0, 9)]
	[InlineData("0123456789\rx", "x123456789", 0, 1)]
	[InlineData("\x0Eq\x0Fq", "─q", 0, 2)]
	[InlineData("ab\eDc", "ab\n  c", 1, 3)]
	[InlineData("ab\eEc", "ab\nc", 1, 1)]
	[InlineData("\e[4G\eH\r\tx", "   x", 0, 4)]
	[InlineData("abc\ecx", "x", 0, 1)]
	[InlineData("0123456789\e[1mx", "0123456789x", 1, 1)]
	[InlineData("0123456789\e[5nx", "0123456789x", 1, 1)]
	[InlineData("0123456789\e[cx", "0123456789x", 1, 1)]
	[InlineData("0123456789\e[18tx", "0123456789x", 1, 1)]
	[InlineData("0123456789\e[4hx", "0123456789x", 1, 1)]
	[InlineData("0123456789\e[4lx", "0123456789x", 1, 1)]
	[InlineData("0123456789\e[Cx", "012345678x", 0, 9)]
	[InlineData("\e[2;3r\e[3;1H\e[5A", "", 1, 0)]
	[InlineData("\e[2;3r\e[2;1H\e[5A", "", 1, 0)]
	[InlineData("\e[2;3r\e[2;1H\e[5B", "", 2, 0)]
	[InlineData("\e[2;3r\e[3;1H\e[5B", "", 2, 0)]
	[InlineData("\e[2;3r\e[4;1H\e[5B", "", 3, 0)]
	[InlineData("\e[2;3r\e[1;1H\e[5B", "", 2, 0)]
	[InlineData("\e[2;3r\e[2;3H\e[5E", "", 2, 0)]
	[InlineData("\e[2;3r\e[4;3H\e[E", "", 3, 0)]
	[InlineData("\e[2;3r\e[3;3H\e[5F", "", 1, 0)]
	[InlineData("\e[2;3r\e[1;3H\e[F", "", 0, 0)]
	[InlineData("\e[99C", "", 0, 9)]
	[InlineData("\e[99G", "", 0, 9)]
	[InlineData("\e[2a", "", 0, 2)]
	[InlineData("\e[2e", "", 2, 0)]
	[InlineData("\e[3`", "", 0, 2)]
	[InlineData("\e[2;3f", "", 1, 2)]
	[InlineData("\e[I", "", 0, 8)]
	[InlineData("\e[2I", "", 0, 9)]
	[InlineData("\e[9G\t", "", 0, 9)]
	[InlineData("\e[10G\t", "", 0, 9)]
	[InlineData("\e[10G\e[Z", "", 0, 8)]
	[InlineData("\e[10G\e[2Z", "", 0, 0)]
	[InlineData("\e[Z", "", 0, 0)]
	[InlineData("\e[9G\e[g\e[G\t", "", 0, 9)]
	[InlineData("\e[3g\e[G\t", "", 0, 9)]
	[InlineData("abcd\e[H\e[4hX", "Xabcd", 0, 1)]
	[InlineData("abcd\e[H\e[4h\e[4lX", "Xbcd", 0, 1)]
	[InlineData("top\e[2r\e[2;1Hb\r\nc\r\nd\r\ne", "top\nc\nd\ne", 3, 1)]
	[InlineData("top\e[2;0r\e[2;1Hb\r\nc\r\nd\r\ne", "top\nc\nd\ne", 3, 1)]
	[InlineData("a\e[2;99r\e[4;1Hx\n\ry", "a\n\nx\ny", 3, 1)]
	[InlineData("a\r\nb\r\nc\e[2;3r\e[1;1H\e[L", "a\nb\nc", 0, 0)]
	[InlineData("a\r\nb\r\nc\e[2;3r\e[1;1H\e[M", "a\nb\nc", 0, 0)]
	[InlineData("a\r\nb\r\nc\r\nd\e[2;3r\e[2;1H\e[M", "a\nc\n\nd", 1, 0)]
	[InlineData("a\r\nb\r\nc\r\nd\e[2;3r\e[2;1H\e[5M", "a\n\n\nd", 1, 0)]
	[InlineData("\e[2;3r\e[4;1H", "", 3, 0)]
	[InlineData("\e[3;3H\e[2;2r", "", 2, 2)]
	[InlineData("\e[3;3H\e[2;3r", "", 0, 0)]
	[InlineData("\e[2;3r\e[3;5H\e[?6h", "", 1, 0)]
	[InlineData("0123456789\e7\e8x", "012345678x", 0, 9)]
	[InlineData("ab\e[?1049h\e[3;3H\e[?1049h\e[?1049l", "ab", 0, 2)]
	[InlineData("\U0001F600x", "\U0001F600x", 0, 3)]
	[InlineData("e\u0301", "e", 0, 1)]
	[InlineData("日\e[2Gx", " x", 0, 2)]
	[InlineData("日\e[1Gx", "x", 0, 1)]
	[InlineData("日日\e[2G本", " 本", 0, 3)]
	[InlineData("\e[?7l\e[10G日", "         日", 0, 9)]
	[InlineData("\e[2;3r\e[4;1HA\nB", "\n\n\nAB", 3, 2)]
	[InlineData("\r\n0123456789\eMx", "         x\n0123456789", 0, 9)]
	[InlineData("\e[3;1H\eM", "", 1, 0)]
	[InlineData("a\e[2;3r\e[1;1H\eM", "a", 0, 0)]
	[InlineData("a\r\nb\r\nc\r\nd\e[2;3r\e[T", "a\n\nb\nd", 0, 0)]
	[InlineData("a\r\nb\r\nc\r\nd\e[2;3r\e[5T", "a\n\n\nd", 0, 0)]
	[InlineData("日\e[2G\e[K", "", 0, 1)]
	[InlineData("0123456789x\e[1;5H\e[K", "0123\nx", 0, 4)]
	[InlineData("abcdefghij\e[5G\e[99@", "abcd", 0, 4)]
	[InlineData("abcdefghij\e[5G\e[99P", "abcd", 0, 4)]
	[InlineData("\e(0`~_\e(B", "◆·_", 0, 3)]
	[InlineData("e\u0301\e[b", "ee", 0, 2)]
	[InlineData("0123456789x\e[1;3H\e[X", "01 3456789x", 0, 2)]
	[InlineData("\e7\e[3;3H\e#8", "", 2, 2)]
	[InlineData("abcd\e[H\e[1hX", "Xbcd", 0, 1)]
	[InlineData("a\r\nb\r\nc\e[2;3r\e[3;1H\e[L", "a\nb", 2, 0)]
	[InlineData("a\r\nb\r\nc\e[2;3r\e[3;1H\e[M", "a\nb", 2, 0)]
	[InlineData("\e[?1047hx\e[?1047l\e[?47h", "", 0, 0)]
	public void Feed_Input_LeavesTextAndCursor(string input, string text, int row, int column)
	{
		// Act
		this.screen.Feed(input);

		// Assert
		_ = this.screen.GetText().Should().Be(text);
		_ = (this.screen.CursorRow, this.screen.CursorColumn).Should().Be((row, column));
	}

	/// <summary>
	/// Checks the answers to more reports, and that a request in a form the terminal doesn't know gets none.
	/// </summary>
	/// <param name="request">The request.</param>
	/// <param name="expected">The answer, or empty for none.</param>
	[Theory]
	[InlineData("\e[=c", "")]
	[InlineData("\e]10;?\a", "\e]10;rgb:dada/dddd/e3e3\e\\")]
	[InlineData("\e[14t", "\e[4;64;80t")]
	[InlineData("\e[16t", "\e[6;16;8t")]
	[InlineData("\e[2;3r\e[?6h\e[2;2H\e[6n", "\e[2;2R")]
	[InlineData("\e[2;3r\e[3;2H\e[6n", "\e[3;2R")]
	public void Feed_Request_Replies(string request, string expected)
	{
		// Act
		this.screen.Feed(request);

		// Assert
		_ = string.Concat(this.replies).Should().Be(expected);
	}

	/// <summary>
	/// Checks that BEL rings the bell.
	/// </summary>
	[Fact]
	public void Feed_Bell_RaisesBell()
	{
		// Arrange
		var rings = 0;
		this.screen.Bell += (_, _) => rings++;

		// Act
		this.screen.Feed("\a");

		// Assert
		_ = rings.Should().Be(1);
	}

	/// <summary>
	/// Checks that OSC 0 and OSC 2 both set the title and say so, and an OSC 2 with no value clears it.
	/// </summary>
	[Fact]
	public void Feed_TitleOscs_SetTitleAndRaiseChanged()
	{
		// Arrange
		var titles = new List<string?>();
		this.screen.TitleChanged += (_, _) => titles.Add(this.screen.Title);

		// Act
		this.screen.Feed("\e]0;one\a\e]2;two\a\e]2\a");

		// Assert
		_ = titles.Should().Equal("one", "two", string.Empty);
	}

	/// <summary>
	/// Checks that an OSC without its text is refused.
	/// </summary>
	[Fact]
	public void Osc_Null_Throws()
	{
		// Act
		var act = () => this.screen.Osc(null!);

		// Assert
		_ = act.Should().Throw<ArgumentNullException>();
	}

	/// <summary>
	/// Checks that ESC c turns every mode and the style back off, so text overwrites in the default style.
	/// </summary>
	[Fact]
	public void Feed_Reset_TurnsModesOff()
	{
		// Act
		this.screen.Feed("\e[?1h\e[?2004h\e[?1004h\e[?25l\e[2;3r\e[?6h\e[4h\e[1m\ec");
		this.screen.Feed("ab\e[Hx");

		// Assert
		_ = this.screen.ApplicationCursorKeys.Should().BeFalse();
		_ = this.screen.BracketedPaste.Should().BeFalse();
		_ = this.screen.FocusEvents.Should().BeFalse();
		_ = this.screen.CursorVisible.Should().BeTrue();
		_ = this.screen.GetText().Should().Be("xb");
		_ = this.screen.GetStyle(this.screen.GetLine(0).Cells[0].Style).Should().Be(default(AnsiStyle));
		this.screen.Feed("\e[4;1H");
		_ = this.screen.CursorRow.Should().Be(3);
	}

	/// <summary>
	/// Checks each mouse mode turning on, and turning off again.
	/// </summary>
	/// <param name="input">The mode changes.</param>
	/// <param name="tracking">The <see cref="MouseTracking" /> expected.</param>
	/// <param name="encoding">The <see cref="MouseEncoding" /> expected.</param>
	[Theory]
	[InlineData("\e[?9h", "Press", "Default")]
	[InlineData("\e[?9h\e[?9l", "None", "Default")]
	[InlineData("\e[?1000h", "PressRelease", "Default")]
	[InlineData("\e[?1002h\e[?1002l", "None", "Default")]
	[InlineData("\e[?1003h", "AnyMove", "Default")]
	[InlineData("\e[?1003h\e[?1003l", "None", "Default")]
	[InlineData("\e[?1000h\e[?1000l", "None", "Default")]
	[InlineData("\e[?1006h\e[?1006l", "None", "Default")]
	[InlineData("\e[?1015h", "None", "Urxvt")]
	[InlineData("\e[?1015h\e[?1015l", "None", "Default")]
	public void Feed_MouseMode_SetsTrackingAndEncoding(string input, string tracking, string encoding)
	{
		// Act
		this.screen.Feed(input);

		// Assert
		_ = (this.screen.MouseTracking, this.screen.MouseEncoding).Should().Be((Enum.Parse<MouseTracking>(tracking), Enum.Parse<MouseEncoding>(encoding)));
	}

	/// <summary>
	/// Checks the cursor shapes DECSCUSR picks, and that a <c>q</c> without the space leaves the shape alone.
	/// </summary>
	/// <param name="input">The request.</param>
	/// <param name="shape">The <see cref="CursorShape" /> expected.</param>
	[Theory]
	[InlineData("\e[1 q", "Block")]
	[InlineData("\e[2 q", "Block")]
	[InlineData("\e[3 q", "Underline")]
	[InlineData("\e[4 q", "Underline")]
	[InlineData("\e[2 q\e[6 q", "Bar")]
	[InlineData("\e[2q", "Bar")]
	public void Feed_CursorShape_SetsShape(string input, string shape)
	{
		// Act
		this.screen.Feed(input);

		// Assert
		_ = this.screen.CursorShape.Should().Be(Enum.Parse<CursorShape>(shape));
	}

	/// <summary>
	/// Checks which ways into the alternate screen keep what was drawn there: mode 47 keeps it, 1047 clears it on the
	/// way out, and 1049 clears it on the way in.
	/// </summary>
	/// <param name="mode">The private mode.</param>
	/// <param name="text">The alternate screen's text on the second visit.</param>
	[Theory]
	[InlineData(47, "x")]
	[InlineData(1047, "")]
	[InlineData(1049, "")]
	public void Feed_AlternateScreenTwice_ClearsByMode(int mode, string text)
	{
		// Act
		this.screen.Feed($"\e[?{mode}hx\e[?{mode}l\e[?{mode}h");

		// Assert
		_ = this.screen.AlternateScreen.Should().BeTrue();
		_ = this.screen.GetText().Should().Be(text);
	}

	/// <summary>
	/// Checks that ED 3 clears the scrollback.
	/// </summary>
	[Fact]
	public void Feed_EraseScrollback_ClearsHistory()
	{
		// Act
		this.screen.Feed("a\r\nb\r\nc\r\nd\r\ne\e[3J");

		// Assert
		_ = this.screen.ScrollbackCount.Should().Be(0);
	}

	/// <summary>
	/// Checks that restoring a cursor saved on a bigger screen keeps it on the screen, with the style it had.
	/// </summary>
	[Fact]
	public void Feed_RestoreAfterShrink_ClampsCursorAndKeepsStyle()
	{
		// Arrange
		this.screen.Feed("\e[4;10H\e[1m\e7\e[0m");
		this.screen.Resize(5, 2);

		// Act
		this.screen.Feed("\e8");
		var cursor = (this.screen.CursorRow, this.screen.CursorColumn);
		this.screen.Feed("x");

		// Assert
		_ = cursor.Should().Be((1, 4));
		_ = this.screen.GetStyle(this.screen.GetLine(this.screen.LineCount - 1).Cells[4].Style).Bold.Should().BeTrue();
	}

	/// <summary>
	/// Checks that resizing to the size the screen already has changes nothing, not even a wrap waiting at the end of
	/// a row.
	/// </summary>
	[Fact]
	public void Resize_SameSize_KeepsWaitingWrap()
	{
		// Arrange
		this.screen.Feed("0123456789");

		// Act
		this.screen.Resize(10, 4);
		this.screen.Feed("x");

		// Assert
		_ = this.screen.GetText().Should().Be("0123456789x");
	}

	/// <summary>
	/// Checks that a wider screen keeps each row's text and fills the new columns with blanks that print as spaces.
	/// </summary>
	[Fact]
	public void Resize_Wider_KeepsTextAndAddsBlanks()
	{
		// Arrange
		this.screen.Feed("abc");

		// Act
		this.screen.Resize(12, 4);
		var widths = string.Concat(this.screen.GetLine(0).Cells.Select(cell => cell.Width));
		this.screen.Feed("\e[12Gz");

		// Assert
		_ = widths.Should().Be("111111111111");
		_ = this.screen.GetText().Should().Be("abc        z");
	}

	/// <summary>
	/// Checks that scrolling up by more than the screen moves every row into the scrollback, once each.
	/// </summary>
	[Fact]
	public void Feed_ScrollUpMoreThanScreen_MovesEachRowOnce()
	{
		// Act
		this.screen.Feed("a\r\nb\r\nc\r\nd\e[9S");

		// Assert
		_ = this.screen.ScrollbackCount.Should().Be(4);
		_ = this.screen.GetText().Should().Be("a\nb\nc\nd");
	}

	/// <summary>
	/// Checks that erasing in an inverse or colored style leaves cells in that background, but never bold.
	/// </summary>
	/// <param name="sgr">The style set before erasing.</param>
	/// <param name="inverse">Whether the erased cells are inverse.</param>
	/// <param name="background">The erased cells' background palette index, or -1 for none.</param>
	[Theory]
	[InlineData("1;7", true, -1)]
	[InlineData("1;41", false, 1)]
	[InlineData("1", false, -1)]
	public void Feed_EraseInStyle_KeepsBackgroundNotBold(string sgr, bool inverse, int background)
	{
		// Act
		this.screen.Feed($"\e[{sgr}m\e[K");

		// Assert
		_ = this.screen.GetStyle(this.screen.GetLine(0).Cells[0].Style).Should().Be(
			new AnsiStyle(null, background < 0 ? null : new AnsiColor(background, 0), Bold: false, Inverse: inverse));
	}

	/// <summary>
	/// Checks that the first half of a surrogate pair followed by an ordinary character is dropped, and the character
	/// still prints. Kept out of the table above, which can't carry half a pair.
	/// </summary>
	[Fact]
	public void Feed_BrokenSurrogatePair_PrintsNextCharacter()
	{
		// Act
		this.screen.Feed("\uD83Dx");

		// Assert
		_ = this.screen.GetText().Should().Be("x");
	}

	/// <summary>
	/// Checks that shrinking the screen while the alternate screen is hidden keeps the alternate screen's top rows,
	/// wherever the main screen's cursor is.
	/// </summary>
	[Fact]
	public void Resize_ShorterWithAlternateHidden_KeepsAlternateTopRows()
	{
		// Arrange
		this.screen.Feed("\e[?47ha\r\nb\r\nc\r\nd\e[?47l\e[4;1H");

		// Act
		this.screen.Resize(10, 2);
		this.screen.Feed("\e[?47h");

		// Assert
		_ = this.screen.GetText().Should().Be("a\nb");
	}
}
