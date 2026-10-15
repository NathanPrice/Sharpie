using System.Text.Json;
using FluentAssertions;
using Serilog;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="LazyLogger" />.
/// </summary>
public sealed class LazyLoggerTests : IDisposable
{
	private readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("sharpie-lazylogger-");

	/// <summary>
	/// Checks that a level method with a property reaches the real logger with that property intact.
	/// </summary>
	[Fact]
	public void Warning_WhenWritten_ReachesRealLoggerWithProperties()
	{
		// Arrange
		using (var logger = new LazyLogger(() => AppLog.Create("sampleapp", this.folder.FullName)))
		{
			// Act: through ILogger, as Log.Warning calls it, since the level methods are the interface's defaults.
			((ILogger)logger).Warning(new IOException("disk gone"), "Can't start {Tool}", "sampletool");
		}

		// Assert
		var file = this.folder.GetFiles("sampleapp-*.clef").Should().ContainSingle().Subject;
		using var line = JsonDocument.Parse(File.ReadAllLines(file.FullName).Should().ContainSingle().Subject);
		var root = line.RootElement;

		_ = root.GetProperty("@l").GetString().Should().Be("Warning");
		_ = root.GetProperty("Tool").GetString().Should().Be("sampletool");
		_ = root.GetProperty("@x").GetString().Should().Contain("disk gone");
	}

	/// <summary>
	/// Checks that the real logger isn't built before anything is written.
	/// </summary>
	[Fact]
	public void IsCreated_BeforeAnyWrite_IsFalse()
	{
		// Arrange
		using var logger = new LazyLogger(() => AppLog.Create("sampleapp", this.folder.FullName));

		// Act
		var created = logger.IsCreated;

		// Assert
		_ = created.Should().BeFalse();
	}

	/// <summary>
	/// Checks that disposing a logger nothing was written to doesn't build the real one.
	/// </summary>
	[Fact]
	public void Dispose_WhenNothingWritten_DoesNotBuildRealLogger()
	{
		// Arrange
		var builds = 0;
		var logger = new LazyLogger(() =>
		{
			builds++;

			return AppLog.Create("sampleapp", this.folder.FullName);
		});

		// Act
		logger.Dispose();

		// Assert
		_ = builds.Should().Be(0);
	}

	/// <summary>
	/// Checks that disposing closes the real logger, so an event written afterwards doesn't reach the file.
	/// </summary>
	[Fact]
	public void Dispose_AfterWrite_ClosesRealLogger()
	{
		// Arrange
		var logger = new LazyLogger(() => AppLog.Create("sampleapp", this.folder.FullName));
		((ILogger)logger).Warning("Before closing");

		// Act
		logger.Dispose();
		((ILogger)logger).Warning("After closing");

		// Assert
		var file = this.folder.GetFiles("sampleapp-*.clef").Should().ContainSingle().Subject;
		_ = File.ReadAllLines(file.FullName).Should().ContainSingle();
	}

	/// <inheritdoc />
	public void Dispose() => this.folder.Delete(recursive: true);
}
