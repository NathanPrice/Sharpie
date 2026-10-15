using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="ShellError" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ShellErrorTests : IDisposable
{
	private readonly ShellEnvironment environment = new();

	/// <summary>
	/// Checks that an error with no source shows only its code and summary.
	/// </summary>
	[Fact]
	public void Render_WithoutSource_ShowsCodeAndSummary()
	{
		// Arrange
		var error = new ShellError("sharpie::sample::broken", "Something broke");

		// Act
		var text = error.Render(color: false);

		// Assert
		_ = text.Should().Be("Error: sharpie::sample::broken\n\n  × Something broke\n");
	}

	/// <summary>
	/// Checks that a hint goes on its own <c>help:</c> line after the summary.
	/// </summary>
	[Fact]
	public void Render_WithHelp_AddsHelpLine()
	{
		// Arrange
		var error = new ShellError("sharpie::sample::broken", "Something broke") { Help = "try again" };

		// Act
		var text = error.Render(color: false);

		// Assert
		_ = text.Should().Be("Error: sharpie::sample::broken\n\n  × Something broke\n  help: try again\n");
	}

	/// <summary>
	/// Checks the whole layout of an error that points into the typed line, as Nushell draws it.
	/// </summary>
	[Fact]
	public void Render_WithSpan_DrawsLineUnderlineAndLabel()
	{
		// Arrange
		var error = new ShellError("sharpie::shell::command_not_found", "Command `gti` not found")
		{
			Source = "gti status",
			Start = 0,
			End = 3,
			Label = "not a builtin, alias or program on PATH",
			Help = "did you mean `git`?",
		};

		// Act
		var text = error.Render(color: false);

		// Assert
		_ = text.Should().Be(
			"Error: sharpie::shell::command_not_found\n" +
			"\n" +
			"  × Command `gti` not found\n" +
			"   ╭─[1:1]\n" +
			" 1 │ gti status\n" +
			"   · ─┬─\n" +
			"   ·  ╰── not a builtin, alias or program on PATH\n" +
			"   ╰────\n" +
			"  help: did you mean `git`?\n");
	}

	/// <summary>
	/// Checks that the underline starts under the span and puts its <c>┬</c> in the middle, with one character
	/// underlined for an empty span.
	/// </summary>
	/// <param name="source">The typed line.</param>
	/// <param name="start">Where the span starts.</param>
	/// <param name="end">Where the span ends.</param>
	/// <param name="underline">The underline row expected.</param>
	/// <param name="label">The label row expected.</param>
	[Theory]
	[InlineData("ls < x", 3, 4, "   ·    ┬", "   ·    ╰── note")]
	[InlineData("echo $(date)", 5, 7, "   ·      ─┬", "   ·       ╰── note")]
	[InlineData("echo hi > /nope/f", 10, 17, "   ·           ───┬───", "   ·              ╰── note")]
	[InlineData("abc", 1, 1, "   ·  ┬", "   ·  ╰── note")]
	[InlineData("abc", 1, 99, "   ·  ─┬", "   ·   ╰── note")]
	public void Render_WithSpan_PlacesUnderline(string source, int start, int end, string underline, string label)
	{
		// Arrange
		var error = new ShellError("sharpie::sample::broken", "Something broke") { Source = source, Start = start, End = end, Label = "note" };

		// Act
		var lines = error.Render(color: false).Split('\n');

		// Assert
		_ = lines[5].Should().Be(underline);
		_ = lines[6].Should().Be(label);
	}

	/// <summary>
	/// Checks that a span on a later line of the source shows that line, with its number in the header and gutter.
	/// </summary>
	[Fact]
	public void Render_WithSpanOnSecondLine_ShowsThatLine()
	{
		// Arrange
		var error = new ShellError("sharpie::sample::broken", "Something broke") { Source = "echo a\nls < x\necho b", Start = 10, End = 11, Label = "note" };

		// Act
		var text = error.Render(color: false);

		// Assert
		_ = text.Should().Contain("   ╭─[2:4]\n 2 │ ls < x\n   ·    ┬\n");
	}

	/// <summary>
	/// Checks that a two-digit line number widens the gutter on every row.
	/// </summary>
	[Fact]
	public void Render_WithLineTen_WidensGutter()
	{
		// Arrange
		var error = new ShellError("sharpie::sample::broken", "Something broke") { Source = new string('\n', 9) + "bad", Start = 9, End = 12, Label = "note" };

		// Act
		var text = error.Render(color: false);

		// Assert
		_ = text.Should().EndWith("    ╭─[10:1]\n 10 │ bad\n    · ─┬─\n    ·  ╰── note\n    ╰────\n");
	}

	/// <summary>
	/// Checks that color puts the code and cross in red, the underline in magenta and <c>help:</c> in cyan.
	/// </summary>
	[Fact]
	public void Render_WithColor_ColorsEachPart()
	{
		// Arrange
		var error = new ShellError("sharpie::sample::broken", "Something broke") { Source = "abc", Start = 0, End = 1, Label = "note", Help = "hint" };

		// Act
		var text = error.Render(color: true);

		// Assert
		_ = text.Should().Contain("Error: \e[31msharpie::sample::broken\e[0m")
			.And.Contain("\e[31m×\e[0m Something broke")
			.And.Contain("\e[35m┬\e[0m")
			.And.Contain("\e[35m╰── note\e[0m")
			.And.Contain("\e[36mhelp:\e[0m hint");
	}

	/// <summary>
	/// Checks that <see cref="ShellError.Write" /> sends the report to standard error, colored only when
	/// <see cref="ShellError.UseColor" /> is set.
	/// </summary>
	/// <param name="color">The value to give <see cref="ShellError.UseColor" />.</param>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Write_WithUseColor_WritesMatchingReport(bool color)
	{
		// Arrange
		var error = new ShellError("sharpie::sample::broken", "Something broke");

		ShellError.UseColor = color;

		// Act
		error.Write();

		// Assert
		_ = this.environment.Error.Should().Be(error.Render(color));
	}

	/// <summary>
	/// Checks which names <see cref="ShellError.DidYouMean" /> offers, and in what order.
	/// </summary>
	/// <param name="name">The name typed.</param>
	/// <param name="candidates">The names that exist.</param>
	/// <param name="expected">The suggestions, closest first.</param>
	[Theory]
	[InlineData("gti", new[] { "gtf", "git" }, new[] { "git", "gtf" })]
	[InlineData("ecoh", new[] { "echo" }, new[] { "echo" })]
	[InlineData("sl", new[] { "sh", "ls" }, new[] { "ls", "sh" })]
	[InlineData("dat", new[] { "cat", "bat" }, new[] { "cat", "bat" })]
	[InlineData("sampletoll", new[] { "sampletool", "sampletoad" }, new[] { "sampletool", "sampletoad" })]
	[InlineData("exti", new[] { "exit", "exit" }, new[] { "exit" })]
	[InlineData("ls", new[] { "ls" }, new string[0])]
	[InlineData("abcdef", new[] { "xyzdef" }, new string[0])]
	[InlineData("cat", new[] { "catalog" }, new string[0])]
	[InlineData("ab", new[] { "ba", "xy" }, new[] { "ba" })]
	[InlineData("abcdef", new[] { "badcfe" }, new string[0])]
	[InlineData("cat", new[] { "cats", "ca" }, new[] { "cats", "ca" })]
	[InlineData("abcdef", new[] { "xyabcd" }, new string[0])]
	[InlineData("kitten", new[] { "sitting" }, new string[0])]
	[InlineData("sampletool", new[] { "samptool" }, new[] { "samptool" })]
	public void DidYouMean_WithCandidates_ReturnsCloseNamesInOrder(string name, string[] candidates, string[] expected)
	{
		// Act
		var result = ShellError.DidYouMean(name, candidates);

		// Assert
		_ = result.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a name too long for the stack is still compared, on the heap.
	/// </summary>
	[Fact]
	public void DidYouMean_WithLongName_FindsCloseName()
	{
		// Arrange
		var name = new string('a', 300);
		var close = new string('a', 299) + "b";

		// Act
		var result = ShellError.DidYouMean(name, [close]);

		// Assert
		_ = result.Should().Equal(close);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		ShellError.UseColor = false;
		this.environment.Dispose();
	}
}
