using FluentAssertions;
using Sharpie.Shell.Features.Arithmetic;

namespace Sharpie.Shell.Tests.Features.Arithmetic;

/// <summary>
/// Tests for <see cref="ArithmeticExpression" />. Each value was checked against bash 5.3.
/// </summary>
public sealed class ArithmeticExpressionTests
{
	/// <summary>
	/// Checks the operators, their precedence, 64-bit wrapping and the number forms.
	/// </summary>
	/// <param name="expression">The expression.</param>
	/// <param name="expected">What bash gives.</param>
	[Theory]
	[InlineData("", 0)]
	[InlineData("  ", 0)]
	[InlineData("1 + 2 * 3", 7)]
	[InlineData("(1 + 2) * 3", 9)]
	[InlineData("10 - 4 - 3", 3)]
	[InlineData("2 ** 3 ** 2", 512)]
	[InlineData("-2**2", 4)]
	[InlineData("2**63", long.MinValue)]
	[InlineData("2**0", 1)]
	[InlineData("0**0", 1)]
	[InlineData("3**3", 27)]
	[InlineData("-9223372036854775807-1", long.MinValue)]
	[InlineData("9223372036854775807 + 1", long.MinValue)]
	[InlineData("(-9223372036854775807-1) / -1", long.MinValue)]
	[InlineData("(-9223372036854775807-1) % -1", 0)]
	[InlineData("99999999999999999999", 7766279631452241919)]
	[InlineData("7 / 2", 3)]
	[InlineData("6 / -1", -6)]
	[InlineData("2 < 2", 0)]
	[InlineData("2 < 1", 0)]
	[InlineData("2 <= 2", 1)]
	[InlineData("2 > 2", 0)]
	[InlineData("1 > 2", 0)]
	[InlineData("2 >= 2", 1)]
	[InlineData("2 == 3", 0)]
	[InlineData("2 != 3", 1)]
	[InlineData("2 != 2", 0)]
	[InlineData("16#a", 10)]
	[InlineData("16#A", 10)]
	[InlineData("5/-2", -2)]
	[InlineData("-5%3", -2)]
	[InlineData("7 % 3", 1)]
	[InlineData("1<<4", 16)]
	[InlineData("1<<64", 1)]
	[InlineData("1<<-1", long.MinValue)]
	[InlineData("-16>>2", -4)]
	[InlineData("6 & 3", 2)]
	[InlineData("6 | 3", 7)]
	[InlineData("6 ^ 3", 5)]
	[InlineData("1 | 2 ^ 3 & 4", 3)]
	[InlineData("1 < 2", 1)]
	[InlineData("2 <= 1", 0)]
	[InlineData("2 > 1", 1)]
	[InlineData("1 >= 2", 0)]
	[InlineData("1 + 1 == 2", 1)]
	[InlineData("1 != 1", 0)]
	[InlineData("1 < 2 == 1", 1)]
	[InlineData("2 && 3", 1)]
	[InlineData("0 || 0", 0)]
	[InlineData("0 || 5", 1)]
	[InlineData("1 || 0 && 0", 1)]
	[InlineData("!5", 0)]
	[InlineData("!0", 1)]
	[InlineData("~5", -6)]
	[InlineData("- -1", 1)]
	[InlineData("--1", 1)]
	[InlineData("++5", 5)]
	[InlineData("+3", 3)]
	[InlineData("1 ? 2 : 3", 2)]
	[InlineData("0 ? 2 : 0 ? 3 : 4", 4)]
	[InlineData("1, 2, 3", 3)]
	[InlineData("010", 8)]
	[InlineData("0x1f", 31)]
	[InlineData("0X1F", 31)]
	[InlineData("0x", 0)]
	[InlineData("0", 0)]
	[InlineData("2#101", 5)]
	[InlineData("16#ff", 255)]
	[InlineData("36#Z", 35)]
	[InlineData("36#z", 35)]
	[InlineData("62#Z", 61)]
	[InlineData("64#@_", 4031)]
	[InlineData("10#08", 8)]
	public void Evaluate_WithExpression_GivesBashValue(string expression, long expected)
	{
		// Act
		var value = ArithmeticExpression.Evaluate(expression, new Dictionary<string, string>());

		// Assert
		_ = value.Should().Be(expected);
	}

	/// <summary>
	/// Checks that names read variables, whose values are expressions in turn, that assignments and <c>++</c> and
	/// <c>--</c> store their result, and that the side <c>&amp;&amp;</c>, <c>||</c> or <c>?:</c> doesn't take
	/// neither assigns nor fails.
	/// </summary>
	/// <param name="expression">The expression.</param>
	/// <param name="expected">What bash gives.</param>
	/// <param name="after">The variables that change, as <c>NAME=value</c> pairs split by spaces, or empty.</param>
	[Theory]
	[InlineData("x", 3, "")]
	[InlineData("sum * 2", 6, "")]
	[InlineData("unset + empty + blank", 0, "")]
	[InlineData("y = 5", 5, "y=5")]
	[InlineData("y = z = 2", 2, "y=2 z=2")]
	[InlineData("x += 4", 7, "x=7")]
	[InlineData("x -= 4", -1, "x=-1")]
	[InlineData("x *= 2", 6, "x=6")]
	[InlineData("x /= 2", 1, "x=1")]
	[InlineData("x %= 2", 1, "x=1")]
	[InlineData("x <<= 2", 12, "x=12")]
	[InlineData("x >>= 1", 1, "x=1")]
	[InlineData("x &= 2", 2, "x=2")]
	[InlineData("x ^= 1", 2, "x=2")]
	[InlineData("x |= 4", 7, "x=7")]
	[InlineData("x++", 3, "x=4")]
	[InlineData("x--", 3, "x=2")]
	[InlineData("++x", 4, "x=4")]
	[InlineData("--x", 2, "x=2")]
	[InlineData("y=3, y++ + ++y", 8, "y=5")]
	[InlineData("y = 2, y * 2", 4, "y=2")]
	[InlineData("0 && 1/0", 0, "")]
	[InlineData("1 || 1/0", 1, "")]
	[InlineData("0 && (y = 1)", 0, "")]
	[InlineData("1 || y++", 1, "")]
	[InlineData("0 && 2**-1", 0, "")]
	[InlineData("0 && bad", 0, "")]
	[InlineData("1 ? y = 7 : (z = 8)", 7, "y=7")]
	[InlineData("0 ? y = 7 : (z = 8)", 8, "z=8")]
	[InlineData("0 ? 1/0 : 2", 2, "")]
	[InlineData("0 && (x /= 0)", 0, "")]
	public void Evaluate_WithVariables_ReadsAndAssignsThem(string expression, long expected, string after)
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["x"] = "3", ["sum"] = "1+2", ["empty"] = string.Empty, ["blank"] = " ", ["bad"] = "1/0" };
		var before = new Dictionary<string, string>(variables);

		// Act
		var value = ArithmeticExpression.Evaluate(expression, variables);

		// Assert
		foreach (var pair in after.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			before[pair.Split('=')[0]] = pair.Split('=')[1];
		}

		_ = value.Should().Be(expected);
		_ = variables.Should().Equal(before);
	}

	/// <summary>
	/// Checks the report for an expression that can't be worked out: its code, and the token it underlines, which is
	/// the one bash names, or the one before the end when the expression stops too early.
	/// </summary>
	/// <param name="expression">The expression.</param>
	/// <param name="code">The last part of the code.</param>
	/// <param name="summary">The summary.</param>
	/// <param name="underlined">The text underlined.</param>
	/// <param name="label">The note under it.</param>
	[Theory]
	[InlineData("1/0", "division_by_zero", "Division by 0", "0", "is 0")]
	[InlineData("1 % (2 - 2)", "division_by_zero", "Division by 0", "(2 - 2)", "is 0")]
	[InlineData("x /= 0", "division_by_zero", "Division by 0", "0", "is 0")]
	[InlineData("08", "bad_number", "Value too great for base 8", "08", "`8` isn't a digit of base 8")]
	[InlineData("37#Z", "bad_number", "Value too great for base 37", "37#Z", "`Z` isn't a digit of base 37")]
	[InlineData("12a", "bad_number", "Value too great for base 10", "12a", "`a` isn't a digit of base 10")]
	[InlineData("2#", "bad_number", "`2#` isn't a number", "2#", "not a number")]
	[InlineData("010#1", "bad_number", "`010#1` isn't a number", "010#1", "not a number")]
	[InlineData("2#1#1", "bad_number", "`2#1#1` isn't a number", "2#1#1", "not a number")]
	[InlineData("65#1", "bad_base", "Invalid arithmetic base", "65#1", "bases go from 2 to 64")]
	[InlineData("1#1", "bad_base", "Invalid arithmetic base", "1#1", "bases go from 2 to 64")]
	[InlineData("2**-1", "negative_exponent", "Exponent less than 0", "-1", "is negative")]
	[InlineData("1 +", "syntax", "Operand expected", "+", "needs a number or a variable after it")]
	[InlineData("1 + ", "syntax", "Operand expected", "+", "needs a number or a variable after it")]
	[InlineData("(", "syntax", "Operand expected", "(", "needs a number or a variable after it")]
	[InlineData("* 2", "syntax", "Operand expected", "*", "expected a number or a variable")]
	[InlineData("$x", "syntax", "Operand expected", "$", "expected a number or a variable")]
	[InlineData("a b", "syntax", "Unexpected `b`", "b", "expected an operator or the end")]
	[InlineData("1.5", "syntax", "Unexpected `.`", ".", "expected an operator or the end")]
	[InlineData("5++", "syntax", "Unexpected `++`", "++", "expected an operator or the end")]
	[InlineData("(1 + 2", "syntax", "`(` is never closed", "(", "needs a `)`")]
	[InlineData("1 ? 2", "syntax", "`:` expected for conditional expression", "2", "expected `:`")]
	[InlineData("1 ? 2 3", "syntax", "`:` expected for conditional expression", "3", "expected `:`")]
	[InlineData("3 = 4", "not_a_variable", "Attempted assignment to non-variable", "=", "the left side isn't a variable name")]
	[InlineData("1 ? 2 : x = 5", "not_a_variable", "Attempted assignment to non-variable", "=", "the left side isn't a variable name")]
	[InlineData("(x) += 1", "not_a_variable", "Attempted assignment to non-variable", "+=", "the left side isn't a variable name")]
	public void Evaluate_WithBadExpression_PointsAtTheToken(string expression, string code, string summary, string underlined, string label)
	{
		// Act
		var act = () => ArithmeticExpression.Evaluate(expression, new Dictionary<string, string> { ["x"] = "1" });

		// Assert
		var error = act.Should().Throw<ArithmeticExpression.Failure>().Which.Error;

		_ = (error.Code, error.Summary, error.Source, error.Label).Should().Be(($"sharpie::arithmetic::{code}", summary, expression, label));
		_ = expression[error.Start..error.End].Should().Be(underlined);
	}

	/// <summary>
	/// Checks that a failure inside a variable's value points into the value, and that a variable that refers back to
	/// itself stops at bash's depth.
	/// </summary>
	[Fact]
	public void Evaluate_WithBadValue_ReportsInsideIt()
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["half"] = "1 +", ["loop"] = "loop" };

		// Act
		var half = () => ArithmeticExpression.Evaluate("half * 2", variables);
		var loop = () => ArithmeticExpression.Evaluate("loop", variables);

		// Assert
		_ = half.Should().Throw<ArithmeticExpression.Failure>().Which.Error.Source.Should().Be("1 +");

		var error = loop.Should().Throw<ArithmeticExpression.Failure>().Which.Error;

		_ = (error.Code, error.Summary, error.Label, error.Start, error.End).Should().Be(("sharpie::arithmetic::recursion", "Expression recursion level exceeded", "refers back to itself", 0, 4));
	}

	/// <summary>
	/// Checks that the hint for a digit too great for octal suggests base 10, and that other bases get none.
	/// </summary>
	/// <param name="expression">The number.</param>
	/// <param name="help">The hint, or <see langword="null" />.</param>
	[Theory]
	[InlineData("09", "a leading 0 makes a number octal; write 10#09 for decimal")]
	[InlineData("2#3", null)]
	[InlineData("8#9", null)]
	[InlineData("0x1g", null)]
	public void Evaluate_WithDigitTooGreat_SuggestsBaseTenOnlyForOctal(string expression, string? help)
	{
		// Act
		var act = () => ArithmeticExpression.Evaluate(expression, new Dictionary<string, string>());

		// Assert
		_ = act.Should().Throw<ArithmeticExpression.Failure>().Which.Error.Help.Should().Be(help);
	}
}
