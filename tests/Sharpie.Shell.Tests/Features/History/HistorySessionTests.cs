using FluentAssertions;
using Sharpie.Shell.Features.History;

namespace Sharpie.Shell.Tests.Features.History;

/// <summary>
/// Tests for <see cref="HistorySession" /> without the encrypted file, so the keyring is never touched.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class HistorySessionTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly HistoryCommand history = new();

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
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
