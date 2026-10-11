using FluentAssertions;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Shell.Tests.Features.DotNetDocs;

/// <summary>
/// Tests for <see cref="Links" />.
/// </summary>
public sealed class LinksTests
{
	/// <summary>
	/// Checks the learn.microsoft.com addresses made from doc ids.
	/// </summary>
	/// <param name="id">The doc id.</param>
	/// <param name="expected">The address.</param>
	[Theory]
	[InlineData("System.String", "https://learn.microsoft.com/dotnet/api/system.string")]
	[InlineData("System.String.Split(System.Char[])", "https://learn.microsoft.com/dotnet/api/system.string.split")]
	[InlineData("System.Collections.Generic.List`1.Add*", "https://learn.microsoft.com/dotnet/api/system.collections.generic.list-1.add")]
	[InlineData("System.Console.WriteLine%2A?displayProperty=nameWithType", "https://learn.microsoft.com/dotnet/api/system.console.writeline")]
	public void ApiUrl_DocId_ReturnsLearnAddress(string id, string expected)
	{
		// Act
		var result = Links.ApiUrl(id);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks the addresses links in pages are turned into.
	/// </summary>
	/// <param name="href">The link as written.</param>
	/// <param name="expected">The address, or <see langword="null" /> when it can't be placed.</param>
	[Theory]
	[InlineData("xref:System.String", "https://learn.microsoft.com/dotnet/api/system.string")]
	[InlineData("/dotnet/standard/io/", "https://learn.microsoft.com/dotnet/standard/io/")]
	[InlineData("https://github.com/dotnet", "https://github.com/dotnet")]
	[InlineData("if-else.md#else", "https://learn.microsoft.com/dotnet/csharp/language-reference/statements/if-else#else")]
	[InlineData("../../samples/x.cs", "https://raw.githubusercontent.com/dotnet/docs/main/docs/csharp/samples/x.cs")]
	public void Resolve_Link_ReturnsAddress(string href, string? expected)
	{
		// Arrange
		var page = new Uri(Links.ArticleRepository, "docs/csharp/language-reference/statements/index.md");

		// Act
		var result = Links.Resolve(href, page);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a relative link with no page to place it against isn't a link.
	/// </summary>
	[Fact]
	public void Resolve_RelativeWithoutPage_ReturnsNull()
	{
		// Act
		var result = Links.Resolve("other.md", null);

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks that an API address gives the name to look up, without its locale, arity markers or overload anchor.
	/// </summary>
	[Fact]
	public void TryParse_ApiAddress_ReturnsApiName()
	{
		// Act
		var found = Links.TryParse("https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.add#overloads", out var api, out var article);

		// Assert
		_ = found.Should().BeTrue();
		_ = api.Should().Be("system.collections.generic.list.add");
		_ = article.Should().BeNull();
	}

	/// <summary>
	/// Checks that any other .NET address gives the article's raw address with its anchor.
	/// </summary>
	[Fact]
	public void TryParse_ArticleAddress_ReturnsRawArticle()
	{
		// Act
		var found = Links.TryParse("https://learn.microsoft.com/dotnet/csharp/language-reference/statements/iteration-statements#the-do-statement", out var api, out var article);

		// Assert
		_ = found.Should().BeTrue();
		_ = api.Should().BeNull();
		_ = article!.AbsoluteUri.Should().Be("https://raw.githubusercontent.com/dotnet/docs/main/docs/csharp/language-reference/statements/iteration-statements.md#the-do-statement");
	}

	/// <summary>
	/// Checks that addresses outside .NET on learn.microsoft.com, and names that aren't addresses, aren't taken.
	/// </summary>
	/// <param name="address">The text.</param>
	[Theory]
	[InlineData("https://github.com/dotnet/docs")]
	[InlineData("https://learn.microsoft.com/azure/storage")]
	[InlineData("String.Split")]
	public void TryParse_OtherText_ReturnsFalse(string address)
	{
		// Act
		var found = Links.TryParse(address, out _, out _);

		// Assert
		_ = found.Should().BeFalse();
	}
}
