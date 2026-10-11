using FluentAssertions;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Shell.Tests.Features.DotNetDocs;

/// <summary>
/// Tests for <see cref="CodeHighlighter" />.
/// </summary>
public sealed class CodeHighlighterTests
{
	/// <summary>
	/// Checks that a C# line gets its keyword, variable, string, number and comment colored.
	/// </summary>
	[Fact]
	public void Highlight_CSharpLine_ColorsEachKind()
	{
		// Act
		var result = CodeHighlighter.Highlight(["var s = \"a\" + 42; // note"], DocLanguage.CSharp);

		// Assert
		_ = result.Should().ContainSingle().Which.Should().Equal(
			new Span("var", SpanStyle.Keyword),
			new Span(" ", SpanStyle.Plain),
			new Span("s", SpanStyle.Variable),
			new Span(" = ", SpanStyle.Plain),
			new Span("\"a\"", SpanStyle.String),
			new Span(" + ", SpanStyle.Plain),
			new Span("42", SpanStyle.Number),
			new Span("; ", SpanStyle.Plain),
			new Span("// note", SpanStyle.Comment));
	}

	/// <summary>
	/// Checks that the compiler tells a type from a variable in calls that look alike, without the snippet importing
	/// <c>System</c> itself.
	/// </summary>
	[Fact]
	public void Highlight_CSharpCalls_ColorsTypeMethodAndVariable()
	{
		// Act
		var result = CodeHighlighter.Highlight(["var text = \"x\";", "Console.WriteLine(text.Trim());"], DocLanguage.CSharp);

		// Assert
		_ = result[1].Should().Equal(
			new Span("Console", SpanStyle.Type),
			new Span(".", SpanStyle.Plain),
			new Span("WriteLine", SpanStyle.Method),
			new Span("(", SpanStyle.Plain),
			new Span("text", SpanStyle.Variable),
			new Span(".", SpanStyle.Plain),
			new Span("Trim", SpanStyle.Method),
			new Span("());", SpanStyle.Plain));
	}

	/// <summary>
	/// Checks that a declared class, method and parameter are colored where they're declared and where they're used.
	/// </summary>
	[Fact]
	public void Highlight_CSharpDeclarations_ColorsClassMethodAndParameter()
	{
		// Act
		var result = CodeHighlighter.Highlight(["class Doubler", "{", "    int Twice(int value) => value * 2;", "}"], DocLanguage.CSharp);

		// Assert
		_ = result[0].Should().Contain(new Span("Doubler", SpanStyle.Type));
		_ = result[2].Should().Contain(new Span("Twice", SpanStyle.Method))
			.And.Subject.Count(span => span == new Span("value", SpanStyle.Parameter)).Should().Be(2);
	}

	/// <summary>
	/// Checks that names the compiler can't find still get a color from where they stand.
	/// </summary>
	[Fact]
	public void Highlight_UnknownNames_FallBackOnPosition()
	{
		// Act
		var result = CodeHighlighter.Highlight(["var widget = new Gadget();", "Frobnicate(widget);"], DocLanguage.CSharp);

		// Assert
		_ = result[0].Should().Contain(new Span("Gadget", SpanStyle.Type));
		_ = result[1].Should().Contain(new Span("Frobnicate", SpanStyle.Method));
	}

	/// <summary>
	/// Checks that a block comment and a verbatim string keep their color across lines.
	/// </summary>
	[Fact]
	public void Highlight_CSharpMultilineTokens_SpanLines()
	{
		// Act
		var result = CodeHighlighter.Highlight(["/* one", "two */ var x = @\"a", "\"\"b\";"], DocLanguage.CSharp);

		// Assert
		_ = result[0].Should().Equal(new Span("/* one", SpanStyle.Comment));
		_ = result[1][0].Should().Be(new Span("two */", SpanStyle.Comment));
		_ = result[1][^1].Should().Be(new Span("@\"a", SpanStyle.String));
		_ = result[2][0].Should().Be(new Span("\"\"b\"", SpanStyle.String));
	}

	/// <summary>
	/// Checks that Visual Basic statements outside any method are still read, as if inside one, so keywords, types,
	/// variables and comments get their colors.
	/// </summary>
	[Fact]
	public void Highlight_VisualBasicStatements_ColorsEachKind()
	{
		// Act
		var result = CodeHighlighter.Highlight(["Dim s As String = \"it\"\"s\" ' note", "Console.WriteLine(s)"], DocLanguage.VisualBasic);

		// Assert
		_ = result[0].Should().Equal(
			new Span("Dim", SpanStyle.Keyword),
			new Span(" ", SpanStyle.Plain),
			new Span("s", SpanStyle.Variable),
			new Span(" ", SpanStyle.Plain),
			new Span("As", SpanStyle.Keyword),
			new Span(" ", SpanStyle.Plain),
			new Span("String", SpanStyle.Keyword),
			new Span(" = ", SpanStyle.Plain),
			new Span("\"it\"\"s\"", SpanStyle.String),
			new Span(" ", SpanStyle.Plain),
			new Span("' note", SpanStyle.Comment));
		_ = result[1].Should().Contain(new Span("Console", SpanStyle.Type))
			.And.Contain(new Span("WriteLine", SpanStyle.Method))
			.And.Contain(new Span("s", SpanStyle.Variable));
	}

	/// <summary>
	/// Checks that code in another language, or none, stays plain.
	/// </summary>
	[Fact]
	public void Highlight_NoLanguage_LeavesPlain()
	{
		// Act
		var result = CodeHighlighter.Highlight(["{ \"if\": 1 }"], null);

		// Assert
		_ = result.Should().ContainSingle().Which.Should().Equal(new Span("{ \"if\": 1 }", SpanStyle.Plain));
	}

	/// <summary>
	/// Checks that a styled page writes code colors as standard color codes, and a plain page leaves them out.
	/// </summary>
	/// <param name="styled"><see langword="true" /> for a styled page.</param>
	/// <param name="expected">The line written.</param>
	[Theory]
	[InlineData(true, "    \e[35mvar\e[39m \e[36mx\e[39m = \e[91m1\e[39m;\n")]
	[InlineData(false, "    var x = 1;\n")]
	public void Verbatim_HighlightedCode_WritesColorCodesWhenStyled(bool styled, string expected)
	{
		// Arrange
		var writer = new ManPageWriter(80, styled);

		// Act
		writer.Verbatim(CodeHighlighter.Highlight(["var x = 1;"], DocLanguage.CSharp), 4);

		// Assert
		_ = writer.ToString().Should().Be(expected);
	}
}
