using FluentAssertions;
using Sharpie.Shell.Features.Variables;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Runs <see cref="DeclareCommand" /> through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class DeclareProcessTests
{
	/// <summary>
	/// Checks that <c>declare</c> is registered with the running shell.
	/// </summary>
	[Fact]
	public async Task Shell_WithTypeDeclare_ReportsBuiltin()
	{
		// Act
		var run = await ShellProcess.RunAsync("type declare\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ declare is a shell builtin\n$ ");
		_ = run.Exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>declare -p</c> on a name that was never set prints a not found error to standard error.
	/// </summary>
	[Fact]
	public async Task Shell_WithDeclarePMissing_ReportsNotFound()
	{
		// Act
		var run = await ShellProcess.RunAsync("declare -p missing_variable\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Error.Should().Be(new ShellError("sharpie::declare::not_found", "No variable named `missing_variable`").Render(color: false));
		_ = run.Output.Should().Be("$ $ ");
		_ = run.Exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>declare NAME=VALUE</c> stores a variable that <c>declare -p</c> prints, and that declaring it
	/// again replaces the value.
	/// </summary>
	[Fact]
	public async Task Shell_WithDeclaredVariable_PrintsAndOverwrites()
	{
		// Act
		var run = await ShellProcess.RunAsync(
			"declare foo=bar\ndeclare -p foo\ndeclare -p missing_variable\ndeclare foo=bar2\ndeclare -p foo\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ declare -- foo=\"bar\"\n$ $ $ declare -- foo=\"bar2\"\n$ ");
		_ = run.Error.Should().Be(new ShellError("sharpie::declare::not_found", "No variable named `missing_variable`").Render(color: false));
		_ = run.Exited.Should().BeTrue();
	}

	/// <summary>
	/// Checks that <c>$NAME</c> on a later line expands to the value <c>declare</c> stored.
	/// </summary>
	[Fact]
	public async Task Shell_WithDeclaredVariable_ExpandsInLaterCommand()
	{
		// Act
		var run = await ShellProcess.RunAsync("declare name=world\necho hello $name\nexit\n").ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("$ $ hello world\n$ ");
		_ = run.Exited.Should().BeTrue();
	}
}
