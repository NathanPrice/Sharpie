using FluentAssertions;
using Sharpie.Shell.Features.Completion;

namespace Sharpie.Shell.Tests.Features.Completion;

/// <summary>
/// Tests for <see cref="CompletionWords" />.
/// </summary>
public sealed class CompletionWordsTests
{
	/// <summary>
	/// Checks that the line splits on blanks and word breaks, with each run of breaks a word of its own and quoted
	/// text kept in its word, and that a trailing blank or break leaves an empty word to complete.
	/// </summary>
	/// <param name="line">The line up to the cursor.</param>
	/// <param name="expected">The words' values, joined by <c>|</c>.</param>
	[Theory]
	[InlineData("", "")]
	[InlineData("git ch", "git|ch")]
	[InlineData("git ", "git|")]
	[InlineData("ls --color=au", "ls|--color|=|au")]
	[InlineData("ls --color=", "ls|--color|=|")]
	[InlineData("scp host:/tmp", "scp|host|:|/tmp")]
	[InlineData("a ;: b", "a|;:|b")]
	[InlineData("cat \"a b\"c d\\ e", "cat|a bc|d e")]
	[InlineData("echo 'a=b' \"x\\\"y\"", "echo|a=b|x\"y")]
	[InlineData("echo a\\", "echo|a")]
	[InlineData("echo \"a\\", "echo|a\\")]
	public void Split_Line_GivesWords(string line, string expected)
	{
		// Act
		var words = CompletionWords.Split(line, CompletionWords.DefaultBreaks);

		// Assert
		_ = string.Join('|', words.Select(word => word.Value)).Should().Be(expected);
	}

	/// <summary>
	/// Checks that a word whose quote is still open is completed from just after that quote, as readline does, while
	/// its start still covers the whole word.
	/// </summary>
	/// <param name="line">The line up to the cursor.</param>
	/// <param name="replaceFrom">Where Tab replaces from.</param>
	/// <param name="value">The text Tab completes.</param>
	/// <param name="quote">The open quote.</param>
	[Theory]
	[InlineData("cat \"my fi", 5, "my fi", '"')]
	[InlineData("cat a'b c", 6, "b c", '\'')]
	[InlineData("cat \"a\\$b", 5, "a$b", '"')]
	public void Split_OpenQuote_ReplacesFromAfterQuote(string line, int replaceFrom, string value, char quote)
	{
		// Act
		var word = CompletionWords.Split(line, CompletionWords.DefaultBreaks)[^1];

		// Assert
		_ = word.Should().Be(new CompletionWord(4, line.Length, value, replaceFrom, quote));
	}
}
