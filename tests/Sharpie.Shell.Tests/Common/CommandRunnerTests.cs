using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Arithmetic;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="CommandRunner" />, through <see cref="CommandList.Run(string, int, ShellHooks, Action{int})" /> with fake
/// hooks: a command that starts with <c>f</c> fails with 1, and any other succeeds.
/// </summary>
public sealed class CommandRunnerTests
{
	private readonly List<(string Command, bool Tested)> ran = [];

	private readonly List<int> failures = [];

	private readonly List<(string Name, string Value)> assigned = [];

	/// <summary>
	/// Checks that <c>if</c> runs the first branch whose condition succeeds, or <c>else</c>, and gives 0 when no branch
	/// runs.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="expectedRan">The commands that ran, in order.</param>
	/// <param name="expectedStatus">The status.</param>
	[Theory]
	[InlineData("if t; then a; fi", new[] { "t", "a" }, 0)]
	[InlineData("if f; then a; fi", new[] { "f" }, 0)]
	[InlineData("if f; then a; elif t; then b; else c; fi", new[] { "f", "t", "b" }, 0)]
	[InlineData("if f; then a; elif f; then b; else c; fi", new[] { "f", "f", "c" }, 0)]
	[InlineData("if t; then fa; fi", new[] { "t", "fa" }, 1)]
	[InlineData("if f; then a; else fb; fi", new[] { "f", "fb" }, 1)]
	public void Run_WithIf_RunsMatchingBranch(string line, string[] expectedRan, int expectedStatus)
	{
		// Act
		var status = CommandList.Run(line, 5, this.Hooks());

		// Assert
		_ = this.ran.Select(command => command.Command).Should().Equal(expectedRan);
		_ = status.Should().Be(expectedStatus);
	}

	/// <summary>
	/// Checks that <c>while</c> runs its body while the condition succeeds and <c>until</c> while it fails, each
	/// giving the body's last status, or 0 when it never ran.
	/// </summary>
	/// <param name="line">The line, where <c>more</c> succeeds the first three times.</param>
	/// <param name="expectedBodies">How many times the body ran.</param>
	/// <param name="expectedStatus">The status.</param>
	[Theory]
	[InlineData("while more; do body; done", 3, 0)]
	[InlineData("while more; do fbody; done", 3, 1)]
	[InlineData("until more; do body; done", 0, 0)]
	public void Run_WithLoop_RepeatsBody(string line, int expectedBodies, int expectedStatus)
	{
		// Arrange
		var rounds = 0;
		var hooks = this.Hooks(command => command == "more" ? (rounds++ < 3 ? 0 : 1) : null);

		// Act
		var status = CommandList.Run(line, 5, hooks);

		// Assert
		_ = this.ran.Count(command => command.Command.EndsWith("body", StringComparison.Ordinal)).Should().Be(expectedBodies);
		_ = status.Should().Be(expectedStatus);
	}

	/// <summary>
	/// Checks that <c>until</c> stops once its condition succeeds.
	/// </summary>
	[Fact]
	public void Run_WithUntil_StopsWhenConditionSucceeds()
	{
		// Arrange
		var rounds = 0;
		var hooks = this.Hooks(command => command == "done-yet" ? (++rounds < 3 ? 1 : 0) : null);

		// Act
		_ = CommandList.Run("until done-yet; do body; done", 0, hooks);

		// Assert
		_ = this.ran.Count(command => command.Command == "body").Should().Be(2);
	}

	/// <summary>
	/// Checks that <c>for</c> puts each expanded word in the variable before its body, and goes over the positional
	/// parameters without <c>in</c>.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="expected">The values assigned, in order.</param>
	[Theory]
	[InlineData("for x in a b; do c; done", new[] { "a", "b" })]
	[InlineData("for x in 'a b' c; do c; done", new[] { "a b", "c" })]
	[InlineData("for x; do c; done", new[] { "p", "q" })]
	[InlineData("for x in; do c; done", new string[0])]
	public void Run_WithFor_AssignsEachWord(string line, string[] expected)
	{
		// Act
		var status = CommandList.Run(line, 5, this.Hooks());

		// Assert
		_ = this.assigned.Should().Equal(expected.Select(value => ("x", value)));
		_ = this.ran.Count(command => command.Command == "c").Should().Be(expected.Length);
		_ = status.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>case</c> runs the first item with a pattern that matches, and gives 0 when none does.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="expectedRan">The commands that ran.</param>
	[Theory]
	[InlineData("case b in a) x;; b|c) y;; b) z;; esac", new[] { "y" })]
	[InlineData("case abc in a*) x;; *) y;; esac", new[] { "x" })]
	[InlineData("case q in a) x;; esac", new string[0])]
	[InlineData("case a in a) ;; *) y;; esac", new string[0])]
	[InlineData("case 'a b' in a) x;; 'a b') y;; esac", new[] { "y" })]
	public void Run_WithCase_RunsFirstMatch(string line, string[] expectedRan)
	{
		// Act
		var status = CommandList.Run(line, 5, this.Hooks());

		// Assert
		_ = this.ran.Select(command => command.Command).Should().Equal(expectedRan);
		_ = status.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>!</c> flips the status between 0 and 1, and stores it as <c>$?</c>.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="expected">The status.</param>
	[Theory]
	[InlineData("! t", 1)]
	[InlineData("! f", 0)]
	[InlineData("! { f; }", 0)]
	[InlineData("! t && a", 1)]
	public void Run_WithNegation_FlipsStatus(string line, int expected)
	{
		// Arrange
		var stored = new List<int>();
		var hooks = new ShellHooks(this.Hooks().Run) { SetStatus = stored.Add };

		// Act
		var status = CommandList.Run(line, 0, hooks);

		// Assert
		_ = status.Should().Be(expected);
		_ = stored[^1].Should().Be(expected);
	}

	/// <summary>
	/// Checks that <c>break</c> and <c>continue</c> leave as many loops as they say, and that the loop depth is back to
	/// 0 after.
	/// </summary>
	/// <param name="line">The line, where <c>breakN</c> and <c>continueN</c> act on <c>N</c> loops.</param>
	/// <param name="expectedRan">The commands that ran, other than those.</param>
	[Theory]
	[InlineData("for i in 1 2 3; do a; break1; b; done; c", new[] { "a", "c" })]
	[InlineData("for i in 1 2; do a; continue1; b; done", new[] { "a", "a" })]
	[InlineData("for i in 1 2; do for j in 1 2; do a; break2; done; b; done; c", new[] { "a", "c" })]
	[InlineData("for i in 1 2; do for j in 1 2; do a; continue2; b; done; c; done", new[] { "a", "a" })]
	[InlineData("while t; do a; break1; done", new[] { "t", "a" })]
	public void Run_WithLoopControl_LeavesLoops(string line, string[] expectedRan)
	{
		// Arrange
		var hooks = this.Hooks(command => command.StartsWith("break", StringComparison.Ordinal) ? throw new LoopControl(command[^1] - '0', next: false)
			: command.StartsWith("continue", StringComparison.Ordinal) ? throw new LoopControl(command[^1] - '0', next: true)
			: null);

		// Act
		var status = CommandList.Run(line, 5, hooks);

		// Assert
		_ = this.ran.Select(command => command.Command).Where(command => !command.StartsWith("break", StringComparison.Ordinal) && !command.StartsWith("continue", StringComparison.Ordinal)).Should().Equal(expectedRan);
		_ = hooks.Declare.LoopDepth.Should().Be(0);
		_ = status.Should().Be(0);
	}

	/// <summary>
	/// Checks that the loop depth counts the loops running around a command.
	/// </summary>
	[Fact]
	public void Run_InsideLoops_CountsLoopDepth()
	{
		// Arrange
		var depths = new List<int>();
		ShellHooks? hooks = null;

		hooks = this.Hooks(command =>
		{
			depths.Add(hooks!.Declare.LoopDepth);

			return command == "break1" ? throw new LoopControl(1, next: false) : null;
		});

		// Act
		_ = CommandList.Run("a; for i in 1; do b; while c; do break1; done; done", 0, hooks);

		// Assert
		_ = depths.Should().Equal(0, 1, 2, 2);
	}

	/// <summary>
	/// Checks which commands run tested: conditions, every pipeline but the last of an and-or list, and a pipeline
	/// after <c>!</c>, with everything inside them.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="expectedTested">The commands that ran tested.</param>
	[Theory]
	[InlineData("if a; then b; fi", new[] { "a" })]
	[InlineData("while fa; do b; done", new[] { "fa" })]
	[InlineData("until a; do b; done", new[] { "a" })]
	[InlineData("a && b", new[] { "a" })]
	[InlineData("! a", new[] { "a" })]
	[InlineData("{ a; b; } && c", new[] { "a", "b" })]
	[InlineData("if { a; b; }; then c; fi", new[] { "a", "b" })]
	[InlineData("if a; then for x in 1; do b && c; done; fi", new[] { "a", "b" })]
	[InlineData("! case x in x) a;; esac", new[] { "a" })]
	[InlineData("a && b && c", new[] { "a", "b" })]
	[InlineData("if ! a; then b; fi", new[] { "a" })]
	[InlineData("a &", new string[0])]
	[InlineData("{ a; } &", new string[0])]
	public void Run_WithTestedCommands_PassesTested(string line, string[] expectedTested)
	{
		// Act
		_ = CommandList.Run(line, 0, this.Hooks());

		// Assert
		_ = this.ran.Where(command => command.Tested).Select(command => command.Command).Should().Equal(expectedTested);
	}

	/// <summary>
	/// Checks that a failure is reported for <c>set -e</c> only where bash's would end the shell: a simple command, a
	/// subshell or a pipeline that fails untested, not a compound command whose failure was tested inside.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="expected">The failures reported.</param>
	[Theory]
	[InlineData("f; t", new[] { 1 })]
	[InlineData("if f; then t; fi", new int[0])]
	[InlineData("if t; then f; fi", new[] { 1 })]
	[InlineData("! t", new int[0])]
	[InlineData("{ f; }", new[] { 1 })]
	[InlineData("{ f && t; }", new int[0])]
	[InlineData("( f && t )", new[] { 1 })]
	[InlineData("while f; do t; done", new int[0])]
	[InlineData("a | { f; }", new[] { 1 })]
	[InlineData("{ f; } || t", new int[0])]
	[InlineData("t && fb && t", new int[0])]
	[InlineData("((0))", new[] { 1 })]
	[InlineData("((0)) || t", new int[0])]
	[InlineData("for ((;0;)); do t; done", new int[0])]
	public void Run_WithFailures_ReportsWhereSetEActs(string line, int[] expected)
	{
		// Arrange
		var hooks = new ShellHooks((command, _) => command.Contains('f', StringComparison.Ordinal) ? 1 : 0) { Failed = this.failures.Add };

		// Act
		_ = CommandList.Run(line, 0, hooks);

		// Assert
		_ = this.failures.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a pipeline with a compound stage runs as one pipeline, each compound stage in a copy of the shell,
	/// and that anything in the background but one simple pipeline runs in a copy too.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="expected">The texts run, in order.</param>
	[Theory]
	[InlineData("a | { b; } | c", "a | copy[{ b; }] | c")]
	[InlineData("while a; do b; done | c", "copy[while a; do b; done] | c")]
	[InlineData("a |& { b; }", "a |& copy[{ b; }]")]
	[InlineData("cat <<A | { b; }\nx\nA", "cat <<A | copy[{ b; }]\nx\nA")]
	[InlineData("{ a; } &", "copy[{ a; }] &")]
	[InlineData("a && b &", "copy[a && b] &")]
	[InlineData("! a &", "copy[! a] &")]
	[InlineData("a | b &", "a | b &")]
	[InlineData("a&", "a&")]
	[InlineData("if a | { b; }; then c; fi", "a | tested-copy[{ b; }]", "c")]
	public void Run_WithCompoundStageOrBackground_RunsCopy(string line, params string[] expected)
	{
		// Arrange
		var hooks = new ShellHooks(this.Hooks().Run) { CopyCommand = (text, tested) => tested ? $"tested-copy[{text}]" : $"copy[{text}]" };

		// Act
		_ = CommandList.Run(line, 0, hooks);

		// Assert
		_ = this.ran.Select(command => command.Command).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a background job's status, 0 once it's started, is stored as <c>$?</c>.
	/// </summary>
	[Fact]
	public void Run_WithBackgroundItem_StoresItsStatus()
	{
		// Arrange
		var stored = new List<int>();
		var hooks = new ShellHooks((_, _) => 0) { SetStatus = stored.Add, CopyCommand = (text, _) => text };

		// Act
		_ = CommandList.Run("f; { a; } &", 0, hooks);

		// Assert
		_ = stored.Should().Equal(0, 0);
	}

	/// <summary>
	/// Checks that a subshell runs through its hook, inside the redirects typed after it.
	/// </summary>
	[Fact]
	public void Run_WithRedirectedSubshell_UsesHooks()
	{
		// Arrange
		var calls = new List<string>();
		var hooks = new ShellHooks(this.Hooks().Run)
		{
			Subshell = body =>
			{
				calls.Add("subshell");

				return body();
			},
			Redirected = (redirects, body) =>
			{
				calls.Add(redirects.Text);

				return body();
			},
		};

		// Act
		var status = CommandList.Run("( a; fb ) > out 2>&1", 0, hooks);

		// Assert
		_ = calls.Should().Equal("> out 2>&1", "subshell");
		_ = this.ran.Select(command => command.Command).Should().Equal("a", "fb");
		_ = status.Should().Be(1);
	}

	/// <summary>
	/// Checks that a function definition goes to its hook and gives 0, without running the body.
	/// </summary>
	[Fact]
	public void Run_WithFunctionDefinition_DefinesIt()
	{
		// Arrange
		var defined = new List<FunctionDefinition>();
		var hooks = new ShellHooks(this.Hooks().Run) { Define = defined.Add };

		// Act
		var status = CommandList.Run("greet() { echo hi; }", 3, hooks);

		// Assert
		_ = status.Should().Be(0);
		_ = this.ran.Should().BeEmpty();
		_ = defined.Should().ContainSingle().Which.Name.Should().Be("greet");
		_ = defined[0].Text.Should().Be("greet() { echo hi; }");
	}

	/// <summary>
	/// Checks that <c>for ((init; test; step))</c> works the init out once, then the test before each round and the
	/// step after it, <c>continue</c> included, and stops with 1 at an expression that can't be worked out.
	/// </summary>
	/// <param name="line">The line, where <c>bad</c> can't be worked out.</param>
	/// <param name="expectedWorked">The expressions worked out, in order.</param>
	/// <param name="expectedBodies">How many times the body ran.</param>
	/// <param name="expectedStatus">The status.</param>
	[Theory]
	[InlineData("for ((i=0; i<2; i++)); do a; done", new[] { "i=0", " i<2", " i++", " i<2", " i++", " i<2" }, 2, 0)]
	[InlineData("for ((i=0; i<2; i++)); do continue; done", new[] { "i=0", " i<2", " i++", " i<2", " i++", " i<2" }, 0, 0)]
	[InlineData("for ((;;)); do break; done", new[] { "", "1" }, 0, 0)]
	[InlineData("for ((;1;bad)); do a; done", new[] { "", "1", "bad" }, 1, 1)]
	[InlineData("for ((;bad;)); do a; done", new[] { "", "bad" }, 0, 1)]
	[InlineData("for ((bad;;)); do a; done", new[] { "bad" }, 0, 1)]
	[InlineData("for ((;1;)); do fa; break; done", new[] { "", "1" }, 1, 0)]
	public void Run_WithArithmeticFor_WorksOutEachPart(string line, string[] expectedWorked, int expectedBodies, int expectedStatus)
	{
		// Arrange
		var worked = new List<string>();
		var variables = new Dictionary<string, string>();
		var hooks = new ShellHooks((command, _) =>
		{
			this.ran.Add((command, false));

			return command is "break" or "continue" ? throw new LoopControl(1, next: command == "continue") : command.StartsWith('f') ? 1 : 0;
		})
		{
			Arithmetic = expression =>
			{
				worked.Add(expression);

				return expression == "bad" ? null : ArithmeticExpression.Evaluate(expression, variables);
			},
		};

		// Act
		var status = CommandList.Run(line, 0, hooks);

		// Assert
		_ = (worked, this.ran.Count(command => command.Command is "a" or "fa"), status).Should().BeEquivalentTo((expectedWorked, expectedBodies, expectedStatus), options => options.WithStrictOrdering());
	}

	/// <summary>
	/// Checks that a <c>for</c> header goes to the trace hook as typed once per value, and not at all for an empty
	/// list, and that a <c>case</c> header goes once.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The lines the trace hook gets.</param>
	[Theory]
	[InlineData("for i in $x b; do a; done", new[] { "for i in $x b", "for i in $x b" })]
	[InlineData("for i; do a; done", new[] { "for i in \"$@\"", "for i in \"$@\"" })]
	[InlineData("for i in; do a; done", new string[0])]
	[InlineData("case $x in b) a;; c) a;; esac", new[] { "case $x in" })]
	public void Run_WithTraceHook_GetsHeaders(string line, string[] expected)
	{
		// Arrange
		var traced = new List<string>();
		var hooks = new ShellHooks(this.Hooks().Run)
		{
			Trace = traced.Add,
			ExpandWords = words => words.Length == 0 ? [] : ["x", "b"],
			Positional = () => ["p", "q"],
		};

		// Act
		_ = CommandList.Run(line, 0, hooks);

		// Assert
		_ = traced.Should().Equal(expected);
	}

	/// <summary>
	/// Makes hooks that record each command and give it a status.
	/// </summary>
	/// <param name="status">
	/// Gives a command's status, or <see langword="null" /> for the default: 1 for a command that starts with
	/// <c>f</c>, 0 for any other.
	/// </param>
	/// <returns>
	/// The hooks.
	/// </returns>
	private ShellHooks Hooks(Func<string, int?>? status = null) =>
		new((command, tested) =>
		{
			this.ran.Add((command, tested));

			return status?.Invoke(command) ?? (command.StartsWith('f') ? 1 : 0);
		})
		{
			Failed = this.failures.Add,
			Assign = (name, value) => this.assigned.Add((name, value)),
			Positional = () => ["p", "q"],
		};
}
