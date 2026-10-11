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
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
