using System.Security.Cryptography;
using FluentAssertions;
using Sharpie.Shell.Features.History;

namespace Sharpie.Shell.Tests.Features.History;

/// <summary>
/// Tests for <see cref="HistorySession" />. The encrypted file gets a temporary path and random key, so the keyring is
/// never touched.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class HistorySessionTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly HistoryCommand history = new();
	private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
	private readonly string path;

	/// <summary>
	/// Initializes a new instance of the <see cref="HistorySessionTests" /> class with an empty history folder.
	/// </summary>
	public HistorySessionTests() => this.path = Path.Combine(this.environment.CreateFolder(), "history");

	/// <summary>
	/// Checks that <c>HISTFILE</c>'s lines are loaded and counted as already written, so <c>history -a</c> doesn't
	/// copy them back.
	/// </summary>
	[Fact]
	public void Start_WithHistFile_LoadsItsLines()
	{
		// Arrange
		var histFile = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "history", executable: false, "a\nb\n");

		// Act
		_ = HistorySession.Start(this.history, useEncryptedFile: false, histFile);

		// Assert
		_ = this.history.Lines.Should().Equal("a", "b");
		_ = this.history.AppendedCount.Should().Be(2);
	}

	/// <summary>
	/// Checks that no <c>HISTFILE</c>, or one that doesn't exist yet, loads nothing.
	/// </summary>
	/// <param name="missing">Whether to name a file that doesn't exist, rather than none.</param>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Start_WithoutHistFile_LoadsNothing(bool missing)
	{
		// Act
		_ = HistorySession.Start(this.history, useEncryptedFile: false, missing ? Path.Combine(this.environment.CreateFolder(), "none") : null);

		// Assert
		_ = this.history.Lines.Should().BeEmpty();
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a blank line isn't recorded and a real one is, and that <c>HISTFILE</c> then gets the whole list.
	/// </summary>
	[Fact]
	public void Record_ThenWriteHistFile_WritesWholeHistory()
	{
		// Arrange
		var histFile = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "history", executable: false, "a\n");
		var sut = HistorySession.Start(this.history, useEncryptedFile: false, histFile);

		// Act
		sut.Record("  ");
		sut.Record("b");
		sut.WriteHistFile();

		// Assert
		_ = this.history.Lines.Should().Equal("a", "b");
		_ = File.ReadAllLines(histFile).Should().Equal("a", "b");
	}

	/// <summary>
	/// Checks that with no <c>HISTFILE</c> nothing is written and nothing is reported.
	/// </summary>
	[Fact]
	public void WriteHistFile_WithoutHistFile_DoesNothing()
	{
		// Arrange
		var sut = HistorySession.Start(this.history, useEncryptedFile: false, null);
		sut.Record("b");

		// Act
		sut.WriteHistFile();

		// Assert
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that two shells open on the same file at once, like two tabs, each keep the other's lines instead of the
	/// last one to save writing only its own.
	/// </summary>
	[Fact]
	public void Record_FromTwoSessions_KeepsBothSessionsLines()
	{
		// Arrange
		var first = HistorySession.Start(this.history, this.path, this.key, null);
		var second = HistorySession.Start(new HistoryCommand(), this.path, this.key, null);

		// Act
		first.Record("a");
		second.Record("b");
		first.Record("c");

		// Assert
		_ = HistoryFile.Load(this.path, this.key).Should().Equal("a", "b", "c");
	}

	/// <summary>
	/// Checks that a session started on an earlier file loads its lines and adds new ones after them.
	/// </summary>
	[Fact]
	public void Start_WithSavedFile_LoadsItsLinesAndAddsAfterThem()
	{
		// Arrange
		_ = HistoryFile.Save(this.path, this.key, ["old"]);
		var sut = HistorySession.Start(this.history, this.path, this.key, null);

		// Act
		sut.Record("new");

		// Assert
		_ = this.history.Lines.Should().Equal("old", "new");
		_ = HistoryFile.Load(this.path, this.key).Should().Equal("old", "new");
	}

	/// <summary>
	/// Checks that lines imported from <c>HISTFILE</c> go into the encrypted file with the first new line, and that
	/// <c>HISTFILE</c> then gets its own lines and the new one but never the decrypted ones. The import is reported,
	/// and <c>HISTFILE</c> is made private to its owner.
	/// </summary>
	[Fact]
	public void Record_WithImportedHistFile_SavesImportedLinesAndKeepsDecryptedOnesOutOfHistFile()
	{
		// Arrange
		_ = HistoryFile.Save(this.path, this.key, ["secret"]);
		var histFile = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "history", executable: false, "plain\n");

		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(histFile, HistoryFile.OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
		}

		var sut = HistorySession.Start(this.history, this.path, this.key, histFile);

		// Act
		sut.Record("new");
		sut.WriteHistFile();

		// Assert
		_ = HistoryFile.Load(this.path, this.key).Should().Equal("secret", "plain", "new");
		_ = File.ReadAllLines(histFile).Should().Equal("plain", "new");
		_ = this.environment.Error.Should().Contain($"imported 1 lines from {histFile}");

		if (!OperatingSystem.IsWindows())
		{
			_ = File.GetUnixFileMode(histFile).Should().Be(HistoryFile.OwnerOnly);
		}
	}

	/// <summary>
	/// Checks that with encryption on, the lines kept for <c>HISTFILE</c> stop at <see cref="HistoryCommand.MaxLines" />,
	/// so a long session doesn't keep every line it ever ran.
	/// </summary>
	[Fact]
	public void Record_PastMaxLinesWithEncryption_WritesNewestToHistFile()
	{
		// Arrange
		var histFile = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "history", executable: false);
		var sut = HistorySession.Start(this.history, this.path, this.key, histFile);

		// Act
		for (var index = 0; index <= HistoryCommand.MaxLines; index++)
		{
			sut.Record($"echo {index}");
		}

		sut.WriteHistFile();

		// Assert
		var lines = File.ReadAllLines(histFile);
		_ = lines.Should().HaveCount(HistoryCommand.MaxLines);
		_ = lines[0].Should().Be("echo 1");
	}

	/// <summary>
	/// Checks that a file changed since the shell started is left alone, and nothing more is saved that session.
	/// </summary>
	[Fact]
	public void Record_AfterFileChanged_LeavesFileAndStopsSaving()
	{
		// Arrange
		var sut = HistorySession.Start(this.history, this.path, this.key, null);
		sut.Record("a");
		var bytes = File.ReadAllBytes(this.path);
		bytes[^1] ^= 1;
		File.WriteAllBytes(this.path, bytes);

		// Act
		sut.Record("b");
		File.Delete(this.path);
		sut.Record("c");

		// Assert
		_ = File.Exists(this.path).Should().BeFalse();
		_ = this.history.Lines.Should().Equal("a", "b", "c");
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
