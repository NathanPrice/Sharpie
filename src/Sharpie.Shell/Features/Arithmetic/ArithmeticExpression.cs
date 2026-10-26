using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Arithmetic;

/// <summary>
/// Works out an integer expression as bash's <c>expr.c</c> does, for <c>$((...))</c>, <c>((...))</c>,
/// <c>for ((;;))</c>, <c>${X:offset:length}</c> and the number tests of <c>[[</c>.
/// </summary>
/// <remarks>
/// Values are 64-bit and wrap on overflow. The operators and their precedence are bash's, lowest first:
/// <c>,</c>, the assignments, <c>?:</c>, <c>||</c>, <c>&amp;&amp;</c>, <c>|</c>, <c>^</c>, <c>&amp;</c>,
/// <c>== !=</c>, <c>&lt; &lt;= &gt; &gt;=</c>, <c>&lt;&lt; &gt;&gt;</c>, <c>+ -</c>, <c>* / %</c>, <c>**</c>, then the
/// unary operators and <c>++</c> and <c>--</c>. A variable's value is itself an expression, and an empty or unset one is
/// 0. The side of <c>&amp;&amp;</c>, <c>||</c> or <c>?:</c> that isn't taken neither assigns nor fails.
/// </remarks>
internal static class ArithmeticExpression
{
	/// <summary>
	/// How deep variables may refer to other variables, as in bash.
	/// </summary>
	private const int MaxDepth = 1024;

	/// <summary>
	/// The operators, longest first so each match is the longest one.
	/// </summary>
	private static readonly string[] Operators =
		["<<=", ">>=", "**", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "++", "--", "*=", "/=", "%=", "+=", "-=", "&=", "^=", "|=", "+", "-", "*", "/", "%", "<", ">", "=", "!", "~", "&", "|", "^", "?", ":", ",", "(", ")"];

	/// <summary>
	/// The assignment operators.
	/// </summary>
	private static readonly string[] Assignments = ["=", "*=", "/=", "%=", "+=", "-=", "<<=", ">>=", "&=", "^=", "|="];

	/// <summary>
	/// The left-associative binary operators, one row per precedence level, lowest first.
	/// </summary>
	private static readonly string[][] Levels = [["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<", "<=", ">", ">="], ["<<", ">>"], ["+", "-"], ["*", "/", "%"]];

	/// <summary>
	/// The kinds of token in an expression.
	/// </summary>
	private enum Kind
	{
		/// <summary>Digits, letters, <c>@</c>, <c>_</c> and <c>#</c>, starting with a digit.</summary>
		Number,

		/// <summary>A variable name.</summary>
		Name,

		/// <summary>One of <see cref="Operators" />.</summary>
		Operator,

		/// <summary>The end of the expression.</summary>
		End,

		/// <summary>A character that is none of the above.</summary>
		Other,
	}

	/// <summary>
	/// Works out <paramref name="expression" />.
	/// </summary>
	/// <param name="expression">The expression, already expanded.</param>
	/// <param name="variables">
	/// The shell variables, which names read and assignments write through <see cref="DeclareCommand.Assign" />.
	/// </param>
	/// <param name="noUnset">
	/// <see langword="true" /> under <c>set -u</c>, where reading a name that isn't set is an error, as in bash.
	/// </param>
	/// <returns>
	/// The value, 0 for an expression of only blanks.
	/// </returns>
	/// <exception cref="Failure">The expression can't be worked out.</exception>
	/// <exception cref="CommandLine.ExpansionError">
	/// <paramref name="noUnset" /> is <see langword="true" /> and the expression reads a name that isn't set.
	/// </exception>
	public static long Evaluate(string expression, IReadOnlyDictionary<string, string> variables, bool noUnset = false) =>
		new Parser(expression.ArgumentNotNull(), variables.ArgumentNotNull(), 0, noUnset).Run();

	/// <summary>
	/// An expression that can't be worked out, like <c>1/0</c>.
	/// </summary>
	/// <param name="error">What went wrong, pointing into the expression.</param>
	[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only the evaluator throws it, and always with a report.")]
	internal sealed class Failure(ShellError error) : Exception(error.ArgumentNotNull().Summary)
	{
		/// <summary>
		/// Gets the report.
		/// </summary>
		/// <value>
		/// What went wrong.
		/// </value>
		public ShellError Error => error;
	}

	/// <summary>
	/// One token of an expression.
	/// </summary>
	/// <param name="Kind">What it is.</param>
	/// <param name="Start">Where it starts.</param>
	/// <param name="End">Where it ends.</param>
	/// <param name="Text">The token as typed.</param>
	private readonly record struct Token(Kind Kind, int Start, int End, string Text);

	/// <summary>
	/// Reads one expression by recursive descent, one method per precedence level.
	/// </summary>
	/// <param name="text">The expression.</param>
	/// <param name="variables">The shell variables.</param>
	/// <param name="depth">How many variables deep this expression is.</param>
	/// <param name="noUnset"><see langword="true" /> under <c>set -u</c>.</param>
	private sealed class Parser(string text, IReadOnlyDictionary<string, string> variables, int depth, bool noUnset)
	{
		/// <summary>
		/// Where the next token starts, or the blanks before it.
		/// </summary>
		private int position;

		/// <summary>
		/// How many short-circuited sides the parser is inside, where nothing is assigned, read or reported.
		/// </summary>
		private int skip;

		/// <summary>
		/// The last token read, which an error at the end points at.
		/// </summary>
		private Token previous;

		/// <summary>
		/// Reads the whole expression.
		/// </summary>
		/// <returns>
		/// Its value.
		/// </returns>
		public long Run()
		{
			if (this.Peek().Kind == Kind.End)
			{
				return 0;
			}

			var value = this.Comma();
			var extra = this.Peek();

			return extra.Kind == Kind.End ? value : throw this.Error("syntax", $"Unexpected `{extra.Text}`", extra, "expected an operator or the end");
		}

		/// <summary>
		/// Gets the token at <paramref name="at" />, after any blanks.
		/// </summary>
		/// <param name="at">Where to start.</param>
		/// <param name="before">The token before it, which decides what <c>++</c> and <c>--</c> are.</param>
		/// <returns>
		/// The token.
		/// </returns>
		/// <remarks>
		/// As in bash, <c>++</c> and <c>--</c> are one operator only after a name or before one; otherwise they're two
		/// signs, so <c>--1</c> is 1 and <c>1--1</c> is 2.
		/// </remarks>
		private Token Read(int at, Token before)
		{
			while (at < text.Length && char.IsWhiteSpace(text[at]))
			{
				at++;
			}

			if (at == text.Length)
			{
				return new Token(Kind.End, at, at, string.Empty);
			}

			var character = text[at];
			var end = at + 1;

			if (char.IsAsciiDigit(character) || char.IsAsciiLetter(character) || character == '_')
			{
				while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_' || (char.IsAsciiDigit(character) && text[end] is '@' or '#')))
				{
					end++;
				}

				return new Token(char.IsAsciiDigit(character) ? Kind.Number : Kind.Name, at, end, text[at..end]);
			}

			if (Operators.FirstOrDefault(typed => text.AsSpan(at).StartsWith(typed, StringComparison.Ordinal)) is not { } found)
			{
				return new Token(Kind.Other, at, end, text[at..end]);
			}

			var nameAfter = text.AsSpan(at + found.Length).TrimStart() is [var first, ..] && (char.IsAsciiLetter(first) || first == '_');

			return found is "++" or "--" && before.Kind != Kind.Name && !nameAfter
				? new Token(Kind.Operator, at, end, found[..1])
				: new Token(Kind.Operator, at, at + found.Length, found);
		}

		/// <summary>
		/// Gets the next token without moving past it.
		/// </summary>
		/// <returns>
		/// The token.
		/// </returns>
		/// <exception cref="Failure">The expression is nested too deep for the stack.</exception>
		private Token Peek()
		{
			var token = this.Read(this.position, this.previous);

			// Every level of nesting reads a token, and running out of stack would end the whole shell.
			return RuntimeHelpers.TryEnsureSufficientExecutionStack() ? token
				: throw this.Error("recursion", "Expression recursion level exceeded", token, "nested too deeply");
		}

		/// <summary>
		/// Moves past the next token.
		/// </summary>
		/// <returns>
		/// The token.
		/// </returns>
		private Token Next()
		{
			var token = this.Peek();

			this.position = token.End;
			this.previous = token.Kind == Kind.End ? this.previous : token;

			return token;
		}

		/// <summary>
		/// Moves past the next token when it's the operator <paramref name="typed" />.
		/// </summary>
		/// <param name="typed">The operator.</param>
		/// <returns>
		/// <see langword="true" /> when it was there.
		/// </returns>
		private bool TryTake(string typed)
		{
			if (this.Peek() is not { Kind: Kind.Operator } token || token.Text != typed)
			{
				return false;
			}

			_ = this.Next();

			return true;
		}

		/// <summary>
		/// Reads expressions joined by <c>,</c>.
		/// </summary>
		/// <returns>
		/// The last one's value.
		/// </returns>
		private long Comma()
		{
			var value = this.Assignment();

			while (this.TryTake(","))
			{
				value = this.Assignment();
			}

			return value;
		}

		/// <summary>
		/// Reads an assignment, which is right-associative, or a conditional expression.
		/// </summary>
		/// <returns>
		/// The value.
		/// </returns>
		private long Assignment()
		{
			var name = this.Peek();

			if (name.Kind == Kind.Name && this.Read(name.End, name) is { Kind: Kind.Operator } assign && Assignments.Contains(assign.Text))
			{
				_ = this.Next();
				_ = this.Next();

				// bash reads the old value before the right side, which may change it, and plain = never reads it.
				var old = assign.Text == "=" ? 0 : this.Value(name);
				var start = this.Peek();
				var right = this.Assignment();
				var value = assign.Text == "=" ? right : this.Operate(assign.Text[..^1], old, right, start);

				this.Assign(name, value);

				return value;
			}

			var result = this.Ternary();
			var after = this.Peek();

			return after.Kind == Kind.Operator && Assignments.Contains(after.Text)
				? throw this.Error("not_a_variable", "Attempted assignment to non-variable", after, "the left side isn't a variable name")
				: result;
		}

		/// <summary>
		/// Reads <c>condition ? value : value</c>, whose last part is right-associative.
		/// </summary>
		/// <returns>
		/// The value of the side the condition picks.
		/// </returns>
		private long Ternary()
		{
			var condition = this.Binary(0);

			if (!this.TryTake("?"))
			{
				return condition;
			}

			var yes = this.Skipping(condition == 0, this.Comma);

			if (!this.TryTake(":"))
			{
				throw this.Error("syntax", "`:` expected for conditional expression", this.Peek(), "expected `:`");
			}

			var no = this.Skipping(condition != 0, this.Ternary);

			return condition != 0 ? yes : no;
		}

		/// <summary>
		/// Reads the left-associative operators of one precedence level and every level above it.
		/// </summary>
		/// <param name="level">The index of the level in <see cref="Levels" />.</param>
		/// <returns>
		/// The value.
		/// </returns>
		private long Binary(int level)
		{
			if (level == Levels.Length)
			{
				return this.Power();
			}

			var left = this.Binary(level + 1);

			while (this.Peek() is { Kind: Kind.Operator } token && Levels[level].Contains(token.Text))
			{
				_ = this.Next();

				var start = this.Peek();
				var right = this.Skipping(token.Text == "&&" ? left == 0 : token.Text == "||" && left != 0, () => this.Binary(level + 1));

				left = this.Operate(token.Text, left, right, start);
			}

			return left;
		}

		/// <summary>
		/// Reads <c>**</c>, which is right-associative and binds less tightly than the unary operators, so
		/// <c>-2**2</c> is 4.
		/// </summary>
		/// <returns>
		/// The value.
		/// </returns>
		private long Power()
		{
			var left = this.Unary();

			if (!this.TryTake("**"))
			{
				return left;
			}

			var start = this.Peek();

			return this.Operate("**", left, this.Power(), start);
		}

		/// <summary>
		/// Reads the unary operators and prefix <c>++</c> and <c>--</c>.
		/// </summary>
		/// <returns>
		/// The value.
		/// </returns>
		private long Unary()
		{
			var token = this.Peek();

			if (token.Kind != Kind.Operator || token.Text is not ("!" or "~" or "-" or "+" or "++" or "--"))
			{
				return this.Primary();
			}

			_ = this.Next();

			// Read only gives ++ and -- before a name.
			if (token.Text is "++" or "--")
			{
				var name = this.Next();
				var value = unchecked(this.Value(name) + (token.Text == "++" ? 1 : -1));

				this.Assign(name, value);

				return value;
			}

			var operand = this.Unary();

			return token.Text switch
			{
				"!" => operand == 0 ? 1 : 0,
				"~" => ~operand,
				"-" => unchecked(-operand),
				_ => operand,
			};
		}

		/// <summary>
		/// Reads a number, a name with an optional postfix <c>++</c> or <c>--</c>, or an expression in parentheses.
		/// </summary>
		/// <returns>
		/// The value.
		/// </returns>
		private long Primary()
		{
			var token = this.Next();

			switch (token.Kind)
			{
				case Kind.Number:
					return this.Number(token);
				case Kind.Name:
					var value = this.Value(token);

					if (this.Peek() is { Kind: Kind.Operator, Text: "++" or "--" } step)
					{
						_ = this.Next();
						this.Assign(token, unchecked(value + (step.Text == "++" ? 1 : -1)));
					}

					return value;
				case Kind.Operator when token.Text == "(":
					var inner = this.Comma();

					return this.TryTake(")") ? inner : throw this.Error("syntax", "`(` is never closed", token, "needs a `)`", "add the missing `)`");
				case Kind.Operator:
				case Kind.End:
				case Kind.Other:
				default:
					throw this.Error("syntax", "Operand expected", token, token.Kind == Kind.End ? "needs a number or a variable after it" : "expected a number or a variable");
			}
		}

		/// <summary>
		/// Runs <paramref name="read" />, short-circuited when <paramref name="skipped" /> is <see langword="true" />.
		/// </summary>
		/// <param name="skipped"><see langword="true" /> for the side that isn't taken.</param>
		/// <param name="read">Reads the side.</param>
		/// <returns>
		/// What <paramref name="read" /> gives.
		/// </returns>
		private long Skipping(bool skipped, Func<long> read)
		{
			this.skip += skipped ? 1 : 0;

			try
			{
				return read();
			}
			finally
			{
				this.skip -= skipped ? 1 : 0;
			}
		}

		/// <summary>
		/// Applies a binary operator.
		/// </summary>
		/// <param name="typed">The operator.</param>
		/// <param name="left">The value on its left.</param>
		/// <param name="right">The value on its right.</param>
		/// <param name="start">The first token of the right side, which an error points at with the rest of the side.</param>
		/// <returns>
		/// The result, wrapped to 64 bits. Comparisons and logic give 0 or 1.
		/// </returns>
		/// <exception cref="Failure">Division by 0, or a negative exponent, outside a skipped side.</exception>
		private long Operate(string typed, long left, long right, Token start)
		{
			// Nothing uses a skipped side's value, and it mustn't fail.
			if (this.skip > 0)
			{
				return 0;
			}

			var side = start with { End = this.previous.End };

			return unchecked(typed switch
			{
				"/" or "%" when right == 0 => throw this.Error("division_by_zero", "Division by 0", side, "is 0"),
				"**" when right < 0 => throw this.Error("negative_exponent", "Exponent less than 0", side, "is negative"),
				"**" => Raise(left, right),

				// long.MinValue / -1 overflows, which .NET throws on and bash wraps.
				"/" => right == -1 ? -left : left / right,
				"%" => right == -1 ? 0 : left % right,
				"*" => left * right,
				"+" => left + right,
				"-" => left - right,
				"<<" => left << (int)right,
				">>" => left >> (int)right,
				"<" => left < right ? 1 : 0,
				"<=" => left <= right ? 1 : 0,
				">" => left > right ? 1 : 0,
				">=" => left >= right ? 1 : 0,
				"==" => left == right ? 1 : 0,
				"!=" => left != right ? 1 : 0,
				"&" => left & right,
				"^" => left ^ right,
				"|" => left | right,
				"&&" => left != 0 && right != 0 ? 1 : 0,
				_ => left != 0 || right != 0 ? 1 : 0,
			});
		}

		/// <summary>
		/// Raises a number to a power, wrapping to 64 bits.
		/// </summary>
		/// <param name="value">The base.</param>
		/// <param name="exponent">The exponent, not negative.</param>
		/// <returns>
		/// The power.
		/// </returns>
		private static long Raise(long value, long exponent)
		{
			var result = 1L;

			for (; exponent > 0; exponent >>= 1)
			{
				result = unchecked((exponent & 1) == 1 ? result * value : result);
				value = unchecked(value * value);
			}

			return result;
		}

		/// <summary>
		/// Gets a variable's value, worked out as an expression in turn.
		/// </summary>
		/// <param name="name">The name.</param>
		/// <returns>
		/// The value, 0 for an empty or unset variable and inside a skipped side.
		/// </returns>
		/// <exception cref="Failure">The value can't be worked out, or refers too deep.</exception>
		/// <exception cref="CommandLine.ExpansionError">The variable isn't set, under <c>set -u</c>.</exception>
		private long Value(Token name)
		{
			return this.skip > 0 ? 0
				: noUnset && !CommandLine.IsSet(variables, name.Text) ? throw new CommandLine.ExpansionError(CommandLine.UnsetError(text, new Word(name.Text, IsOperator: false, name.Start, name.End)), fatal: true)
				: depth >= MaxDepth ? throw this.Error("recursion", "Expression recursion level exceeded", name, "refers back to itself")
				: new Parser(CommandLine.Lookup(variables, name.Text), variables, depth + 1, noUnset).Run();
		}

		/// <summary>
		/// Sets a variable to a number, outside a skipped side.
		/// </summary>
		/// <param name="name">The name.</param>
		/// <param name="value">The number.</param>
		private void Assign(Token name, long value)
		{
			if (this.skip == 0)
			{
				DeclareCommand.Assign(variables, name.Text, value.ToString(CultureInfo.InvariantCulture));
			}
		}

		/// <summary>
		/// Reads a number: decimal, <c>0x</c> hexadecimal, octal after a leading <c>0</c>, or <c>base#digits</c> for a
		/// base from 2 to 64.
		/// </summary>
		/// <param name="token">The number as typed.</param>
		/// <returns>
		/// The value, wrapped to 64 bits.
		/// </returns>
		/// <exception cref="Failure">A digit is too great for the base, or the base isn't one.</exception>
		/// <remarks>
		/// The digits after 9 are <c>a</c> to <c>z</c>, <c>A</c> to <c>Z</c>, <c>@</c> and <c>_</c>. Up to base 36
		/// the case of a letter doesn't matter.
		/// </remarks>
		private long Number(Token token)
		{
			var typed = token.Text;
			var prefixed = typed is ['0', _, ..];
			long radix = !prefixed ? 10 : typed[1] is 'x' or 'X' ? 16 : 8;
			var value = 0L;

			for (var index = !prefixed ? 0 : radix == 16 ? 2 : 1; index < typed.Length; index++)
			{
				var character = typed[index];

				if (character == '#')
				{
					if (prefixed || index + 1 == typed.Length)
					{
						throw this.Error("bad_number", $"`{typed}` isn't a number", token, "not a number");
					}

					radix = value is >= 2 and <= 64 ? value : throw this.Error("bad_base", "Invalid arithmetic base", token, "bases go from 2 to 64");
					value = 0;
					prefixed = true;

					continue;
				}

				var digit = character switch
				{
					>= '0' and <= '9' => character - '0',
					>= 'a' and <= 'z' => character - 'a' + 10,
					>= 'A' and <= 'Z' => character - 'A' + (radix <= 36 ? 10 : 36),
					'@' => 62,
					_ => 63,
				};

				value = digit < radix
					? unchecked((value * radix) + digit)
					: throw this.Error("bad_number", $"Value too great for base {radix}", token, $"`{character}` isn't a digit of base {radix}", radix == 8 && typed[0] == '0' ? $"a leading 0 makes a number octal; write 10#{typed} for decimal" : null);
			}

			return value;
		}

		/// <summary>
		/// Builds the report for an expression that can't be worked out.
		/// </summary>
		/// <param name="code">The last part of the code.</param>
		/// <param name="summary">What went wrong.</param>
		/// <param name="at">The token at fault, or the span it covers; at the end, the last token read.</param>
		/// <param name="label">What's wrong with it.</param>
		/// <param name="help">A hint, or <see langword="null" />.</param>
		/// <returns>
		/// The exception to throw.
		/// </returns>
		private Failure Error(string code, string summary, Token at, string label, string? help = null) =>
			new(new ShellError($"sharpie::arithmetic::{code}", summary)
			{
				Source = text,
				Start = at.Kind == Kind.End ? this.previous.Start : at.Start,
				End = at.Kind == Kind.End ? this.previous.End : at.End,
				Label = label,
				Help = help,
			});
	}
}
