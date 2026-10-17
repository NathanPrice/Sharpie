using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for <see cref="ReadCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ReadCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The process-wide reader, borrowed only to put it back.")]
	private readonly TextReader originalIn = Console.In;
	private readonly DeclareCommand declare = new();
	private readonly Queue<ConsoleKeyInfo> keys = new();

	/// <summary>
	/// Checks that piped input is split on the default separators, with the last name getting the rest and names past
	/// the last field getting an empty value, as bash gives.
	/// </summary>
	/// <param name="input">What standard input holds.</param>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">Each name and its value, as <c>name=value</c>.</param>
	[Theory]
	[InlineData("x y z\n", new[] { "a", "b" }, new[] { "a=x", "b=y z" })]
	[InlineData(" a  b  c \n", new[] { "a", "b" }, new[] { "a=a", "b=b  c" })]
	[InlineData("  only  \n", new[] { "a" }, new[] { "a=only" })]
	[InlineData("  a b  \n", new[] { "a" }, new[] { "a=a b" })]
	[InlineData("a\tb\n", new[] { "a", "b", "c" }, new[] { "a=a", "b=b", "c=" })]
	[InlineData("  a b  \n", new string[0], new[] { "REPLY=  a b  " })]
	[InlineData(@"a\ b c" + "\n", new[] { "a", "b" }, new[] { "a=a b", "b=c" })]
	[InlineData(@"\  a  " + "\n", new[] { "a" }, new[] { "a=  a" })]
	[InlineData(@"a\tb\\" + "\n", new[] { "a" }, new[] { @"a=atb\" })]
	[InlineData(@"a\ b c" + "\n", new[] { "-r", "a", "b" }, new[] { @"a=a\", "b=b c" })]
	[InlineData(@"a\" + "\nb c\n", new[] { "a", "b" }, new[] { "a=ab", "b=c" })]
	[InlineData(@"a\" + "\nb c\n", new[] { "-r", "a", "b" }, new[] { @"a=a\", "b=" })]
	[InlineData(@"x \y" + "\n", new string[0], new[] { "REPLY=x y" })]
	[InlineData("x\n", new[] { "--", "a" }, new[] { "a=x" })]
	[InlineData("x\n", new[] { "-rs", "-pPrompt", "a" }, new[] { "a=x" })]
	[InlineData("x\n", new[] { "-p", "Prompt", "a" }, new[] { "a=x" })]
	public void Run_WithPipedLine_SplitsIntoNames(string input, string[] args, string[] expected)
	{
		// Arrange
		SetInput(input);

		// Act
		var status = this.Sut(terminal: false).Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.Values(expected).Should().Equal(expected);
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a set <c>IFS</c> splits on its own characters: spaces run together, any other character ends a field
	/// on its own, one separator after the last field is dropped, and an empty <c>IFS</c> doesn't split or trim at all.
	/// </summary>
	/// <param name="ifs">The value of <c>IFS</c>.</param>
	/// <param name="input">What standard input holds.</param>
	/// <param name="expected">Each name and its value, as <c>name=value</c>.</param>
	[Theory]
	[InlineData(":", "a:b:\n", new[] { "a=a", "b=b" })]
	[InlineData(":", "a:b:c:\n", new[] { "a=a", "b=b:c:" })]
	[InlineData(":", "a::b\n", new[] { "a=a", "b=", "c=b" })]
	[InlineData(":", ":a\n", new[] { "a=", "b=a" })]
	[InlineData(":", @"a\:b:c" + "\n", new[] { "a=a:b", "b=c" })]
	[InlineData(":", " a :b\n", new[] { "a= a ", "b=b" })]
	[InlineData(" :", "a : b\n", new[] { "a=a", "b=b", "c=" })]
	[InlineData(" :", "a :: b\n", new[] { "a=a", "b=", "c=b" })]
	[InlineData(" :", "a  :  b\n", new[] { "a=a", "b=b", "c=" })]
	[InlineData(" :", "a : \n", new[] { "a=a" })]
	[InlineData("", " a b \n", new[] { "a= a b ", "b=" })]
	public void Run_WithIfs_SplitsOnIt(string ifs, string input, string[] expected)
	{
		// Arrange
		_ = this.declare.Run([$"IFS={ifs}"]);
		SetInput(input);

		// Act
		var status = this.Sut(terminal: false).Run([.. expected.Select(pair => pair.Split('=')[0])]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.Values(expected).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that an <c>IFS</c> in the environment is used when there's no shell variable of that name.
	/// </summary>
	[Fact]
	public void Run_WithIfsInEnvironment_SplitsOnIt()
	{
		// Arrange
		SetInput("a:b c\n");
		Environment.SetEnvironmentVariable("IFS", ":");

		try
		{
			// Act
			_ = this.Sut(terminal: false).Run(["x", "y"]);
		}
		finally
		{
			Environment.SetEnvironmentVariable("IFS", null);
		}

		// Assert
		_ = this.declare.Variables["x"].Should().Be("a");
		_ = this.declare.Variables["y"].Should().Be("b c");
	}

	/// <summary>
	/// Checks that input ending before a newline gives 1 but still fills the names, as bash does.
	/// </summary>
	/// <param name="input">What standard input holds.</param>
	/// <param name="expected">The value of <c>a</c>.</param>
	[Theory]
	[InlineData("", "")]
	[InlineData("x y", "x y")]
	[InlineData(@"x\", "x")]
	[InlineData(@"x\" + "\ny", "xy")]
	public void Run_WithInputEndingEarly_GivesOneAndKeepsText(string input, string expected)
	{
		// Arrange
		_ = this.declare.Run(["a=old"]);
		SetInput(input);

		// Act
		var status = this.Sut(terminal: false).Run(["a"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.declare.Variables["a"].Should().Be(expected);
	}

	/// <summary>
	/// Checks that only one line is read, so the next one is left for whatever reads after.
	/// </summary>
	[Fact]
	public void Run_WithTwoLines_LeavesSecond()
	{
		// Arrange
		SetInput("one\ntwo\n");

		// Act
		_ = this.Sut(terminal: false).Run([]);

		// Assert
		_ = this.declare.Variables["REPLY"].Should().Be("one");
		_ = Console.In.ReadToEnd().Should().Be("two\n");
	}

	/// <summary>
	/// Checks that a bad option is reported with status 2 and a bad name with status 1, before any input is read.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="code">The error code.</param>
	/// <param name="message">The message.</param>
	/// <param name="help">The hint, or <see langword="null" /> for none.</param>
	/// <param name="expected">The status.</param>
	[Theory]
	[InlineData(new[] { "-z", "a" }, "sharpie::read::invalid_option", "`-z` isn't an option of `read`", "read takes -r, -s and -p prompt", 2)]
	[InlineData(new[] { "-rx" }, "sharpie::read::invalid_option", "`-x` isn't an option of `read`", "read takes -r, -s and -p prompt", 2)]
	[InlineData(new[] { "-p" }, "sharpie::read::missing_prompt", "`-p` needs the prompt text after it", null, 2)]
	[InlineData(new[] { "a", "1b" }, "sharpie::read::invalid_identifier", "`1b` isn't a valid variable name", DeclareCommand.IdentifierHelp, 1)]
	[InlineData(new[] { "--", "-a" }, "sharpie::read::invalid_identifier", "`-a` isn't a valid variable name", DeclareCommand.IdentifierHelp, 1)]
	public void Run_WithBadArguments_ReportsThemWithoutReading(string[] args, string code, string message, string? help, int expected)
	{
		// Arrange
		SetInput("x\n");

		// Act
		var status = this.Sut(terminal: false).Run(args);

		// Assert
		_ = status.Should().Be(expected);
		_ = this.environment.Error.Should().Be(new ShellError(code, message) { Help = help }.Render(color: false));
		_ = Console.In.ReadToEnd().Should().Be("x\n");
		_ = this.declare.Variables.Should().NotContainKey("a");
	}

	/// <summary>
	/// Checks that typed keys fill the name and are echoed to standard error after the prompt, with Backspace
	/// erasing, Tab kept, a plain D on an empty line taken as text, and other control keys ignored.
	/// </summary>
	/// <param name="args">The arguments, which give the prompt as its own word or joined to <c>-p</c>.</param>
	/// <param name="terminal">
	/// Always <see langword="true" />. It's there because an array as the only argument doesn't compile.
	/// </param>
	[Theory]
	[InlineData(new[] { "-p", "Name: " }, true)]
	[InlineData(new[] { "-rpName: " }, true)]
	public void Run_OnTerminal_EchoesTypingAfterPrompt(string[] args, bool terminal)
	{
		// Arrange
		this.Type("db");
		this.Press(ConsoleKey.Backspace);
		this.Type("c\t");
		this.Press(ConsoleKey.A, control: true);
		this.Press(ConsoleKey.D, control: true);
		this.Press(ConsoleKey.Enter);

		// Act
		var status = this.Sut(terminal).Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.declare.Variables["REPLY"].Should().Be("dc\t");
		_ = this.environment.Error.Should().Be($"Name: db\b \bc\t{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that Backspace on an empty line does nothing.
	/// </summary>
	[Fact]
	public void Run_OnTerminalBackspaceAtStart_ErasesNothing()
	{
		// Arrange
		this.Press(ConsoleKey.Backspace);
		this.Type("x");
		this.Press(ConsoleKey.Enter);

		// Act
		_ = this.Sut(terminal: true).Run(["a"]);

		// Assert
		_ = this.declare.Variables["a"].Should().Be("x");
		_ = this.environment.Error.Should().Be($"x{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <c>-s</c> shows nothing on a terminal, not even the newline.
	/// </summary>
	[Fact]
	public void Run_OnTerminalSilent_EchoesNothing()
	{
		// Arrange
		this.Type("ab");
		this.Press(ConsoleKey.Backspace);
		this.Press(ConsoleKey.Enter);

		// Act
		var status = this.Sut(terminal: true).Run(["-s", "a"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.declare.Variables["a"].Should().Be("a");
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a backslash at the end of a typed line joins the next typed line on.
	/// </summary>
	[Fact]
	public void Run_OnTerminalTrailingBackslash_ReadsNextLine()
	{
		// Arrange
		this.Type(@"a\");
		this.Press(ConsoleKey.Enter);
		this.Type("b c");
		this.Press(ConsoleKey.Enter);

		// Act
		var status = this.Sut(terminal: true).Run(["x", "y"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.declare.Variables["x"].Should().Be("ab");
		_ = this.declare.Variables["y"].Should().Be("c");
	}

	/// <summary>
	/// Checks that Ctrl+D on an empty line ends the input with status 1.
	/// </summary>
	[Fact]
	public void Run_OnTerminalControlDOnEmptyLine_GivesOne()
	{
		// Arrange
		this.Press(ConsoleKey.D, control: true);

		// Act
		var status = this.Sut(terminal: true).Run(["a"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.declare.Variables["a"].Should().BeEmpty();
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that Ctrl+C gives 130, moves to a new line and sets nothing.
	/// </summary>
	[Fact]
	public void Run_OnTerminalControlC_GivesInterruptedStatus()
	{
		// Arrange
		this.Type("ab");
		this.Press(ConsoleKey.C, control: true);

		// Act
		var status = this.Sut(terminal: true).Run(["a"]);

		// Assert
		_ = status.Should().Be(ReadCommand.InterruptedStatus);
		_ = this.declare.Variables.Should().NotContainKey("a");
		_ = this.environment.Error.Should().Be($"ab{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that the prompt isn't written when standard input isn't a terminal, as in bash.
	/// </summary>
	[Fact]
	public void Run_WithPromptOnPipe_WritesNoPrompt()
	{
		// Arrange
		SetInput("x\n");

		// Act
		_ = this.Sut(terminal: false).Run(["-p", "Name: "]);

		// Assert
		_ = this.environment.Error.Should().BeEmpty();
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose()
	{
		Console.SetIn(this.originalIn);
		this.environment.Dispose();
	}

	/// <summary>
	/// Creates the command under test, reading keys from <see cref="keys" /> on a terminal.
	/// </summary>
	/// <param name="terminal"><see langword="true" /> to act as if standard input is a terminal.</param>
	/// <returns>
	/// The command.
	/// </returns>
	private ReadCommand Sut(bool terminal) => new(this.declare, terminal ? this.keys.Dequeue : null);

	/// <summary>
	/// Points <see cref="Console.In" /> at <paramref name="input" />.
	/// </summary>
	/// <param name="input">What standard input holds.</param>
	private static void SetInput(string input) => Console.SetIn(new StringReader(input));

	/// <summary>
	/// Reads back the variables named in <paramref name="expected" /> in the same <c>name=value</c> form.
	/// </summary>
	/// <param name="expected">The expected pairs, which give the names.</param>
	/// <returns>
	/// Each name with its stored value, or with <c>(unset)</c> when it isn't set.
	/// </returns>
	private string[] Values(string[] expected) =>
		[.. expected.Select(pair => pair.Split('=')[0]).Select(name => $"{name}={this.declare.Variables.GetValueOrDefault(name, "(unset)")}")];

	/// <summary>
	/// Queues one key press per character of <paramref name="text" />.
	/// </summary>
	/// <param name="text">The characters to type.</param>
	private void Type(string text)
	{
		foreach (var character in text)
		{
			this.keys.Enqueue(new ConsoleKeyInfo(character, character == '\t' ? ConsoleKey.Tab : (ConsoleKey)char.ToUpperInvariant(character), shift: false, alt: false, control: false));
		}
	}

	/// <summary>
	/// Queues a press of <paramref name="key" />.
	/// </summary>
	/// <param name="key">The key.</param>
	/// <param name="control"><see langword="true" /> to hold Ctrl.</param>
	private void Press(ConsoleKey key, bool control = false) =>
		this.keys.Enqueue(new ConsoleKeyInfo(control ? (char)(key - ConsoleKey.A + 1) : key == ConsoleKey.Enter ? '\r' : key == ConsoleKey.Backspace ? '\b' : '\0', key, shift: false, alt: false, control));
}
