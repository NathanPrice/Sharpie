using FluentAssertions;
using Sharpie.Shell.Features.History;

namespace Sharpie.Shell.Tests.Features.History;

/// <summary>
/// Tests for <see cref="HistoryCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class HistoryCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly HistoryCommand sut = new();

	/// <summary>
	/// Checks that <see cref="HistoryCommand.Run" /> numbers each line from 1, right-aligned in five columns.
	/// </summary>
	[Fact]
	public void Run_WithLines_PrintsNumberedLines()
	{
		// Arrange
		this.sut.Add("echo hi");
		this.sut.Add("history");

		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().Be($"    1  echo hi{Environment.NewLine}    2  history{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <see cref="HistoryCommand.Run" /> prints nothing before any line is added.
	/// </summary>
	[Fact]
	public void Run_WithNoLines_PrintsNothing()
	{
		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a count prints only the newest lines and keeps their original numbers.
	/// </summary>
	[Fact]
	public void Run_WithCount_PrintsNewestLinesWithOriginalNumbers()
	{
		// Arrange
		this.sut.Add("echo hello");
		this.sut.Add("echo world");
		this.sut.Add("invalid_command");
		this.sut.Add("history 2");

		// Act
		_ = this.sut.Run(["2"]);

		// Assert
		_ = this.environment.Output.Should().Be($"    3  invalid_command{Environment.NewLine}    4  history 2{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that a count larger than the history prints every line.
	/// </summary>
	[Fact]
	public void Run_WithCountAboveLineCount_PrintsAll()
	{
		// Arrange
		this.sut.Add("echo hi");
		this.sut.Add("history 10");

		// Act
		_ = this.sut.Run(["10"]);

		// Assert
		_ = this.environment.Output.Should().Be($"    1  echo hi{Environment.NewLine}    2  history 10{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that a count that isn't a non-negative number prints no lines.
	/// </summary>
	/// <param name="count">The bad count argument.</param>
	[Theory]
	[InlineData("abc")]
	[InlineData("-1")]
	public void Run_WithInvalidCount_PrintsNothing(string count)
	{
		// Arrange
		this.sut.Add("echo hi");

		// Act
		_ = this.sut.Run([count]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Contain($"× `{count}` isn't a number");
	}

	/// <summary>
	/// Checks that adding past <see cref="HistoryCommand.MaxLines" /> drops the oldest line.
	/// </summary>
	[Fact]
	public void Add_PastMaxLines_DropsOldest()
	{
		// Arrange
		for (var i = 0; i < HistoryCommand.MaxLines; i++)
		{
			this.sut.Add($"echo {i}");
		}

		// Act
		this.sut.Add("newest");

		// Assert
		_ = this.sut.Lines.Should().HaveCount(HistoryCommand.MaxLines)
			.And.StartWith("echo 1")
			.And.EndWith("newest");
	}

	/// <summary>
	/// Checks that <c>-r</c> appends each non-blank line of the file after the lines already there.
	/// </summary>
	[Fact]
	public void Run_WithReadFlag_AppendsFileLines()
	{
		// Arrange
		var path = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "history", executable: false,
			"echo hello\necho world\n\n");
		this.sut.Add($"history -r {path}");

		// Act
		_ = this.sut.Run(["-r", path]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.sut.Lines.Should().Equal($"history -r {path}", "echo hello", "echo world");
		_ = this.environment.Error.Should().NotContain("sharpie::history::");
	}

	/// <summary>
	/// Checks that <c>-r</c> with a missing file leaves the history alone and prints nothing to standard output.
	/// </summary>
	[Fact]
	public void Run_WithReadFlagAndMissingFile_LeavesLinesUnchanged()
	{
		// Arrange
		var path = Path.Combine(this.environment.CreateFolder(), "missing");
		this.sut.Add("echo hi");

		// Act
		_ = this.sut.Run(["-r", path]);

		// Assert
		_ = this.sut.Lines.Should().Equal("echo hi");
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Contain($"× No such file: `{path}`");
	}

	/// <summary>
	/// Checks that dropping the oldest line also takes one off the count of lines already written by <c>-a</c>, so
	/// the next <c>-a</c> still writes exactly the new lines.
	/// </summary>
	[Fact]
	public void Add_PastMaxLines_LowersAppendedCount()
	{
		// Arrange
		for (var i = 0; i < HistoryCommand.MaxLines; i++)
		{
			this.sut.Add($"echo {i}");
		}

		this.sut.AppendedCount = HistoryCommand.MaxLines;

		// Act
		this.sut.Add("newest");

		// Assert
		_ = this.sut.AppendedCount.Should().Be(HistoryCommand.MaxLines - 1);
	}

	/// <summary>
	/// Checks that <c>-r</c> with an empty path reports it instead of ending the shell.
	/// </summary>
	[Fact]
	public void Run_WithReadFlagAndEmptyPath_PrintsError()
	{
		// Act
		_ = this.sut.Run(["-r", string.Empty]);

		// Assert
		_ = this.environment.Error.Should().Contain("× ``: ");
		_ = this.sut.Lines.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>-w</c> writes every line, and a later <c>-a</c> adds only the lines run since.
	/// </summary>
	[Fact]
	public void Run_WithWriteThenAppendFlag_WritesAllThenOnlyNewLines()
	{
		// Arrange
		var path = Path.Combine(this.environment.CreateFolder(), "history");
		this.sut.Add("one");
		this.sut.Add("two");
		_ = this.sut.Run(["-w", path]);
		this.sut.Add("three");

		// Act
		_ = this.sut.Run(["-a", path]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
		_ = File.ReadAllLines(path).Should().Equal("one", "two", "three");
		_ = this.environment.Error.Should().NotContain("sharpie::history::");
	}

	/// <summary>
	/// Checks that <c>-w</c> replaces what the file had.
	/// </summary>
	[Fact]
	public void Run_WithWriteFlag_ReplacesFile()
	{
		// Arrange
		var path = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "history", executable: false, "old\n");
		this.sut.Add("new");

		// Act
		_ = this.sut.Run(["-w", path]);

		// Assert
		_ = File.ReadAllLines(path).Should().Equal("new");
	}

	/// <summary>
	/// Checks that a failed <c>-a</c> reports why and leaves the lines counted as unwritten, so the next <c>-a</c>
	/// writes them.
	/// </summary>
	[Fact]
	public void Run_WithAppendFlagToMissingFolder_ReportsAndKeepsLinesUnwritten()
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		var missing = Path.Combine(folder, "missing", "history");
		var path = Path.Combine(folder, "history");
		this.sut.Add("one");
		_ = this.sut.Run(["-a", missing]);

		// Act
		_ = this.sut.Run(["-a", path]);

		// Assert
		_ = this.environment.Error.Should().Contain($"× No such file: `{missing}`");
		_ = File.ReadAllLines(path).Should().Equal("one");
	}

	/// <summary>
	/// Checks that <c>-w</c> with an empty path reports it instead of ending the shell.
	/// </summary>
	[Fact]
	public void Run_WithWriteFlagAndEmptyPath_PrintsError()
	{
		// Act
		_ = this.sut.Run(["-w", string.Empty]);

		// Assert
		_ = this.environment.Error.Should().Contain("× ``: ");
	}

	/// <summary>
	/// Checks that <c>-a</c> adds to what the file already has instead of replacing it.
	/// </summary>
	[Fact]
	public void Run_WithAppendFlag_KeepsFileContents()
	{
		// Arrange
		var path = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "history", executable: false, "other\n");
		this.sut.Add("new");

		// Act
		_ = this.sut.Run(["-a", path]);

		// Assert
		_ = File.ReadAllLines(path).Should().Equal("other", "new");
	}

	/// <summary>
	/// Checks that <c>-w</c> writes every line, including those an earlier <c>-a</c> already wrote elsewhere.
	/// </summary>
	[Fact]
	public void Run_WithWriteFlagAfterAppend_WritesEveryLine()
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		this.sut.Add("one");
		_ = this.sut.Run(["-a", Path.Combine(folder, "first")]);
		this.sut.Add("two");

		// Act
		_ = this.sut.Run(["-w", Path.Combine(folder, "second")]);

		// Assert
		_ = File.ReadAllLines(Path.Combine(folder, "second")).Should().Equal("one", "two");
	}

	/// <summary>
	/// Restores the console and working directory after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Checks the number of leading file lines found in a row inside the saved history.
	/// </summary>
	/// <param name="saved">The saved lines, separated by <c>|</c>.</param>
	/// <param name="file">The file lines, separated by <c>|</c>.</param>
	/// <param name="expected">How many leading file lines should be skipped.</param>
	[Theory]
	[InlineData("a|b", "a|b", 2)]
	[InlineData("a|b", "a|b|c", 2)]
	[InlineData("a|b|x|y", "a|b", 2)]
	[InlineData("x|a|b|y", "a|b|c", 2)]
	[InlineData("x|y", "a|b", 0)]
	[InlineData("", "a|b", 0)]
	[InlineData("a|b", "", 0)]
	public void Overlap_WithSavedAndFileLines_ReturnsLeadingMatchLength(string saved, string file, int expected)
	{
		// Arrange
		var savedLines = saved.Split('|', StringSplitOptions.RemoveEmptyEntries);
		var fileLines = file.Split('|', StringSplitOptions.RemoveEmptyEntries);

		// Act
		var overlap = HistoryCommand.Overlap(savedLines, fileLines);

		// Assert
		_ = overlap.Should().Be(expected);
	}
}
