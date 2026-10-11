using System.Text;
using FluentAssertions;
using Sharpie.Features.Ansi;

namespace Sharpie.Tests.Features.Ansi;

/// <summary>
/// Tests for <see cref="AnsiParser" />.
/// </summary>
public sealed class AnsiParserTests
{
	private readonly AnsiParser parser = new();
	private readonly StringBuilder plain = new();
	private readonly List<AnsiRun> runs = [];

	/// <summary>
	/// Checks that text without escape sequences passes through unchanged and gets no runs.
	/// </summary>
	[Fact]
	public void Parse_PlainText_PassesThroughWithNoRuns()
	{
		// Act
		this.parser.Parse("hello world", this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be("hello world");
		_ = this.runs.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that bold and a color set by separate sequences make one run, and a reset ends it.
	/// </summary>
	[Fact]
	public void Parse_BoldGreenThenReset_ReportsOneRun()
	{
		// Act
		this.parser.Parse("a\e[1m\e[32mhi\e[mb", this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be("ahib");
		_ = this.runs.Should().Equal(new AnsiRun(1, 2, new AnsiStyle(new AnsiColor(2, 0), null, Bold: true)));
	}

	/// <summary>
	/// Checks the foreground codes for standard, bright, 256-color and exact colors.
	/// </summary>
	/// <param name="code">The SGR parameters.</param>
	/// <param name="index">The expected standard color, or <see langword="null" /> for an RGB one.</param>
	/// <param name="rgb">The expected RGB value when <paramref name="index" /> is <see langword="null" />.</param>
	[Theory]
	[InlineData("31", 1, 0u)]
	[InlineData("97", 15, 0u)]
	[InlineData("38;5;4", 4, 0u)]
	[InlineData("38;5;196", null, 0xFF0000u)]
	[InlineData("38;5;244", null, 0x808080u)]
	[InlineData("38;2;1;2;3", null, 0x010203u)]
	[InlineData("30", 0, 0u)]
	[InlineData("37", 7, 0u)]
	[InlineData("38;5;16", null, 0u)]
	[InlineData("38;5;28", null, 0x008700u)]
	[InlineData("38;5;232", null, 0x080808u)]
	[InlineData("38;5;255", null, 0xEEEEEEu)]
	public void Parse_ForegroundCode_SetsForeground(string code, int? index, uint rgb)
	{
		// Act
		this.parser.Parse($"\e[{code}mx", this.plain, this.runs);

		// Assert
		_ = this.runs.Should().ContainSingle().Which.Style.Foreground.Should().Be(new AnsiColor(index, rgb));
	}

	/// <summary>
	/// Checks the background codes for standard and bright colors.
	/// </summary>
	/// <param name="code">The SGR parameters.</param>
	/// <param name="index">The expected standard color.</param>
	[Theory]
	[InlineData("44", 4)]
	[InlineData("101", 9)]
	[InlineData("48;5;3", 3)]
	[InlineData("40", 0)]
	[InlineData("47", 7)]
	[InlineData("100", 8)]
	[InlineData("107", 15)]
	public void Parse_BackgroundCode_SetsBackground(string code, int index)
	{
		// Act
		this.parser.Parse($"\e[{code}m ", this.plain, this.runs);

		// Assert
		_ = this.runs.Should().ContainSingle().Which.Style.Background.Should().Be(new AnsiColor(index, 0));
	}

	/// <summary>
	/// Checks that sequences other than colors, like erasing a line or hiding the cursor, are dropped from the text.
	/// </summary>
	[Fact]
	public void Parse_NonColorSequences_AreStripped()
	{
		// Act
		this.parser.Parse("\e[2Ka\e[?25lb\e[3A", this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be("ab");
		_ = this.runs.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that an OSC 7 sequence stays in the text, since the window reads the working directory from it.
	/// </summary>
	[Fact]
	public void Parse_OscSequence_IsKept()
	{
		// Act
		this.parser.Parse("\e]7;file:///tmp\a$ ", this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be("\e]7;file:///tmp\a$ ");
	}

	/// <summary>
	/// Checks that a sequence split between two chunks is held back and applied once the rest arrives.
	/// </summary>
	[Theory]
	[InlineData("a\e", "[31mb")]
	[InlineData("a\e[", "31mb")]
	[InlineData("a\e[3", "1mb")]
	public void Parse_SequenceSplitAcrossChunks_AppliesItWithoutLeakingText(string first, string second)
	{
		// Act
		this.parser.Parse(first, this.plain, this.runs);
		this.parser.Parse(second, this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be("ab");
		_ = this.runs.Should().Equal(new AnsiRun(1, 1, new AnsiStyle(new AnsiColor(1, 0), null, Bold: false)));
	}

	/// <summary>
	/// Checks that a style carries over to the next chunk until it's reset.
	/// </summary>
	[Fact]
	public void Parse_StyleSetInEarlierChunk_AppliesToNextChunk()
	{
		// Arrange
		this.parser.Parse("\e[36m", this.plain, this.runs);

		// Act
		this.parser.Parse("cyan", this.plain, this.runs);

		// Assert
		_ = this.runs.Should().Equal(new AnsiRun(0, 4, new AnsiStyle(new AnsiColor(6, 0), null, Bold: false)));
	}

	/// <summary>
	/// Checks that 39 and 22 turn off the color and bold without touching the background.
	/// </summary>
	[Fact]
	public void Parse_DefaultColorAndNormalWeight_KeepBackground()
	{
		// Act
		this.parser.Parse("\e[1;31;42m\e[39;22mx", this.plain, this.runs);

		// Assert
		_ = this.runs.Should().Equal(new AnsiRun(0, 1, new AnsiStyle(null, new AnsiColor(2, 0), Bold: false)));
	}

	private static readonly AnsiStyle ManBold = new(new AnsiColor(4, 0), null, Bold: true);
	private static readonly AnsiStyle ManUnderline = new(new AnsiColor(3, 0), null, Bold: false);

	/// <summary>
	/// Checks that a bold word in man's overstrike form loses its backspaces and becomes one accent-blue bold run.
	/// </summary>
	[Fact]
	public void Parse_OverstrikeBold_ReportsOneBoldRun()
	{
		// Act
		this.parser.Parse("N\bNA\bA\n", this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be("NA\n");
		_ = this.runs.Should().Equal(new AnsiRun(0, 2, ManBold));
	}

	/// <summary>
	/// Checks that underline overstrike and stacked bold-underline get their styles.
	/// </summary>
	/// <param name="input">The overstruck character.</param>
	/// <param name="bold"><see langword="true" /> when bold is expected; otherwise, <see langword="false" />.</param>
	[Theory]
	[InlineData("_\bx\n", false)]
	[InlineData("_\bX\bX\n", true)]
	public void Parse_OverstrikeUnderline_ReportsItsStyle(string input, bool bold)
	{
		// Act
		this.parser.Parse(input, this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().HaveLength(2);
		_ = this.runs.Should().Equal(new AnsiRun(0, 1, bold ? ManBold : ManUnderline));
	}

	/// <summary>
	/// Checks that overstrike cut off at a chunk boundary gives the same result as when it arrives whole.
	/// </summary>
	/// <param name="first">The first chunk.</param>
	/// <param name="second">The second chunk.</param>
	/// <param name="expected">The plain text both chunks should give.</param>
	[Theory]
	[InlineData("a\bab\bbN", "\bN\n", "abN\n")]
	[InlineData("N\b", "N\n", "N\n")]
	[InlineData("_\bN", "\bN\n", "N\n")]
	public void Parse_OverstrikeSplitAcrossChunks_StylesIt(string first, string second, string expected)
	{
		// Act
		this.parser.Parse(first, this.plain, this.runs);
		this.parser.Parse(second, this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be(expected);
		_ = this.runs.Should().Equal(new AnsiRun(0, expected.Length - 1, ManBold));
	}

	/// <summary>
	/// Checks that a backspace with nothing to overstrike is dropped and the color set before it carries on.
	/// </summary>
	[Fact]
	public void Parse_BackspaceAfterColorCode_IsDroppedAndKeepsStyle()
	{
		// Act
		this.parser.Parse("\e[32m\bok\n", this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be("ok\n");
		_ = this.runs.Should().Equal(new AnsiRun(0, 3, new AnsiStyle(new AnsiColor(2, 0), null, Bold: false)));
	}

	/// <summary>
	/// Checks that OSC 8 hyperlinks, ended by either terminator, are taken out of the text and reported as links when
	/// the caller asks for them.
	/// </summary>
	/// <param name="terminator">The sequence that ends each OSC 8 command.</param>
	[Theory]
	[InlineData("\e\\")]
	[InlineData("\a")]
	public void Parse_Hyperlink_ReportsLink(string terminator)
	{
		// Arrange
		var links = new List<AnsiLink>();

		// Act
		this.parser.Parse($"a \e]8;;https://x.test{terminator}link\e]8;;{terminator} b", this.plain, this.runs, links);

		// Assert
		_ = this.plain.ToString().Should().Be("a link b");
		_ = links.Should().Equal(new AnsiLink(2, 4, "https://x.test"));
	}

	/// <summary>
	/// Checks the codes that turn one attribute on or off, leaving the others alone.
	/// </summary>
	/// <param name="codes">The SGR codes, separated by <c>;</c>.</param>
	/// <param name="underline">Whether text is underlined afterwards.</param>
	/// <param name="inverse">Whether text is inverse afterwards.</param>
	/// <param name="hidden">Whether text is hidden afterwards.</param>
	[Theory]
	[InlineData("8", false, false, true)]
	[InlineData("4;7;8;24", false, true, true)]
	[InlineData("4;7;8;27", true, false, true)]
	[InlineData("4;7;8;28", true, true, false)]
	public void ApplySgr_AttributeCodes_SetOnlyTheirAttribute(string codes, bool underline, bool inverse, bool hidden)
	{
		// Act
		var style = AnsiParser.ApplySgr(default, [.. codes.Split(';').Select(int.Parse)]);

		// Assert
		_ = (style.Underline, style.Inverse, style.Hidden).Should().Be((underline, inverse, hidden));
	}

	/// <summary>
	/// Checks that an SGR sequence with more parameters than are kept still applies the ones it keeps.
	/// </summary>
	[Fact]
	public void Parse_SgrWithTooManyParameters_KeepsTheFirstOnes()
	{
		// Act
		this.parser.Parse($"\e[{string.Join(';', Enumerable.Repeat("31", 40))}mx", this.plain, this.runs);

		// Assert
		_ = this.runs.Should().ContainSingle().Which.Style.Foreground.Should().Be(new AnsiColor(1, 0));
	}

	/// <summary>
	/// Checks that CSI sequences ending in the first and last final characters, <c>@</c> and <c>~</c>, are taken out.
	/// </summary>
	[Fact]
	public void Parse_CsiEndingAtEitherEdge_IsStripped()
	{
		// Act
		this.parser.Parse("a\e[@b\e[2~c", this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be("abc");
	}

	/// <summary>
	/// Checks that a character overstruck by a different one shows the second, plain, and that one overstruck twice is
	/// still bold.
	/// </summary>
	[Fact]
	public void Parse_OverstrikeByOtherAndThreeTimes_StylesEach()
	{
		// Act
		this.parser.Parse("a\bbX\bX\bX\n", this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be("bX\n");
		_ = this.runs.Should().ContainSingle().Which.Should().Be(new AnsiRun(1, 1, new AnsiStyle(new AnsiColor(4, 0), null, Bold: true)));
	}

	/// <summary>
	/// Checks that a chunk ending right after a color code that followed overstrike holds nothing back.
	/// </summary>
	[Fact]
	public void Parse_OverstrikeThenColorAtEnd_WritesEverything()
	{
		// Act
		this.parser.Parse("a\bab\e[1m", this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be("ab");
	}

	/// <summary>
	/// Checks that OSC 8 stays in the text when the caller doesn't ask for links, like any other OSC.
	/// </summary>
	[Fact]
	public void Parse_HyperlinkWithoutLinkList_IsKept()
	{
		// Act
		this.parser.Parse("\e]8;;u\ago\e]8;;\a", this.plain, this.runs);

		// Assert
		_ = this.plain.ToString().Should().Be("\e]8;;u\ago\e]8;;\a");
	}

	/// <summary>
	/// Checks link edge cases: a color code while links are asked for, a link ended by ESC backslash with a BEL after
	/// it, a link with no text, one with no parameters part, and one split across chunks.
	/// </summary>
	[Fact]
	public void Parse_HyperlinkEdgeCases_ReportsOnlyRealLinks()
	{
		// Arrange
		var links = new List<AnsiLink>();

		// Act
		this.parser.Parse("\e[1mx\e[m\e]8;;u1\e\\a\e]8;;\e\\\a\e]8;;u2\a\e]8;;\a\e]8;u3\ab\e]8;;\a\e]8;;u4", this.plain, this.runs, links);
		this.parser.Parse("\ac\e]8;;\a", this.plain, this.runs, links);

		// Assert
		_ = this.plain.ToString().Should().Be("xa\abc");
		_ = this.runs.Should().ContainSingle().Which.Style.Bold.Should().BeTrue();
		_ = links.Should().Equal(new AnsiLink(1, 1, "u1"), new AnsiLink(4, 1, "u4"));
	}
}
