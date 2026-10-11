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

	/// <inheritdoc />
	public void Dispose() => this.server.Dispose();
}
