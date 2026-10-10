using FluentAssertions;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="PwdCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class PwdCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly PwdCommand sut = new();

	/// <summary>
	/// Checks that <see cref="PwdCommand.Run" /> prints the current working directory on its own line.
	/// </summary>
	[Fact]
	public void Run_InFolder_PrintsThatFolder()
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		Directory.SetCurrentDirectory(folder);

		// Act
		this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().Be(folder + Environment.NewLine);
	}

	/// <summary>
	/// Restores the console and working directory after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
