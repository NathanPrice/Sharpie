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
	[InlineData(60, "1m 0s")]
	[InlineData(3600, "1h 0m")]
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

	/// <summary>
	/// Checks that what every prompt starts with reports the folder to the terminal, then fills a row with spaces and
	/// returns over them, so output without a final newline doesn't swallow the prompt.
	/// </summary>
	[Fact]
	public void Start_Folder_ReportsFolderThenClearsRow()
	{
		// Act
		var start = PromptHeader.Start("/tmp", 4);

		// Assert
		_ = start.Should().Be("\e]7;file:///tmp\a    \r\e[K");
	}

	/// <summary>
	/// Checks where folders split into the dimmed parent and the bright name: after the last separator, except for
	/// the root and the home folder itself, which are shown whole.
	/// </summary>
	/// <param name="directory">The folder, with <c>~</c> standing for the home folder.</param>
	/// <param name="parent">The dimmed part.</param>
	/// <param name="name">The bright part.</param>
	[Theory]
	[InlineData("/tmp", "/", "tmp")]
	[InlineData("/", "", "/")]
	[InlineData("~", "", "~")]
	public void SplitForDisplay_Folder_SplitsAfterLastSeparator(string directory, string parent, string name)
	{
		// Arrange
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

		// Act
		var result = PromptHeader.SplitForDisplay(directory == "~" ? home : directory);

		// Assert
		_ = result.Should().Be((parent, name));
	}

	/// <summary>
	/// Checks the whole header for a folder in a git repository after a command that took exactly the slow limit:
	/// the folder's address for the terminal, a space even with no width so the clear works, the folder, the branch
	/// and the time.
	/// </summary>
	[Fact]
	public void Build_InRepositoryAfterSlowCommand_WritesWholeHeader()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("prompt-tests-").FullName;
		_ = Directory.CreateDirectory(Path.Combine(folder, ".git"));
		File.WriteAllText(Path.Combine(folder, ".git", "HEAD"), "ref: refs/heads/main\n");
		var (parent, name) = PromptHeader.SplitForDisplay(folder);

		// Act
		var header = PromptHeader.Build(folder, TimeSpan.FromSeconds(2), 0, blankLine: false);

		// Assert
		_ = header.Should().Be($"\e]7;{new Uri(folder).AbsoluteUri}\a \r\e[K\e[30m{parent}\e[1;34m{name}\e[0m\e[30m on \e[35mmain\e[0m\e[30m took \e[33m2s\e[0m\r\n");
		Directory.Delete(folder, recursive: true);
	}
}
