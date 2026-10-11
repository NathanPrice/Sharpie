using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Shell.Tests.Features.DotNetDocs;

/// <summary>
/// Tests for <see cref="OfflineDocs" /> and the archive import in <see cref="DocsSource" /> it uses.
/// </summary>
public sealed class OfflineDocsTests : IDisposable
{
	private const string Archive = "https://archive.test/repo.tar.gz";
	private const string ApiArchive = "https://codeload.github.com/dotnet/dotnet-api-docs/tar.gz/refs/heads/main";
	private const string Docs = "https://raw.githubusercontent.com/dotnet/docs/main/docs/";

	private static readonly Uri Repository = new("https://raw.test/repo/main/");

	private readonly FakeDocs server = new();
	private readonly ManualTime time = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
	private readonly DocsSource source;

	/// <summary>
	/// Initializes a new instance of the <see cref="OfflineDocsTests" /> class.
	/// </summary>
	public OfflineDocsTests() => this.source = this.server.CreateSource(this.time);

	/// <summary>
	/// Checks that an imported file is read back from the address it would be downloaded from, without asking the
	/// server, and that files the filter drops aren't saved.
	/// </summary>
	[Fact]
	public void Import_KeptFile_IsServedFromCache()
	{
		// Arrange
		this.server.Add(Archive, BuildArchive(("repo-main/xml/System/String.xml", "<Type />"), ("repo-main/snippets/fsharp/a.fs", "let x = 1")));

		// Act
		var count = this.source.Import(new Uri(Archive), Repository, path => path.StartsWith("xml/", StringComparison.Ordinal), _ => { });
		var result = this.source.Get(new Uri(Repository, "xml/System/String.xml"));

		// Assert
		_ = count.Should().Be(1);
		_ = result!.Text.Should().Be("<Type />");
		_ = this.source.IsSaved(new Uri(Repository, "snippets/fsharp/a.fs")).Should().BeFalse();
		_ = this.server.Requests.Should().Equal(Archive);
	}

	/// <summary>
	/// Checks that an archive entry whose path climbs out with <c>..</c> can't write outside the cache folder.
	/// </summary>
	[Fact]
	public void Import_ParentSegments_StaysInsideCacheFolder()
	{
		// Arrange
		var folder = this.server.CreateFolder();
		var cache = Path.Combine(folder, "cache");
		var nested = new DocsSource(this.server.Client, cache, this.time);

		this.server.Add(Archive, BuildArchive(("repo-main/../../../../escaped.txt", "text")));

		// Act
		_ = nested.Import(new Uri(Archive), Repository, _ => true, _ => { });

		// Assert
		_ = Directory.GetFiles(folder, "escaped.txt", SearchOption.AllDirectories)
			.Should().AllSatisfy(path => path.Should().StartWith(cache));
	}

	/// <summary>
	/// Checks which files of the API docs archive are kept.
	/// </summary>
	/// <param name="path">A path inside the repository.</param>
	/// <param name="kept">Whether it's kept.</param>
	[Theory]
	[InlineData("xml/System/String.xml", true)]
	[InlineData("xml/ns-System.IO.xml", true)]
	[InlineData("snippets/csharp/System/String/Split/split.cs", true)]
	[InlineData("snippets/visualbasic/System/String/Split/split.vb", true)]
	[InlineData("snippets/fsharp/System/String/Split/split.fs", false)]
	[InlineData("xml/SourceLinkInformation/x.json", false)]
	[InlineData("xml/FrameworksIndex/net-4.8.xml", false)]
	[InlineData("README.md", false)]
	public void KeepApiFile_Path_KeepsWhatDmanReads(string path, bool kept)
	{
		// Act
		var result = OfflineDocs.KeepApiFile(path);

		// Assert
		_ = result.Should().Be(kept);
	}

	/// <summary>
	/// Checks that this .NET version's name indexes are kept.
	/// </summary>
	[Fact]
	public void KeepApiFile_CurrentIndexes_AreKept()
	{
		// Act
		var result = OfflineDocs.KeepApiFile($"xml/FrameworksIndex/{ApiDocs.Moniker}.xml") && OfflineDocs.KeepApiFile($"xml/FrameworksIndex/{ApiDocs.Moniker}-pp.xml");

		// Assert
		_ = result.Should().BeTrue();
	}

	/// <summary>
	/// Checks that a refresh starts only once the user has an offline copy and it's a week old.
	/// </summary>
	/// <param name="downloaded">Whether <c>dman --download</c> ran.</param>
	/// <param name="days">How many days ago it ran.</param>
	/// <param name="expected">Whether a refresh starts.</param>
	[Theory]
	[InlineData(false, 0, false)]
	[InlineData(true, 6, false)]
	[InlineData(true, 8, true)]
	public void RefreshIfDue_StampAge_StartsOnlyWhenOld(bool downloaded, int days, bool expected)
	{
		// Arrange
		if (downloaded)
		{
			this.source.MarkOffline();
		}

		// Act
		var refresh = OfflineDocs.RefreshIfDue(this.source, this.time.Now + TimeSpan.FromDays(days));

		// Waited for, or the download would recreate the cache folder after Dispose deletes it.
		refresh?.Wait();

		// Assert
		_ = (refresh is not null).Should().Be(expected);
	}

	/// <summary>
	/// Checks that a second download doesn't start while another holds the lock.
	/// </summary>
	[Fact]
	public void Download_WhileLocked_ReportsBusy()
	{
		// Arrange
		using var held = this.source.TryLock();
		using var progress = new StringWriter();

		// Act
		var result = OfflineDocs.Download(this.source, progress);

		// Assert
		_ = held.Should().NotBeNull();
		_ = result.Should().BeFalse();
		_ = progress.ToString().Should().Contain("already downloading");
		_ = this.server.Requests.Should().BeEmpty();
	}

	/// <summary>
	/// Checks a whole download: the API archive with a progress line every 5000 files, then each keyword page in both
	/// languages, then the time is recorded so dman knows when to refresh.
	/// </summary>
	[Fact]
	public void Download_ApiArchiveAndKeywords_SavesAllAndReports()
	{
		// Arrange
		this.server.Add(ApiArchive, BuildArchive([.. Enumerable.Range(0, 5000).Select(index => ($"repo-main/xml/N/T{index}.xml", "<Type />"))]));
		this.server.Add($"{Docs}csharp/language-reference/keywords/index.md", "[`if`](../statements/if.md)");
		this.server.Add($"{Docs}csharp/language-reference/statements/if.md", "Runs a branch.");
		this.server.Add($"{Docs}visual-basic/language-reference/keywords/index.md", "[`Dim`](../statements/dim.md)");
		this.server.Add($"{Docs}visual-basic/language-reference/statements/dim.md", "Declares a variable.");
		using var progress = new StringWriter();

		// Act
		var result = OfflineDocs.Download(this.source, progress);

		// Assert
		_ = result.Should().BeTrue();
		_ = progress.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Should().Equal(
			"dman: downloading the .NET API docs from GitHub, one archive of about 140 MB that unpacks to about 600 MB...",
			"dman: 5000 files saved",
			"dman: downloading the C# and Visual Basic keyword pages...",
			$"dman: saved 5000 API files and 2 keyword pages in {this.source.CacheFolder}. dman downloads them again in the background once they're a week old.");
		_ = this.source.IsSaved(new Uri($"{Docs}csharp/language-reference/statements/if.md")).Should().BeTrue();
		_ = this.source.IsSaved(new Uri($"{Docs}visual-basic/language-reference/statements/dim.md")).Should().BeTrue();
		_ = this.source.OfflineSince.Should().Be(this.time.Now);
	}

	/// <summary>
	/// Checks that a download that can't get the archive reports why and doesn't mark the docs as downloaded.
	/// </summary>
	[Fact]
	public void Download_ArchiveMissing_ReportsFailure()
	{
		// Arrange
		using var progress = new StringWriter();

		// Act
		var result = OfflineDocs.Download(this.source, progress);

		// Assert
		_ = result.Should().BeFalse();
		_ = progress.ToString().Should().Contain("dman: the download stopped: ");
		_ = this.source.OfflineSince.Should().BeNull();
	}

	/// <inheritdoc />
	public void Dispose() => this.server.Dispose();

	/// <summary>
	/// Builds a <c>.tar.gz</c> archive in memory.
	/// </summary>
	/// <param name="files">The entries' paths and contents.</param>
	/// <returns>
	/// The archive's bytes.
	/// </returns>
	private static byte[] BuildArchive(params (string Path, string Contents)[] files)
	{
		using var buffer = new MemoryStream();

		using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
		using (var tar = new TarWriter(gzip))
		{
			foreach (var (path, contents) in files)
			{
				tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(contents)) });
			}
		}

		return buffer.ToArray();
	}
}
