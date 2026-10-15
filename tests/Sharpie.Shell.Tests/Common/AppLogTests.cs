using System.Text.Json;
using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="AppLog" />.
/// </summary>
public sealed class AppLogTests : IDisposable
{
	private readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("sharpie-applog-");

	/// <summary>
	/// Checks that an event is written as one compact JSON line carrying its level, the app name, the process id and
	/// its own properties.
	/// </summary>
	[Fact]
	public void Create_WhenWarningLogged_WritesCompactJsonLine()
	{
		// Arrange
		using (var logger = AppLog.Create("sampleapp", this.folder.FullName))
		{
			// Act
			logger.Warning("Can't start {Tool}", "sampletool");
		}

		// Assert
		var file = this.folder.GetFiles("sampleapp-*.clef").Should().ContainSingle().Subject;
		using var line = JsonDocument.Parse(File.ReadAllLines(file.FullName).Should().ContainSingle().Subject);
		var root = line.RootElement;

		_ = root.GetProperty("@l").GetString().Should().Be("Warning");
		_ = root.GetProperty("App").GetString().Should().Be("sampleapp");
		_ = root.GetProperty("Pid").GetInt32().Should().Be(Environment.ProcessId);
		_ = root.GetProperty("Tool").GetString().Should().Be("sampletool");
	}

	/// <summary>
	/// Checks that a full file rolls over to a new one and that files past the count limit are deleted.
	/// </summary>
	[Fact]
	public void Create_WhenSizeLimitExceeded_RollsOverAndDeletesOldestFiles()
	{
		// Arrange
		var padding = new string('x', 100);

		using (var logger = AppLog.Create("sampleapp", this.folder.FullName, fileSizeLimit: 1024, retainedFileCount: 2))
		{
			// Act
			for (var index = 0; index < 50; index++)
			{
				logger.Information("Line {Index} {Padding}", index, padding);
			}
		}

		// Assert
		_ = this.folder.GetFiles("sampleapp-*.clef").Should().HaveCount(2);
	}

	/// <summary>
	/// Checks that two loggers open at once, like two shells in two tabs, add to one file without writing over each
	/// other's lines.
	/// </summary>
	[Fact]
	public void Create_WhenTwoLoggersTakeTurns_KeepsEveryLine()
	{
		// Arrange
		using (var first = AppLog.Create("sampleapp", this.folder.FullName))
		using (var second = AppLog.Create("sampleapp", this.folder.FullName))
		{
			// Act
			first.Warning("From the first shell");
			second.Warning("From the second shell");
			first.Warning("From the first shell again");
		}

		// Assert
		var file = this.folder.GetFiles("sampleapp-*.clef").Should().ContainSingle().Subject;
		_ = File.ReadAllLines(file.FullName).Should().HaveCount(3);
	}

	/// <inheritdoc />
	public void Dispose() => this.folder.Delete(recursive: true);
}
