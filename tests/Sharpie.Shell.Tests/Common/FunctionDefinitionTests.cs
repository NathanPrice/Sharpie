using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="FunctionDefinition" />.
/// </summary>
public sealed class FunctionDefinitionTests
{
	/// <summary>
	/// Checks that every header form bash takes gives the name, the body between the braces and what follows the last
	/// <c>}</c> word.
	/// </summary>
	/// <param name="command">One whole definition.</param>
	/// <param name="name">The expected name.</param>
	/// <param name="body">The expected body.</param>
	/// <param name="after">The expected text after the body.</param>
	[Theory]
	[InlineData("samplefn() { a; b; }", "samplefn", " a; b; ", "")]
	[InlineData("samplefn() { a\nb\n}", "samplefn", " a\nb\n", "")]
	[InlineData("samplefn () { a; }", "samplefn", " a; ", "")]
	[InlineData("samplefn( ) { a; }", "samplefn", " a; ", "")]
	[InlineData("samplefn(){ a; }", "samplefn", " a; ", "")]
	[InlineData("function samplefn { a; }", "samplefn", " a; ", "")]
	[InlineData("function samplefn() { a; }", "samplefn", " a; ", "")]
	[InlineData("function samplefn () { a; }", "samplefn", " a; ", "")]
	[InlineData("samplefn()\n{\na\n}", "samplefn", "\na\n", "")]
	[InlineData("function samplefn\n{ a; }", "samplefn", " a; ", "")]
	[InlineData("sample-fn.x() { a; }", "sample-fn.x", " a; ", "")]
	[InlineData("samplefn() {\techo }\n}", "samplefn", "\techo }\n", "")]
	[InlineData("samplefn() { a\n} > out}", "samplefn", " a\n", " > out}")]
	[InlineData("samplefn() { a;}", "samplefn", " a;", "")]
	[InlineData("samplefn() {\n}", "samplefn", "\n", "")]
	public void Parse_WithDefinition_GivesNameBodyAndAfter(string command, string name, string body, string after)
	{
		// Act
		var definition = FunctionDefinition.Parse(command);

		// Assert
		_ = definition.Should().Be((name, body, after));
	}

	/// <summary>
	/// Checks that a command that isn't a whole definition gives <see langword="null" />: no header, a <c>{</c> that
	/// isn't a word of its own, no <c>{</c>, a name with a character the shell gives a meaning, or no <c>}</c> word.
	/// </summary>
	/// <param name="command">The command.</param>
	[Theory]
	[InlineData("echo samplefn() { a; }")]
	[InlineData("samplefn(){x; }")]
	[InlineData("samplefn()")]
	[InlineData("function samplefn")]
	[InlineData("function{ a; }")]
	[InlineData("a=(1) { b; }")]
	[InlineData("$samplefn() { a; }")]
	[InlineData("sample'fn'() { a; }")]
	[InlineData("samplefn() { a")]
	[InlineData("samplefn() { a}")]
	[InlineData("samplefn() {}")]
	[InlineData("(samplefn) { a; }")]
	[InlineData("samplefn( { a; }")]
	[InlineData("ls -la")]
	public void Parse_WithoutWholeDefinition_GivesNull(string command)
	{
		// Act
		var definition = FunctionDefinition.Parse(command);

		// Assert
		_ = definition.Should().BeNull();
	}

	/// <summary>
	/// Checks that a header with its <c>{</c>, or alone with the <c>{</c> still to come, starts a definition, and
	/// nothing else does.
	/// </summary>
	/// <param name="command">One command.</param>
	/// <param name="expected">Whether it starts a definition.</param>
	[Theory]
	[InlineData("samplefn() { a", true)]
	[InlineData("samplefn() {", true)]
	[InlineData("samplefn()", true)]
	[InlineData("samplefn ( )", true)]
	[InlineData("function samplefn", true)]
	[InlineData("function samplefn()", true)]
	[InlineData("samplefn() x", false)]
	[InlineData("samplefn(){x", false)]
	[InlineData("{ a", false)]
	[InlineData("echo samplefn()", false)]
	[InlineData("function", false)]
	public void IsHeader_WithCommand_SaysWhetherItStartsDefinition(string command, bool expected)
	{
		// Act
		var header = FunctionDefinition.IsHeader(command);

		// Assert
		_ = header.Should().Be(expected);
	}

	/// <summary>
	/// Checks that the braces at a command's start are counted, headers included, up to the first other word.
	/// </summary>
	/// <param name="command">One command.</param>
	/// <param name="expected">The count.</param>
	[Theory]
	[InlineData("samplefn() { a", 1)]
	[InlineData("samplefn() {", 1)]
	[InlineData("samplefn() { { a", 2)]
	[InlineData("samplefn() { other() { a", 2)]
	[InlineData("{ a", 1)]
	[InlineData("{ { a } }", 2)]
	[InlineData("}", -1)]
	[InlineData("} }", -2)]
	[InlineData("} > out", -1)]
	[InlineData("  }\t}  ", -2)]
	[InlineData("echo }", 0)]
	[InlineData("echo {", 0)]
	[InlineData("samplefn()", 0)]
	[InlineData("samplefn(){x", 0)]
	[InlineData("'{' a", 0)]
	[InlineData("{a", 0)]
	[InlineData("", 0)]
	public void Braces_WithCommand_CountsLeadingBraces(string command, int expected)
	{
		// Act
		var braces = FunctionDefinition.Braces(command);

		// Assert
		_ = braces.Should().Be(expected);
	}
}
