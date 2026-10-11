using System.Globalization;
using System.Text.RegularExpressions;
using Sharpie.Shell.Properties;

namespace Sharpie.Shell.Features.DotNetDocs;

/// <summary>
/// Builds man pages for C# and Visual Basic keywords from the language reference in Microsoft's <c>dotnet/docs</c>
/// repository.
/// </summary>
/// <remarks>
/// Each language's keyword index links every keyword to the article that explains it, often to one heading inside a
/// longer article, like <c>foreach</c> to "The foreach statement" in the iteration statements article. The page shows
/// just that part.
/// </remarks>
internal static partial class LanguageDocs
{
	/// <summary>
	/// Builds the page for a keyword.
	/// </summary>
	/// <param name="keyword">The keyword, like <c>foreach</c> or <c>Select</c>.</param>
	/// <param name="language">The language whose keywords to look in.</param>
	/// <param name="source">Where the docs are downloaded from.</param>
	/// <param name="width">The number of columns to fill.</param>
	/// <param name="styled"><see langword="true" /> for bold and underline; <see langword="false" /> for plain text.</param>
	/// <returns>
	/// The page, or <see langword="null" /> when the word isn't a keyword or the docs can't be downloaded.
	/// </returns>
	/// <remarks>
	/// C# keywords match case-sensitively, so <c>string</c> is the keyword and <c>String</c> falls through to the API
	/// page. Visual Basic ignores case, as the language does.
	/// </remarks>
	public static string? Render(string keyword, DocLanguage language, DocsSource source, int width, bool styled)
	{
		var comparison = language == DocLanguage.CSharp ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		var (word, target) = Keywords(language, source).FirstOrDefault(entry => entry.Word.Equals(keyword, comparison));

		return target is null ? null : RenderArticle(target, word, source, width, styled);
	}

	/// <summary>
	/// Lists a language's keywords with the articles that explain them.
	/// </summary>
	/// <param name="language">The language.</param>
	/// <param name="source">Where the keyword index is downloaded from.</param>
	/// <returns>
	/// Each keyword with its article's raw address and anchor, in the index's order; nothing when the index can't be
	/// downloaded.
	/// </returns>
	public static IEnumerable<(string Word, Uri Article)> Keywords(DocLanguage language, DocsSource source)
	{
		var indexUrl = new Uri(Links.ArticleRepository, language == DocLanguage.CSharp
			? "docs/csharp/language-reference/keywords/index.md"
			: "docs/visual-basic/language-reference/keywords/index.md");

		return source.Get(indexUrl) is { } index
			? KeywordLinkPattern().Matches(index.Text).Select(match => (match.Groups["word"].Value, new Uri(indexUrl, match.Groups["href"].Value)))
			: [];
	}

	/// <summary>
	/// Builds the page for an article in the <c>dotnet/docs</c> repository, or for the part of it under one heading.
	/// </summary>
	/// <param name="article">The article's raw address, with an anchor to show only that heading's part.</param>
	/// <param name="name">The page name, like a keyword, or <see langword="null" /> to use the article's file name.</param>
	/// <param name="source">Where the docs are downloaded from.</param>
	/// <param name="width">The number of columns to fill.</param>
	/// <param name="styled"><see langword="true" /> for bold and underline; <see langword="false" /> for plain text.</param>
	/// <returns>
	/// The page, or <see langword="null" /> when the article can't be downloaded.
	/// </returns>
	/// <remarks>
	/// Examples are shown in the article's own language: Visual Basic under <c>docs/visual-basic</c>, C# everywhere
	/// else.
	/// </remarks>
	public static string? RenderArticle(Uri article, string? name, DocsSource source, int width, bool styled)
	{
		var pageUrl = new Uri(article.GetLeftPart(UriPartial.Query));

		if (source.Get(pageUrl) is not { } page)
		{
			return null;
		}

		var area = article.AbsolutePath[(Links.ArticleRepository.AbsolutePath.Length + "docs/".Length)..];
		var language = area.StartsWith("visual-basic/", StringComparison.Ordinal) ? DocLanguage.VisualBasic : DocLanguage.CSharp;
		var (description, body) = FrontMatter(page.Text);
		var sectionLevel = 2;
		var anchor = article.Fragment.TrimStart('#');

		if (anchor.Length > 0 && MarkdownRenderer.CutSection(body, anchor) is { } section)
		{
			description = section.Heading;
			body = section.Body;
			sectionLevel = section.Level + 1;
		}

		var word = name ?? Path.GetFileNameWithoutExtension(pageUrl.AbsolutePath);
		var title = $"{word}({(area.StartsWith("csharp/", StringComparison.Ordinal) ? "7cs" : language == DocLanguage.VisualBasic ? "7vb" : "7net")})";
		var manual = (area.StartsWith("csharp/", StringComparison.Ordinal) ? "C# " : language == DocLanguage.VisualBasic ? "Visual Basic " : ".NET ")
			+ (area.Contains("/language-reference/", StringComparison.Ordinal) ? "Language Reference" : "Guide");
		var writer = new ManPageWriter(width, styled);

		writer.Header(title, manual);
		writer.Section("NAME");
		writer.Paragraph(description.Length == 0
			? [new Span(word, SpanStyle.Bold)]
			: [new Span(word, SpanStyle.Bold), new Span(Resources.DmanNameSeparator, SpanStyle.Plain), .. MarkdownRenderer.Inline(description, pageUrl)]);
		new MarkdownRenderer(writer, source, language).Render(body, pageUrl, Links.ArticleRepository, sectionLevel, "DESCRIPTION");
		writer.Section("SEE ALSO");

		var learnUrl = Links.ArticleUrl(article);

		writer.Paragraph([new Span(learnUrl, SpanStyle.Plain, learnUrl)]);
		writer.Footer("dotnet/docs", $"checked {page.Checked.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", title);

		return writer.ToString();
	}

	/// <summary>
	/// Splits the YAML front matter off an article.
	/// </summary>
	/// <param name="markdown">The article.</param>
	/// <returns>
	/// The front matter's <c>description</c>, or its <c>title</c> without one, and the markdown after it.
	/// </returns>
	private static (string Description, string Body) FrontMatter(string markdown)
	{
		var match = FrontMatterPattern().Match(markdown);

		if (!match.Success)
		{
			return (string.Empty, markdown);
		}

		var fields = FieldPattern().Matches(match.Groups[1].Value).DistinctBy(field => field.Groups[1].Value).ToDictionary(field => field.Groups[1].Value, field => field.Groups[2].Value.Trim().Trim('"', '\''), StringComparer.Ordinal);

		return (fields.GetValueOrDefault("description") ?? fields.GetValueOrDefault("title") ?? string.Empty, markdown[match.Length..]);
	}

	[GeneratedRegex(@"\[`(?<word>[^`]+)`\]\((?<href>[^)\s]+)\)")]
	private static partial Regex KeywordLinkPattern();

	[GeneratedRegex(@"\A﻿?---\r?\n(.*?)\r?\n---\r?\n", RegexOptions.Singleline)]
	private static partial Regex FrontMatterPattern();

	[GeneratedRegex(@"^(\w+):[ \t]*(.+)$", RegexOptions.Multiline)]
	private static partial Regex FieldPattern();
}
