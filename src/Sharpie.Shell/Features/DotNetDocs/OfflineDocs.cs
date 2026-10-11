using System.Globalization;
using System.Text;
using Sharpie.Shell.Properties;

namespace Sharpie.Shell.Features.DotNetDocs;

/// <summary>
/// Downloads every page dman can show, so it works without the network, and refreshes that copy once it's old.
/// </summary>
/// <remarks>
/// The API reference comes as one archive of Microsoft's <c>dotnet-api-docs</c> repository rather than file by file:
/// one request instead of tens of thousands, which GitHub would start refusing. The language reference is small but
/// lives in the much larger <c>dotnet/docs</c> repository, so its keyword pages are fetched one by one instead.
/// </remarks>
internal static class OfflineDocs
{
	/// <summary>
	/// How old the full copy gets before it's downloaded again in the background. The user chose weekly.
	/// </summary>
	public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

	/// <summary>
	/// How many keyword pages are fetched at once.
	/// </summary>
	private const int ParallelPages = 8;

	private static readonly Uri ApiArchive = new("https://codeload.github.com/dotnet/dotnet-api-docs/tar.gz/refs/heads/main");

	private static readonly CompositeFormat ProgressFormat = CompositeFormat.Parse(Resources.DmanDownloadProgress);
	private static readonly CompositeFormat DoneFormat = CompositeFormat.Parse(Resources.DmanDownloadDone);
	private static readonly CompositeFormat FailedFormat = CompositeFormat.Parse(Resources.DmanDownloadFailed);

	/// <summary>
	/// Downloads every API and keyword page.
	/// </summary>
	/// <param name="source">Where the pages are downloaded from and kept.</param>
	/// <param name="progress">Where progress and the outcome are written, like standard error.</param>
	/// <returns>
	/// <see langword="true" /> when everything was downloaded; <see langword="false" /> when another shell is already
	/// downloading or the download failed, which <paramref name="progress" /> says.
	/// </returns>
	/// <remarks>
	/// The date of the full copy is only recorded once both parts finished, so a download cut short is tried again.
	/// Files saved before the cut stay usable, since each one is written whole.
	/// </remarks>
	public static bool Download(DocsSource source, TextWriter progress)
	{
		using var held = source.TryLock();

		if (held is null)
		{
			progress.WriteLine(Resources.DmanDownloadBusy);

			return false;
		}

		try
		{
			progress.WriteLine(Resources.DmanDownloadingApi);

			var files = source.Import(ApiArchive, Links.ApiRepository, KeepApiFile, count =>
			{
				if (count % 5000 == 0)
				{
					progress.WriteLine(string.Format(CultureInfo.CurrentCulture, ProgressFormat, count));
				}
			});

			progress.WriteLine(Resources.DmanDownloadingKeywords);

			var keywords = ((DocLanguage[])[DocLanguage.CSharp, DocLanguage.VisualBasic]).SelectMany(language => LanguageDocs.Keywords(language, source)).ToList();

			// Rendering a page downloads the article and every example it shows; the page itself is thrown away.
			_ = Parallel.ForEach(keywords, new ParallelOptions { MaxDegreeOfParallelism = ParallelPages }, keyword =>
				_ = LanguageDocs.RenderArticle(keyword.Article, keyword.Word, source, 80, styled: false));

			source.MarkOffline();
			progress.WriteLine(string.Format(CultureInfo.CurrentCulture, DoneFormat, files, keywords.Count, source.CacheFolder));

			return true;
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
		{
			progress.WriteLine(string.Format(CultureInfo.CurrentCulture, FailedFormat, exception.Message));

			return false;
		}
	}

	/// <summary>
	/// Starts downloading everything again in the background when the user keeps an offline copy that's older than
	/// <see cref="RefreshAfter" />.
	/// </summary>
	/// <param name="source">Where the pages are kept.</param>
	/// <param name="now">The current time.</param>
	/// <returns>
	/// <see langword="true" /> when a refresh was started.
	/// </returns>
	/// <remarks>
	/// It runs quietly on a background thread of the shell, so the page asked for opens straight away from the
	/// current copy. The lock in <see cref="Download" /> keeps a second shell from starting another.
	/// </remarks>
	public static bool RefreshIfDue(DocsSource source, DateTimeOffset now)
	{
		if (source.OfflineSince is not { } since || now - since < RefreshAfter)
		{
			return false;
		}

		_ = Task.Run(() => Download(source, TextWriter.Null));

		return true;
	}

	/// <summary>
	/// Decides which files of the API docs archive dman needs.
	/// </summary>
	/// <param name="path">A path inside the repository, like <c>xml/System/String.xml</c>.</param>
	/// <returns>
	/// <see langword="true" /> for type and namespace docs, this .NET version's name index, and C# and Visual Basic
	/// examples.
	/// </returns>
	/// <remarks>
	/// Left out: <c>xml/SourceLinkInformation</c> (about 200 MB that dman never reads), other versions' name indexes
	/// (about 90 MB), and examples in other languages.
	/// </remarks>
	internal static bool KeepApiFile(string path) =>
		(path.StartsWith("xml/", StringComparison.Ordinal)
			&& !path.StartsWith("xml/SourceLinkInformation/", StringComparison.Ordinal)
			&& (!path.StartsWith("xml/FrameworksIndex/", StringComparison.Ordinal) || path.StartsWith($"xml/FrameworksIndex/{ApiDocs.Moniker}", StringComparison.Ordinal)))
		|| path.StartsWith("snippets/csharp/", StringComparison.Ordinal)
		|| path.StartsWith("snippets/visualbasic/", StringComparison.Ordinal);
}
