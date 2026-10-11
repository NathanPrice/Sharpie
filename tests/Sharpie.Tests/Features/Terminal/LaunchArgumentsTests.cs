using FluentAssertions;
using Sharpie.Features.Terminal;

namespace Sharpie.Tests.Features.Terminal;

/// <summary>
/// Tests for <see cref="LaunchArguments" />.
/// </summary>
public sealed class LaunchArgumentsTests
{
	/// <summary>
	/// Checks that the words after the first <c>-e</c> are the command, and that no <c>-e</c>, or one with nothing
	/// after it, means the interactive shell.
	/// </summary>
	/// <param name="args">The window's arguments.</param>
	/// <param name="expected">The command, or <see langword="null" /> for the shell.</param>
	[Theory]
	[InlineData(new string[0], null)]
	[InlineData(new[] { "-e", "htop" }, new[] { "htop" })]
	[InlineData(new[] { "-e", "vim", "a file", "b" }, new[] { "vim", "a file", "b" })]
	[InlineData(new[] { "--other", "-e", "less", "-e" }, new[] { "less", "-e" })]
	[InlineData(new[] { "-e" }, null)]
	[InlineData(new[] { "htop" }, null)]
	public void Command_WithArguments_ReturnsWordsAfterExecuteOption(string[] args, string[]? expected)
	{
		// Act
		var command = LaunchArguments.Command(args);

		// Assert
		_ = command.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
	}
}
