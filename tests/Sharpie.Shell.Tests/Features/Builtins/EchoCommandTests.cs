using FluentAssertions;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="EchoCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class EchoCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly EchoCommand sut = new();

	/// <summary>
	/// Checks that <see cref="EchoCommand.Run" /> prints its arguments on one line, separated by single spaces.
	/// </summary>
	/// <param name="arguments">The words passed to the command.</param>
	/// <param name="expected">The line that should be printed, without its line ending.</param>
	[Theory]
	[InlineData(new string[0], "")]
	[InlineData(new[] { "hello" }, "hello")]
	[InlineData(new[] { "hello", "world", "again" }, "hello world again")]
	public void Run_WithArguments_PrintsThemJoinedBySpaces(string[] arguments, string expected)
	{
		// Act
		_ = this.sut.Run(arguments);

		// Assert
		_ = this.environment.Output.Should().Be(expected + Environment.NewLine);
	}

	/// <summary>
	/// Checks that <see cref="EchoCommand.Run" /> reads the <c>-n</c>, <c>-e</c> and <c>-E</c> options the way
	/// bash does and prints every other word as is.
	/// </summary>
	/// <param name="arguments">The words passed to the command.</param>
	/// <param name="expected">The text that should be printed, without any line ending.</param>
	/// <param name="endsWithNewline">Whether a line ending should follow <paramref name="expected" />.</param>
	[Theory]
	[InlineData(new[] { "-n", "foo" }, "foo", false)]
	[InlineData(new[] { "-n" }, "", false)]
	[InlineData(new[] { "-n", "-e", "a\\tb" }, "a\tb", false)]
	[InlineData(new[] { "-ne", "a\\tb" }, "a\tb", false)]
	[InlineData(new[] { "-en", "x" }, "x", false)]
	[InlineData(new[] { "-e", "-E", "a\\nb" }, "a\\nb", true)]
	[InlineData(new[] { "-eE", "a\\nb" }, "a\\nb", true)]
	[InlineData(new[] { "-E", "-e", "a\\nb" }, "a\nb", true)]
	[InlineData(new[] { "-x", "foo" }, "-x foo", true)]
	[InlineData(new[] { "-nx", "foo" }, "-nx foo", true)]
	[InlineData(new[] { "-" }, "-", true)]
	[InlineData(new[] { "--" }, "--", true)]
	[InlineData(new[] { "--", "-n" }, "-- -n", true)]
	[InlineData(new[] { "foo", "-n" }, "foo -n", true)]
	[InlineData(new[] { "a\\nb" }, "a\\nb", true)]
	public void Run_WithOptions_FollowsBash(string[] arguments, string expected, bool endsWithNewline)
	{
		// Act
		_ = this.sut.Run(arguments);

		// Assert
		_ = this.environment.Output.Should().Be(expected + (endsWithNewline ? Environment.NewLine : string.Empty));
	}

	/// <summary>
	/// Checks that <see cref="EchoCommand.Run" /> with <c>-e</c> turns backslash escapes into the characters
	/// they stand for, and keeps unknown ones as written.
	/// </summary>
	/// <param name="text">The word printed after <c>-e</c>.</param>
	/// <param name="expected">The text that should be printed, without its line ending.</param>
	[Theory]
	[InlineData("a\\nb", "a\nb")]
	[InlineData("\\t", "\t")]
	[InlineData("\\a\\b\\f\\r\\v", "\a\b\f\r\v")]
	[InlineData("\\e[0m", "\u001b[0m")]
	[InlineData("\\E", "\u001b")]
	[InlineData("\\\\", "\\")]
	[InlineData("\\0101", "A")]
	[InlineData("\\01017", "A7")]
	[InlineData("\\0", "\0")]
	[InlineData("\\08", "\u00008")]
	[InlineData("\\0777", "ÿ")]
	[InlineData("\\x41", "A")]
	[InlineData("\\x4", "\u0004")]
	[InlineData("\\x414", "A4")]
	[InlineData("\\x4a", "J")]
	[InlineData("\\x4G", "\u0004G")]
	[InlineData("\\xg", "\\xg")]
	[InlineData("\\x", "\\x")]
	[InlineData("\\q", "\\q")]
	[InlineData("a\\", "a\\")]
	public void Run_WithEscapesOn_ExpandsThem(string text, string expected)
	{
		// Act
		_ = this.sut.Run(["-e", text]);

		// Assert
		_ = this.environment.Output.Should().Be(expected + Environment.NewLine);
	}

	/// <summary>
	/// Checks that a <c>\c</c> under <c>-e</c> stops all output, the words after it and the line ending included.
	/// </summary>
	[Fact]
	public void Run_WithStopEscape_PrintsNothingAfterIt()
	{
		// Act
		_ = this.sut.Run(["-e", "a\\cb", "more"]);

		// Assert
		_ = this.environment.Output.Should().Be("a");
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
