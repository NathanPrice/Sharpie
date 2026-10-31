using System.Globalization;
using System.Text;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Jobs;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Completion;

/// <summary>
/// Keeps the completion specs <c>complete</c> sets, and runs one for Tab, as bash's programmable completion does.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which holds the variables a completion function reads and sets.</param>
/// <param name="commands">The builtins and functions, by name.</param>
/// <param name="aliases">The aliases, by name.</param>
/// <param name="runLine">Runs a command line in the shell itself and gives its status.</param>
/// <remarks>
/// A command with no spec gets one by loading its bash-completion script the first time Tab completes it, from the
/// folders bash-completion looks in. bash-completion's own main file isn't loaded, so a script that needs its
/// helpers fails, and Tab then falls back to the usual completion. git's and dotnet's scripts need none.
/// </remarks>
internal sealed partial class CompletionEngine(DeclareCommand declare, IReadOnlyDictionary<string, ICommand> commands, IReadOnlyDictionary<string, string> aliases, Func<string, int> runLine)
{
	/// <summary>
	/// The status a completion function gives after it loaded a new spec, asking for the lookup to run again.
	/// </summary>
	private const int Retry = 124;

	/// <summary>
	/// The variables bash sets for a completion function and unsets after.
	/// </summary>
	private static readonly string[] Variables = ["COMP_WORDS", "COMP_CWORD", "COMP_LINE", "COMP_POINT", "COMP_TYPE", "COMP_KEY", "COMPREPLY"];

	private readonly Dictionary<string, CompletionSpec> specs = new(StringComparer.Ordinal);

	/// <summary>
	/// The commands whose bash-completion script was already looked for, found or not.
	/// </summary>
	private readonly HashSet<string> searched = new(StringComparer.Ordinal);

	/// <summary>
	/// Gets or sets the spec <c>complete -D</c> sets, for a command that has none.
	/// </summary>
	/// <value>
	/// The spec, or <see langword="null" />.
	/// </value>
	public CompletionSpec? Default { get; set; }

	/// <summary>
	/// Gets the options of the completion running now, which <c>compopt</c> with no name changes.
	/// </summary>
	/// <value>
	/// The options, or <see langword="null" /> outside a completion function.
	/// </value>
	public HashSet<string>? Running { get; private set; }

	/// <summary>
	/// Gets the specs, by command name.
	/// </summary>
	/// <value>
	/// The specs, which <c>complete -p</c> lists.
	/// </value>
	public IReadOnlyDictionary<string, CompletionSpec> Specs => this.specs;

	/// <summary>
	/// Sets the spec for <paramref name="name" />, in place of any it had.
	/// </summary>
	/// <param name="name">The command name.</param>
	/// <param name="spec">The spec.</param>
	public void Set(string name, CompletionSpec spec) => this.specs[name] = spec;

	/// <summary>
	/// Removes the spec for <paramref name="name" />.
	/// </summary>
	/// <param name="name">The command name.</param>
	/// <returns>
	/// <see langword="true" /> when it had one.
	/// </returns>
	public bool Remove(string name) => this.specs.Remove(name);

	/// <summary>
	/// Removes every spec, as <c>complete -r</c> with no names does.
	/// </summary>
	public void Clear() => this.specs.Clear();

	/// <summary>
	/// Runs the completion for the word at <paramref name="cursor" />, when its command has a spec.
	/// </summary>
	/// <param name="line">The whole line.</param>
	/// <param name="cursor">Where the cursor is.</param>
	/// <param name="listing">
	/// <see langword="true" /> for the Tab that lists the matches, which bash tells the function through
	/// <c>COMP_TYPE</c>.
	/// </param>
	/// <returns>
	/// The words and the options they're inserted with, or <see langword="null" /> when the cursor is on the command's
	/// name or the command has no spec.
	/// </returns>
	/// <remarks>
	/// A function that gives 124 after setting a new spec for the command is run again with it once, as bash does
	/// for a function that loads a completion script.
	/// </remarks>
	public CompletionResult? Complete(string line, int cursor, bool listing)
	{
		if (CompletionWords.ForCommand(line, cursor, Breaks(declare.Variables)) is not { } command)
		{
			return null;
		}

		for (var attempt = 0; ; attempt++)
		{
			if (this.Find(command.Name) is not { } spec)
			{
				return null;
			}

			var (result, status) = this.Run(spec, command, line, cursor, listing);

			if (status != Retry || attempt > 0 || ReferenceEquals(this.Find(command.Name), spec))
			{
				return result;
			}
		}
	}

	/// <summary>
	/// Finds the spec for a command, loading its bash-completion script the first time.
	/// </summary>
	/// <param name="name">The command's name as typed, which may be a path.</param>
	/// <returns>
	/// The spec for the name, else for its last part, else the <c>-D</c> one, or <see langword="null" />.
	/// </returns>
	public CompletionSpec? Find(string name)
	{
		var program = Path.GetFileName(name);

		if (!this.specs.ContainsKey(name) && !this.specs.ContainsKey(program))
		{
			this.Load(program);
		}

		return this.specs.GetValueOrDefault(name) ?? this.specs.GetValueOrDefault(program) ?? this.Default;
	}

	/// <summary>
	/// Gets the word-break characters in use.
	/// </summary>
	/// <param name="variables">The shell variables.</param>
	/// <returns>
	/// <c>COMP_WORDBREAKS</c>, or bash's default when it isn't set.
	/// </returns>
	internal static string Breaks(IReadOnlyDictionary<string, string> variables) =>
		variables.TryGetValue("COMP_WORDBREAKS", out var breaks) ? breaks : CompletionWords.DefaultBreaks;

	/// <summary>
	/// Runs one spec for the word the cursor is in.
	/// </summary>
	/// <param name="spec">The spec.</param>
	/// <param name="command">The command's words.</param>
	/// <param name="line">The whole line.</param>
	/// <param name="cursor">Where the cursor is.</param>
	/// <param name="listing"><see langword="true" /> for the Tab that lists.</param>
	/// <returns>
	/// The result, and the function's status, or 0 with none.
	/// </returns>
	/// <remarks>
	/// The function runs in the shell itself, with job control off, so the programs it starts, like <c>git</c>, stay in
	/// the shell's process group and never take the terminal from the line editor. Its input, output and errors go
	/// to the null device, and <c>$?</c> is put back after.
	/// </remarks>
	private (CompletionResult Result, int Status) Run(CompletionSpec spec, CommandWords command, string line, int cursor, bool listing)
	{
		var current = command.Words[command.Current];
		var word = CompletionWords.Split(line[..cursor], Breaks(declare.Variables))[^1];
		var typed = line[word.ReplaceFrom..cursor];
		var previous = command.Words[command.Current - 1];
		var status = 0;
		var saved = declare.Status;
		var jobs = JobControl.Enabled;

		this.Running = [.. spec.Options];

		try
		{
			DeclareCommand.AssignArray(declare.Variables, "COMP_WORDS", command.Words);
			declare.Set("COMP_CWORD", command.Current.ToString(CultureInfo.InvariantCulture));
			declare.Set("COMP_LINE", line[command.Start..command.End]);
			declare.Set("COMP_POINT", Encoding.UTF8.GetByteCount(line.AsSpan(command.Start, cursor - command.Start)).ToString(CultureInfo.InvariantCulture));
			declare.Set("COMP_TYPE", listing ? "63" : "9");
			declare.Set("COMP_KEY", "9");
			declare.Remove("COMPREPLY");
			JobControl.Enabled = false;

			var matches = this.Generate(spec, typed, () =>
			{
				var found = new List<string>();

				if (spec.Function is { } function)
				{
					status = runLine($"{CommandLine.Quote(function)} {CommandLine.Quote(command.Words[0])} {CommandLine.Quote(typed)} {CommandLine.Quote(previous)} </dev/null >/dev/null 2>/dev/null");
					found.AddRange(DeclareCommand.Elements(declare.Variables, "COMPREPLY").Select(element => element.Value));
				}

				if (spec.Command is { } script)
				{
					found.AddRange(RunCompleter(script, line[command.Start..command.End], cursor - command.Start, command.Words[0], typed, previous));
				}

				return found;
			});

			return (new CompletionResult(matches, this.Running, Failed: matches.Count == 0 && status != 0), status);
		}
		finally
		{
			JobControl.Enabled = jobs;

			foreach (var name in Variables)
			{
				declare.Remove(name);
			}

			declare.Status = saved;
			this.Running = null;
		}
	}

	/// <summary>
	/// Sources the bash-completion script for <paramref name="name" />, the first time it's asked for.
	/// </summary>
	/// <param name="name">The command name, without a folder.</param>
	/// <remarks>
	/// Each folder is tried for <c>NAME.bash</c>, <c>NAME</c> and <c>_NAME</c>, in bash-completion's order, and the
	/// first found is sourced with its output dropped. A name with no script is remembered, so it's only looked for
	/// once.
	/// </remarks>
	private void Load(string name)
	{
		if (!this.searched.Add(name))
		{
			return;
		}

		var script = ScriptFolders(declare.Variables)
			.SelectMany(folder => new[] { $"{name}.bash", name, $"_{name}" }.Select(file => Path.Combine(folder, file)))
			.FirstOrDefault(File.Exists);

		if (script is not null)
		{
			_ = runLine($"source {CommandLine.Quote(script)} </dev/null >/dev/null 2>/dev/null");
		}
	}

	/// <summary>
	/// Lists the folders bash-completion loads scripts from, in its order.
	/// </summary>
	/// <param name="variables">The shell variables.</param>
	/// <returns>
	/// <c>$BASH_COMPLETION_USER_DIR/completions</c>, or <c>$XDG_DATA_HOME/bash-completion/completions</c>, then
	/// <c>../share/bash-completion/completions</c> beside each <c>PATH</c> folder, then
	/// <c>bash-completion/completions</c> in each of <c>$XDG_DATA_DIRS</c>, each once.
	/// </returns>
	internal static IEnumerable<string> ScriptFolders(IReadOnlyDictionary<string, string> variables)
	{
		var home = Lookup("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var user = Lookup("BASH_COMPLETION_USER_DIR") ?? Path.Combine(Lookup("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"), "bash-completion");
		var beside = (Lookup("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Select(folder => Path.GetFullPath(Path.Combine(folder, "..", "share", "bash-completion", "completions")));
		var data = (Lookup("XDG_DATA_DIRS") is { Length: > 0 } dirs ? dirs : "/usr/local/share:/usr/share")
			.Split(':', StringSplitOptions.RemoveEmptyEntries)
			.Select(folder => Path.Combine(folder, "bash-completion", "completions"));

		return new[] { Path.Combine(user, "completions") }.Concat(beside).Concat(data).Distinct(StringComparer.Ordinal);

		string? Lookup(string name) => variables.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name);
	}
}

/// <summary>
/// What a completion spec gave for one Tab.
/// </summary>
/// <param name="Matches">The words, each the whole new text of the word being completed.</param>
/// <param name="Options">The <c>-o</c> options, as the spec set them and <c>compopt</c> changed them.</param>
/// <param name="Failed">
/// <see langword="true" /> when the function failed and gave no words, as one that needs bash-completion's helpers
/// does, so Tab falls back to the usual completion.
/// </param>
internal sealed record CompletionResult(IReadOnlyList<string> Matches, IReadOnlySet<string> Options, bool Failed);
