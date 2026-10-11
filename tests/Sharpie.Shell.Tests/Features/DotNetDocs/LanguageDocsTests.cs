using FluentAssertions;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Shell.Tests.Features.DotNetDocs;

/// <summary>
/// Tests for <see cref="LanguageDocs" />, against keyword indexes and an article shaped like the <c>dotnet/docs</c> ones.
/// </summary>
public sealed class LanguageDocsTests : IDisposable
{
	private const string Docs = "https://raw.githubusercontent.com/dotnet/docs/main/docs/";

	private const string Article = """
		---
		title: "Iteration statements"
		description: "Learn about loops."
		---
		# Iteration statements

		Intro.

		## The `foreach` statement

		Runs once per element.

		### Async streams

		Use `await foreach`.

		## The `do` statement

		Runs at least once.
		""";

	private readonly FakeDocs server = new();
	private readonly DocsSource source;

	/// <summary>
	/// Initializes a new instance of the <see cref="LanguageDocsTests" /> class.
	/// </summary>
	public LanguageDocsTests()
	{
		this.server.Add($"{Docs}csharp/language-reference/keywords/index.md", "[`foreach`](../statements/iteration-statements.md#the-foreach-statement)  \n[`do`](../statements/iteration-statements.md#the-do-statement)");
		this.server.Add($"{Docs}csharp/language-reference/statements/iteration-statements.md", Article);
		this.server.Add($"{Docs}visual-basic/language-reference/keywords/index.md", "[`Select`](../statements/select-case-statement.md)");
		this.server.Add($"{Docs}visual-basic/language-reference/statements/select-case-statement.md", "---\ndescription: \"Select...Case Statement\"\n---\n# Select...Case\n\nRuns one group.\n");
		this.server.Add($"{Docs}standard/io/titled.md", "---\ntitle: File and stream I/O\n---\nBody.\n");
		this.server.Add($"{Docs}standard/io/plain.md", "Just text.\n");
		this.source = this.server.CreateSource(new ManualTime(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));
	}

	/// <summary>
	/// Checks that a keyword linked to a heading shows only that heading's part of the article, with its subheadings
	/// as sections.
	/// </summary>
	[Fact]
	public void Render_AnchoredKeyword_ShowsOnlyItsSection()
	{
		// Act
		var result = LanguageDocs.Render("foreach", DocLanguage.CSharp, this.source, 80, styled: false);

		// Assert
		_ = result.Should().Be("""
			foreach(7cs)                 C# Language Reference                  foreach(7cs)

			NAME
			       foreach - The foreach statement

			DESCRIPTION
			       Runs once per element.

			ASYNC STREAMS
			       Use await foreach.

			SEE ALSO
			       https://learn.microsoft.com/dotnet/csharp/language-reference/statements/iteration-statements#the-foreach-statement

			dotnet/docs                   checked 2026-01-15                    foreach(7cs)

			""");
	}

	/// <summary>
	/// Checks that C# keywords match case-sensitively, so <c>Foreach</c> isn't the keyword.
	/// </summary>
	[Fact]
	public void Render_CSharpWrongCase_ReturnsNull()
	{
		// Act
		var result = LanguageDocs.Render("Foreach", DocLanguage.CSharp, this.source, 80, styled: false);

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks that a guide article outside the language references is a <c>7net</c> page in the .NET Guide, named
	/// after its file and described by its title when it has no description.
	/// </summary>
	[Fact]
	public void RenderArticle_GuideWithTitleOnly_UsesTitle()
	{
		// Act
		var result = LanguageDocs.RenderArticle(new Uri($"{Docs}standard/io/titled.md"), null, this.source, 80, styled: false);

		// Assert
		_ = result.Should().Be("""
			titled(7net)                       .NET Guide                       titled(7net)

			NAME
			       titled - File and stream I/O

			DESCRIPTION
			       Body.

			SEE ALSO
			       https://learn.microsoft.com/dotnet/standard/io/titled

			dotnet/docs                   checked 2026-01-15                    titled(7net)

			""");
	}

	/// <summary>
	/// Checks that an article with no front matter has just its name on the NAME line, without a dangling dash.
	/// </summary>
	[Fact]
	public void RenderArticle_NoFrontMatter_NameHasNoDash()
	{
		// Act
		var result = LanguageDocs.RenderArticle(new Uri($"{Docs}standard/io/plain.md"), null, this.source, 80, styled: false);

		// Assert
		_ = result.Should().Contain("NAME\n       plain\n\nDESCRIPTION\n       Just text.\n");
	}

	/// <summary>
	/// Checks that Visual Basic keywords match in any case and use the article's description.
	/// </summary>
	[Fact]
	public void Render_VisualBasicAnyCase_ShowsArticle()
	{
		// Act
		var result = LanguageDocs.Render("select", DocLanguage.VisualBasic, this.source, 80, styled: false);

		// Assert
		_ = result.Should().Be("""
			Select(7vb)             Visual Basic Language Reference              Select(7vb)

			NAME
			       Select - Select...Case Statement

			DESCRIPTION
			       Runs one group.

			SEE ALSO
			       https://learn.microsoft.com/dotnet/visual-basic/language-reference/statements/select-case-statement

			dotnet/docs                    checked 2026-01-15                    Select(7vb)

			""");
	}

	/// <inheritdoc />
	public void Dispose() => this.server.Dispose();
}
