using System.Text;
using FluentAssertions;
using Sharpie.Features.Ansi;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Tests.Features.ManPages;

/// <summary>
/// Checks that pages <see cref="ManPageWriter" /> builds for <c>dman</c> read in the window's reader the way
/// <c>man</c> pages do.
/// </summary>
public sealed class DmanPageTests
{
	/// <summary>
	/// Checks that the window's <see cref="AnsiParser" /> turns a page's headings and names bold and its parameters
	/// underlined, with no overstrike left in the text.
	/// </summary>
	[Fact]
	public void Parse_DmanPage_StylesLikeManPage()
	{
		// Arrange
		var writer = new ManPageWriter(80, styled: true);
		var plain = new StringBuilder();
		var runs = new List<AnsiRun>();

		writer.Section("Name");
		writer.Paragraph([new Span("Split", SpanStyle.Bold), new Span(" takes ", SpanStyle.Plain), new Span("separator", SpanStyle.Underline)]);

		// Act
		new AnsiParser().Parse(writer.ToString(), plain, runs);

		// Assert
		_ = plain.ToString().Should().Be("\nNAME\n       Split takes separator\n");
		_ = runs.Select(run => (plain.ToString(run.Start, run.Length), run.Style.Bold)).Should().Equal(("NAME", true), ("Split", true), ("separator", false));
	}

	/// <summary>
	/// Checks that the window's <see cref="AnsiParser" /> reads the colors <see cref="CodeHighlighter" /> gives example
	/// code as the standard colors the shell's palette draws.
	/// </summary>
	[Fact]
	public void Parse_HighlightedCode_UsesStandardColors()
	{
		// Arrange
		var writer = new ManPageWriter(80, styled: true);
		var plain = new StringBuilder();
		var runs = new List<AnsiRun>();

		writer.Verbatim(CodeHighlighter.Highlight(["var s = \"x\"; // c"], DocLanguage.CSharp), 0);

		// Act
		new AnsiParser().Parse(writer.ToString(), plain, runs);

		// Assert
		_ = plain.ToString().Should().Be("var s = \"x\"; // c\n");
		_ = runs.Select(run => (plain.ToString(run.Start, run.Length), run.Style.Foreground?.Index)).Should().Equal(("var", 5), ("s", 6), ("\"x\"", 3), ("// c", 8));
	}
}
