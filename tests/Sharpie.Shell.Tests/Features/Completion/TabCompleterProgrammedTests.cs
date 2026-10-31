using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Completion;

/// <summary>
/// Tests for how <see cref="TabCompleter" /> inserts what a completion function gives, under each <c>-o</c> option,
/// as readline does in bash. The function is a stand-in that sets <c>COMPREPLY</c>.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class TabCompleterProgrammedTests : IDisposable
{
	private readonly ShellEnvironment environment = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="TabCompleterProgrammedTests" /> class, in a folder holding the
	/// folder <c>sub</c> and the files <c>my file</c> and <c>sampledata.txt</c>.
	/// </summary>
	public TabCompleterProgrammedTests()
	{
		var folder = this.environment.CreateFolder();

		_ = Directory.CreateDirectory(Path.Combine(folder, "sub"));
		_ = ShellEnvironment.CreateFile(folder, "my file", executable: false);
		_ = ShellEnvironment.CreateFile(folder, "sampledata.txt", executable: false);
		Directory.SetCurrentDirectory(folder);
	}

	/// <summary>
	/// Checks what one Tab makes of the function's words under each option.
	/// </summary>
	/// <param name="line">The line, with the cursor at its end.</param>
	/// <param name="replies">The words the function gives.</param>
	/// <param name="options">The spec's options, joined by <c>,</c>.</param>
	/// <param name="expected">The line after Tab.</param>
	[Theory]
	[InlineData("sampletool al", new[] { "alpha" }, "", "sampletool alpha ")]
	[InlineData("sampletool al", new[] { "alpha" }, "nospace", "sampletool alpha")]
	[InlineData("sampletool ch", new[] { "checkout " }, "nospace", "sampletool checkout ")]
	[InlineData("sampletool ch", new[] { "checkout", "cherry" }, "", "sampletool che")]
	[InlineData("sampletool ch", new[] { "checkout", "checkout" }, "", "sampletool checkout ")]
	[InlineData("sampletool s", new[] { "sub" }, "filenames", "sampletool sub/")]
	[InlineData("sampletool m", new[] { "my file" }, "filenames", @"sampletool my\ file ")]
	[InlineData("sampletool m", new[] { "my file" }, "filenames,noquote", "sampletool my file ")]
	[InlineData("sampletool \"m", new[] { "my file" }, "filenames", "sampletool \"my file\" ")]
	[InlineData("sampletool m", new[] { "my file" }, "", "sampletool my file ")]
	[InlineData("sampletool \"a", new[] { "alpha" }, "", "sampletool \"alpha\" ")]
	[InlineData("sampletool 'a", new[] { "alpha" }, "nospace", "sampletool 'alpha'")]
	[InlineData("sampletool \"s", new[] { "sub" }, "filenames", "sampletool \"sub/")]
	[InlineData("sampletool #", new[] { "#tag" }, "filenames", @"sampletool \#tag ")]
	[InlineData("sampletool ", new[] { "alpha" }, "dirnames", "sampletool alpha ")]
	[InlineData("sampletool su", new string[0], "dirnames", "sampletool sub/")]
	[InlineData("sampletool sampled", new string[0], "default", "sampletool sampledata.txt ")]
	[InlineData("sampletool sampled", new string[0], "bashdefault", "sampletool sampledata.txt ")]
	[InlineData("ls | sampletool al", new[] { "alpha" }, "", "ls | sampletool alpha ")]
	public void Press_WithFunctionWords_InsertsAsReadline(string line, string[] replies, string options, string expected)
	{
		// Arrange
		var sut = Completer(replies, 0, options.Split(',', StringSplitOptions.RemoveEmptyEntries));

		// Act
		var result = sut.Press(line, line.Length);

		// Assert
		_ = result.Line.Should().Be(expected);
		_ = result.Cursor.Should().Be(expected.Length);
		_ = result.Bell.Should().BeFalse();
	}

	/// <summary>
	/// Checks that no words ring the bell, unless the function failed, as one needing bash-completion's helpers does,
	/// when Tab falls back to the usual completion.
	/// </summary>
	/// <param name="status">The function's status.</param>
	/// <param name="expected">The line after Tab.</param>
	/// <param name="bell">Whether the bell rings.</param>
	[Theory]
	[InlineData(0, "sampletool sampled", true)]
	[InlineData(127, "sampletool sampledata.txt ", false)]
	public void Press_WithNoWords_RingsOrFallsBack(int status, string expected, bool bell)
	{
		// Arrange
		var sut = Completer([], status);

		// Act
		var result = sut.Press("sampletool sampled", 18);

		// Assert
		_ = result.Line.Should().Be(expected);
		_ = result.Bell.Should().Be(bell);
	}

	/// <summary>
	/// Checks that the second Tab lists the words sorted and each once, or in the function's order with
	/// <c>nosort</c>, and that <c>plusdirs</c> adds the folders.
	/// </summary>
	/// <param name="options">The spec's options, joined by <c>,</c>.</param>
	/// <param name="expected">The listed words.</param>
	[Theory]
	[InlineData("", new[] { "alpha", "zeta" })]
	[InlineData("nosort", new[] { "zeta", "alpha" })]
	[InlineData("plusdirs", new[] { "alpha", "sub/", "zeta" })]
	public void Press_Twice_ListsWords(string options, string[] expected)
	{
		// Arrange
		var sut = Completer(["zeta", "alpha", "zeta"], 0, options.Split(',', StringSplitOptions.RemoveEmptyEntries));

		// Act
		var bell = sut.Press("sampletool ", 11);
		var listed = sut.Press("sampletool ", 11);

		// Assert
		_ = bell.Bell.Should().BeTrue();
		_ = listed.Listed.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that the Tab that lists tells the function so, with <c>COMP_TYPE</c> 63 after 9, as bash does.
	/// </summary>
	[Fact]
	public void Press_Twice_TellsFunctionItLists()
	{
		// Arrange
		var declare = new DeclareCommand(new ShellSettings());
		var types = new List<string>();
		var engine = new CompletionEngine(declare, new Dictionary<string, ICommand>(), new Dictionary<string, string>(), _ =>
		{
			types.Add(declare.Variables["COMP_TYPE"]);

			return 0;
		});

		engine.Set("sampletool", new CompletionSpec { Function = "_sampletool" });

		var sut = new TabCompleter(["echo"], engine, declare.Variables);

		// Act
		var first = sut.Press("sampletool ", 11);
		var second = sut.Press("sampletool ", 11);

		// Assert
		_ = types.Should().Equal("9", "63");
		_ = (first.Bell, second.Bell).Should().Be((true, true));
	}

	/// <summary>
	/// Puts the console and folder back after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Creates a completer whose spec for <c>sampletool</c> runs a function that gives <paramref name="replies" />.
	/// </summary>
	/// <param name="replies">The words the function puts in <c>COMPREPLY</c>.</param>
	/// <param name="status">The function's status.</param>
	/// <param name="options">The spec's options.</param>
	/// <returns>
	/// The completer.
	/// </returns>
	private static TabCompleter Completer(string[] replies, int status, params string[] options)
	{
		var declare = new DeclareCommand(new ShellSettings());
		var engine = new CompletionEngine(declare, new Dictionary<string, ICommand>(), new Dictionary<string, string>(), _ =>
		{
			DeclareCommand.AssignArray(declare.Variables, "COMPREPLY", replies);

			return status;
		});

		engine.Set("sampletool", new CompletionSpec { Function = "_sampletool", Options = new HashSet<string>(options) });

		return new TabCompleter(["echo"], engine, declare.Variables);
	}
}
