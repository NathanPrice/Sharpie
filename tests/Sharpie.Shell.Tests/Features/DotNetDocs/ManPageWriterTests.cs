using FluentAssertions;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Shell.Tests.Features.DotNetDocs;

/// <summary>
/// Tests for <see cref="ManPageWriter" />.
/// </summary>
public sealed class ManPageWriterTests
{
	/// <summary>
	/// Checks that bold is written as each letter struck over itself and underline as an underscore struck over each
	/// letter, the way groff hands a page to a pager.
	/// </summary>
	[Fact]
	public void Section_Styled_WritesOverstrike()
	{
		// Arrange
		var sut = new ManPageWriter(80, styled: true);

		// Act
		sut.Section("name");
		sut.Paragraph([new Span("x", SpanStyle.Underline)]);

		// Assert
		_ = sut.ToString().Should().Be("\nN\bNA\bAM\bME\bE\n       _\bx\n");
	}

	/// <summary>
	/// Checks that an unstyled page has no overstrike, for a page written to a file or a pipe.
	/// </summary>
	[Fact]
	public void Section_Unstyled_WritesPlainText()
	{
		// Arrange
		var sut = new ManPageWriter(80, styled: false);

		// Act
		sut.Section("name");

		// Assert
		_ = sut.ToString().Should().Be("\nNAME\n");
	}

	/// <summary>
	/// Checks that a paragraph wraps between words to the width and every line starts at the body indent.
	/// </summary>
	[Fact]
	public void Paragraph_LongText_WrapsAtWidthAndIndents()
	{
		// Arrange
		var sut = new ManPageWriter(40, styled: false);
		var words = string.Join(' ', Enumerable.Repeat("word", 12));

		// Act
		sut.Paragraph([new Span(words, SpanStyle.Plain)]);

		// Assert
		var lines = sut.ToString().TrimEnd('\n').Split('\n');

		_ = lines.Should().HaveCount(2).And.AllSatisfy(line => line.Should().StartWith("       word").And.Subject.Length.Should().BeLessThanOrEqualTo(40));
	}

	/// <summary>
	/// Checks that the header puts the title at both edges and the manual name in the middle, as <c>man</c> does.
	/// </summary>
	[Fact]
	public void Header_FitsWidth_CentersManualName()
	{
		// Arrange
		var sut = new ManPageWriter(40, styled: false);

		// Act
		sut.Header("ls(1)", "Commands");

		// Assert
		_ = sut.ToString().Should().Be("ls(1)           Commands           ls(1)\n");
	}

	/// <summary>
	/// Checks that a long signature wraps with its later lines indented further than its first.
	/// </summary>
	[Fact]
	public void Hanging_LongLine_IndentsContinuation()
	{
		// Arrange
		var sut = new ManPageWriter(40, styled: false);

		// Act
		sut.Hanging([[new Span("public static void Method(string first, string second)", SpanStyle.Plain)]]);

		// Assert
		_ = sut.ToString().Should().Be("       public static void Method(string\n           first, string second)\n");
	}

	/// <summary>
	/// Checks that linked text is wrapped in an OSC 8 hyperlink on a styled page, and written alone on a plain one.
	/// </summary>
	/// <param name="styled"><see langword="true" /> for a styled page.</param>
	/// <param name="expected">The line written.</param>
	[Theory]
	[InlineData(true, "       \e]8;;https://x.test/a\e\\Go\e]8;;\e\\ on\n")]
	[InlineData(false, "       Go on\n")]
	public void Paragraph_LinkedText_WritesHyperlinkWhenStyled(bool styled, string expected)
	{
		// Arrange
		var sut = new ManPageWriter(80, styled);

		// Act
		sut.Paragraph([new Span("Go", SpanStyle.Plain, "https://x.test/a"), new Span(" on", SpanStyle.Plain)]);

		// Assert
		_ = sut.ToString().Should().Be(expected);
	}

	/// <summary>
	/// Checks a whole page through every kind of block, with blank lines between blocks but not right after a heading,
	/// and none after a list line.
	/// </summary>
	[Fact]
	public void WholePage_EveryBlock_WritesManLayout()
	{
		// Arrange
		var sut = new ManPageWriter(40, styled: false);

		// Act
		sut.Header("x(1)", "Manual");
		sut.Section("one");
		sut.Line([new Span("• first", SpanStyle.Plain)], 9, 7);
		sut.Line([new Span("• second", SpanStyle.Plain)], 9, 7);
		sut.Paragraph([new Span("Text.", SpanStyle.Plain)]);
		sut.Subsection("Sub");
		sut.Tagged([[new Span("a tag long enough to wrap onto a second line", SpanStyle.Plain)]], [new Span("Body.", SpanStyle.Plain)]);
		sut.Tagged([[new Span("bare", SpanStyle.Plain)]], []);
		sut.Hanging([[new Span("sig", SpanStyle.Plain)]]);
		sut.Verbatim([[new Span("code", SpanStyle.Plain)], [], [new Span("more", SpanStyle.Plain)]], 11);
		sut.Footer("left", "mid", "x(1)");

		// Assert
		_ = sut.ToString().Should().Be("""
			x(1)             Manual             x(1)

			ONE
			       • first
			       • second

			       Text.

			   Sub
			       a tag long enough to wrap onto a
			           second line
			              Body.

			       bare

			       sig

			           code

			           more

			left              mid               x(1)

			""");
	}

	/// <summary>
	/// Checks that a word that ends exactly at the page's edge stays on the line, and one more column wraps it.
	/// </summary>
	/// <param name="word">The second word.</param>
	/// <param name="expected">The paragraph.</param>
	[Theory]
	[InlineData("abcdefghijklmnopqrstuvwxyz1", "       abcde abcdefghijklmnopqrstuvwxyz1\n")]
	[InlineData("abcdefghijklmnopqrstuvwxyz12", "       abcde\n       abcdefghijklmnopqrstuvwxyz12\n")]
	public void Paragraph_WordAtEdge_WrapsOnlyPastWidth(string word, string expected)
	{
		// Arrange
		var sut = new ManPageWriter(40, styled: false);

		// Act
		sut.Paragraph([new Span($"abcde {word}", SpanStyle.Plain)]);

		// Assert
		_ = sut.ToString().Should().Be(expected);
	}

	/// <summary>
	/// Checks that a word made of several differently styled pieces is measured as a whole when wrapping.
	/// </summary>
	[Fact]
	public void Paragraph_WordOfSeveralSpans_MeasuresWholeWord()
	{
		// Arrange
		var sut = new ManPageWriter(40, styled: false);

		// Act
		sut.Paragraph([new Span("abcdefghijklmnopqrstuvwxyz ", SpanStyle.Plain), new Span("abc", SpanStyle.Plain), new Span("defgh", SpanStyle.Bold)]);

		// Assert
		_ = sut.ToString().Should().Be("       abcdefghijklmnopqrstuvwxyz\n       abcdefgh\n");
	}

	/// <summary>
	/// Checks that runs of spaces and trailing spaces don't add space to the page.
	/// </summary>
	[Fact]
	public void Paragraph_ExtraSpaces_CollapsesThem()
	{
		// Arrange
		var sut = new ManPageWriter(40, styled: false);

		// Act
		sut.Paragraph([new Span("a  b ", SpanStyle.Plain)]);

		// Assert
		_ = sut.ToString().Should().Be("       a b\n");
	}

	/// <summary>
	/// Checks that styled code gets the standard color for each kind of token, and bold text keeps its spaces plain.
	/// </summary>
	[Fact]
	public void Verbatim_StyledCode_ColorsEachKind()
	{
		// Arrange
		var sut = new ManPageWriter(80, styled: true);
		SpanStyle[] styles = [SpanStyle.Keyword, SpanStyle.String, SpanStyle.Comment, SpanStyle.Number, SpanStyle.Type, SpanStyle.Method, SpanStyle.Parameter, SpanStyle.Variable];

		// Act
		sut.Verbatim([[.. styles.Select(style => new Span("x", style)), new Span("a b", SpanStyle.Bold)]], 0);

		// Assert
		_ = sut.ToString().Should().Be("\e[35mx\e[39m\e[33mx\e[39m\e[90mx\e[39m\e[91mx\e[39m\e[32mx\e[39m\e[94mx\e[39m\e[96mx\e[39m\e[36mx\e[39ma\ba b\bb\n");
	}

	/// <summary>
	/// Checks the header when the title leaves just enough room for the manual's name with a space each side, and
	/// when it leaves too little or none, which drops the name and keeps at least one space.
	/// </summary>
	/// <param name="title">The page title.</param>
	/// <param name="expected">The header line.</param>
	[Theory]
	[InlineData("abcdefghijklm(1)", "abcdefghijklm(1) Manual abcdefghijklm(1)\n")]
	[InlineData("abcdefghijklmn(1)", "abcdefghijklmn(1)      abcdefghijklmn(1)\n")]
	[InlineData("abcdefghijklmnopqrstu(1)", "abcdefghijklmnopqrstu(1) abcdefghijklmnopqrstu(1)\n")]
	public void Header_LittleRoom_DropsManualName(string title, string expected)
	{
		// Arrange
		var sut = new ManPageWriter(40, styled: false);

		// Act
		sut.Header(title, "Manual");

		// Assert
		_ = sut.ToString().Should().Be(expected);
	}

	/// <summary>
	/// Checks that a paragraph with no text writes nothing, so a page has no lines of only spaces.
	/// </summary>
	[Fact]
	public void Paragraph_NoText_WritesNothing()
	{
		// Arrange
		var sut = new ManPageWriter(40, styled: false);
		sut.Section("one");

		// Act
		sut.Paragraph([]);
		sut.Paragraph([new Span(string.Empty, SpanStyle.Plain)]);
		sut.Paragraph([new Span("x", SpanStyle.Plain)]);

		// Assert
		_ = sut.ToString().Should().Be("\nONE\n       x\n");
	}
}
