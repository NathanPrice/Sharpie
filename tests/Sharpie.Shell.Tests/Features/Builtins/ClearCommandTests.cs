using FluentAssertions;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="ClearCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ClearCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly ClearCommand sut = new();

	/// <summary>
	/// Checks that <see cref="ClearCommand.Run" /> prints only the clear-screen escape sequence, with no newline after
	/// it, so the next prompt lands at the top of the cleared screen.
	/// </summary>
	[Fact]
	public void Run_WithRedirectedOutput_PrintsClearSequenceOnly()
	{
		// Arrange
		// ShellEnvironment has already redirected Console.Out, which is what made Console.Clear unusable here.

		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().Be("\e[H\e[2J\e[3J");
	}

	/// <summary>
	/// Restores the console and working directory after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
