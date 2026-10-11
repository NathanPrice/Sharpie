using FluentAssertions;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Shell.Tests.Features.DotNetDocs;

/// <summary>
/// Tests for <see cref="MarkdownRenderer" />.
/// </summary>
public sealed class MarkdownRendererTests : IDisposable
{
	private static readonly Uri Page = new("https://docs.test/docs/csharp/page.md");
	private static readonly Uri Root = new("https://docs.test/");

	// One of every block the renderer knows, for Render_WholeArticle_WritesEveryBlock.
	private static readonly string[] Article =
	[
		"# Page title",
		"Intro line one",
		"continues here.<br/>After\\* break.",
		"## First section",
		"Text with ![image](pic.png) and [](xref:System.String) and [label](xref:System.Int32) and [](https://x.test).",
		"### A subsection ###",
		"<!-- a comment",
		"still comment -->",
		"- top item",
		"  continued with enough words that the item has to wrap past the right edge of the page",
		"  - nested item",
		"      - deep",
		"        - deeper",
		"          - deepest",
		"10. numbered item with a long text that keeps going until it has to wrap onto a second line",
		"",
		"After the list.",
		"- again",
		"Paragraph right after.",
		"- after paragraph",
		"> [!div class=\"checklist\"]",
		"> * hidden",
		"After div.",
		"",
		"> [!TIP]",
		"> Use it.",
		"- after quote",
		"- item before code",
		"  ```",
		"  code();",
		"  ```",
		"",
		"[!code-csharp[Main](snippets/Program.cs#1)]",
		"[!code-csharp[Main](snippets/Whole.cs)]",
		"[!code-fsharp[Main](snippets/Program.cs#1)]",
		":::code language=\"csharp\" source=\"~/samples/Root.cs\" id=\"Main\":::",
		":::code language=\"csharp\" source=\"snippets/Program.cs\" id=\"Snippet1\" range=\"2-3\":::",
		":::code language=\"fsharp\" source=\"snippets/Program.cs\":::",
		":::row:::",
		"~~~json",
		"\t{",
		"\t\t\"a\": 1",
		"\t}",
		"~~~",
		"```fsharp",
		"let x = 1",
		"```",
		"| Name | Kind | Notes |",
		"|:-----|:----:|------:|",
		"| `a` | int | A sentence long enough to make this table read better as a list of entries. | extra |",
		"| b | text |",
		"Following text.",
		"",
		"| Short | Table |",
		"|---|---|",
		"| x | y |",
		"| - | minus |",
		"| only |",
		"",
		"| Key | abcdefghijklmnopqrstuvwxyz0123456789 |",
		"| k | v |",
		"",
		"| Key | abcdefghijklmnopqrstuvwxyz0123456789! |",
		"| k | v |",
		"",
		"| Lonely | header with a sentence far too long to fit in half of the page |",
		"## Last section",
		"End.",
		"```cs",
		"unclosed();",
	];

	private readonly FakeDocs server = new();
	private readonly ManPageWriter writer = new(80, styled: false);
	private readonly MarkdownRenderer sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="MarkdownRendererTests" /> class.
	/// </summary>
	public MarkdownRendererTests()
	{
		this.sut = new MarkdownRenderer(this.writer, this.server.CreateSource(TimeProvider.System), DocLanguage.CSharp);
	}

	/// <summary>
	/// Checks that code, bold and emphasis get man styles, and links and API references show their text linked to
	/// learn.microsoft.com.
	/// </summary>
	[Fact]
	public void Inline_MixedMarkup_StylesAndLinks()
	{
		// Act
		var result = MarkdownRenderer.Inline("Use `Add`, **not** *this*, [the docs](../guide/x.md#part) or <xref:System.String.Split*>.", Page);

		// Assert
		_ = result.Should().Equal(
			new Span("Use ", SpanStyle.Plain),
			new Span("Add", SpanStyle.Bold),
			new Span(", ", SpanStyle.Plain),
			new Span("not", SpanStyle.Bold),
			new Span(" ", SpanStyle.Plain),
			new Span("this", SpanStyle.Underline),
			new Span(", ", SpanStyle.Plain),
			new Span("the docs", SpanStyle.Plain, "https://docs.test/docs/guide/x.md#part"),
			new Span(" or ", SpanStyle.Plain),
			new Span("Split", SpanStyle.Bold, "https://learn.microsoft.com/dotnet/api/system.string.split"),
			new Span(".", SpanStyle.Plain));
	}

	/// <summary>
	/// Checks that a relative link in an article of Microsoft's docs repository points to the same article on
	/// learn.microsoft.com.
	/// </summary>
	[Fact]
	public void Inline_RelativeLinkInDocsRepository_LinksToLearn()
	{
		// Arrange
		var page = new Uri(Links.ArticleRepository, "docs/csharp/language-reference/keywords/index.md");

		// Act
		var result = MarkdownRenderer.Inline("[`if`](../statements/selection-statements.md#the-if-statement)", page);

		// Assert
		_ = result.Should().Equal(new Span("if", SpanStyle.Bold, "https://learn.microsoft.com/dotnet/csharp/language-reference/statements/selection-statements#the-if-statement"));
	}

	/// <summary>
	/// Checks the names shown for API references.
	/// </summary>
	/// <param name="xref">The reference id.</param>
	/// <param name="expected">The name shown.</param>
	[Theory]
	[InlineData("System.String", "String")]
	[InlineData("System.String.Split*", "Split")]
	[InlineData("System.Collections.Generic.List`1", "List")]
	[InlineData("System.IO.File.ReadAllText(System.String)", "ReadAllText")]
	[InlineData("System.Collections.IEnumerable?displayProperty=nameWithType", "System.Collections.IEnumerable")]
	[InlineData("System.Console.WriteLine%2A?displayProperty=fullName", "System.Console.WriteLine")]
	public void XrefName_Id_ReturnsDisplayName(string xref, string expected)
	{
		// Act
		var result = MarkdownRenderer.XrefName(xref);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a named region is cut out of a code file, without its markers or the indent it shared.
	/// </summary>
	[Fact]
	public void CutRegion_NamedRegion_ReturnsDedentedLines()
	{
		// Arrange
		const string Code = "using System;\n    // <Snippet1>\n    var x = 1;\n    // <Inner>\n      x++;\n    // </Inner>\n    // </Snippet1>\nother();";

		// Act
		var result = MarkdownRenderer.CutRegion(Code, "Snippet1");

		// Assert
		_ = result.Should().Equal("var x = 1;", "  x++;");
	}

	/// <summary>
	/// Checks that asking for a region a file doesn't have gives nothing.
	/// </summary>
	[Fact]
	public void CutRegion_MissingRegion_ReturnsNull()
	{
		// Act
		var result = MarkdownRenderer.CutRegion("code();", "Snippet9");

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks that the section under an anchored heading is cut out, stopping at the next heading of its level and
	/// ignoring heading-like lines in code.
	/// </summary>
	[Fact]
	public void CutSection_Anchor_ReturnsThatSection()
	{
		// Arrange
		const string Markdown = "# Title\n## The `foreach` statement\nBody.\n```\n# not a heading\n```\n### Detail\nMore.\n## The `do` statement\nOther.";

		// Act
		var result = MarkdownRenderer.CutSection(Markdown, "the-foreach-statement");

		// Assert
		_ = result.Should().NotBeNull();
		_ = result!.Value.Heading.Should().Be("The foreach statement");
		_ = result.Value.Level.Should().Be(2);
		_ = result.Value.Body.Should().Be("Body.\n```\n# not a heading\n```\n### Detail\nMore.");
	}

	/// <summary>
	/// Checks that headings at the section level become sections, text before them goes under the default section,
	/// and code keeps its lines unwrapped.
	/// </summary>
	[Fact]
	public void Render_HeadingsAndCode_WritesSectionsAndVerbatimCode()
	{
		// Arrange
		const string Markdown = "# Title\n\nIntro text.\n\n## Examples\n\n```csharp\nif (a)\n    b();\n```\n";

		// Act
		this.sut.Render(Markdown, Page, Root, 2, "DESCRIPTION");

		// Assert
		_ = this.writer.ToString().Should().Be("\nDESCRIPTION\n       Intro text.\n\nEXAMPLES\n           if (a)\n               b();\n");
	}

	/// <summary>
	/// Checks that a code block in another .NET language is dropped from a C# page.
	/// </summary>
	[Fact]
	public void Render_VisualBasicFence_IsDroppedFromCSharpPage()
	{
		// Act
		this.sut.Render("```vb\nDim x = 1\n```\n\n```csharp\nvar x = 1;\n```", Page, Root, 2, null);

		// Assert
		_ = this.writer.ToString().Should().Be("           var x = 1;\n");
	}

	/// <summary>
	/// Checks that list items hang their text after a bullet, one after another.
	/// </summary>
	[Fact]
	public void Render_List_WritesBulletedItems()
	{
		// Act
		this.sut.Render("- one\n- two", Page, Root, 2, null);

		// Assert
		_ = this.writer.ToString().Should().Be("       • one\n       • two\n");
	}

	/// <summary>
	/// Checks that a table of short cells keeps its columns lined up.
	/// </summary>
	[Fact]
	public void Render_ShortTable_AlignsColumns()
	{
		// Act
		this.sut.Render("| A | Long head |\n|---|---|\n| xx | y |", Page, Root, 2, null);

		// Assert
		_ = this.writer.ToString().Should().Be("       A   Long head\n       xx  y\n");
	}

	/// <summary>
	/// Checks that a table with sentences in it becomes a list with the first cell as each entry's tag.
	/// </summary>
	[Fact]
	public void Render_ProseTable_WritesTaggedEntries()
	{
		// Arrange
		var sentence = string.Join(' ', Enumerable.Repeat("words", 12));

		// Act
		this.sut.Render($"| Term | Definition |\n|---|---|\n| item | {sentence} |", Page, Root, 2, null);

		// Assert
		_ = this.writer.ToString().Should().StartWith("       item\n              words words");
	}

	/// <summary>
	/// Checks that a <c>:::code</c> line downloads the example relative to the page and shows only its region.
	/// </summary>
	[Fact]
	public void Render_CodeReference_ShowsDownloadedRegion()
	{
		// Arrange
		this.server.Add("https://docs.test/docs/csharp/snippets/Program.cs", "// <Loop>\nfor (;;) { }\n// </Loop>\nrest();");

		// Act
		this.sut.Render(":::code language=\"csharp\" source=\"snippets/Program.cs\" id=\"Loop\":::", Page, Root, 2, null);

		// Assert
		_ = this.writer.ToString().Should().Be("           for (;;) { }\n");
	}

	/// <summary>
	/// Checks that a note becomes a labeled paragraph.
	/// </summary>
	[Fact]
	public void Render_Note_WritesLabel()
	{
		// Act
		this.sut.Render("> [!NOTE]\n> Be careful.", Page, Root, 2, null);

		// Assert
		_ = this.writer.ToString().Should().Be("       NOTE: Be careful.\n");
	}

	/// <summary>
	/// Checks a whole article with every kind of block: dropped title, default section, subsections, comments, nested
	/// and numbered list items that wrap, quotes, downloaded examples, fenced code, and both kinds of table, with text
	/// right after a block that ends it.
	/// </summary>
	[Fact]
	public void Render_WholeArticle_WritesEveryBlock()
	{
		// Arrange
		this.server.Add("https://docs.test/docs/csharp/snippets/Program.cs", "outside();\r\n// <Snippet1>\r\n\tfirst();\r\n\t\tsecond();\r\n\tthird();\r\n// </Snippet1>");
		this.server.Add("https://docs.test/docs/csharp/snippets/Whole.cs", "whole();");
		this.server.Add("https://docs.test/samples/Root.cs", "// <Main>\nroot();\n");

		// Act
		this.sut.Render(string.Join("\r\n", Article), Page, Root, 2, "DESCRIPTION");

		// Assert
		_ = this.writer.ToString().Should().Be("""

			DESCRIPTION
			       Intro line one continues here. After* break.

			FIRST SECTION
			       Text with and String and label and .

			   A subsection
			       • top item continued with enough words that the item has to wrap past the
			         right edge of the page
			         • nested item
			             • deep
			             • deeper
			             • deepest
			       10. numbered item with a long text that keeps going until it has to wrap
			           onto a second line

			       After the list.

			       • again

			       Paragraph right after.

			       • after paragraph

			       After div.

			       TIP: Use it.

			       • after quote
			       • item before code

			           code();

			           first();
			               second();
			           third();

			           whole();

			           root();

			               second();
			           third();

			           {
			               "a": 1
			           }

			       a
			              Kind: int Notes: A sentence long enough to make this table read
			              better as a list of entries. Notes: extra

			       b
			              Kind: text

			       Following text.

			       Short  Table
			       x      y
			       -      minus
			       only

			       Key  abcdefghijklmnopqrstuvwxyz0123456789
			       k    v

			       k
			              v

			       Lonely  header with a sentence far too long to fit in half of the page

			LAST SECTION
			       End.

			           unclosed();

			""");
	}

	/// <summary>
	/// Checks that a Visual Basic page keeps Visual Basic code and code in no .NET language, and drops C#, F# and C++
	/// under every name the docs use for them.
	/// </summary>
	[Fact]
	public void Render_VisualBasicPage_KeepsVisualBasicAndPlainCode()
	{
		// Arrange
		var writer = new ManPageWriter(80, styled: false);
		var sut = new MarkdownRenderer(writer, this.server.CreateSource(TimeProvider.System), DocLanguage.VisualBasic);
		string[] names = ["csharp", "cs", "c#", "vb", "vbnet", "visualbasic", "fsharp", "f#", "cpp", "c++", "cppcli", "json"];

		// Act
		sut.Render(string.Concat(names.Select(name => $"```{name}\n{name}\n```\n")), Page, Root, 2, null);

		// Assert
		_ = writer.ToString().Should().Be("           vb\n\n           vbnet\n\n           visualbasic\n\n           json\n");
	}

	/// <summary>
	/// Checks that a styled page colors C# code but leaves code in other languages, like json, plain.
	/// </summary>
	[Fact]
	public void Render_StyledPage_ColorsOnlyDotNetCode()
	{
		// Arrange
		var writer = new ManPageWriter(80, styled: true);
		var sut = new MarkdownRenderer(writer, this.server.CreateSource(TimeProvider.System), DocLanguage.CSharp);

		// Act
		sut.Render("```cs\nvar x = \"a\";\n```\n```json\nvar x = \"a\";\n```", Page, Root, 2, null);

		// Assert
		var blocks = writer.ToString().Split("\n\n");
		_ = blocks[0].Should().Contain("\e[");
		_ = blocks[1].Should().Be("           var x = \"a\";\n");
	}

	/// <summary>
	/// Checks that a styled table shows its heading row in bold.
	/// </summary>
	[Fact]
	public void Render_StyledTable_BoldsHeadingRow()
	{
		// Arrange
		var writer = new ManPageWriter(80, styled: true);
		var sut = new MarkdownRenderer(writer, this.server.CreateSource(TimeProvider.System), DocLanguage.CSharp);

		// Act
		sut.Render("| H | I |\n|---|---|\n| d | e |", Page, Root, 2, null);

		// Assert
		_ = writer.ToString().Should().Be("       H\bH  I\bI\n       d  e\n");
	}

	/// <summary>
	/// Checks that a deeper heading before any section heading still goes under the default section.
	/// </summary>
	[Fact]
	public void Render_SubsectionFirst_StartsDefaultSection()
	{
		// Act
		this.sut.Render("### Sub\nText.", Page, Root, 2, "DESCRIPTION");

		// Assert
		_ = this.writer.ToString().Should().Be("\nDESCRIPTION\n   Sub\n       Text.\n");
	}

	/// <summary>
	/// Checks that a blank line between two lines of text makes two paragraphs.
	/// </summary>
	[Fact]
	public void Render_BlankLineBetweenText_MakesTwoParagraphs()
	{
		// Act
		this.sut.Render("one\n\ntwo", Page, Root, 2, null);

		// Assert
		_ = this.writer.ToString().Should().Be("       one\n\n       two\n");
	}

	/// <summary>
	/// Checks that a comment that's never closed hides the rest of the text, without running past the end.
	/// </summary>
	[Fact]
	public void Render_UnclosedComment_HidesRest()
	{
		// Act
		this.sut.Render("a\n\n<!-- never closed\nb", Page, Root, 2, null);

		// Assert
		_ = this.writer.ToString().Should().Be("       a\n");
	}

	/// <summary>
	/// Checks that a heading's anchor drops its punctuation, as learn.microsoft.com's does.
	/// </summary>
	[Fact]
	public void CutSection_HeadingWithPunctuation_FindsItByAnchor()
	{
		// Act
		var result = MarkdownRenderer.CutSection("## What's new?\nText.", "whats-new");

		// Assert
		_ = result!.Value.Body.Should().Be("Text.");
	}

	/// <summary>
	/// Checks that a styled Visual Basic page colors its Visual Basic code.
	/// </summary>
	[Fact]
	public void Render_StyledVisualBasicPage_ColorsVisualBasicCode()
	{
		// Arrange
		var writer = new ManPageWriter(80, styled: true);
		var sut = new MarkdownRenderer(writer, this.server.CreateSource(TimeProvider.System), DocLanguage.VisualBasic);

		// Act
		sut.Render("```vb\nDim x = 1\n```", Page, Root, 2, null);

		// Assert
		_ = writer.ToString().Should().Contain("\e[");
	}

	/// <summary>
	/// Checks that each call starts afresh, so a list at the end of one text doesn't run into a list in the next, and
	/// a paragraph at the very end is written.
	/// </summary>
	[Fact]
	public void Render_CalledAgain_StartsFresh()
	{
		// Act
		this.sut.Render("- a", Page, Root, 2, null);
		this.sut.Render("- b\nlast words", Page, Root, 2, null);

		// Assert
		_ = this.writer.ToString().Should().Be("       • a\n\n       • b\n\n       last words\n");
	}

	/// <summary>
	/// Checks that emphasis around bold text underlines the plain parts and leaves the bold part bold.
	/// </summary>
	[Fact]
	public void Inline_BoldInsideEmphasis_KeepsBold()
	{
		// Act
		var result = MarkdownRenderer.Inline("_under **bold** under_");

		// Assert
		_ = result.Should().Equal(
			new Span("under ", SpanStyle.Underline),
			new Span("bold", SpanStyle.Bold),
			new Span(" under", SpanStyle.Underline));
	}

	/// <summary>
	/// Checks how regions are cut at the edges: an empty region, blank lines around it, a blank line inside it, and a
	/// region that is never closed.
	/// </summary>
	/// <param name="code">The code file.</param>
	/// <param name="expected">The region's lines.</param>
	[Theory]
	[InlineData("// <A>\n// </A>", new string[0])]
	[InlineData("// <A>\n\n  x\n\n// </A>", new[] { "x" })]
	[InlineData("// <A>\n  a\n\n  b\n// </A>", new[] { "a", "", "b" })]
	[InlineData("// <A>\nx\ny", new[] { "x", "y" })]
	public void CutRegion_EdgeCases_TrimsAndDedents(string code, string[] expected)
	{
		// Act
		var result = MarkdownRenderer.CutRegion(code, "A");

		// Assert
		_ = result.Should().Equal(expected);
	}

	/// <inheritdoc />
	public void Dispose() => this.server.Dispose();
}
