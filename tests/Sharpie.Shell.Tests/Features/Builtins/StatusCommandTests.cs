using FluentAssertions;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="StatusCommand" />.
/// </summary>
public sealed class StatusCommandTests
{
	/// <summary>
	/// Checks that the command gives its fixed status whatever arguments it's given, as bash's <c>:</c>,
	/// <c>true</c> and <c>false</c> do.
	/// </summary>
	/// <param name="status">The status the command is made with.</param>
	/// <param name="args">The arguments.</param>
	[Theory]
	[InlineData(0, new string[0])]
	[InlineData(0, new[] { "--help", "x" })]
	[InlineData(1, new string[0])]
	[InlineData(1, new[] { "--help" })]
	public void Run_WithAnyArguments_GivesFixedStatus(int status, string[] args)
	{
		// Arrange
		var sut = new StatusCommand("sample", status, "Sample command");

		// Act
		var result = sut.Run(args);

		// Assert
		_ = result.Should().Be(status);
	}

	/// <summary>
	/// Checks that the name and description are the ones the command was made with.
	/// </summary>
	[Fact]
	public void NameAndDescription_AreTheGivenOnes()
	{
		// Act
		var sut = new StatusCommand(":", 0, "Does nothing and succeeds");

		// Assert
		_ = sut.Name.Should().Be(":");
		_ = sut.Description.Should().Be("Does nothing and succeeds");
	}
}
