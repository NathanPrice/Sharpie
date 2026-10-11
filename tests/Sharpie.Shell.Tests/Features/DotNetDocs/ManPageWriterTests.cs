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
}
