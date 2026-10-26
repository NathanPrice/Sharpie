using FluentAssertions;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Shell.Tests.Features.LineEditing;

/// <summary>
/// Tests for the <see cref="LineEditor" /> history search, Ctrl+R and Ctrl+S.
/// </summary>
public sealed partial class LineEditorTests
{
	/// <summary>
	/// Checks Ctrl+R and Ctrl+S against what bash gives for the same keys and the same history.
	/// </summary>
	/// <param name="script">The keys, written as for <see cref="ReadLine_EditingKeyScript_GivesLine" />.</param>
	/// <param name="expected">The line Enter returns.</param>
	[Theory]
	[InlineData("{^R}x", "echo p xa q xa r")]
	[InlineData("{^R}xa{^K}", "echo p xa q ")]
	[InlineData("{^R}x{^R}{^K}", "echo p ")]
	[InlineData("{^R}x{^R}{^R}", "echo xc")]
	[InlineData("{^R}x{^R}{^R}{^R}{^R}", "echo xa")]
	[InlineData("{^R}xa{Backspace}", "echo p xa q xa r")]
	[InlineData("{^R}xa{^R}{^R}{Backspace}", "echo xa")]
	[InlineData("{^R}xb{Backspace}{Backspace}", "echo xb")]
	[InlineData("orig{^R}xa{^G}", "orig")]
	[InlineData("orig{LeftArrow}{^R}xa{^G}z", "orizg")]
	[InlineData("{^R}xq", "echo p xa q xa r")]
	[InlineData("echo zz yy{^R}zz{^K}", "echo ")]
	[InlineData("{^R}x{^R}{^R}{^S}", "echo xc")]
	[InlineData("{^R}x{^R}{^R}{^S}{^S}", "echo p xa q xa r")]
	[InlineData("{^S}x", "")]
	[InlineData("{^R}{^R}", "")]
	[InlineData("{^R}{Backspace}", "")]
	[InlineData("{^R}xc{Home}z", "zecho xc")]
	[InlineData("{^R}xc{Escape}{End}!", "echo xc!")]
	[InlineData("{^R}xc{%B}z", "zecho xc")]
	[InlineData("{^R}a r{^K}", "echo p xa q x")]
	[InlineData("abc{^R}{^R}{Escape}z", "abcz")]
	public void ReadLine_ReverseSearchScript_GivesLine(string script, string expected)
	{
		// Arrange
		this.UseSearchHistory();
		this.Script(script);
		this.Press(ConsoleKey.Enter);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be(expected);
	}

	/// <summary>
	/// Checks that Ctrl+R with nothing typed searches again for what the search on an earlier line looked for, even
	/// after a search in between that was given up before anything was typed.
	/// </summary>
	[Fact]
	public void ReadLine_EmptyReverseSearchOnNextLine_ReusesLastText()
	{
		// Arrange
		this.UseSearchHistory();
		this.Script("{^R}xb");
		this.Press(ConsoleKey.Enter);
		this.Script("{^R}{^G}");
		this.Press(ConsoleKey.Enter);
		this.Script("{^R}{^R}");
		this.Press(ConsoleKey.Enter);
		_ = this.sut.ReadLine(string.Empty);
		_ = this.sut.ReadLine(string.Empty);

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().Be("echo xb");
	}

	/// <summary>
	/// Checks that a search shows bash's prompt with the cursor at the match, and that a failed search says so and
	/// rings the bell.
	/// </summary>
	[Fact]
	public void ReadLine_ReverseSearch_ShowsBashPrompt()
	{
		// Arrange
		const string Searching = "(reverse-i-search)`xa': ";
		this.UseSearchHistory();
		this.Script("{^R}xa{Backspace}q");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(0).Should().StartWith("\r\e[J(reverse-i-search)`': ");
		_ = this.Frame(2).Should().StartWith($"\r\e[J{Searching}").And.EndWith($"\r\e[{Searching.Length + 12}C");
		_ = this.Frame(4).Should().Be($"\a\r\e[J(failed reverse-i-search)`xq': \e[32mecho\e[0m p xa q xa r\r\e[43C");
	}

	/// <summary>
	/// Checks the prompt after the last key, compared against bash for the same keys.
	/// </summary>
	/// <param name="script">The keys, written as for <see cref="ReadLine_EditingKeyScript_GivesLine" />.</param>
	/// <param name="expected">The prompt drawn after the last key.</param>
	[Theory]
	[InlineData("{^S}", "(i-search)`': ")]
	[InlineData("{^R}xq{Backspace}", "(reverse-i-search)`x': ")]
	[InlineData("{^R}z{Backspace}", "(failed reverse-i-search)`': ")]
	public void ReadLine_SearchScript_ShowsPrompt(string script, string expected)
	{
		// Arrange
		this.UseSearchHistory();
		this.Script(script);

		var last = this.keys.Count - 1;

		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(last).Should().Contain($"\r\e[J{expected}");
	}

	/// <summary>
	/// Checks that Ctrl+R with nothing typed and no earlier search does nothing, while Backspace with nothing typed
	/// rings the bell.
	/// </summary>
	[Fact]
	public void ReadLine_EmptySearch_RingsOnlyOnBackspace()
	{
		// Arrange
		this.UseSearchHistory();
		this.Script("{^R}{^R}{Backspace}");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(1).Should().NotContain("\a");
		_ = this.Frame(2).Should().StartWith("\a");
	}

	/// <summary>
	/// Checks that Ctrl+C during a search drops the line and draws it again after the usual prompt.
	/// </summary>
	[Fact]
	public void ReadLine_CtrlCDuringSearch_CancelsLine()
	{
		// Arrange
		this.UseSearchHistory();
		this.Script("{^R}xa{^C}");

		// Act
		var line = this.sut.ReadLine(string.Empty);

		// Assert
		_ = line.Should().BeEmpty();
		_ = this.Frame(3).Should().StartWith($"\r\e[J{Prompt}").And.EndWith("^C\r\n");
	}

	/// <summary>
	/// Checks that no dimmed suggestion is drawn during a search, even with the cursor at the end of a line that
	/// starts a history line.
	/// </summary>
	[Fact]
	public void ReadLine_Searching_DrawsNoSuggestion()
	{
		// Arrange
		this.UseSearchHistory();
		this.Script("echo{^R}");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.sut.ReadLine(string.Empty);

		// Assert
		_ = this.Frame(3).Should().Contain("\e[30m");
		_ = this.Frame(4).Should().NotContain("\e[30m");
	}

	/// <summary>
	/// Replaces the history with the lines the search tests were checked against in bash, oldest first.
	/// </summary>
	private void UseSearchHistory()
	{
		this.history.Clear();
		this.history.AddRange(["echo xa", "echo xb", "echo xb", "echo xc", "echo p xa q xa r"]);
	}
}
