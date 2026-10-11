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
		this.source = this.server.CreateSource(TimeProvider.System);
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
		_ = result.Should().StartWith("foreach(7cs)")
			.And.Contain("NAME\n       foreach - The foreach statement\n")
			.And.Contain("DESCRIPTION\n       Runs once per element.\n")
			.And.Contain("ASYNC STREAMS\n       Use await foreach.\n")
			.And.Contain("https://learn.microsoft.com/dotnet/csharp/language-reference/statements/iteration-statements#the-foreach-statement")
			.And.NotContain("Runs at least once");
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
	/// Checks that Visual Basic keywords match in any case and use the article's description.
	/// </summary>
	[Fact]
	public void Render_VisualBasicAnyCase_ShowsArticle()
	{
		// Act
		var result = LanguageDocs.Render("select", DocLanguage.VisualBasic, this.source, 80, styled: false);

		// Assert
		_ = result.Should().StartWith("Select(7vb)")
			.And.Contain("NAME\n       Select - Select...Case Statement\n")
			.And.Contain("DESCRIPTION\n       Runs one group.\n");
	}

	/// <inheritdoc />
	public void Dispose() => this.server.Dispose();
}
