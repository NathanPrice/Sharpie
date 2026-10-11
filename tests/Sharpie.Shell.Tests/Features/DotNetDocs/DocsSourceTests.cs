using FluentAssertions;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Shell.Tests.Features.DotNetDocs;

/// <summary>
/// Tests for <see cref="DocsSource" />.
/// </summary>
public sealed class DocsSourceTests : IDisposable
{
	private const string Address = "https://docs.test/xml/System/String.xml";

	private readonly FakeDocs server = new();
	private readonly ManualTime time = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
	private readonly DocsSource sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="DocsSourceTests" /> class.
	/// </summary>
	public DocsSourceTests()
	{
		this.server.Add(Address, "first");
		this.sut = this.server.CreateSource(this.time);
	}

	/// <summary>
	/// Checks that a copy downloaded less than a day ago is used without asking the server again.
	/// </summary>
	[Fact]
	public void Get_RecentCopy_DoesNotAskAgain()
	{
		// Arrange
		_ = this.sut.Get(new Uri(Address));
		this.time.Now += TimeSpan.FromHours(23);

		// Act
		var result = this.sut.Get(new Uri(Address));

		// Assert
		_ = result!.Text.Should().Be("first");
		_ = this.server.Requests.Should().ContainSingle();
	}

	/// <summary>
	/// Checks that an old copy is checked with its ETag, and an unchanged answer keeps it and dates it now.
	/// </summary>
	[Fact]
	public void Get_OldCopyUnchanged_KeepsCopyAndUpdatesDate()
	{
		// Arrange
		_ = this.sut.Get(new Uri(Address));
		this.server.Add(Address, "changed");
		this.time.Now += TimeSpan.FromDays(2);

		// Act
		var result = this.sut.Get(new Uri(Address));

		// Assert
		_ = result!.Text.Should().Be("first");
		_ = result.Checked.Should().Be(this.time.Now);
		_ = this.server.Requests.Should().HaveCount(2);
	}

	/// <summary>
	/// Checks that an old copy is replaced when Microsoft changed the file.
	/// </summary>
	[Fact]
	public void Get_OldCopyChanged_DownloadsNewFile()
	{
		// Arrange
		_ = this.sut.Get(new Uri(Address));
		this.server.Add(Address, "changed");
		this.server.ETag = "\"v2\"";
		this.time.Now += TimeSpan.FromDays(2);

		// Act
		var result = this.sut.Get(new Uri(Address));

		// Assert
		_ = result!.Text.Should().Be("changed");
	}

	/// <summary>
	/// Checks that an old copy is still shown when the network is down, dated from when it was last confirmed.
	/// </summary>
	[Fact]
	public void Get_OldCopyOffline_ReturnsOldCopyWithOldDate()
	{
		// Arrange
		var downloaded = this.time.Now;

		_ = this.sut.Get(new Uri(Address));
		this.server.Offline = true;
		this.time.Now += TimeSpan.FromDays(5);

		// Act
		var result = this.sut.Get(new Uri(Address));

		// Assert
		_ = result!.Text.Should().Be("first");
		_ = result.Checked.Should().Be(downloaded);
	}

	/// <summary>
	/// Checks that a file Microsoft doesn't have, or one that can't be reached with no copy saved, gives nothing.
	/// </summary>
	/// <param name="offline"><see langword="true" /> to make the network unreachable.</param>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Get_NoFileAndNoCopy_ReturnsNull(bool offline)
	{
		// Arrange
		this.server.Offline = offline;

		// Act
		var result = this.sut.Get(new Uri("https://docs.test/xml/System/Missing.xml"));

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks that an address with escaped <c>..</c> segments can't save a copy outside the cache folder.
	/// </summary>
	[Fact]
	public void Get_EscapedParentSegments_SavesInsideCacheFolder()
	{
		// Arrange
		const string Escaped = "https://docs.test/a/%2E%2E/%2E%2E/%2E%2E/escaped.txt";
		var folder = Directory.CreateTempSubdirectory("dman-tests-").FullName;
		var source = new DocsSource(this.server.Client, Path.Combine(folder, "cache"), this.time);

		this.server.Add(new Uri(Escaped).AbsoluteUri, "text");

		// Act
		_ = source.Get(new Uri(Escaped));

		// Assert
		_ = Directory.GetFiles(folder, "escaped.txt", SearchOption.AllDirectories).Should().ContainSingle()
			.Which.Should().StartWith(Path.Combine(folder, "cache"));
	}

	/// <summary>
	/// Checks that a copy exactly a day old is checked again.
	/// </summary>
	[Fact]
	public void Get_CopyExactlyADayOld_AsksAgain()
	{
		// Arrange
		_ = this.sut.Get(new Uri(Address));
		this.time.Now += TimeSpan.FromDays(1);

		// Act
		_ = this.sut.Get(new Uri(Address));

		// Assert
		_ = this.server.Requests.Should().HaveCount(2);
	}

	/// <summary>
	/// Checks that an unchanged answer dates the copy now, so it isn't checked again for another day.
	/// </summary>
	[Fact]
	public void Get_AfterUnchangedAnswer_WaitsADayToAskAgain()
	{
		// Arrange
		_ = this.sut.Get(new Uri(Address));
		this.time.Now += TimeSpan.FromDays(2);
		_ = this.sut.Get(new Uri(Address));
		this.time.Now += TimeSpan.FromHours(23);

		// Act
		_ = this.sut.Get(new Uri(Address));

		// Assert
		_ = this.server.Requests.Should().HaveCount(2);
	}

	/// <summary>
	/// Checks that a saved ETag without its copy isn't sent, since an unchanged answer would leave nothing to show.
	/// </summary>
	[Fact]
	public void Get_CopyDeletedButTagKept_DownloadsAgain()
	{
		// Arrange
		_ = this.sut.Get(new Uri(Address));
		File.Delete(Path.Combine(this.sut.CacheFolder, "docs.test", "xml", "System", "String.xml"));

		// Act
		var result = this.sut.Get(new Uri(Address));

		// Assert
		_ = result!.Text.Should().Be("first");
	}

	/// <summary>
	/// Checks that a file Microsoft removed gives nothing, even with an old copy saved.
	/// </summary>
	[Fact]
	public void Get_RemovedFromServer_ReturnsNull()
	{
		// Arrange
		_ = this.sut.Get(new Uri(Address));
		this.server.Remove(Address);
		this.time.Now += TimeSpan.FromDays(2);

		// Act
		var result = this.sut.Get(new Uri(Address));

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks that escaped slashes in an address can't make extra folders: each segment stays one file name.
	/// </summary>
	[Fact]
	public void Get_EscapedSlashes_SavesAsOneFile()
	{
		// Arrange
		const string Escaped = "https://docs.test/x/a%2Fb%5Cc.txt";
		this.server.Add(new Uri(Escaped).AbsoluteUri, "text");

		// Act
		_ = this.sut.Get(new Uri(Escaped));

		// Assert
		_ = File.Exists(Path.Combine(this.sut.CacheFolder, "docs.test", "x", "a_b_c.txt")).Should().BeTrue();
	}

	/// <summary>
	/// Checks that marking the docs offline records when.
	/// </summary>
	[Fact]
	public void MarkOffline_RecordsTime()
	{
		// Act
		this.sut.MarkOffline();

		// Assert
		_ = this.sut.OfflineSince.Should().Be(this.time.Now);
	}

	/// <summary>
	/// Checks that only one lock on the cache can be held at a time.
	/// </summary>
	[Fact]
	public void TryLock_WhileLocked_ReturnsNull()
	{
		// Arrange
		using var first = this.sut.TryLock();

		// Act
		using var second = this.sut.TryLock();

		// Assert
		_ = first.Should().NotBeNull();
		_ = second.Should().BeNull();
	}

	/// <inheritdoc />
	public void Dispose() => this.server.Dispose();
}
