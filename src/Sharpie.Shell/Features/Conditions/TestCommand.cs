using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Conditions;

/// <summary>
/// Checks a condition about files, text or numbers, and gives 0 when it holds and 1 when it doesn't.
/// </summary>
/// <param name="name">
/// The name the command is typed as: <c>test</c>, or <c>[</c>, which needs <c>]</c> as its last argument.
/// </param>
/// <param name="declare">The <c>declare</c> builtin, whose variables <c>-v</c> checks.</param>
/// <remarks>
/// With up to four arguments the POSIX rules decide what each one is, by how many there are, as bash's
/// <c>posixtest</c> does, so <c>[ -n ]</c> checks that the word <c>-n</c> isn't empty. With more, <c>!</c> binds
/// tighter than <c>-a</c>, which binds tighter than <c>-o</c>, and <c>( )</c> groups, as in bash's <c>expr</c>.
/// <c>&lt;</c> and <c>&gt;</c> sort in character code order. <c>-o OPTION</c>, which checks a <c>set</c> option, isn't
/// there.
/// </remarks>
internal sealed class TestCommand(string name, DeclareCommand declare) : ICommand
{
	/// <summary>
	/// The status of a test that can't be read or run, as in bash.
	/// </summary>
	internal const int ErrorStatus = 2;

	/// <inheritdoc />
	public string Name => name;

	/// <inheritdoc />
	public string Description => "Checks a condition about files, text or numbers";

	/// <inheritdoc />
	/// <remarks>
	/// The status is 0 when the condition holds, 1 when it doesn't, and 2 after an error is reported: a missing
	/// <c>]</c>, an operator without the words it needs, or a word <c>-eq</c> and the other number operators can't
	/// read as a whole number.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (this.Name == "[")
		{
			if (args is not [.., "]"])
			{
				new ShellError("sharpie::test::missing_bracket", "`[` is missing its closing `]`") { Help = "end the condition with a separate ] word" }.Write();

				return ErrorStatus;
			}

			args = args[..^1];
		}

		try
		{
			return new Parser(args, declare.Variables).Run() ? 0 : 1;
		}
		catch (Condition.Failure failure)
		{
			failure.Error.Write();

			return ErrorStatus;
		}
	}

	/// <summary>
	/// Reads and runs the arguments of one <c>test</c>, from left to right.
	/// </summary>
	/// <param name="args">The arguments, without a <c>[</c>'s closing <c>]</c>.</param>
	/// <param name="variables">The shell variables.</param>
	private sealed class Parser(string[] args, IReadOnlyDictionary<string, string> variables)
	{
		/// <summary>
		/// The argument being read.
		/// </summary>
		private int position;

		/// <summary>
		/// Runs the whole test.
		/// </summary>
		/// <returns>
		/// Whether the condition holds.
		/// </returns>
		/// <exception cref="Condition.Failure">The arguments can't be read, or a test can't run.</exception>
		public bool Run() => args.Length switch
		{
			0 => false,
			1 => args[0].Length > 0,
			2 => this.TwoArguments(),
			3 => this.ThreeArguments(),
			4 when args[0] == "!" => this.Skip(1) && !this.ThreeArguments(),
			4 when args is ["(", _, _, ")"] => this.Skip(1) && this.TwoArguments(),
			_ => this.Expression(),
		};

		/// <summary>
		/// Reads every argument as tests joined by <c>-a</c> and <c>-o</c>.
		/// </summary>
		/// <returns>
		/// Whether the condition holds.
		/// </returns>
		private bool Expression()
		{
			var value = this.Or();

			return this.position < args.Length
				? throw Syntax("too_many_arguments", $"`{args[this.position]}` doesn't belong to any test", "join the tests with -a or -o")
				: value;
		}

		/// <summary>
		/// Reads <c>! word</c> or a unary test, the POSIX rule for two arguments.
		/// </summary>
		/// <returns>
		/// Whether the condition holds.
		/// </returns>
		private bool TwoArguments()
		{
			var (first, second) = (args[this.position], args[this.position + 1]);

			return first == "!" ? second.Length == 0
				: Condition.IsUnary(first) ? Condition.Unary(first, second, variables)
				: throw Syntax("unary_expected", $"`{first}` isn't a unary operator", "use one like -f, -d, -n or -z");
		}

		/// <summary>
		/// Reads a binary test, <c>a -a b</c>, <c>a -o b</c>, <c>! a b</c> or <c>( a )</c>, the POSIX rule for three
		/// arguments.
		/// </summary>
		/// <returns>
		/// Whether the condition holds.
		/// </returns>
		private bool ThreeArguments()
		{
			var (first, middle, last) = (args[this.position], args[this.position + 1], args[this.position + 2]);

			return Condition.IsBinary(middle) ? Condition.Binary(first, middle, last, StringComparer.Ordinal)
				: middle == "-a" ? first.Length > 0 && last.Length > 0
				: middle == "-o" ? first.Length > 0 || last.Length > 0
				: first == "!" ? this.Skip(1) && !this.TwoArguments()
				: first == "(" && last == ")" ? middle.Length > 0
				: throw Syntax("binary_expected", $"`{middle}` isn't a binary operator", "use one like =, !=, -eq or -lt");
		}

		/// <summary>
		/// Reads tests joined by <c>-o</c>.
		/// </summary>
		/// <returns>
		/// Whether any holds.
		/// </returns>
		private bool Or()
		{
			var value = this.And();

			while (this.Next("-o"))
			{
				// Both sides always run, as in bash, so an error on either is reported.
				value = this.And() | value;
			}

			return value;
		}

		/// <summary>
		/// Reads tests joined by <c>-a</c>.
		/// </summary>
		/// <returns>
		/// Whether all hold.
		/// </returns>
		private bool And()
		{
			var value = this.Term();

			while (this.Next("-a"))
			{
				value = this.Term() & value;
			}

			return value;
		}

		/// <summary>
		/// Reads one test: <c>! test</c>, <c>( tests )</c>, a binary or unary test, or a word that holds when it isn't
		/// empty.
		/// </summary>
		/// <returns>
		/// Whether the test holds.
		/// </returns>
		private bool Term()
		{
			if (this.position == args.Length)
			{
				throw Syntax("argument_expected", "A test is missing at the end", "add the word or test the operator before it needs");
			}

			var word = args[this.position];

			if (this.Next("!"))
			{
				return !this.Term();
			}

			if (this.Next("("))
			{
				var value = this.Or();

				return this.Next(")") ? value : throw Syntax("paren_expected", "`(` is never closed", "add a separate ) word");
			}

			if (this.position + 2 < args.Length && Condition.IsBinary(args[this.position + 1]))
			{
				var (middle, last) = (args[this.position + 1], args[this.position + 2]);

				this.position += 3;

				return Condition.Binary(word, middle, last, StringComparer.Ordinal);
			}

			if (this.position + 1 < args.Length && Condition.IsUnary(word))
			{
				this.position += 2;

				return Condition.Unary(word, args[this.position - 1], variables);
			}

			this.position++;

			return word.Length > 0;
		}

		/// <summary>
		/// Moves past the argument being read when it's <paramref name="expected" />.
		/// </summary>
		/// <param name="expected">The word to look for.</param>
		/// <returns>
		/// <see langword="true" /> when it was there.
		/// </returns>
		private bool Next(string expected)
		{
			var found = this.position < args.Length && args[this.position] == expected;

			this.position += found ? 1 : 0;

			return found;
		}

		/// <summary>
		/// Moves past arguments the caller has already read.
		/// </summary>
		/// <param name="count">How many.</param>
		/// <returns>
		/// <see langword="true" />, so the move fits in a condition.
		/// </returns>
		private bool Skip(int count)
		{
			this.position += count;

			return true;
		}

		/// <summary>
		/// Builds the error for arguments <c>test</c> can't read.
		/// </summary>
		/// <param name="code">The last part of the error's code.</param>
		/// <param name="summary">What's wrong.</param>
		/// <param name="help">How to fix it.</param>
		/// <returns>
		/// The error to throw.
		/// </returns>
		private static Condition.Failure Syntax(string code, string summary, string help) =>
			new(new ShellError($"sharpie::test::{code}", summary) { Help = help });
	}
}
