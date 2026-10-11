using System.Globalization;
using FluentAssertions;
using Sharpie.Shell.Features.Highlighting;

namespace Sharpie.Shell.Tests.Features.Highlighting;

/// <summary>
/// Tests for <see cref="Highlighter" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class HighlighterTests : IDisposable
{
	private static readonly string[] Builtins = ["echo", "cd", "type"];

	private readonly ShellEnvironment environment = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="HighlighterTests" /> class with <c>PATH</c> and the working
	/// directory pointed at an empty temporary folder holding one program, <c>prog</c>, and one file, <c>notes.txt</c>.
	/// </summary>
	public HighlighterTests()
	{
		var folder = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(folder, "prog", executable: true);
		_ = ShellEnvironment.CreateFile(folder, "notes.txt", executable: false);
		ShellEnvironment.SetPath(folder);
		Directory.SetCurrentDirectory(folder);
	}

	/// <summary>
	/// Checks that the command word is green when it's a builtin or a program on <c>PATH</c>, and red otherwise.
	/// </summary>
	/// <param name="line">The typed line.</param>
	/// <param name="expected">The kind the command word should get.</param>
	[Theory]
	[InlineData("echo hi", TokenKind.Command)]
	[InlineData("prog", TokenKind.Command)]
	[InlineData("ehco hi", TokenKind.UnknownCommand)]
	public void Highlight_CommandWord_MarksWhetherItExists(string line, TokenKind expected)
	{
		// Act
		var tokens = Highlighter.Highlight(line, Builtins);

		// Assert
		_ = tokens.Should().ContainSingle().Which.Should().Be(new Token(0, line.Split(' ')[0].Length, expected));
	}

	/// <summary>
	/// Checks that a quoted argument is colored as a string, quotes included.
	/// </summary>
	[Fact]
	public void Highlight_QuotedArgument_MarksStringWithQuotes()
	{
		// Act
		var tokens = Highlighter.Highlight("echo 'a b'", Builtins);

		// Assert
		_ = tokens.Should().Equal(new Token(0, 4, TokenKind.Command), new Token(5, 5, TokenKind.Quoted));
	}

	/// <summary>
	/// Checks that redirect operators are marked and a target in an existing folder isn't flagged.
	/// </summary>
	/// <param name="operator">The redirect operator.</param>
	[Theory]
	[InlineData(">")]
	[InlineData("1>")]
	[InlineData(">>")]
	[InlineData("1>>")]
	[InlineData("2>")]
	[InlineData("2>>")]
	public void Highlight_RedirectToExistingFolder_MarksOnlyOperator(string @operator)
	{
		// Act
		var tokens = Highlighter.Highlight($"echo hi {@operator} out.txt", Builtins);

		// Assert
		_ = tokens.Should().Equal(new Token(0, 4, TokenKind.Command), new Token(8, @operator.Length, TokenKind.Redirect));
	}

	/// <summary>
	/// Checks that arguments naming paths that don't exist are flagged, including a redirect into a missing folder
	/// and <c>cd</c> to something that isn't a folder.
	/// </summary>
	/// <param name="line">The typed line.</param>
	/// <param name="start">Where the flagged word starts.</param>
	/// <param name="length">How long the flagged word is.</param>
	[Theory]
	[InlineData("cat ./nope", 4, 6)]
	[InlineData("echo hi > nope/out.txt", 10, 12)]
	[InlineData("cd nope", 3, 4)]
	[InlineData("cd notes.txt", 3, 9)]
	public void Highlight_MissingPath_FlagsWord(string line, int start, int length)
	{
		// Act
		var tokens = Highlighter.Highlight(line, Builtins);

		// Assert
		_ = tokens.Should().ContainEquivalentOf(new Token(start, length, TokenKind.MissingPath));
	}

	/// <summary>
	/// Checks that plain words and paths that exist aren't flagged.
	/// </summary>
	/// <param name="line">The typed line.</param>
	[Theory]
	[InlineData("echo hello")]
	[InlineData("echo ./notes.txt")]
	[InlineData("cd .")]
	public void Highlight_ExistingOrPlainArguments_FlagsNothing(string line)
	{
		// Act
		var tokens = Highlighter.Highlight(line, Builtins);

		// Assert
		_ = tokens.Should().NotContain(token => token.Kind == TokenKind.MissingPath);
	}

	/// <summary>
	/// Checks every token of lines that test the edges: a redirect target that is a new file, the word after a redirect
	/// target, paths starting with <c>.</c> or <c>~</c>, <c>~</c> as home, escapes inside and outside quotes, quotes that
	/// are never closed or hold nothing, and a target no file system allows.
	/// </summary>
	/// <param name="line">The typed line.</param>
	/// <param name="expected">The tokens as <c>start,length,kind</c>, separated by <c>;</c>.</param>
	[Theory]
	[InlineData("echo > ./new.txt", "0,4,Command;5,1,Redirect")]
	[InlineData("echo > out ~nope", "0,4,Command;5,1,Redirect;11,5,MissingPath")]
	[InlineData("echo .hidden", "0,4,Command;5,7,MissingPath")]
	[InlineData("echo ~gone", "0,4,Command;5,5,MissingPath")]
	[InlineData("cd ~", "0,2,Command")]
	[InlineData("cd ~/", "0,2,Command")]
	[InlineData("cd ~/gone", "0,2,Command;3,6,MissingPath")]
	[InlineData("echo \"a\\\"b\"", "0,4,Command;5,6,Quoted")]
	[InlineData("echo 'a\\'", "0,4,Command;5,4,Quoted")]
	[InlineData("echo \"abc", "0,4,Command;5,4,Quoted")]
	[InlineData("echo \"\"", "0,4,Command;5,2,Quoted")]
	[InlineData("echo a\\ ~x", "0,4,Command")]
	[InlineData("echo a\\b \"q\"", "0,4,Command;9,3,Quoted")]
	[InlineData("echo > a\0b", "0,4,Command;5,1,Redirect;7,3,MissingPath")]
	public void Highlight_EdgeCaseLine_GivesExactTokens(string line, string expected)
	{
		// Arrange
		ShellEnvironment.SetHome(this.environment.CreateFolder());

		// Act
		var tokens = Highlighter.Highlight(line, Builtins);

		// Assert
		_ = tokens.Should().Equal(expected.Split(';').Select(token => token.Split(',')).Select(parts =>
			new Token(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), Enum.Parse<TokenKind>(parts[2]))));
	}

	/// <inheritdoc />
	public void Dispose() => this.environment.Dispose();
}
