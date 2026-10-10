using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Sharpie.Shell.Features.Aliases;

namespace Sharpie.Shell.Tests.Features.Aliases;

/// <summary>
/// Tests for <see cref="AliasCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class AliasCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The process-wide writer, borrowed only to put it back.")]
	private readonly TextWriter originalError = Console.Error;
	private readonly StringWriter error = new();
	private readonly AliasCommand sut = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="AliasCommandTests" /> class.
	/// </summary>
	public AliasCommandTests() => Console.SetError(this.error);

	/// <summary>
	/// Checks that <c>alias NAME=VALUE</c> stores the alias and <c>alias NAME</c> prints it back.
	/// </summary>
	/// <param name="name">The alias name.</param>
	[Theory]
	[InlineData("greet")]
	[InlineData("sample-run")]
	[InlineData("..")]
	public void Run_WithDefinition_StoresAlias(string name)
	{
		// Arrange
		this.sut.Run([$"{name}=echo hello"]);

		// Act
		this.sut.Run([name]);

		// Assert
		_ = this.environment.Output.Should().Be($"alias {name}='echo hello'{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <c>alias</c> alone lists every alias sorted by name, with a <c>'</c> in a value written so the line
	/// can be typed back in.
	/// </summary>
	[Fact]
	public void Run_WithNoArguments_ListsAliasesQuoted()
	{
		// Arrange
		this.sut.Run(["zed=echo it's"]);
		this.sut.Run(["abc=ls"]);

		// Act
		this.sut.Run([]);

		// Assert
		_ = this.environment.Output.Should().Be($"alias abc='ls'{Environment.NewLine}alias zed='echo it'\\''s'{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that asking for an alias that doesn't exist reports it on standard error.
	/// </summary>
	[Fact]
	public void Run_WithUnknownName_PrintsNotFound()
	{
		// Act
		this.sut.Run(["missing"]);

		// Assert
		_ = this.error.ToString().Should().Be($"alias: missing: not found{Environment.NewLine}");
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a name with a character POSIX doesn't allow is reported and nothing is stored.
	/// </summary>
	[Fact]
	public void Run_WithInvalidName_PrintsErrorAndStoresNothing()
	{
		// Act
		this.sut.Run(["a/b=ls"]);

		// Assert
		_ = this.error.ToString().Should().Be($"alias: `a/b': invalid alias name{Environment.NewLine}");
		_ = this.sut.Aliases.Should().BeEmpty();
	}

	/// <summary>
	/// Checks which words <see cref="AliasCommand.Expand" /> replaces: only command names, which are the first word
	/// and the word after each <c>|</c>.
	/// </summary>
	/// <param name="line">The words, joined by spaces.</param>
	/// <param name="expected">The expanded words, joined by spaces.</param>
	[Theory]
	[InlineData("greet world", "echo hello world")]
	[InlineData("ls | greet", "ls | echo hello")]
	[InlineData("echo greet", "echo greet")]
	[InlineData("other arg", "other arg")]
	[InlineData("chain", "echo hello")]
	[InlineData("loud x", "loud --shout x")]
	[InlineData("piped", "ls | echo hello")]
	public void Expand_WithAliases_ReplacesCommandNames(string line, string expected)
	{
		// Arrange
		this.sut.Define("greet", "echo hello");
		this.sut.Define("chain", "greet");
		this.sut.Define("loud", "loud --shout");
		this.sut.Define("piped", "ls | greet");

		// Act
		var words = this.sut.Expand(line.Split(' '), null);

		// Assert
		_ = string.Join(' ', words).Should().Be(expected);
	}

	/// <summary>
	/// Checks that a quoted alias value stays one word and a <c>$NAME</c> in it expands when the alias is used.
	/// </summary>
	[Fact]
	public void Expand_WithQuotesAndVariable_SplitsValueLikeTypedLine()
	{
		// Arrange
		this.sut.Define("say", "echo 'two words' $WHO");

		// Act
		var words = this.sut.Expand(["say"], new Dictionary<string, string> { ["WHO"] = "you" });

		// Assert
		_ = words.Should().Equal("echo", "two words", "you");
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose()
	{
		Console.SetError(this.originalError);
		this.error.Dispose();
		this.environment.Dispose();
	}
}
