namespace Sharpie.Shell.Common;

/// <summary>
/// One command of the tree <see cref="CommandParser" /> builds.
/// </summary>
/// <param name="Start">Where the command starts in the parsed text.</param>
/// <param name="End">Where it ends, one past its last character, its redirects included.</param>
internal abstract record CommandNode(int Start, int End)
{
	/// <summary>
	/// Gets the command as typed.
	/// </summary>
	/// <value>
	/// The text from <see cref="Start" /> to <see cref="End" />, followed by the bodies of its here-documents that come
	/// after it, so a copy of the shell can run the text on its own.
	/// </value>
	public string Text { get; init; } = string.Empty;
}

/// <summary>
/// A pipeline of simple commands, run as one by <c>Program.RunCommand</c>.
/// </summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Line">The commands as typed, without their here-document bodies.</param>
/// <param name="Bodies">
/// The bodies of the here-documents it starts, each after a newline and with its delimiter line, as
/// <see cref="CommandLine.Split" /> reads them after the first line; empty when there are none.
/// </param>
internal sealed record Leaf(int Start, int End, string Line, string Bodies) : CommandNode(Start, End);

/// <summary>
/// Commands that run one after another, joined by <c>;</c>, <c>&amp;</c> or newlines.
/// </summary>
/// <param name="Items">The commands in order.</param>
internal sealed record ListNode(IReadOnlyList<ListItem> Items);

/// <summary>
/// One command of a <see cref="ListNode" />.
/// </summary>
/// <param name="Command">The and-or list.</param>
/// <param name="Background"><see langword="true" /> when an <c>&amp;</c> ends it.</param>
/// <param name="Text">
/// For a background command that is one simple pipeline, its text up to and including the <c>&amp;</c>, which
/// <c>Program.RunCommand</c> runs as a job. Otherwise the and-or list's own text.
/// </param>
internal sealed record ListItem(AndOr Command, bool Background, string Text);

/// <summary>
/// Pipelines joined by <c>&amp;&amp;</c> and <c>||</c>, read from the left with equal precedence.
/// </summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="First">The first pipeline.</param>
/// <param name="Rest">Each later pipeline with the operator before it.</param>
internal sealed record AndOr(int Start, int End, Pipeline First, IReadOnlyList<(string Operator, Pipeline Pipeline)> Rest) : CommandNode(Start, End);

/// <summary>
/// Commands joined by <c>|</c>, each reading the one before.
/// </summary>
/// <param name="Start">Where it starts, at the <c>!</c> when there is one.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Negated"><see langword="true" /> after <c>!</c>, which flips the status between 0 and 1.</param>
/// <param name="Stages">
/// The commands. A pipeline of only simple commands is a single <see cref="Leaf" />, so it runs as it always has.
/// </param>
/// <param name="Pipes">The operator between each two stages, <c>|</c> or <c>|&amp;</c>, as typed.</param>
internal sealed record Pipeline(int Start, int End, bool Negated, IReadOnlyList<CommandNode> Stages, IReadOnlyList<string> Pipes) : CommandNode(Start, End);

/// <summary>
/// A compound command, which may have redirects of its own after it.
/// </summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends, its redirects included.</param>
internal abstract record Compound(int Start, int End) : CommandNode(Start, End)
{
	/// <summary>
	/// Gets the redirects typed after the command, which apply to everything inside it.
	/// </summary>
	/// <value>
	/// The redirects as a leaf, which also holds the bodies of their here-documents, or <see langword="null" />.
	/// </value>
	public Leaf? Redirects { get; init; }
}

/// <summary>
/// <c>{ list; }</c>, which runs the list in the shell itself.
/// </summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Body">The list.</param>
internal sealed record Group(int Start, int End, ListNode Body) : Compound(Start, End);

/// <summary>
/// <c>( list )</c>, which runs the list in a subshell, so what it changes is undone after.
/// </summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Body">The list.</param>
internal sealed record Subshell(int Start, int End, ListNode Body) : Compound(Start, End);

/// <summary>
/// <c>if</c> with its <c>elif</c> branches and <c>else</c>.
/// </summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Branches">The <c>if</c> and each <c>elif</c>, as the list tested and the list run when it succeeds.</param>
/// <param name="Else">The <c>else</c> list, or <see langword="null" />.</param>
internal sealed record If(int Start, int End, IReadOnlyList<(ListNode Condition, ListNode Body)> Branches, ListNode? Else) : Compound(Start, End);

/// <summary>
/// <c>while</c> or <c>until</c>.
/// </summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Condition">The list tested before each round.</param>
/// <param name="Body">The list run each round.</param>
/// <param name="Until"><see langword="true" /> for <c>until</c>, which runs while the condition fails.</param>
internal sealed record Loop(int Start, int End, ListNode Condition, ListNode Body, bool Until) : Compound(Start, End);

/// <summary>
/// <c>for name in words; do list; done</c>.
/// </summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Name">The variable each word is put in.</param>
/// <param name="Words">
/// The words after <c>in</c> as typed, expanded when the loop starts, or <see langword="null" /> without <c>in</c>,
/// which loops over the positional parameters.
/// </param>
/// <param name="Body">The list run for each word.</param>
internal sealed record For(int Start, int End, string Name, string? Words, ListNode Body) : Compound(Start, End);

/// <summary>
/// <c>case word in patterns) list;; esac</c>.
/// </summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Word">The word matched, as typed.</param>
/// <param name="Items">Each item's patterns as typed, and the list run when one matches.</param>
internal sealed record Case(int Start, int End, string Word, IReadOnlyList<(IReadOnlyList<string> Patterns, ListNode Body)> Items) : Compound(Start, End);

/// <summary>
/// <c>name() compound</c>, which defines a function.
/// </summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Name">The function's name.</param>
/// <param name="Body">The command a call runs, with its own redirects.</param>
/// <remarks>
/// <see cref="CommandNode.Text" /> is the definition as typed, which a copy of the shell runs to get the function.
/// </remarks>
internal sealed record FunctionDefinition(int Start, int End, string Name, Compound Body) : CommandNode(Start, End);
