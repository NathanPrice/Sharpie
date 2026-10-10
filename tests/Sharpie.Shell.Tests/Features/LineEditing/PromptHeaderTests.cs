using FluentAssertions;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Shell.Tests.Features.LineEditing;

/// <summary>
/// Tests for <see cref="PromptHeader" />.
/// </summary>
public sealed class PromptHeaderTests
{
	/// <summary>
	/// Checks that durations show their two largest units.
	/// </summary>
	/// <param name="seconds">How long the command ran.</param>
	/// <param name="expected">How the header writes it.</param>
	[Theory]
	[InlineData(4, "4s")]
	[InlineData(125, "2m 5s")]
	[InlineData(3780, "1h 3m")]
	public void FormatDuration_WritesTwoLargestUnits(int seconds, string expected)
	{
		// Act
		var text = PromptHeader.FormatDuration(TimeSpan.FromSeconds(seconds));

		// Assert
		_ = text.Should().Be(expected);
	}

	/// <summary>
	/// Checks that the home folder becomes <c>~</c> and the last folder is split off for bold.
	/// </summary>
	[Fact]
	public void SplitForDisplay_UnderHome_UsesTilde()
	{
		// Arrange
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var folder = Path.Combine(home, "code", "app");

		// Act
		var (parent, name) = PromptHeader.SplitForDisplay(folder);

		// Assert
		_ = parent.Should().Be($"~{Path.DirectorySeparatorChar}code{Path.DirectorySeparatorChar}");
		_ = name.Should().Be("app");
	}

	/// <summary>
	/// Checks that a slow command's time shows, a quick one's doesn't, and the blank line is only there when asked.
	/// </summary>
	[Fact]
	public void Build_SlowCommand_ShowsTookAndBlankLine()
	{
		// Act
		var slow = PromptHeader.Build(Path.GetTempPath(), TimeSpan.FromSeconds(3), 40, blankLine: true);
		var quick = PromptHeader.Build(Path.GetTempPath(), TimeSpan.FromSeconds(1), 40, blankLine: false);

		// Assert
		_ = slow.Should().Contain(" took \e[33m3s").And.Contain("\r\e[K\r\n");
		_ = quick.Should().NotContain("took").And.NotContain("\r\e[K\r\n");
	}
}
