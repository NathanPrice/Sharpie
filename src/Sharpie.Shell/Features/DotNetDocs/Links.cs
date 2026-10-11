using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Sharpie.Shell.Features.DotNetDocs;

/// <summary>
/// Maps between the links in Microsoft's docs, the learn.microsoft.com addresses pages link to, and the files dman
/// reads.
/// </summary>
/// <remarks>
/// Pages always link to learn.microsoft.com, never to the raw files, so a link clicked in a terminal opens the same
/// page in a browser, and dman turns the address back into the API or article it names.
/// </remarks>
internal static partial class Links
{
	/// <summary>
	/// The raw address of the root of the <c>dotnet/dotnet-api-docs</c> repository, which holds the API reference.
	/// </summary>
	public static readonly Uri ApiRepository = new("https://raw.githubusercontent.com/dotnet/dotnet-api-docs/main/");

	/// <summary>
	/// The raw address of the root of the <c>dotnet/docs</c> repository, which holds the articles.
	/// </summary>
	public static readonly Uri ArticleRepository = new("https://raw.githubusercontent.com/dotnet/docs/main/");

	private const string Learn = "https://learn.microsoft.com";

	/// <summary>
	/// Gets the learn.microsoft.com address of an API.
	/// </summary>
	/// <param name="id">
	/// The API's doc id without its prefix, like <c>System.String.Split(System.Char[])</c> or
	/// <c>System.Collections.Generic.List`1</c>, optionally with a <c>?displayProperty=</c> query.
	/// </param>
	/// <returns>
	/// The address, like <c>https://learn.microsoft.com/dotnet/api/system.collections.generic.list-1</c>. Overloads
	/// share one page, so parameters are dropped, and generic arity is written <c>-1</c> as the site does.
	/// </returns>
	[SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "learn.microsoft.com addresses are lowercase.")]
	public static string ApiUrl(string id)
	{
		var query = id.IndexOf('?', StringComparison.Ordinal);
		var name = Uri.UnescapeDataString(query < 0 ? id : id[..query]);
		var parenthesis = name.IndexOf('(', StringComparison.Ordinal);

		name = (parenthesis < 0 ? name : name[..parenthesis]).TrimEnd('*').Replace("#ctor", "-ctor", StringComparison.Ordinal);

		return $"{Learn}/dotnet/api/{ArityPattern().Replace(name, "-$1").ToLowerInvariant()}";
	}

	/// <summary>
	/// Turns a link found in a page into a learn.microsoft.com address.
	/// </summary>
	/// <param name="href">The link as written, like <c>xref:System.String</c>, <c>../statements/if.md#else</c>, <c>/dotnet/standard/io</c> or a full address.</param>
	/// <param name="page">The raw address of the page the link is in, for relative links; or <see langword="null" /> when unknown.</param>
	/// <returns>
	/// The address, or <see langword="null" /> when the link can't be placed, like a relative link with no page.
	/// </returns>
	public static string? Resolve(string href, Uri? page)
	{
		return href.StartsWith("xref:", StringComparison.Ordinal) ? ApiUrl(href[5..])
			: href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? href
			: href.StartsWith('/') ? Learn + href
			: page is null || href.Length == 0 ? null
			: ArticleUrl(new Uri(page, href));
	}

	/// <summary>
	/// Gets the learn.microsoft.com address of an article in the <c>dotnet/docs</c> repository.
	/// </summary>
	/// <param name="article">The article's raw address, with any anchor.</param>
	/// <returns>
	/// The web address, like <c>https://learn.microsoft.com/dotnet/csharp/language-reference/statements/iteration-statements#the-foreach-statement</c>;
	/// or the raw address itself for a file outside the articles, like a sample.
	/// </returns>
	public static string ArticleUrl(Uri article)
	{
		var docs = ArticleRepository.AbsolutePath + "docs/";
		var path = article.AbsolutePath;

		return !path.StartsWith(docs, StringComparison.Ordinal) || !path.EndsWith(".md", StringComparison.Ordinal)
			? article.AbsoluteUri
			: $"{Learn}/dotnet/{path[docs.Length..^".md".Length]}{article.Fragment}";
	}

	/// <summary>
	/// Finds what a learn.microsoft.com address names.
	/// </summary>
	/// <param name="address">The address.</param>
	/// <param name="api">The API name to look up, like <c>system.collections.generic.list.add</c>, for an API page.</param>
	/// <param name="article">The article's raw address with its anchor, for any other .NET page.</param>
	/// <returns>
	/// <see langword="true" /> when the address is a .NET page on learn.microsoft.com; otherwise, <see langword="false" />.
	/// </returns>
	/// <remarks>
	/// A locale like <c>/en-us</c> is skipped. On an API page the <c>-1</c> arity markers are dropped, since the name
	/// index matches names without them, and the anchor is dropped, since it only picks an overload on the web page.
	/// </remarks>
	public static bool TryParse(string address, out string? api, out Uri? article)
	{
		api = null;
		article = null;

		if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || !url.Host.Equals("learn.microsoft.com", StringComparison.OrdinalIgnoreCase)
			|| LearnPathPattern().Match(url.AbsolutePath) is not { Success: true } match)
		{
			return false;
		}

		var path = match.Groups["path"].Value.TrimEnd('/');

		if (path.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
		{
			api = UrlArityPattern().Replace(path[4..], string.Empty);

			return api.Length > 0;
		}

		article = new Uri(ArticleRepository, $"docs/{path}.md{url.Fragment}");

		return path.Length > 0;
	}

	[GeneratedRegex(@"`+(\d+)")]
	private static partial Regex ArityPattern();

	[GeneratedRegex(@"-\d+(?=\.|$)")]
	private static partial Regex UrlArityPattern();

	[GeneratedRegex(@"^(?:/[a-z]{2}-[a-z]{2})?/dotnet/(?<path>.+)$", RegexOptions.IgnoreCase)]
	private static partial Regex LearnPathPattern();
}
