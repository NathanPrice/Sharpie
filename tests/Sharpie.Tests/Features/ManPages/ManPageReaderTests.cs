using System.Runtime.Versioning;
using FluentAssertions;
using Sharpie.Features.ManPages;

namespace Sharpie.Tests.Features.ManPages;

/// <summary>
/// Tests for <see cref="ManPageReader" />, running a stand-in <c>man</c> script that prints what it was given.
/// </summary>
public sealed class ManPageReaderTests : IDisposable
{
	private readonly string folder = Directory.CreateTempSubdirectory("man-tests-").FullName;

	/// <summary>
	/// Checks that <c>man</c> runs in the folder asked for with its arguments, and with the settings that keep bold and
	/// underline and set the width.
	/// </summary>
	[Fact]
	public void Read_FakeMan_PassesFolderArgumentsAndSettings()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var man = this.Script("pwd\necho \"$1 $2\"\necho \"$MAN_KEEP_FORMATTING|$MANROFFOPT|$MANWIDTH|$COLUMNS\"\n");

		// Act
		var page = ManPageReader.Read(man, ["1", "ls"], 72, this.folder);

		// Assert
		_ = page.Should().Be($"{this.folder}\n1 ls\n1|-c|72|72\n");
	}

	/// <summary>
	/// Checks that a folder that doesn't exist is left out, so <c>man</c> runs where the window does instead of failing.
	/// </summary>
	[Fact]
	public void Read_MissingFolder_RunsInWindowsFolder()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var man = this.Script("pwd\n");

		// Act
		var page = ManPageReader.Read(man, [], 80, Path.Combine(this.folder, "missing"));

		// Assert
		_ = page.Should().Be($"{Directory.GetCurrentDirectory()}\n");
	}

	/// <summary>
	/// Checks that a failed run and a run that prints nothing both give no page.
	/// </summary>
	/// <param name="body">The stand-in's script.</param>
	[Theory]
	[InlineData("echo page\nexit 1\n")]
	[InlineData("exit 0\n")]
	public void Read_FailedOrEmpty_ReturnsNull(string body)
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var man = this.Script(body);

		// Act
		var page = ManPageReader.Read(man, [], 80, null);

		// Assert
		_ = page.Should().BeNull();
	}

	/// <inheritdoc />
	public void Dispose() => Directory.Delete(this.folder, recursive: true);

	/// <summary>
	/// Writes an executable shell script into the test's folder.
	/// </summary>
	/// <param name="body">The script after its <c>#!</c> line.</param>
	/// <returns>
	/// The script's path.
	/// </returns>
	[UnsupportedOSPlatform("windows")]
	private string Script(string body)
	{
		var path = Path.Combine(this.folder, "man");

		File.WriteAllText(path, $"#!/bin/sh\n{body}");
		File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

		return path;
	}
}
