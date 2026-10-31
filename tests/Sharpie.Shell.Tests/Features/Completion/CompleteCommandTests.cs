using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Completion;

/// <summary>
/// Tests for <see cref="CompleteCommand" />. Each printed line was checked against bash 5.3.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class CompleteCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly CompletionEngine engine = new(new DeclareCommand(new ShellSettings()), new Dictionary<string, ICommand>(), new Dictionary<string, string>(), _ => 0);
	private readonly CompleteCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="CompleteCommandTests" /> class.
	/// </summary>
	public CompleteCommandTests() => this.sut = new CompleteCommand(this.engine);

	/// <summary>
	/// Checks that a spec set with any mix of options prints back with <c>-p</c> in bash's order and quoting.
	/// </summary>
	/// <param name="args">The arguments that set the spec for <c>sampletool</c>.</param>
	/// <param name="expected">What <c>complete -p sampletool</c> prints.</param>
	[Theory]
	[InlineData(new[] { "-o", "bashdefault", "-o", "default", "-o", "nospace", "-F", "_sampletool", "sampletool" }, "complete -o bashdefault -o default -o nospace -F _sampletool sampletool")]
	[InlineData(new[] { "-f", "-F", "_sampletool", "sampletool" }, "complete -f -F _sampletool sampletool")]
	[InlineData(new[] { "-W", "a b", "-P", "p", "-S", "s", "-X", "!x*", "sampletool" }, "complete -W 'a b' -P 'p' -S 's' -X '!x*' sampletool")]
	[InlineData(new[] { "-A", "user", "-A", "function", "-d", "-c", "sampletool" }, "complete -c -d -u -A function sampletool")]
	[InlineData(new[] { "-C", "/x y", "sampletool" }, "complete -C '/x y' sampletool")]
	[InlineData(new[] { "-o", "filenames", "-G", "*.c", "sampletool" }, "complete -o filenames -G '*.c' sampletool")]
	[InlineData(new[] { "-onospace", "-Wa", "-fd", "--", "sampletool" }, "complete -o nospace -d -f -W 'a' sampletool")]
	[InlineData(new[] { "-A", "arrayvar", "-v", "-A", "setopt", "sampletool" }, "complete -v -A arrayvar -A setopt sampletool")]
	[InlineData(new[] { "-A", "group", "-A", "job", "-A", "service", "-A", "binding", "-A", "disabled", "-A", "enabled", "-A", "helptopic", "-A", "hostname", "-A", "running", "-A", "shopt", "-A", "signal", "-A", "stopped", "sampletool" }, "complete -g -j -s -A binding -A disabled -A enabled -A helptopic -A hostname -A running -A shopt -A signal -A stopped sampletool")]
	[InlineData(new[] { "-A", "alias", "-A", "builtin", "-A", "command", "-A", "directory", "-A", "export", "-A", "file", "-A", "keyword", "-A", "user", "-A", "variable", "sampletool" }, "complete -a -b -c -d -e -f -k -u -v sampletool")]
	[InlineData(new[] { "-o", "bashdefault", "-o", "default", "-o", "dirnames", "-o", "filenames", "-o", "noquote", "-o", "nosort", "-o", "nospace", "-o", "plusdirs", "sampletool" }, "complete -o bashdefault -o default -o dirnames -o filenames -o noquote -o nosort -o nospace -o plusdirs sampletool")]
	public void Run_ThenPrint_GivesSpecAsBash(string[] args, string expected)
	{
		// Arrange
		var status = this.sut.Run(args);

		// Act
		var printed = this.sut.Run(["-p", "sampletool"]);

		// Assert
		_ = (status, printed).Should().Be((0, 0));
		_ = this.environment.Output.Should().Be(expected + Environment.NewLine);
	}

	/// <summary>
	/// Checks that <c>complete</c> with no arguments, or <c>-p</c> alone, lists every spec in name order, and that a
	/// later spec for a command replaces the earlier one.
	/// </summary>
	/// <param name="args">The listing arguments.</param>
	[Theory]
	[InlineData([new string[0]])]
	[InlineData([new[] { "-p" }])]
	public void Run_WithoutNames_ListsEverySpec(string[] args)
	{
		// Arrange
		_ = this.sut.Run(["-W", "x", "sampletool"]);
		_ = this.sut.Run(["-F", "_old", "othertool"]);
		_ = this.sut.Run(["-F", "_new", "othertool"]);

		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be($"complete -F _new othertool{Environment.NewLine}complete -W 'x' sampletool{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <c>-p</c> and <c>-r</c> report a command with no spec and give 1, still handling the others.
	/// </summary>
	/// <param name="flag">The flag.</param>
	[Theory]
	[InlineData("-p")]
	[InlineData("-r")]
	public void Run_WithMissingName_ReportsIt(string flag)
	{
		// Arrange
		_ = this.sut.Run(["-W", "x", "sampletool"]);

		// Act
		var status = this.sut.Run([flag, "sampletool", "nope"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::complete::not_found", "No completion is set for `nope`").Render(color: false));
		_ = this.engine.Specs.ContainsKey("sampletool").Should().Be(flag == "-p");
	}

	/// <summary>
	/// Checks that <c>-r</c> with no names removes every spec.
	/// </summary>
	[Fact]
	public void Run_WithRemoveAlone_RemovesEverySpec()
	{
		// Arrange
		_ = this.sut.Run(["-W", "x", "sampletool", "othertool"]);

		// Act
		var status = this.sut.Run(["-r"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.engine.Specs.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>-D</c> sets the spec for commands with none, which <see cref="CompletionEngine.Find" /> then
	/// gives for any name.
	/// </summary>
	[Fact]
	public void Run_WithDefault_SetsSpecForEveryCommand()
	{
		// Act
		var status = this.sut.Run(["-D", "-F", "_loader"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.engine.Find("sampletool")!.Function.Should().Be("_loader");
		_ = this.engine.Specs.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a bad option, option value or action gives 2 and sets nothing.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="code">The error code.</param>
	/// <param name="summary">The error summary.</param>
	[Theory]
	[InlineData(new[] { "-z", "sampletool" }, "sharpie::complete::invalid_option", "`-z` isn't an option of `complete`")]
	[InlineData(new[] { "-o", "bogus", "sampletool" }, "sharpie::complete::invalid_option", "`bogus` isn't a completion option")]
	[InlineData(new[] { "-A", "bogus", "sampletool" }, "sharpie::complete::invalid_action", "`bogus` isn't an action name")]
	[InlineData(new[] { "-F" }, "sharpie::complete::missing_argument", "`-F` needs a value after it")]
	public void Run_WithBadOption_GivesTwo(string[] args, string code, string summary)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(2);
		_ = this.engine.Specs.Should().BeEmpty();
		_ = this.environment.Error.Should().Be(new ShellError(code, summary)
		{
			Help = "complete takes -abcdefgjksuv, -o option, -A action, -G pattern, -W words, -F function, -C command, -X filter, -P prefix and -S suffix",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that a spec with no command to set it for gives 2, as bash's usage error does.
	/// </summary>
	[Fact]
	public void Run_WithoutName_GivesTwo()
	{
		// Act
		var status = this.sut.Run(["-F", "_sampletool"]);

		// Assert
		_ = status.Should().Be(2);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::complete::missing_name", "`complete` needs the commands to set it for")
		{
			Help = "complete [-abcdefgjksuv] [-o option] [-A action] [-W words] [-F function] [-C command] name...",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that <c>-E</c> and <c>-I</c> are taken without a name and set nothing.
	/// </summary>
	/// <param name="flag">The flag.</param>
	[Theory]
	[InlineData("-E")]
	[InlineData("-I")]
	public void Run_WithEmptyOrInitial_IsTaken(string flag)
	{
		// Act
		var status = this.sut.Run([flag, "-F", "_sampletool"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.engine.Specs.Should().BeEmpty();
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
