using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Functions;

/// <summary>
/// Tests for <see cref="FunctionCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class FunctionCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new(new ShellSettings());
	private readonly Dictionary<string, ICommand> commands = [];
	private readonly List<string> ran = [];

	/// <summary>
	/// Checks that a call runs the body with the arguments as <c>$1</c> onwards and <c>$#</c>, gives the body's
	/// status, and puts the caller's parameters back.
	/// </summary>
	[Fact]
	public void Run_WithArguments_RunsBodyWithThemAsPositional()
	{
		// Arrange
		this.declare.SetPositional(["outer"]);

		var sut = new FunctionCommand("samplefn", " a; ", null, this.declare, body =>
		{
			this.ran.Add($"{body}|{string.Join(',', this.declare.Positional)}|{this.declare.Variables["#"]}|{this.declare.FunctionDepth}");

			return 4;
		});

		// Act
		var status = sut.Run(["x", "y z"]);

		// Assert
		_ = status.Should().Be(4);
		_ = this.ran.Should().Equal(" a; |x,y z|2|1");
		_ = this.declare.Positional.Should().Equal("outer");
		_ = this.declare.FunctionDepth.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>return</c> inside the body ends it with its status.
	/// </summary>
	[Fact]
	public void Run_WithReturnInBody_GivesItsStatus()
	{
		// Arrange
		var sut = new FunctionCommand("samplefn", "return 7", null, this.declare, _ => new ReturnCommand(this.declare, () => false).Run(["7"]));

		// Act
		var status = sut.Run([]);

		// Assert
		_ = status.Should().Be(7);
	}

	/// <summary>
	/// Checks that a function that never stops calling itself is stopped at <see cref="FunctionCommand.MaxDepth" />
	/// with one error and status 1, and that every call down to the first ends there, so a body that calls itself twice
	/// doesn't run on.
	/// </summary>
	[Fact]
	public void Run_CallingItselfForever_StopsAtMaxDepth()
	{
		// Arrange
		var calls = 0;
		FunctionCommand? sut = null;

		sut = new FunctionCommand("samplefn", "samplefn; samplefn", null, this.declare, body =>
		{
			calls++;
			_ = sut!.Run([]);

			return sut.Run([]);
		});

		// Act
		var status = sut.Run([]);

		// Assert
		_ = status.Should().Be(1);
		_ = calls.Should().Be(FunctionCommand.MaxDepth);
		_ = this.declare.FunctionDepth.Should().Be(0);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::function::too_deep", $"`samplefn` is nested more than {FunctionCommand.MaxDepth} calls deep")
		{
			Help = "a function that calls itself needs a test that stops it",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that a definition adds the function under its name, keeping the builtin it hides, and that a call runs
	/// the body as typed.
	/// </summary>
	[Fact]
	public void Define_WithDefinition_AddsFunctionOverBuiltin()
	{
		// Arrange
		var echo = new EchoCommand();

		this.commands["echo"] = echo;

		// Act
		this.Define("echo() { a\n}");

		// Assert
		_ = this.commands.Should().ContainKey("echo").WhoseValue.Should().BeOfType<FunctionCommand>().Which.Shadowed.Should().BeSameAs(echo);
		_ = this.commands["echo"].Run(["b"]).Should().Be(0);
		_ = this.ran.Should().Equal("{ a\n}");
	}

	/// <summary>
	/// Checks that defining a function again replaces the body and keeps the builtin the first one hid.
	/// </summary>
	[Fact]
	public void Define_Again_KeepsShadowedBuiltin()
	{
		// Arrange
		var echo = new EchoCommand();

		this.commands["echo"] = echo;
		this.Define("echo() { a; }");

		// Act
		this.Define("function echo { b; }");

		// Assert
		var function = this.commands["echo"].Should().BeOfType<FunctionCommand>().Subject;

		_ = function.Shadowed.Should().BeSameAs(echo);
		_ = function.Name.Should().Be("echo");
		_ = function.Body.Should().Be("{ b; }");
		_ = function.Run([]);
		_ = this.ran.Should().Equal("{ b; }");
	}

	/// <summary>
	/// Checks that a function with no builtin of its name hides none.
	/// </summary>
	[Fact]
	public void Define_WithNewName_HidesNothing()
	{
		// Act
		this.Define("samplefn() { a; }");

		// Assert
		_ = this.commands["samplefn"].Should().BeOfType<FunctionCommand>().Which.Shadowed.Should().BeNull();
	}

	/// <summary>
	/// Checks that a redirect after the body's <c>}</c> stays with the body, so every call applies it, as in bash.
	/// </summary>
	[Fact]
	public void Define_WithRedirectAfterBody_KeepsItWithTheBody()
	{
		// Act
		this.Define("samplefn() { a\n}  > out");
		_ = this.commands["samplefn"].Run([]);

		// Assert
		_ = this.ran.Should().Equal("{ a\n}  > out");
	}

	/// <summary>
	/// Checks that <see cref="FunctionCommand.Remove" /> puts back the builtin a function hid, removes one that hid
	/// none, and leaves a builtin alone.
	/// </summary>
	[Fact]
	public void Remove_WithFunction_PutsBackWhatItHid()
	{
		// Arrange
		var echo = new EchoCommand();

		this.commands["echo"] = echo;
		this.commands["true"] = new StatusCommand("true", 0, "Does nothing and succeeds");
		this.Define("echo() { a; }");
		this.Define("samplefn() { a; }");

		// Act
		FunctionCommand.Remove(this.commands, "echo");
		FunctionCommand.Remove(this.commands, "samplefn");
		FunctionCommand.Remove(this.commands, "true");
		FunctionCommand.Remove(this.commands, "missing");

		// Assert
		_ = this.commands["echo"].Should().BeSameAs(echo);
		_ = this.commands.Should().NotContainKey("samplefn").And.ContainKey("true");
	}

	/// <summary>
	/// Checks the description <c>type</c> and completion show.
	/// </summary>
	[Fact]
	public void Description_IsShellFunction()
	{
		// Arrange
		var sut = new FunctionCommand("samplefn", string.Empty, null, this.declare, this.Record);

		// Act
		var description = sut.Description;

		// Assert
		_ = description.Should().Be("Runs a shell function");
	}

	/// <summary>
	/// Defines the function that <paramref name="text" /> holds, as the shell does once it's parsed.
	/// </summary>
	/// <param name="text">The definition.</param>
	private void Define(string text) =>
		FunctionCommand.Define(this.commands, (FunctionDefinition)CommandParser.Parse(text).List!.Items[0].Command.First.Stages[0], this.declare, this.Record);

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Records a body as it runs.
	/// </summary>
	/// <param name="body">The body.</param>
	/// <returns>
	/// 0.
	/// </returns>
	private int Record(string body)
	{
		this.ran.Add(body);

		return 0;
	}
}
