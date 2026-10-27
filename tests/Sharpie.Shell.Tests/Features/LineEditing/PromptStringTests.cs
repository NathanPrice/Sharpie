using FluentAssertions;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Shell.Tests.Features.LineEditing;

/// <summary>
/// Tests for <see cref="PromptString" />.
/// </summary>
public sealed class PromptStringTests
{
	private static readonly Dictionary<string, string> NoVariables = [];

	/// <summary>
	/// Checks the escapes whose value doesn't depend on the machine, and that one bash doesn't have stays as typed.
	/// </summary>
	/// <param name="ps1">The prompt string.</param>
	/// <param name="expected">The prompt it shows.</param>
	[Theory]
	[InlineData("a\\nb", "a\nb")]
	[InlineData("\\e[1m", "\e[1m")]
	[InlineData("\\a\\r", "\a\r")]
	[InlineData("\\101\\0612", "A12")]
	[InlineData("x\\101", "xA")]
	[InlineData("\\01x\\7", "\\01x\\7")]
	[InlineData("\\\\", "\\")]
	[InlineData("\\\\$NAME \\044NAME", "$NAME x")]
	[InlineData("\\\"q\\\"", "\"q\"")]
	[InlineData("\\q", "\\q")]
	[InlineData("\\[x\\]", "\u0001x\u0002")]
	[InlineData("\\! \\#", "5 7")]
	[InlineData("end\\", "end\\")]
	[InlineData("\\s", "sampleshell")]
	public void Expand_Escape_GivesItsValue(string ps1, string expected)
	{
		// Act
		var prompt = Expand(ps1);

		// Assert
		_ = prompt.Should().Be(expected);
	}

	/// <summary>
	/// Checks the shape of the date, the times and the version, whose values change.
	/// </summary>
	/// <param name="ps1">The prompt string.</param>
	/// <param name="pattern">What the prompt it shows looks like.</param>
	[Theory]
	[InlineData("\\d", "^[A-Z][a-z]{2} [A-Z][a-z]{2} [0-9]{2}$")]
	[InlineData("\\t", "^[0-9]{2}:[0-9]{2}:[0-9]{2}$")]
	[InlineData("\\T", "^(0[1-9]|1[0-2]):[0-9]{2}:[0-9]{2}$")]
	[InlineData("\\@", "^(0[1-9]|1[0-2]):[0-9]{2} (AM|PM)$")]
	[InlineData("\\A", "^[0-9]{2}:[0-9]{2}$")]
	[InlineData("\\v \\V", "^[0-9]+\\.[0-9]+ [0-9]+\\.[0-9]+\\.[0-9]+$")]
	public void Expand_ClockOrVersion_HasItsShape(string ps1, string pattern)
	{
		// Act
		var prompt = Expand(ps1);

		// Assert
		_ = prompt.Should().MatchRegex(pattern);
	}

	/// <summary>
	/// Checks the user, the last folder and the prompt sign, with the home folder shown as <c>~</c>.
	/// </summary>
	[Fact]
	public void Expand_UserFolderAndSign_ShowsThem()
	{
		// Arrange
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var folder = Path.Combine(home, "code", "app");
		var sign = Environment.IsPrivilegedProcess ? "#" : "$";

		// Act
		var prompt = PromptString.Expand("\\u \\w \\W\\$ ", folder, NoVariables, _ => string.Empty, 1, 1);

		// Assert
		_ = prompt.Should().Be($"{Environment.UserName} ~/code/app app{sign} ");
	}

	/// <summary>
	/// Checks that a folder whose name holds <c>$(...)</c>, backquotes or <c>$NAME</c> is shown as it is and nothing
	/// in it runs.
	/// </summary>
	[Fact]
	public void Expand_FolderWithCommandSubstitution_ShowsItWithoutRunningIt()
	{
		// Arrange
		const string folder = "/tmp/a$(echo HACK)`id`$HOME\\";
		var ran = new List<string>();

		// Act
		var prompt = PromptString.Expand("\\w", folder, NoVariables, list => { ran.Add(list); return "ran"; }, 1, 1);

		// Assert
		_ = prompt.Should().Be(folder);
		_ = ran.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that variables and command substitutions typed in the prompt string are expanded, quotes are kept, and an
	/// escaped <c>$</c> stays literal.
	/// </summary>
	[Fact]
	public void Expand_VariablesAndSubstitution_ExpandsThemKeepingQuotes()
	{
		// Arrange
		Dictionary<string, string> variables = new() { ["NAME"] = "x y" };

		// Act
		var prompt = PromptString.Expand("$NAME \"$NAME\" $(branch) \\$NAME", "/", variables, list => $"[{list}]", 1, 1);

		// Assert
		_ = prompt.Should().Be("x y \"x y\" [branch] $NAME");
	}

	/// <summary>
	/// Checks that text between the markers is written but takes no columns.
	/// </summary>
	[Fact]
	public void Visible_MarkedText_IsWrittenWithoutWidth()
	{
		// Act
		var (text, width) = PromptString.Visible("\u0001\e[32m\u0002ab\u0001\e[0m\u0002 ");

		// Assert
		_ = text.Should().Be("\e[32mab\e[0m ");
		_ = width.Should().Be(3);
	}

	/// <summary>
	/// Checks that the last line becomes the prompt and the lines above it the header, with raw line breaks and no
	/// markers.
	/// </summary>
	/// <param name="expanded">The expanded prompt.</param>
	/// <param name="header">The lines printed once.</param>
	/// <param name="prompt">The line redrawn on each key.</param>
	[Theory]
	[InlineData("$ ", "", "$ ")]
	[InlineData("\u0001c\u0002a\nb\n\u0001d\u0002$ ", "ca\r\nb\r\n", "\u0001d\u0002$ ")]
	public void Split_Lines_PutsLastLineInPrompt(string expanded, string header, string prompt)
	{
		// Act
		var result = PromptString.Split(expanded);

		// Assert
		_ = result.Should().Be((header, prompt));
	}

	private static string Expand(string ps1) => PromptString.Expand(ps1, "/", new Dictionary<string, string> { ["NAME"] = "x", ["0"] = "/usr/bin/sampleshell" }, _ => string.Empty, 5, 7);
}
