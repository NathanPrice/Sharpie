using System.Diagnostics.CodeAnalysis;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Features.Arithmetic;
using Sharpie.Shell.Features.Time;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Common;

/// <summary>
/// What <see cref="CommandRunner" /> asks the shell to do, as bash's <c>execute_cmd.c</c> calls into the rest of
/// bash.
/// </summary>
/// <param name="run">
/// Runs one pipeline of simple commands as typed and gives its status. Its second argument is
/// <see langword="true" /> when the command is tested, so <c>set -e</c> ignores its failure.
/// </param>
/// <remarks>
/// Every hook but <paramref name="run" /> has a default that does the least it can, so tests and
/// <see cref="CommandList.Run(string, int, Func{string, bool, int}, Action{int}, Action{int})" /> set only what they use.
/// </remarks>
internal sealed class ShellHooks(Func<string, bool, int> run)
{
	/// <summary>
	/// Gets the hook that runs one pipeline of simple commands.
	/// </summary>
	/// <value>
	/// The hook passed to the constructor.
	/// </value>
	public Func<string, bool, int> Run { get; } = run.ArgumentNotNull();

	/// <summary>
	/// Gets the hook called with the status of a command that fails and isn't tested, which is where <c>set -e</c>
	/// ends the shell.
	/// </summary>
	/// <value>
	/// The hook. By default it does nothing.
	/// </value>
	public Action<int> Failed { get; init; } = _ => { };

	/// <summary>
	/// Gets the hook that stores a status as <c>$?</c>.
	/// </summary>
	/// <value>
	/// The hook. By default it does nothing.
	/// </value>
	public Action<int> SetStatus { get; init; } = _ => { };

	/// <summary>
	/// Gets the hook that expands the words of a <c>for</c> loop into the values it goes over.
	/// </summary>
	/// <value>
	/// The hook. By default it splits the words with no expansion.
	/// </value>
	public Func<string, IReadOnlyList<string>> ExpandWords { get; init; } =
		words => [.. CommandLine.Split(words).Where(word => !word.IsOperator).Select(word => word.Text)];

	/// <summary>
	/// Gets the hook that expands the word a <c>case</c> matches, with no wildcards.
	/// </summary>
	/// <value>
	/// The hook. By default it only removes the quotes.
	/// </value>
	public Func<string, string> ExpandWord { get; init; } = word => CommandLine.ExpandText(word, null);

	/// <summary>
	/// Gets the hook that turns a <c>case</c> pattern as typed into a pattern for <see cref="Glob.IsMatch" />.
	/// </summary>
	/// <value>
	/// The hook. By default it only removes the quotes, escaping the wildcards they held.
	/// </value>
	public Func<string, string> Pattern { get; init; } = pattern => CommandLine.Pattern(pattern, null);

	/// <summary>
	/// Gets the hook that works out the expression of a <c>((...))</c> or <c>for ((;;))</c>, as typed.
	/// </summary>
	/// <value>
	/// The hook, which gives <see langword="null" /> after reporting an expression it can't work out. By default it
	/// works the expression out with no variables.
	/// </value>
	public Func<string, long?> Arithmetic { get; init; } = expression =>
	{
		try
		{
			return ArithmeticExpression.Evaluate(expression, new Dictionary<string, string>());
		}
		catch (ArithmeticExpression.Failure)
		{
			return null;
		}
	};

	/// <summary>
	/// Gets the hook that sets a shell variable, for a <c>for</c> loop.
	/// </summary>
	/// <value>
	/// The hook. By default it does nothing.
	/// </value>
	public Action<string, string> Assign { get; init; } = (_, _) => { };

	/// <summary>
	/// Gets the hook that gives the positional parameters, which <c>for name</c> with no <c>in</c> goes over.
	/// </summary>
	/// <value>
	/// The hook. By default there are none.
	/// </value>
	public Func<IReadOnlyList<string>> Positional { get; init; } = () => [];

	/// <summary>
	/// Gets the hook that shows a <c>for</c> or <c>case</c> header under <c>set -x</c>, as typed, before each pass.
	/// </summary>
	/// <value>
	/// The hook. By default it does nothing.
	/// </value>
	public Action<string> Trace { get; init; } = _ => { };

	/// <summary>
	/// Gets the hook that runs a list as a subshell, so whatever it changes is undone after.
	/// </summary>
	/// <value>
	/// The hook. By default it just runs the list.
	/// </value>
	public Func<Func<int>, int> Subshell { get; init; } = body => body();

	/// <summary>
	/// Gets the hook that runs a compound command with the redirects typed after it.
	/// </summary>
	/// <value>
	/// The hook. By default it ignores the redirects.
	/// </value>
	public Func<Leaf, Func<int>, int> Redirected { get; init; } = (_, body) => body();

	/// <summary>
	/// Gets the hook that gives the text of a simple command that runs a command in a copy of the shell, as a
	/// pipeline stage or background job does in bash. Its second argument tells whether the copy runs tested.
	/// </summary>
	/// <value>
	/// The hook. By default the command is given back as it is.
	/// </value>
	public Func<string, bool, string> CopyCommand { get; init; } = (text, _) => text;

	/// <summary>
	/// Gets the hook that shows how long a pipeline after <c>time</c> took.
	/// </summary>
	/// <value>
	/// The hook. By default it does nothing.
	/// </value>
	public Action<ElapsedTimes, Timing> ReportTime { get; init; } = (_, _) => { };

	/// <summary>
	/// Gets the hook that stores a function definition.
	/// </summary>
	/// <value>
	/// The hook. By default it does nothing.
	/// </value>
	public Action<FunctionDefinition> Define { get; init; } = _ => { };

	/// <summary>
	/// Gets the <c>declare</c> builtin, where the loop depth that <c>break</c> and <c>continue</c> read is kept.
	/// </summary>
	/// <value>
	/// The builtin. By default one of its own.
	/// </value>
	public DeclareCommand Declare { get; init; } = new(new ShellSettings());
}

/// <summary>
/// Thrown by <c>break</c> and <c>continue</c> to leave the loops around them.
/// </summary>
/// <param name="levels">How many loops to leave, at least 1.</param>
/// <param name="next">
/// <see langword="true" /> for <c>continue</c>, which starts the next round of the last loop left.
/// </param>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only break and continue throw it, always with a count.")]
internal sealed class LoopControl(int levels, bool next) : Exception
{
	/// <summary>
	/// Gets how many loops are left to leave.
	/// </summary>
	/// <value>
	/// At least 1.
	/// </value>
	public int Levels => levels;

	/// <summary>
	/// Gets a value indicating whether this is a <c>continue</c>.
	/// </summary>
	/// <value>
	/// <see langword="true" /> for <c>continue</c>; <see langword="false" /> for <c>break</c>.
	/// </value>
	public bool Next => next;
}

/// <summary>
/// Runs the tree <see cref="CommandParser" /> builds, as bash's <c>execute_cmd.c</c> does, handing each pipeline of
/// simple commands to <see cref="ShellHooks.Run" />.
/// </summary>
/// <param name="hooks">What the runner asks the shell to do.</param>
/// <param name="starting">
/// Called right before each pipeline of simple commands runs, with where it starts in the parsed text, or
/// <see langword="null" />.
/// </param>
/// <remarks>
/// The lists an <c>if</c>, <c>while</c> or <c>until</c> tests, every pipeline but the last of an <c>&amp;&amp;</c>
/// and <c>||</c> list, and a pipeline after <c>!</c> are tested, and so is everything inside them, so
/// <c>set -e</c> ignores their failures, as POSIX says.
/// </remarks>
internal sealed class CommandRunner(ShellHooks hooks, Action<int>? starting = null)
{
	/// <summary>
	/// Runs the commands of <paramref name="list" /> in order.
	/// </summary>
	/// <param name="list">The commands.</param>
	/// <param name="status">The status before the list, which an empty list gives back.</param>
	/// <param name="tested"><see langword="true" /> when the list is tested.</param>
	/// <returns>
	/// The last command's status.
	/// </returns>
	/// <remarks>
	/// A background command that is one simple pipeline runs as typed, <c>&amp;</c> included. Anything else in the
	/// background runs in a copy of the shell, so it's a job like any other.
	/// </remarks>
	public int Run(ListNode list, int status, bool tested = false)
	{
		_ = list.ArgumentNotNull();

		foreach (var (command, background, text) in list.Items)
		{
			if (background)
			{
				starting?.Invoke(command.Start);
				status = hooks.Run(IsSimple(command) ? text : hooks.CopyCommand(command.Text, false) + " &", false);
				hooks.SetStatus(status);

				continue;
			}

			status = this.RunAndOr(command, tested);
		}

		return status;
	}

	/// <summary>
	/// Runs one command of a pipeline that has no other stages.
	/// </summary>
	/// <param name="command">The command.</param>
	/// <param name="tested"><see langword="true" /> when it's tested.</param>
	/// <returns>
	/// Its status. A function definition gives 0.
	/// </returns>
	public int RunCommand(CommandNode command, bool tested)
	{
		switch (command.ArgumentNotNull())
		{
			case Leaf leaf:
				starting?.Invoke(leaf.Start);

				return hooks.Run(leaf.Text, tested);
			case FunctionDefinition definition:
				hooks.Define(definition);

				return 0;
			default:
				var compound = (Compound)command;

				return compound.Redirects is { } redirects
					? hooks.Redirected(redirects, () => this.RunCompound(compound, tested))
					: this.RunCompound(compound, tested);
		}
	}

	/// <summary>
	/// Checks whether a background command runs as typed.
	/// </summary>
	/// <param name="command">The command.</param>
	/// <returns>
	/// <see langword="true" /> for one pipeline of simple commands with no <c>!</c> or <c>time</c>.
	/// </returns>
	private static bool IsSimple(AndOr command) => command is { Rest: [], First: { Negated: false, Time: Timing.None, Stages: [Leaf] } };

	/// <summary>
	/// Runs pipelines joined by <c>&amp;&amp;</c> and <c>||</c>, skipping each one its operator rules out.
	/// </summary>
	/// <param name="command">The list.</param>
	/// <param name="tested"><see langword="true" /> when it's tested.</param>
	/// <returns>
	/// The status of the last pipeline that ran.
	/// </returns>
	/// <remarks>
	/// POSIX gives <c>&amp;&amp;</c> and <c>||</c> the same precedence, read from the left, so in
	/// <c>false &amp;&amp; a || b</c> the skipped <c>a</c> keeps the status at 1 and <c>b</c> runs. Only the last
	/// pipeline can fail for <c>set -e</c>, and only when it ran.
	/// </remarks>
	private int RunAndOr(AndOr command, bool tested)
	{
		var status = this.RunPipeline(command.First, tested || command.Rest.Count > 0);
		var last = command.Rest.Count == 0 ? command.First : null;

		for (var index = 0; index < command.Rest.Count; index++)
		{
			var (typed, pipeline) = command.Rest[index];

			if (typed != (status == 0 ? CommandList.And : CommandList.Or))
			{
				continue;
			}

			var isLast = index == command.Rest.Count - 1;

			status = this.RunPipeline(pipeline, tested || !isLast);
			last = isLast ? pipeline : null;
		}

		if (status != 0 && !tested && last is { Negated: false } && CanFail(last))
		{
			hooks.Failed(status);
		}

		return status;
	}

	/// <summary>
	/// Checks whether a pipeline's own failure counts for <c>set -e</c>.
	/// </summary>
	/// <param name="pipeline">The pipeline.</param>
	/// <returns>
	/// <see langword="false" /> for a compound command other than a subshell or <c>((...))</c>, as bash has it: a
	/// failure inside counted already, or was one <c>set -e</c> ignores, as in <c>{ false &amp;&amp; true; }</c>.
	/// </returns>
	private static bool CanFail(Pipeline pipeline) => pipeline.Stages is not [Compound and not (Subshell or ArithmeticCommand)];

	/// <summary>
	/// Runs a pipeline and stores its status as <c>$?</c>.
	/// </summary>
	/// <param name="pipeline">The pipeline.</param>
	/// <param name="tested"><see langword="true" /> when it's tested.</param>
	/// <returns>
	/// The last stage's status, flipped after <c>!</c>. After <c>time</c> the times are reported once it ends.
	/// </returns>
	/// <remarks>
	/// A pipeline with a compound stage runs each compound stage in a copy of the shell, with real pipes between the
	/// stages, as bash runs every stage in a subshell. So <c>tail -f log | while read l; do ...; done</c> streams.
	/// </remarks>
	private int RunPipeline(Pipeline pipeline, bool tested)
	{
		tested |= pipeline.Negated;

		var clock = pipeline.Time == Timing.None ? null : ProcessTimes.Start();
		var status = pipeline.Stages is [var only] ? this.RunCommand(only, tested) : this.RunJoined(pipeline, tested);

		if (clock is not null)
		{
			hooks.ReportTime(clock.Stop(), pipeline.Time);
		}

		status = pipeline.Negated ? (status == 0 ? 1 : 0) : status;
		hooks.SetStatus(status);

		return status;
	}

	/// <summary>
	/// Runs a pipeline with a compound stage as one pipeline of simple commands.
	/// </summary>
	/// <param name="pipeline">The pipeline.</param>
	/// <param name="tested"><see langword="true" /> when it's tested.</param>
	/// <returns>
	/// The pipeline's status.
	/// </returns>
	private int RunJoined(Pipeline pipeline, bool tested)
	{
		starting?.Invoke(pipeline.Start);

		return hooks.Run(this.Joined(pipeline, tested), tested);
	}

	/// <summary>
	/// Gets the text that runs a pipeline with a compound stage as one pipeline of simple commands.
	/// </summary>
	/// <param name="pipeline">The pipeline.</param>
	/// <param name="tested"><see langword="true" /> when it's tested.</param>
	/// <returns>
	/// The stages joined by their pipes, each compound stage replaced by a command that runs it in a copy of the
	/// shell, and the here-document bodies of the simple stages after it.
	/// </returns>
	private string Joined(Pipeline pipeline, bool tested)
	{
		var stages = pipeline.Stages.Select(stage => stage is Leaf leaf ? leaf.Line : hooks.CopyCommand(stage.Text, tested)).ToArray();
		var text = stages[0] + string.Concat(pipeline.Pipes.Select((pipe, index) => $" {pipe} {stages[index + 1]}"));

		return text + string.Concat(pipeline.Stages.OfType<Leaf>().Select(leaf => leaf.Bodies));
	}

	/// <summary>
	/// Runs a compound command, its redirects already applied.
	/// </summary>
	/// <param name="command">The command.</param>
	/// <param name="tested"><see langword="true" /> when it's tested.</param>
	/// <returns>
	/// Its status. An <c>if</c> with no branch taken, a <c>for</c> with no words, a loop whose body never ran and a
	/// <c>case</c> with no match give 0, as in bash. <c>((...))</c> gives 1 for 0, and both it and
	/// <c>for ((;;))</c> give 1 for an expression they can't work out.
	/// </returns>
	private int RunCompound(Compound command, bool tested)
	{
		switch (command)
		{
			case Group group:
				return this.Run(group.Body, 0, tested);
			case Subshell subshell:
				return hooks.Subshell(() => this.Run(subshell.Body, 0, tested));
			case If conditional:
				foreach (var (condition, body) in conditional.Branches)
				{
					if (this.Run(condition, 0, tested: true) == 0)
					{
						return this.Run(body, 0, tested);
					}
				}

				return conditional.Else is { } otherwise ? this.Run(otherwise, 0, tested) : 0;
			case Loop loop:
				return this.Repeat(tested, () => this.Run(loop.Condition, 0, tested: true) == 0 != loop.Until, loop.Body);
			case ArithmeticCommand arithmetic:
				return hooks.Arithmetic(arithmetic.Expression) is null or 0 ? 1 : 0;
			case ArithmeticFor loop:
				return this.RepeatArithmetic(loop, tested);
			case For loop:
				var values = loop.Words is { } words ? hooks.ExpandWords(words) : hooks.Positional();
				var next = 0;

				return this.Repeat(tested, () => next < values.Count && Assign(values[next++]), loop.Body);
			default:
				var match = (Case)command;

				hooks.Trace($"case {match.Word} in");

				var word = hooks.ExpandWord(match.Word);

				foreach (var (patterns, body) in match.Items)
				{
					if (patterns.Any(pattern => Glob.IsMatch(word, hooks.Pattern(pattern))))
					{
						return this.Run(body, 0, tested);
					}
				}

				return 0;
		}

		bool Assign(string value)
		{
			var loop = (For)command;

			hooks.Trace($"for {loop.Name} in {loop.Words ?? "\"$@\""}");
			hooks.Assign(loop.Name, value);

			return true;
		}
	}

	/// <summary>
	/// Runs <c>for ((init; test; step))</c>: the init once, then the body while the test isn't 0, with the step after
	/// each round, <c>continue</c> included.
	/// </summary>
	/// <param name="loop">The loop.</param>
	/// <param name="tested"><see langword="true" /> when the loop is tested.</param>
	/// <returns>
	/// The body's last status, or 1 once an expression can't be worked out, which ends the loop.
	/// </returns>
	private int RepeatArithmetic(ArithmeticFor loop, bool tested)
	{
		if (hooks.Arithmetic(loop.Init) is null)
		{
			return 1;
		}

		var first = true;
		var failed = false;

		var status = this.Repeat(tested, () =>
		{
			var test = first || hooks.Arithmetic(loop.Step) is not null ? hooks.Arithmetic(loop.Condition) : null;

			first = false;
			failed = test is null;

			return test is not (null or 0);
		}, loop.Body);

		return failed ? 1 : status;
	}

	/// <summary>
	/// Runs a loop's body for as long as <paramref name="again" /> says, handling <c>break</c> and
	/// <c>continue</c>.
	/// </summary>
	/// <param name="tested"><see langword="true" /> when the loop is tested.</param>
	/// <param name="again">Tests whether to run the body once more.</param>
	/// <param name="body">The body.</param>
	/// <returns>
	/// The body's last status, 0 when it never ran or after <c>break</c>.
	/// </returns>
	/// <remarks>
	/// A <c>break</c> or <c>continue</c> for more loops than this one goes on outward with one fewer.
	/// </remarks>
	private int Repeat(bool tested, Func<bool> again, ListNode body)
	{
		var status = 0;

		hooks.Declare.LoopDepth++;

		try
		{
			while (true)
			{
				// The condition is inside too, since while break; do ...; done leaves the loop, as in bash.
				try
				{
					if (!again())
					{
						break;
					}

					status = this.Run(body, status, tested);
				}
				catch (LoopControl control) when (control.Levels == 1)
				{
					status = 0;

					if (!control.Next)
					{
						break;
					}
				}
				catch (LoopControl control)
				{
					throw new LoopControl(control.Levels - 1, control.Next);
				}
			}
		}
		finally
		{
			hooks.Declare.LoopDepth--;
		}

		return status;
	}
}
