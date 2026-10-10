using FluentAssertions;
using Sharpie.Shell.Features.Completion;

namespace Sharpie.Shell.Tests.Features.Completion;

/// <summary>
/// Tests for <see cref="CompleteCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class CompleteCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly CompleteCommand sut = new();

	/// <summary>
	/// Checks that <see cref="CompleteCommand.Run" /> with <c>-p</c> reports that the named command has no
	/// completion specification.
	/// </summary>
	/// <param name="command">The command name passed after <c>-p</c>.</param>
	[Theory]
	[InlineData("git")]
	[InlineData("foo")]
	public void Run_WithPFlag_PrintsNoSpecificationError(string command)
	{
		// Act
		this.sut.Run(["-p", command]);

		// Assert
		_ = this.environment.Output.Should().Be($"complete: {command}: no completion specification" + Environment.NewLine);
	}

	/// <summary>
	/// Checks that <see cref="CompleteCommand.Run" /> prints nothing when there's no <c>-p</c> with a command name.
	/// </summary>
	/// <param name="arguments">The words passed to the command.</param>
	[Theory]
	[InlineData([new string[0]])]
	[InlineData([new[] { "-p" }])]
	public void Run_WithoutPFlagAndCommand_PrintsNothing(string[] arguments)
	{
		// Act
		this.sut.Run(arguments);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <see cref="CompleteCommand.Run" /> with <c>-C</c> registers silently.
	/// </summary>
	[Fact]
	public void Run_WithCFlag_PrintsNothing()
	{
		// Act
		this.sut.Run(["-C", "/path/to/git/completer", "git"]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <see cref="CompleteCommand.Run" /> with <c>-p</c> prints a registered completer in the
	/// normalized <c>complete -C '&lt;path&gt;' &lt;command&gt;</c> form.
	/// </summary>
	/// <param name="script">The completer script path registered with <c>-C</c>.</param>
	/// <param name="command">The command the script completes.</param>
	[Theory]
	[InlineData("/path/to/git/completer", "git")]
	[InlineData("/path/to/docker/completer", "docker")]
	public void Run_WithPFlagAfterCFlag_PrintsNormalizedSpecification(string script, string command)
	{
		// Arrange
		this.sut.Run(["-C", script, command]);

		// Act
		this.sut.Run(["-p", command]);

		// Assert
		_ = this.environment.Output.Should().Be($"complete -C '{script}' {command}" + Environment.NewLine);
	}

	/// <summary>
	/// Checks that registering a command again with <c>-C</c> replaces the earlier script.
	/// </summary>
	[Fact]
	public void Run_WithCFlagTwice_PrintsLatestScript()
	{
		// Arrange
		this.sut.Run(["-C", "/old", "git"]);
		this.sut.Run(["-C", "/new", "git"]);

		// Act
		this.sut.Run(["-p", "git"]);

		// Assert
		_ = this.environment.Output.Should().Be("complete -C '/new' git" + Environment.NewLine);
	}

	/// <summary>
	/// Checks that <see cref="CompleteCommand.FindCompleter" /> gives the script registered with <c>-C</c>, and
	/// <see langword="null" /> for a command with none.
	/// </summary>
	[Fact]
	public void FindCompleter_AfterCFlag_ReturnsScriptOnlyForThatCommand()
	{
		// Arrange
		this.sut.Run(["-C", "/path/to/git/completer", "git"]);

		// Act / Assert
		_ = this.sut.FindCompleter("git").Should().Be("/path/to/git/completer");
		_ = this.sut.FindCompleter("docker").Should().BeNull();
	}

	/// <summary>
	/// Checks that <c>-p</c> reports no completion specification once <c>-r</c> has removed the command's completer.
	/// </summary>
	[Fact]
	public void Run_WithRFlagAfterCFlag_PrintsNoSpecification()
	{
		// Arrange
		this.sut.Run(["-C", "/path/to/git/completer", "git"]);
		this.sut.Run(["-r", "git"]);

		// Act
		this.sut.Run(["-p", "git"]);

		// Assert
		_ = this.environment.Output.Should().Be("complete: git: no completion specification" + Environment.NewLine);
	}

	/// <summary>
	/// Checks that <see cref="CompleteCommand.Run" /> with <c>-r</c> prints nothing, whether or not the command had a
	/// completer registered.
	/// </summary>
	/// <param name="registerFirst">Whether to register a completer with <c>-C</c> before removing it.</param>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Run_WithRFlag_PrintsNothing(bool registerFirst)
	{
		// Arrange
		if (registerFirst)
		{
			this.sut.Run(["-C", "/path/to/git/completer", "git"]);
		}

		// Act
		this.sut.Run(["-r", "git"]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <see cref="CompleteCommand.FindCompleter" /> gives <see langword="null" /> after <c>-r</c>, so Tab
	/// no longer runs the removed script.
	/// </summary>
	[Fact]
	public void FindCompleter_AfterRFlag_ReturnsNull()
	{
		// Arrange
		this.sut.Run(["-C", "/path/to/git/completer", "git"]);

		// Act
		this.sut.Run(["-r", "git"]);

		// Assert
		_ = this.sut.FindCompleter("git").Should().BeNull();
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
