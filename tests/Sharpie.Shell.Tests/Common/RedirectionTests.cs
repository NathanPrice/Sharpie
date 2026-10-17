using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="Redirection" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class RedirectionTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly string folder;

	/// <summary>
	/// Initializes a new instance of the <see cref="RedirectionTests" /> class, working in an empty folder.
	/// </summary>
	public RedirectionTests()
	{
		this.folder = this.environment.CreateFolder();
		Directory.SetCurrentDirectory(this.folder);
	}

	/// <summary>
	/// Checks that <c>&gt;f 2&gt;&amp;1</c> sends both streams to the file, while <c>2&gt;&amp;1 &gt;f</c> sends errors
	/// where output went before, as in bash.
	/// </summary>
	[Fact]
	public void Open_WithCopyAfterFile_SendsBothStreamsToFile()
	{
		// Act
		using var fileFirst = Open("x >f 2>&1")!;
		using var copyFirst = Open("x 2>&1 >g")!;

		// Assert
		_ = fileFirst.Targets[1].Opened!.Name.Should().EndWith("f");
		_ = fileFirst.Targets[2].Should().Be(fileFirst.Targets[1]);
		_ = copyFirst.Targets[1].Opened!.Name.Should().EndWith("g");
		_ = copyFirst.Targets[2].Should().Be(StreamTarget.Own(1));
	}

	/// <summary>
	/// Checks that with no redirects every stream is its own, and that a stream number past 2 adds closed streams up
	/// to it.
	/// </summary>
	[Fact]
	public void Open_WithHighStreamNumber_ClosesTheOnesBetween()
	{
		// Act
		using var none = Open("x")!;
		using var high = Open("x 5>f")!;

		// Assert
		_ = none.Targets.Should().Equal(StreamTarget.Own(0), StreamTarget.Own(1), StreamTarget.Own(2));
		_ = high.Targets.Should().HaveCount(6);
		_ = high.Targets[3..5].Should().Equal(StreamTarget.None, StreamTarget.None);
		_ = high.Targets[5].Opened.Should().NotBeNull();
	}

	/// <summary>
	/// Checks that <c>3&gt;&amp;1 1&gt;&amp;2 2&gt;&amp;3</c> swaps output and errors, and that <c>&gt;&amp;-</c> and
	/// <c>&lt;&amp;-</c> close a stream.
	/// </summary>
	[Fact]
	public void Open_WithCopiesAndCloses_AppliesThemInOrder()
	{
		// Act
		using var swapped = Open("x 3>&1 1>&2 2>&3")!;
		using var closed = Open("x >&- <&-")!;

		// Assert
		_ = swapped.Targets.Should().Equal(StreamTarget.Own(0), StreamTarget.Own(2), StreamTarget.Own(1), StreamTarget.Own(1));
		_ = closed.Targets.Should().Equal(StreamTarget.None, StreamTarget.None, StreamTarget.Own(2));
	}

	/// <summary>
	/// Checks that copying from a stream that isn't open, or from a word that isn't a stream number, is refused and
	/// reported at the word.
	/// </summary>
	/// <param name="line">The command with the redirect.</param>
	/// <param name="target">The word the error should name.</param>
	[Theory]
	[InlineData("x 2>&5", "5")]
	[InlineData("x 2>&3", "3")]
	[InlineData("x <&foo", "foo")]
	[InlineData("x 2>&99999999999", "99999999999")]
	[InlineData("x 1>&- 2>&1", "1")]
	[InlineData("x 4>f 2>&3", "3")]
	public void Open_WithBadStreamNumber_ReportsIt(string line, string target)
	{
		// Act
		using var redirection = Open(line);

		// Assert
		_ = redirection.Should().BeNull();
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::shell::redirect_failed", $"`{target}`: bad stream number")
		{
			Source = line,
			Start = line.LastIndexOf(target, StringComparison.Ordinal),
			End = line.LastIndexOf(target, StringComparison.Ordinal) + target.Length,
			Label = "not a stream this command has open",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that <c>&gt;</c> empties a file, <c>&gt;&gt;</c> and <c>&amp;&gt;&gt;</c> add to its end, and
	/// <c>&amp;&gt;</c> points output and errors at it.
	/// </summary>
	[Fact]
	public void Open_WithWriteOperators_TruncatesOrAppends()
	{
		// Arrange
		File.WriteAllText("f", "old\n");
		File.WriteAllText("g", "old\n");

		// Act
		using (var truncated = Open("x > f")!)
		using (var appended = Open("x &>> g")!)
		{
			truncated.Targets[1].Opened!.Write("new\n"u8);
			appended.Targets[2].Opened!.Write("new\n"u8);

			_ = appended.Targets[1].Should().Be(appended.Targets[2]);
		}

		using var both = Open("x &> h")!;

		// Assert
		_ = File.ReadAllText("f").Should().Be("new\n");
		_ = File.ReadAllText("g").Should().Be("old\nnew\n");
		_ = both.Targets[1].Opened!.Name.Should().EndWith("h");
		_ = both.Targets[2].Should().Be(both.Targets[1]);
	}

	/// <summary>
	/// Checks that <c>&lt;</c> opens a file to read, and that a missing one is reported at its name.
	/// </summary>
	[Fact]
	public void Open_WithInputFile_ReadsItOrReportsIt()
	{
		// Arrange
		File.WriteAllText("in", "data");

		// Act
		using var opened = Open("x < in")!;
		using var missing = Open("x < nosuchfile");

		// Assert
		_ = Read(opened.Targets[0].Opened!).Should().Be("data");
		_ = missing.Should().BeNull();
		_ = this.environment.Error.Should().StartWith("Error: sharpie::shell::redirect_failed\n\n  × Can't open `nosuchfile` for reading\n   ╭─[1:5]\n");
	}

	/// <summary>
	/// Checks that a here-string is read with a newline added, and a here-document's body as it is, from a file that
	/// is already gone from disk.
	/// </summary>
	[Fact]
	public void Open_WithText_ReadsItFromFile()
	{
		// Act
		using var hereString = Open("x <<< 'two words'")!;
		using var hereDocument = Open("x 3<<EOF\nline\nEOF")!;

		// Assert
		_ = Read(hereString.Targets[0].Opened!).Should().Be("two words\n");
		_ = Read(hereDocument.Targets[3].Opened!).Should().Be("line\n");
		_ = File.Exists(hereString.Targets[0].Opened!.Name).Should().BeFalse();
	}

	/// <summary>
	/// Checks that with <c>set -C</c> on, <c>&gt;</c> and <c>&amp;&gt;</c> refuse to replace an existing file and leave
	/// it alone, while <c>&gt;|</c> replaces it and <c>&gt;&gt;</c> adds to it.
	/// </summary>
	/// <param name="line">The command with the redirect.</param>
	/// <param name="expected">What the file holds afterwards, or <see langword="null" /> when the redirect is refused.</param>
	[Theory]
	[InlineData("x > f", null)]
	[InlineData("x &> f", null)]
	[InlineData("x >| f", "new\n")]
	[InlineData("x >> f", "old\nnew\n")]
	[InlineData("x &>> f", "old\nnew\n")]
	public void Open_WithNoClobber_RefusesOnlyToReplaceFile(string line, string? expected)
	{
		// Arrange
		File.WriteAllText("f", "old\n");

		// Act
		using (var redirection = Open(line, noClobber: true))
		{
			redirection?.Targets[1].Opened!.Write("new\n"u8);
		}

		// Assert
		_ = File.ReadAllText("f").Should().Be(expected ?? "old\n");
		_ = this.environment.Error.Should().Be(expected is not null ? string.Empty : new ShellError("sharpie::shell::redirect_failed", "Can't overwrite `f`")
		{
			Source = line,
			Start = line.Length - 1,
			End = line.Length,
			Label = "it already exists, and `set -C` is on",
			Help = "use `>|` to replace it anyway",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that with <c>set -C</c> on, <c>&gt;</c> still creates a new file and still writes to a device like
	/// <c>/dev/null</c>.
	/// </summary>
	[Fact]
	public void Open_WithNoClobberAndNoRegularFile_Opens()
	{
		// Act
		using var created = Open("x > new", noClobber: true);
		using var device = Open("x > /dev/null", noClobber: true);

		// Assert
		_ = created!.Targets[1].Opened.Should().NotBeNull();
		_ = File.Exists("new").Should().BeTrue();
		_ = device!.Targets[1].Opened!.Name.Should().Be("/dev/null");
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a file that can't be opened is reported at its name, after the redirects before it were applied.
	/// </summary>
	[Fact]
	public void Open_WhenLaterFileFails_ReportsIt()
	{
		// Act
		using var redirection = Open("x > f > nosuchfolder/g");

		// Assert
		_ = redirection.Should().BeNull();
		_ = File.Exists("f").Should().BeTrue();
		_ = this.environment.Error.Should().StartWith("Error: sharpie::shell::redirect_failed\n\n  × Can't open `nosuchfolder/g` for writing\n   ╭─[1:9]\n");
	}

	/// <summary>
	/// Checks that <see cref="Redirection.Detach" /> hands the open files to the new owner, so closing the old one
	/// leaves them open.
	/// </summary>
	[Fact]
	public void Detach_ThenDispose_LeavesFilesToNewOwner()
	{
		// Arrange
		var redirection = Open("x > f 2>&1")!;
		var file = redirection.Targets[1].Opened!;

		// Act
		var owner = redirection.Detach();
		redirection.Dispose();
		var openAfterOld = file.CanWrite;
		owner.Dispose();

		// Assert
		_ = openAfterOld.Should().BeTrue();
		_ = file.CanWrite.Should().BeFalse();
		_ = owner.Targets.Should().Equal(redirection.Targets);
	}

	/// <inheritdoc />
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Opens the redirects of <paramref name="line" />.
	/// </summary>
	/// <param name="line">A command with redirects.</param>
	/// <param name="noClobber"><see langword="true" /> as with <c>set -C</c>.</param>
	/// <returns>
	/// What <see cref="Redirection.Open" /> gives.
	/// </returns>
	private static Redirection? Open(string line, bool noClobber = false) =>
		Redirection.Open(CommandLine.SplitRedirect(CommandLine.Split(line)).Redirects, line, noClobber);

	/// <summary>
	/// Reads the rest of an opened file as text.
	/// </summary>
	/// <param name="file">The file, which stays open.</param>
	/// <returns>
	/// The text.
	/// </returns>
	private static string Read(FileStream file)
	{
		using var reader = new StreamReader(file, leaveOpen: true);

		return reader.ReadToEnd();
	}
}
