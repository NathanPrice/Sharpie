using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Features.DotNetDocs;

/// <summary>
/// A downloaded file and when it was last confirmed to match Microsoft's copy.
/// </summary>
/// <param name="Text">The file's contents.</param>
/// <param name="Checked">When the file was last downloaded or confirmed unchanged.</param>
internal sealed record CachedText(string Text, DateTimeOffset Checked);

/// <summary>
/// Downloads files from Microsoft's documentation repositories and keeps a copy on disk, so a page looked up once
/// opens again without the network.
/// </summary>
/// <param name="client">The client that downloads files.</param>
/// <param name="cacheFolder">The folder the copies are kept in.</param>
/// <param name="time">The clock that decides when a copy is due to be checked again.</param>
internal sealed class DocsSource(HttpClient client, string cacheFolder, TimeProvider time)
{
	/// <summary>
	/// How long a copy is used before asking Microsoft whether it changed.
	/// </summary>
	/// <remarks>
	/// The docs are edited often and pages should stay current, but the check costs a round trip on every lookup that
	/// makes it. A day keeps repeat lookups instant and still picks up edits. The check sends the saved ETag, so an
	/// unchanged file comes back as a small 304 rather than the whole file.
	/// </remarks>
	private static readonly TimeSpan RevalidateAfter = TimeSpan.FromDays(1);

	/// <summary>
	/// How long to wait for Microsoft before giving up and using the saved copy.
	/// </summary>
	/// <remarks>
	/// The shell waits on <c>dman</c> before showing the next prompt, so a dead network shouldn't hold it for the
	/// default 100 seconds. The first lookup downloads the name index, about 11 MB, which needs the longer end.
	/// </remarks>
	private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Gets the source the shell uses, which keeps its copies in the user's cache folder.
	/// </summary>
	/// <value>
	/// A source with gzip enabled and a <see cref="DownloadTimeout" /> limit.
	/// </value>
	/// <remarks>
	/// Spargine's <c>WebHelper.DownloadStringAsync</c> was considered, to reuse its shared client. That client is
	/// private and the method returns only the body, so the ETag check would be lost and every daily check would
	/// download the whole file again. Its handler also has gzip off, which makes the name index about 11 MB instead of
	/// about 1 MB, and it keeps the 100 second default timeout.
	/// </remarks>
	public static DocsSource Shared { get; } = new(
		new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = DownloadTimeout },
		DefaultCacheFolder(),
		TimeProvider.System);

	/// <summary>
	/// Gets a file, from the saved copy while it's recent and from Microsoft otherwise.
	/// </summary>
	/// <param name="url">The file's address.</param>
	/// <returns>
	/// The file, or <see langword="null" /> when Microsoft has no such file, or it can't be reached and there's no
	/// saved copy. When it can't be reached, an old saved copy is returned with its old <see cref="CachedText.Checked" />
	/// time, which the page footer shows.
	/// </returns>
	public CachedText? Get(Uri url)
	{
		var path = this.CachePath(url.ArgumentNotNull());
		var file = new FileInfo(path);
		var now = time.GetUtcNow();

		if (file.Exists && now - file.LastWriteTimeUtc < RevalidateAfter)
		{
			return Read(file);
		}

		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			var tagPath = path + ".etag";

			if (file.Exists && File.Exists(tagPath) && EntityTagHeaderValue.TryParse(File.ReadAllText(tagPath), out var tag))
			{
				request.Headers.IfNoneMatch.Add(tag);
			}

			using var response = client.Send(request);

			if (response.StatusCode == HttpStatusCode.NotModified && file.Exists)
			{
				File.SetLastWriteTimeUtc(path, now.UtcDateTime);

				return new CachedText(File.ReadAllText(path), now);
			}

			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				return null;
			}

			_ = response.EnsureSuccessStatusCode();

			using var reader = new StreamReader(response.Content.ReadAsStream());
			var text = reader.ReadToEnd();

			Save(path, text);

			if (response.Headers.ETag is { } etag)
			{
				Save(tagPath, etag.ToString());
			}

			File.SetLastWriteTimeUtc(path, now.UtcDateTime);

			return new CachedText(text, now);
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
		{
			return file.Exists ? Read(file) : null;
		}
	}

	/// <summary>
	/// Checks whether a file has a saved copy, so a caller can say a long first download is coming.
	/// </summary>
	/// <param name="url">The file's address.</param>
	/// <returns>
	/// <see langword="true" /> when a copy is saved, however old.
	/// </returns>
	public bool IsSaved(Uri url) => File.Exists(this.CachePath(url.ArgumentNotNull()));

	/// <summary>
	/// Downloads a repository's <c>.tar.gz</c> archive and saves the files it keeps where <see cref="Get" /> looks for
	/// them, dated now.
	/// </summary>
	/// <param name="archive">The archive's address.</param>
	/// <param name="repository">The raw address of the repository's root, which the saved files are filed under.</param>
	/// <param name="keep">Decides from a path inside the repository, like <c>xml/System/String.xml</c>, whether to save it.</param>
	/// <param name="progress">Called with the number of files saved so far after each one.</param>
	/// <returns>
	/// The number of files saved.
	/// </returns>
	/// <exception cref="HttpRequestException">The archive couldn't be downloaded.</exception>
	/// <remarks>
	/// The archive is read as it arrives, so it's never stored whole. Every entry sits in one folder named after the
	/// repository and branch, which is taken off. Only regular files are saved, and each path goes through the same
	/// cleanup as a downloaded address, so an entry can't write outside the cache folder.
	/// </remarks>
	public int Import(Uri archive, Uri repository, Func<string, bool> keep, Action<int> progress)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, archive.ArgumentNotNull());
		using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);

		_ = response.EnsureSuccessStatusCode();

		using var gzip = new GZipStream(response.Content.ReadAsStream(), CompressionMode.Decompress);
		using var tar = new TarReader(gzip);
		var now = time.GetUtcNow().UtcDateTime;
		var count = 0;

		while (tar.GetNextEntry() is { } entry)
		{
			var slash = entry.Name.IndexOf('/', StringComparison.Ordinal);
			var relative = slash < 0 ? string.Empty : entry.Name[(slash + 1)..];

			if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is not { } data
				|| relative.Length == 0 || !keep(relative))
			{
				continue;
			}

			var escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
			var path = this.CachePath(new Uri(repository, escaped));

			Save(path, temporary =>
			{
				using var file = File.Create(temporary);

				data.CopyTo(file);
			});
			File.SetLastWriteTimeUtc(path, now);
			progress(++count);
		}

		return count;
	}

	/// <summary>
	/// Gets when the whole set of docs was last downloaded for offline use.
	/// </summary>
	/// <value>
	/// The time, or <see langword="null" /> when the user never ran <c>dman --download</c>.
	/// </value>
	public DateTimeOffset? OfflineSince => File.Exists(this.StampPath) ? File.GetLastWriteTimeUtc(this.StampPath) : null;

	/// <summary>
	/// Records that the whole set of docs was downloaded now.
	/// </summary>
	public void MarkOffline()
	{
		Save(this.StampPath, string.Empty);
		File.SetLastWriteTimeUtc(this.StampPath, time.GetUtcNow().UtcDateTime);
	}

	/// <summary>
	/// Takes the lock that lets one download of the whole set run at a time, across every shell.
	/// </summary>
	/// <returns>
	/// The lock, released when disposed; or <see langword="null" /> when another download holds it.
	/// </returns>
	/// <remarks>
	/// The file is opened with no sharing, which .NET backs with an advisory lock on Unix, and deleted on close.
	/// </remarks>
	public FileStream? TryLock()
	{
		_ = Directory.CreateDirectory(cacheFolder);

		try
		{
			return new FileStream(Path.Combine(cacheFolder, "offline.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
		}
		catch (IOException)
		{
			return null;
		}
	}

	/// <summary>
	/// Gets the folder copies are kept in.
	/// </summary>
	/// <value>
	/// The folder, which may not exist yet.
	/// </value>
	public string CacheFolder => cacheFolder;

	/// <summary>
	/// Gets the file whose date records the last full download.
	/// </summary>
	/// <value>
	/// A path in the cache folder.
	/// </value>
	private string StampPath => Path.Combine(cacheFolder, "offline.stamp");

	/// <summary>
	/// Gets the folder copies are kept in when nothing else is set.
	/// </summary>
	/// <returns>
	/// <c>$XDG_CACHE_HOME/sharpie/dotnet-docs</c>, or <c>~/.cache/sharpie/dotnet-docs</c> without it, on Unix; the
	/// local application data folder on Windows, which has no separate cache folder.
	/// </returns>
	private static string DefaultCacheFolder()
	{
		var root = OperatingSystem.IsWindows()
			? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
			: Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } cache
				? cache
				: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");

		return Path.Combine(root, "sharpie", "dotnet-docs");
	}

	/// <summary>
	/// Reads a saved copy.
	/// </summary>
	/// <param name="file">The copy.</param>
	/// <returns>
	/// The copy, dated by when it was last confirmed.
	/// </returns>
	private static CachedText Read(FileInfo file) => new(File.ReadAllText(file.FullName), file.LastWriteTimeUtc);

	/// <summary>
	/// Writes a file through a temporary file, so a crash mid-write never leaves half a page behind.
	/// </summary>
	/// <param name="path">Where the file goes.</param>
	/// <param name="contents">The text to write.</param>
	private static void Save(string path, string contents) => Save(path, temporary => File.WriteAllText(temporary, contents));

	/// <summary>
	/// Writes a file through a temporary file of its own, so a crash mid-write never leaves half a file behind, and
	/// two downloads saving the same file at once don't write into one temporary file.
	/// </summary>
	/// <param name="path">Where the file goes.</param>
	/// <param name="write">Writes the contents to the temporary path it's given.</param>
	private static void Save(string path, Action<string> write)
	{
		_ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		var temporary = $"{path}.{Path.GetRandomFileName()}.tmp";

		try
		{
			write(temporary);
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			File.Delete(temporary);
		}
	}

	/// <summary>
	/// Gets where the copy of a file is saved.
	/// </summary>
	/// <param name="url">The file's address.</param>
	/// <returns>
	/// The host and path under the cache folder.
	/// </returns>
	/// <remarks>
	/// Addresses come from links inside downloaded pages. An escaped <c>%2E%2E</c> or <c>%2F</c> survives
	/// <see cref="Uri" />'s own cleanup and only turns into <c>..</c> or a separator once unescaped here, so those are
	/// replaced to keep every copy inside the cache folder.
	/// </remarks>
	private string CachePath(Uri url)
	{
		var segments = url.AbsolutePath
			.Split('/', StringSplitOptions.RemoveEmptyEntries)
			.Select(Uri.UnescapeDataString)
			.Select(static segment => segment is "." or ".." ? "_" : segment.Replace('/', '_').Replace('\\', '_'));

		return Path.Combine([cacheFolder, url.Host, .. segments]);
	}
}
