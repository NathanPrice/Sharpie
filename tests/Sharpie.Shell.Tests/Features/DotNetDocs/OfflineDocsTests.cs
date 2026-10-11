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
		var folder = Directory.CreateTempSubdirectory("dman-tests-").FullName;
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
		var result = OfflineDocs.RefreshIfDue(this.source, this.time.Now + TimeSpan.FromDays(days));

		// Assert
		_ = result.Should().Be(expected);
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
