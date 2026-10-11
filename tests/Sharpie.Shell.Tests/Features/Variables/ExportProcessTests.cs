using FluentAssertions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Runs <see cref="ExportCommand" />, <see cref="UnsetCommand" /> and bare <c>NAME=VALUE</c> lines through the built
/// shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class ExportProcessTests
{
	/// <summary>
	/// Checks that a line of bare assignments sets shell variables that later lines expand, without exporting them.
	/// </summary>
	[Fact]
	public async Task Shell_WithBareAssignment_SetsShellVariable()
	{
		// Act
		var run = await ShellProcess.RunAsync("SHARPIE_SAMPLE_A=one SHARPIE_SAMPLE_B=two\necho $SHARPIE_SAMPLE_A $SHARPIE_SAMPLE_B\nprintenv SHARPIE_SAMPLE_A\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ one two\n$ $ ");
		_ = run.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that an exported variable reaches the programs the shell starts, that assigning it again updates them,
	/// and that <c>unset</c> takes it away.
	/// </summary>
	[Fact]
	public async Task Shell_WithExportAssignAndUnset_UpdatesChildEnvironment()
	{
		// Act
		var run = await ShellProcess.RunAsync(
			"export SHARPIE_SAMPLE_X=first\nprintenv SHARPIE_SAMPLE_X\nSHARPIE_SAMPLE_X=second\nprintenv SHARPIE_SAMPLE_X\nunset SHARPIE_SAMPLE_X\nprintenv SHARPIE_SAMPLE_X\necho [$SHARPIE_SAMPLE_X]\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ first\n$ $ second\n$ $ $ []\n$ ");
	}

	/// <summary>
	/// Checks that <c>$NAME</c> expands a variable the shell got from its environment.
	/// </summary>
	[Fact]
	public async Task Shell_WithInheritedVariable_ExpandsIt()
	{
		// Act
		var run = await ShellProcess.RunAsync("echo $SHARPIE_SAMPLE_INHERITED\nexit\n", environment: new Dictionary<string, string> { ["SHARPIE_SAMPLE_INHERITED"] = "inherited" }).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ inherited\n$ ");
	}

	/// <summary>
	/// Checks that <c>export</c>, <c>unset</c>, <c>.</c> and <c>source</c> are registered as builtins.
	/// </summary>
	/// <param name="name">The builtin name.</param>
	[Theory]
	[InlineData("export")]
	[InlineData("unset")]
	[InlineData(".")]
	[InlineData("source")]
	public async Task Shell_WithType_ReportsBuiltin(string name)
	{
		// Act
		var run = await ShellProcess.RunAsync($"type {name}\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be($"$ {name} is a shell builtin\n$ ");
	}
}
