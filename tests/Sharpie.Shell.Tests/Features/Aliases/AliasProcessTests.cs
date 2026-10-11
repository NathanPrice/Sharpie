using FluentAssertions;
using Sharpie.Shell.Features.Aliases;

namespace Sharpie.Shell.Tests.Features.Aliases;

/// <summary>
/// Runs <see cref="AliasCommand" /> through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class AliasProcessTests
{
	/// <summary>
	/// Checks that an alias defined at the prompt runs its value with the arguments typed after it.
	/// </summary>
	[Fact]
	public async Task Shell_WithAlias_RunsAliasValue()
	{
		// Act
		var run = await ShellProcess.RunAsync("alias greet='echo hello'\ngreet world\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ hello world\n$ ");
		_ = run.Exited.Should().BeTrue();
	}
}
