using System.Net;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Shell.Tests.Features.DotNetDocs;

/// <summary>
/// Stands in for Microsoft's documentation server: answers from files the test adds, and counts requests.
/// </summary>
internal sealed class FakeDocs : HttpMessageHandler
{
	private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

	// Deleted on dispose. Stryker runs these tests thousands of times, and the copies left behind filled /tmp.
	private readonly List<string> folders = [];

	/// <summary>
	/// Initializes a new instance of the <see cref="FakeDocs" /> class.
	/// </summary>
	public FakeDocs() => this.Client = new HttpClient(this, disposeHandler: false);

	/// <summary>
	/// Gets a client that sends its requests to this server.
	/// </summary>
	/// <value>
	/// One client for the server's lifetime, disposed with it.
	/// </value>
	public HttpClient Client { get; }

	/// <summary>
	/// Gets the addresses asked for, in order.
	/// </summary>
	/// <value>
	/// Every request, including ones answered 304 or 404.
	/// </value>
	public List<string> Requests { get; } = [];

	/// <summary>
	/// Gets or sets a value indicating whether the server is unreachable.
	/// </summary>
	/// <value>
	/// <see langword="true" /> to fail every request as a dead network would.
	/// </value>
	public bool Offline { get; set; }

	/// <summary>
	/// Gets or sets the ETag the server sends with every file, and answers 304 to when it comes back.
	/// </summary>
	/// <value>
	/// A quoted tag, like <c>"v1"</c>.
	/// </value>
	public string ETag { get; set; } = "\"v1\"";

	/// <summary>
	/// Adds a file the server can send.
	/// </summary>
	/// <param name="url">The file's address.</param>
	/// <param name="contents">The file.</param>
	public void Add(string url, string contents) => this.files[url] = System.Text.Encoding.UTF8.GetBytes(contents);

	/// <summary>
	/// Adds a binary file the server can send, like an archive.
	/// </summary>
	/// <param name="url">The file's address.</param>
	/// <param name="contents">The file.</param>
	public void Add(string url, byte[] contents) => this.files[url] = contents;

	/// <summary>
	/// Removes a file, so the server answers 404 for it from now on.
	/// </summary>
	/// <param name="url">The file's address.</param>
	public void Remove(string url) => this.files.Remove(url);

	/// <summary>
	/// Creates a source that downloads from this server into a new temporary folder.
	/// </summary>
	/// <param name="time">The clock the source checks copies' age against.</param>
	/// <returns>
	/// The source.
	/// </returns>
	public DocsSource CreateSource(TimeProvider time) => new(this.Client, this.CreateFolder(), time);

	/// <summary>
	/// Creates an empty temporary folder that is deleted with this server.
	/// </summary>
	/// <returns>
	/// The folder's full path.
	/// </returns>
	public string CreateFolder()
	{
		var folder = Directory.CreateTempSubdirectory("dman-tests-").FullName;

		this.folders.Add(folder);

		return folder;
	}

	/// <inheritdoc />
	protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var url = request.RequestUri!.AbsoluteUri;

		this.Requests.Add(url);

		if (this.Offline)
		{
			throw new HttpRequestException("offline");
		}

		if (!this.files.TryGetValue(url, out var contents))
		{
			return new HttpResponseMessage(HttpStatusCode.NotFound);
		}

		if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == this.ETag))
		{
			return new HttpResponseMessage(HttpStatusCode.NotModified);
		}

		var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contents) };

		response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(this.ETag);

		return response;
	}

	/// <inheritdoc />
	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			this.Client.Dispose();
			this.folders.ForEach(folder => Directory.Delete(folder, recursive: true));
		}

		base.Dispose(disposing);
	}

	/// <inheritdoc />
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
		Task.FromResult(this.Send(request, cancellationToken));
}

/// <summary>
/// A clock a test moves by hand.
/// </summary>
/// <param name="now">The starting time.</param>
internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
	/// <summary>
	/// Gets or sets the current time.
	/// </summary>
	/// <value>
	/// The time <see cref="GetUtcNow" /> returns.
	/// </value>
	public DateTimeOffset Now { get; set; } = now;

	/// <inheritdoc />
	public override DateTimeOffset GetUtcNow() => this.Now;
}
