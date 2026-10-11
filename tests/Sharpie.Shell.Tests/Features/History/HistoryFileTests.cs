using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Sharpie.Shell.Features.History;

namespace Sharpie.Shell.Tests.Features.History;

/// <summary>
/// Tests for <see cref="HistoryFile" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class HistoryFileTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
	private readonly string path;

	/// <summary>
	/// Initializes a new instance of the <see cref="HistoryFileTests" /> class with an empty history folder.
	/// </summary>
	public HistoryFileTests() => this.path = Path.Combine(this.environment.CreateFolder(), "history");

	/// <summary>
	/// Checks that saved lines load back unchanged and in order.
	/// </summary>
	[Fact]
	public void Load_AfterSave_ReturnsSameLines()
	{
		// Arrange
		_ = HistoryFile.Save(this.path, this.key, ["echo one", "echo two"]);

		// Act
		var lines = HistoryFile.Load(this.path, this.key);

		// Assert
		_ = lines.Should().Equal("echo one", "echo two");
	}

	/// <summary>
	/// Checks that the saved file doesn't contain the command text.
	/// </summary>
	[Fact]
	public void Save_WritesNoPlaintext()
	{
		// Act
		_ = HistoryFile.Save(this.path, this.key, ["echo secret-value"]);

		// Assert
		_ = Encoding.UTF8.GetString(File.ReadAllBytes(this.path)).Should().NotContain("secret-value");
	}

	/// <summary>
	/// Checks that encrypting the same text twice gives different bytes, because each save uses a fresh nonce.
	/// </summary>
	[Fact]
	public void Encrypt_SameTextTwice_DiffersEachTime()
	{
		// Arrange
		var plaintext = Encoding.UTF8.GetBytes("echo hi");

		// Act
		var first = HistoryFile.Encrypt(this.key, plaintext);
		var second = HistoryFile.Encrypt(this.key, plaintext);

		// Assert
		_ = first.Should().NotEqual(second);
	}

	/// <summary>
	/// Checks that loading with a different key fails instead of returning garbage.
	/// </summary>
	[Fact]
	public void Load_WithWrongKey_ReturnsNull()
	{
		// Arrange
		_ = HistoryFile.Save(this.path, this.key, ["echo hi"]);

		// Act
		var lines = HistoryFile.Load(this.path, RandomNumberGenerator.GetBytes(32));

		// Assert
		_ = lines.Should().BeNull();
	}

	/// <summary>
	/// Checks that changing any byte of the file, including the version byte, fails authentication.
	/// </summary>
	/// <param name="index">The byte to flip: 0 is the version, 1 is in the nonce, and -1 is the last tag byte.</param>
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(-1)]
	public void Load_AfterByteChanged_ReturnsNull(int index)
	{
		// Arrange
		_ = HistoryFile.Save(this.path, this.key, ["echo hi"]);
		var bytes = File.ReadAllBytes(this.path);
		bytes[index < 0 ? bytes.Length + index : index] ^= 1;
		File.WriteAllBytes(this.path, bytes);

		// Act
		var lines = HistoryFile.Load(this.path, this.key);

		// Assert
		_ = lines.Should().BeNull();
	}

	/// <summary>
	/// Checks that a missing file loads as empty history.
	/// </summary>
	[Fact]
	public void Load_WhenFileMissing_ReturnsEmpty()
	{
		// Act
		var lines = HistoryFile.Load(this.path, this.key);

		// Assert
		_ = lines.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a file holding more than <see cref="HistoryCommand.MaxLines" /> lines loads only the newest ones.
	/// </summary>
	[Fact]
	public void Load_OverMaxLines_KeepsNewest()
	{
		// Arrange
		var saved = Enumerable.Range(0, HistoryCommand.MaxLines + 5).Select(i => $"echo {i}").ToList();
		_ = HistoryFile.Save(this.path, this.key, saved);

		// Act
		var lines = HistoryFile.Load(this.path, this.key);

		// Assert
		_ = lines.Should().Equal(saved.Skip(5));
	}

	/// <summary>
	/// Checks that the saved file can be read and written only by its owner.
	/// </summary>
	[Fact]
	public void Save_SetsOwnerOnlyMode()
	{
		// Act
		_ = HistoryFile.Save(this.path, this.key, ["echo hi"]);

		// Assert
		_ = File.GetUnixFileMode(this.path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}

	/// <summary>
	/// Checks that a missing folder is created with owner-only access.
	/// </summary>
	[Fact]
	public void PrepareFolder_WhenMissing_CreatesOwnerOnlyFolder()
	{
		// Arrange
		var folder = Path.Combine(this.environment.CreateFolder(), "history");

		// Act
		var prepared = HistoryFile.PrepareFolder(folder);

		// Assert
		_ = prepared.Should().BeTrue();
		_ = File.GetUnixFileMode(folder).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
	}

	/// <summary>
	/// Checks that a symlinked folder is refused, so nothing is read from or written to wherever it points.
	/// </summary>
	[Fact]
	public void PrepareFolder_WhenSymlink_ReturnsFalse()
	{
		// Arrange
		var link = Path.Combine(this.environment.CreateFolder(), "history");
		_ = Directory.CreateSymbolicLink(link, this.environment.CreateFolder());

		// Act
		var prepared = HistoryFile.PrepareFolder(link);

		// Assert
		_ = prepared.Should().BeFalse();
		_ = this.environment.Error.Should().Contain($"history: {link} is a symbolic link, so history won't be saved");
	}

	/// <summary>
	/// Checks that a folder other users can write to is refused.
	/// </summary>
	[Fact]
	public void PrepareFolder_WhenGroupWritable_ReturnsFalse()
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		File.SetUnixFileMode(folder, File.GetUnixFileMode(folder) | UnixFileMode.GroupWrite);

		// Act
		var prepared = HistoryFile.PrepareFolder(folder);

		// Assert
		_ = prepared.Should().BeFalse();
		_ = this.environment.Error.Should().Contain($"history: {folder} is writable by other users, so history won't be saved");
	}

	/// <summary>
	/// Checks that a folder that can't be created, because a file is in the way, is reported and refused.
	/// </summary>
	[Fact]
	public void PrepareFolder_UnderAFile_ReportsAndReturnsFalse()
	{
		// Arrange
		var file = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "file", executable: false);

		// Act
		var prepared = HistoryFile.PrepareFolder(Path.Combine(file, "history"));

		// Assert
		_ = prepared.Should().BeFalse();
		_ = this.environment.Error.Should().Contain("history: ");
	}

	/// <summary>
	/// Checks that a file saved with another key is reported and left alone.
	/// </summary>
	[Fact]
	public void Load_WithWrongKey_ReportsFileLeftAlone()
	{
		// Arrange
		_ = HistoryFile.Save(this.path, RandomNumberGenerator.GetBytes(32), ["echo hi"]);

		// Act
		_ = HistoryFile.Load(this.path, this.key);

		// Assert
		_ = this.environment.Error.Should().Contain($"history: {this.path} was changed or saved with another key, so it was left alone");
	}

	/// <summary>
	/// Checks that a file this account can't read is reported and gives no lines, so it isn't overwritten.
	/// </summary>
	[Fact]
	public void Load_UnreadableFile_ReportsAndReturnsNull()
	{
		// Arrange
		_ = HistoryFile.Save(this.path, this.key, ["echo hi"]);
		File.SetUnixFileMode(this.path, UnixFileMode.None);

		// Act
		var lines = HistoryFile.Load(this.path, this.key);

		// Assert
		_ = lines.Should().BeNull();
		_ = this.environment.Error.Should().Contain("history: ");
	}

	/// <summary>
	/// Checks that two shells adding to the same file each keep the other's lines, instead of the last one writing
	/// only its own list.
	/// </summary>
	[Fact]
	public void Append_FromTwoSessions_KeepsBothSessionsLines()
	{
		// Arrange
		_ = HistoryFile.Save(this.path, this.key, ["a"]);

		// Act
		var first = HistoryFile.Append(this.path, this.key, ["b"]);
		var second = HistoryFile.Append(this.path, this.key, ["c"]);

		// Assert
		_ = (first && second).Should().BeTrue();
		_ = HistoryFile.Load(this.path, this.key).Should().Equal("a", "b", "c");
	}

	/// <summary>
	/// Checks that adds running at the same moment wait for each other's lock, so none of their lines is lost.
	/// </summary>
	[Fact]
	public void Append_InParallel_KeepsEveryLine()
	{
		// Act
		var results = Enumerable.Range(0, 20).AsParallel().Select(index => HistoryFile.Append(this.path, this.key, [$"echo {index}"])).ToList();

		// Assert
		_ = results.Should().AllSatisfy(result => result.Should().BeTrue());
		_ = HistoryFile.Load(this.path, this.key).Should().BeEquivalentTo(Enumerable.Range(0, 20).Select(index => $"echo {index}"));
	}

	/// <summary>
	/// Checks that adding to a file that fails authentication reports it and leaves the file as it was.
	/// </summary>
	[Fact]
	public void Append_WithChangedFile_ReturnsFalseAndLeavesFile()
	{
		// Arrange
		_ = HistoryFile.Save(this.path, this.key, ["echo hi"]);
		var bytes = File.ReadAllBytes(this.path);
		bytes[^1] ^= 1;
		File.WriteAllBytes(this.path, bytes);

		// Act
		var appended = HistoryFile.Append(this.path, this.key, ["echo new"]);

		// Assert
		_ = appended.Should().BeFalse();
		_ = File.ReadAllBytes(this.path).Should().Equal(bytes);
	}

	/// <summary>
	/// Checks that adding past <see cref="HistoryCommand.MaxLines" /> drops the oldest lines.
	/// </summary>
	[Fact]
	public void Append_PastMaxLines_DropsOldest()
	{
		// Arrange
		_ = HistoryFile.Save(this.path, this.key, Enumerable.Range(0, HistoryCommand.MaxLines).Select(index => $"echo {index}"));

		// Act
		_ = HistoryFile.Append(this.path, this.key, ["echo new"]);

		// Assert
		// Read raw, because Load trims on its own and would hide a file that keeps growing.
		var lines = Encoding.UTF8.GetString(HistoryFile.Decrypt(this.key, File.ReadAllBytes(this.path))).Split('\n');
		_ = lines.Should().HaveCount(HistoryCommand.MaxLines);
		_ = lines[0].Should().Be("echo 1");
		_ = lines[^1].Should().Be("echo new");
	}

	/// <summary>
	/// Checks that the lock file is readable and writable by its owner only.
	/// </summary>
	[Fact]
	public void Append_OnUnix_CreatesOwnerOnlyLockFile()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		_ = HistoryFile.Append(this.path, this.key, ["echo hi"]);

		// Assert
		_ = File.GetUnixFileMode($"{this.path}.lock").Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}

	/// <summary>
	/// Checks that when another shell keeps the lock, adding waits for it a while, then gives up, reports it, and
	/// leaves the file alone instead of waiting forever.
	/// </summary>
	[Fact]
	public void Append_WhileAnotherShellHoldsLock_GivesUpAndReports()
	{
		// Arrange
		_ = HistoryFile.Save(this.path, this.key, ["echo hi"]);
		using var held = new FileStream($"{this.path}.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		var clock = System.Diagnostics.Stopwatch.StartNew();

		// Act
		var appended = HistoryFile.Append(this.path, this.key, ["echo new"]);

		// Assert
		_ = appended.Should().BeFalse();
		_ = clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500));
		_ = this.environment.Error.Should().Contain("history: ");
		_ = HistoryFile.Load(this.path, this.key).Should().Equal("echo hi");
	}

	/// <summary>
	/// Checks that a lock file that can't be opened at all is reported instead of ending the shell.
	/// </summary>
	[Fact]
	public void Append_WhenLockIsAFolder_ReturnsFalseAndReports()
	{
		// Arrange
		_ = Directory.CreateDirectory($"{this.path}.lock");

		// Act
		var appended = HistoryFile.Append(this.path, this.key, ["echo hi"]);

		// Assert
		_ = appended.Should().BeFalse();
		_ = this.environment.Error.Should().Contain("history: ");
	}

	/// <summary>
	/// Checks that saving again replaces the file.
	/// </summary>
	[Fact]
	public void Save_Twice_ReplacesFile()
	{
		// Arrange
		_ = HistoryFile.Save(this.path, this.key, ["old"]);

		// Act
		var saved = HistoryFile.Save(this.path, this.key, ["new"]);

		// Assert
		_ = saved.Should().BeTrue();
		_ = HistoryFile.Load(this.path, this.key).Should().Equal("new");
	}

	/// <summary>
	/// Checks that a save that can't finish is reported, returns <see langword="false" />, and leaves no temporary
	/// file behind.
	/// </summary>
	[Fact]
	public void Save_OverAFolder_ReportsAndCleansUp()
	{
		// Arrange
		_ = Directory.CreateDirectory(this.path);

		// Act
		var saved = HistoryFile.Save(this.path, this.key, ["echo hi"]);

		// Assert
		_ = saved.Should().BeFalse();
		_ = this.environment.Error.Should().Contain("history: ");
		_ = Directory.GetFileSystemEntries(Path.GetDirectoryName(this.path)!).Should().Equal(this.path);
	}

	/// <summary>
	/// Checks that an empty history encrypts to the smallest file and reads back.
	/// </summary>
	[Fact]
	public void Decrypt_EmptyPlaintext_RoundTrips()
	{
		// Act
		var plaintext = HistoryFile.Decrypt(this.key, HistoryFile.Encrypt(this.key, []));

		// Assert
		_ = plaintext.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that data too short to hold the version, nonce and tag is rejected as not a history file.
	/// </summary>
	[Fact]
	public void Decrypt_TooShort_Throws()
	{
		// Act
		var act = () => HistoryFile.Decrypt(this.key, [1, 0, 0]);

		// Assert
		_ = act.Should().Throw<CryptographicException>();
	}

	/// <summary>
	/// Checks that the Spargine guard rejects an empty history path before any file is touched.
	/// </summary>
	[Fact]
	public void Load_WithEmptyPath_ThrowsArgumentException()
	{
		// Act
		var act = () => HistoryFile.Load(string.Empty, new byte[32]);

		// Assert
		_ = act.Should().Throw<ArgumentException>();
	}

	/// <summary>
	/// Restores the console and working directory after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
