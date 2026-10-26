using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Serilog;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Aliases;
using Sharpie.Shell.Features.Arithmetic;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell;

/// <summary>
/// The parts of the shell that run compound commands: subshells, copies of the shell, redirects around a compound
/// command, and function calls. Simple commands run through <see cref="Program.RunCommand" />.
/// </summary>
/// <remarks>
/// Kept out of <see cref="Program" /> so that it doesn't add to everything <see cref="Program" /> depends on, which
/// the build caps.
/// </remarks>
internal static class CompoundCommands
{
	/// <summary>
	/// Gets how many command substitutions and subshells are running, one inside another, so <c>exit</c> ends only
	/// the innermost.
	/// </summary>
	/// <value>
	/// 0 in the shell itself.
	/// </value>
	internal static int SubshellDepth { get; private set; }

	/// <summary>
	/// Gets how many command substitutions, <c>eval</c>s and sourced files are running, one inside another, which
	/// <see cref="Trace(DeclareCommand, ShellSettings, string)" /> marks as bash does.
	/// </summary>
	/// <value>
	/// 0 in the shell itself. Subshells and function calls don't raise it.
	/// </value>
	internal static int TraceLevel { get; set; }

	/// <summary>
	/// How many <see cref="RunList" /> calls this thread is inside, so only the outermost one stops an expansion error.
	/// </summary>
	[ThreadStatic]
	private static int lineDepth;

	/// <summary>
	/// Gets where a command's streams go before its own redirects.
	/// </summary>
	/// <value>
	/// <see cref="Redirection.Standard" />, or what the redirects of a compound command or function call around it
	/// set up.
	/// </value>
	internal static StreamTarget[] InheritedTargets { get; private set; } = Redirection.Standard;

	/// <summary>
	/// What the command runner asks the shell to do, made the first time a line runs.
	/// </summary>
	private static ShellHooks? hooks;

	/// <summary>
	/// Runs the commands of one line through <see cref="Hooks" />, as <see cref="Program.RunLine" /> does.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables and the last status.</param>
	/// <param name="line">The line.</param>
	/// <param name="first">The line number the line starts on, for <c>LINENO</c>.</param>
	/// <returns>
	/// The last command's status.
	/// </returns>
	/// <remarks>
	/// A <c>${...}</c> that can't expand skips the rest of the outermost line, through any function, <c>eval</c> or
	/// <c>source</c> it's in, and gives status 1, as in bash. <c>${X:?}</c> with <c>X</c> unset also ends a shell that
	/// isn't interactive, with <see cref="ShellSettings.ExpansionExitStatus" />, through <see cref="Program.Fail" />. A subshell stops at its own boundary in
	/// <see cref="RunIsolated" />.
	/// </remarks>
	internal static int RunList(Dictionary<string, ICommand> commands, AliasCommand aliases, DeclareCommand declare, string line, int first)
	{
		// Kept apart from the field, since an exception filter runs before the inner calls' finally blocks lower it.
		var depth = ++lineDepth;

		try
		{
			return CommandList.Run(line, declare.Status, Hooks(commands, aliases, declare), offset => declare.LineNumber = first + offset);
		}
		catch (CommandLine.ExpansionError failure) when (depth == 1)
		{
			if (failure.Fatal)
			{
				var settings = ((SetCommand)commands["set"]).Settings;

				return Program.Fail(failure.Error, settings.ExpansionExitStatus, settings.Interactive);
			}

			failure.Error.Write();

			return 1;
		}
		finally
		{
			lineDepth--;
		}
	}

	/// <summary>
	/// Gets the hooks for <see cref="CommandList.Run(string, int, ShellHooks, Action{int})" />, making them the first time.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables and functions.</param>
	/// <returns>
	/// The hooks, the same for the whole session.
	/// </returns>
	internal static ShellHooks Hooks(Dictionary<string, ICommand> commands, AliasCommand aliases, DeclareCommand declare)
	{
		var settings = ((SetCommand)commands["set"]).Settings;

		return hooks ??= new ShellHooks((command, tested) =>
		{
			settings.TestDepth += tested ? 1 : 0;

			int status;

			try
			{
				status = declare.Status = Program.RunCommand(commands, aliases, declare, settings, command);
			}
			finally
			{
				settings.TestDepth -= tested ? 1 : 0;
			}

			// A signal that arrives in a subshell waits for the shell itself, as bash's subshell is another process and
			// the shell runs the trap once the subshell ends.
			if (SubshellDepth == 0)
			{
				TrapCommand.RunPending(settings.Interactive, status);
			}

			return status;
		})
		{
			Failed = status =>
			{
				// Inside a subshell set -e ends only the subshell, as it would bash's own.
				if (settings.ErrExit && settings.TestDepth == 0 && SubshellDepth > 0)
				{
					throw new ExitCommand.SubshellExit(status);
				}

				if (settings.ErrExit && settings.TestDepth == 0)
				{
					TrapCommand.Exit(status);
				}
			},
			SetStatus = status => declare.Status = status,
			ExpandWords = words => Expand(words, unset => CommandLine.Split(words, declare.Variables, unset, Substitution).Where(word => !word.IsOperator).Select(word => word.Text).ToArray()),
			ExpandWord = word => Expand(word, unset => CommandLine.ExpandText(word, declare.Variables, unset, Substitution)),
			Pattern = pattern => Expand(pattern, unset => CommandLine.Pattern(pattern, declare.Variables, unset, Substitution)),
			Arithmetic = expression =>
			{
				var expanded = Expand(expression, unset => CommandLine.ExpandExpression(expression, declare.Variables, unset, Substitution));

				Trace(declare, settings, $"(( {expanded} ))");

				try
				{
					return ArithmeticExpression.Evaluate(expanded, declare.Variables, settings.NoUnset);
				}
				catch (ArithmeticExpression.Failure failure)
				{
					failure.Error.Write();

					return null;
				}
			},
			Assign = declare.Set,
			Positional = () => declare.Positional,
			Trace = text => Trace(declare, settings, text),
			Subshell = body => RunIsolated(commands, aliases, declare, settings, body),
			Redirected = (redirects, body) => Redirected(commands, aliases, declare, settings, redirects, body),
			// One SHLVL down, since the copy raises it again and bash's subshell stays on the same level.
			CopyCommand = (text, tested) => $"SHLVL={DeclareCommand.ShellLevel - 1} " + string.Join(' ', ((string[])[Environment.ProcessPath!, .. CopyArguments(commands, aliases, declare, settings, text, tested)]).Select(CommandLine.Quote)),
			Define = definition => BuiltinCommands.DefineFunction(commands, definition, declare, body => Program.RunLine(commands, aliases, declare, body)),
			Declare = declare,
		};

		string Substitution(string list) => Substitute(commands, aliases, declare, settings, list).Output;

		// Expands a for loop's words or a case word or pattern, reporting a variable set -u finds unset.
		T Expand<T>(string typed, Func<Action<Word>?, T> expand)
		{
			Word? unset = null;
			var result = expand(settings.NoUnset ? name => unset ??= name : null);

			if (unset is { } missing)
			{
				_ = Program.ReportUnset(typed, missing, settings);
			}

			return result;
		}
	}

	/// <summary>
	/// Runs <paramref name="run" /> as a subshell, then puts back the folder, the shell variables, locals and positional
	/// parameters, <c>$!</c>, <c>RANDOM</c> and <c>SECONDS</c>, the functions, the environment, the aliases, the
	/// <c>set</c> options, the <c>pushd</c> stack and the umask, as bash's subshell would leave them.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables and functions.</param>
	/// <param name="settings">The <c>set</c> options.</param>
	/// <param name="run">What to run, which gives a status.</param>
	/// <param name="output">
	/// Where the subshell's <c>EXIT</c> trap writes, or <see langword="null" /> for where commands write now.
	/// </param>
	/// <returns>
	/// The status. An <c>exit</c>, or a failure under <c>set -e</c>, ends only the subshell, with its own status, and
	/// so do <c>break</c> and <c>continue</c>, and <c>return</c>, whose signal is an <c>exit</c>'s. An <c>EXIT</c> trap
	/// the subshell set runs at its end, and an <c>exit</c> inside it sets the status, as in bash. The traps go back to
	/// the shell's after.
	/// </returns>
	/// <remarks>
	/// The subshell runs inside the shell's own process rather than a forked one, which .NET can't do safely, so
	/// whatever it changes is undone here instead. shortcut: <c>ulimit</c> limits aren't put back, and a lowered hard
	/// limit can't be; save and restore them here if a subshell that changes one is ever needed.
	/// </remarks>
	internal static int RunIsolated(Dictionary<string, ICommand> commands, AliasCommand aliases, DeclareCommand declare, ShellSettings settings, Func<int> run, Stream? output = null)
	{
		var traps = TrapCommand.Save();
		var folder = Directory.GetCurrentDirectory();
		var variables = declare.Variables.ToDictionary();
		var restoreDynamic = declare.SaveDynamic();
		var locals = declare.LocalCount;
		var sets = declare.PositionalSetCount;
		var functions = commands.ToDictionary();
		var aliasValues = aliases.Aliases.ToDictionary();
		var environment = Environment.GetEnvironmentVariables();
		var options = ShellSettings.Options.Where(option => settings.IsOn(option.Name)).Select(option => option.Name).ToHashSet();
		var stack = ((DirsCommand)commands["dirs"]).Stack;
		var folders = stack.ToArray();

		// Read by setting it, the only way POSIX offers, and put straight back.
		var mask = OperatingSystem.IsWindows() ? 0 : NativeMethods.SetFileMask(0);

		if (!OperatingSystem.IsWindows())
		{
			_ = NativeMethods.SetFileMask(mask);
		}

		SubshellDepth++;

		try
		{
			var status = Guard(run);

			return TrapCommand.TakeSubshellExitTrap(traps) is { } exitTrap
				? Guard(() => Program.WithOutput(output, () => exitTrap(status)))
				: status;
		}
		finally
		{
			TrapCommand.Restore(traps);

			try
			{
				Directory.SetCurrentDirectory(folder);
			}
			// The subshell removed the folder or its access, and the shell stays where the subshell left it.
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				Log.Debug(exception, "Can't go back to {Folder} after a subshell", folder);
			}

			restoreDynamic();
			declare.Restore(variables, locals);
			declare.PositionalSetCount = sets;
			commands.Clear();

			foreach (var (name, command) in functions)
			{
				commands[name] = command;
			}

			aliases.Restore(aliasValues);
			stack.Clear();
			stack.AddRange(folders);

			foreach (var (name, _) in ShellSettings.Options)
			{
				_ = settings.Set(name, options.Contains(name));
			}

			if (!OperatingSystem.IsWindows())
			{
				_ = NativeMethods.SetFileMask(mask);
			}

			foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
			{
				if (!environment.Contains(entry.Key))
				{
					Environment.SetEnvironmentVariable((string)entry.Key, null);
				}
			}

			// Only the changed ones, so a variable the subshell didn't touch is never written again, an empty one included.
			foreach (DictionaryEntry entry in environment)
			{
				if (Environment.GetEnvironmentVariable((string)entry.Key) != (string?)entry.Value)
				{
					Environment.SetEnvironmentVariable((string)entry.Key, (string?)entry.Value);
				}
			}

			SubshellDepth--;
		}

		static int Guard(Func<int> run)
		{
			try
			{
				return run();
			}
			catch (ExitCommand.SubshellExit exit)
			{
				return exit.Status;
			}
			catch (CommandLine.ExpansionError failure)
			{
				// Even ${X:?} ends only the subshell, with 1, as bash's does.
				failure.Error.Write();

				return 1;
			}
			catch (LoopControl)
			{
				return 0;
			}
		}
	}

	/// <summary>
	/// Runs the command list of a <c>$(...)</c> or backquotes as a subshell, with its output captured.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables and the last status.</param>
	/// <param name="settings">The <c>set</c> options. <c>set -e</c> starts off inside, as bash doesn't pass it on.</param>
	/// <param name="list">The text inside the substitution.</param>
	/// <returns>
	/// What the list wrote to standard output, decoded as UTF-8, and its status.
	/// </returns>
	internal static (string Output, int Status) Substitute(Dictionary<string, ICommand> commands, AliasCommand aliases, DeclareCommand declare, ShellSettings settings, string list)
	{
		using var buffer = new MemoryStream();

		// Background programs the list starts copy into it from other threads.
		var output = Stream.Synchronized(buffer);

		var status = RunIsolated(
			commands,
			aliases,
			declare,
			settings,
			() =>
			{
				_ = settings.Set("errexit", false);

				return Nested(() => Program.RunLine(commands, aliases, declare, list, output));
			},
			output);

		ExternalProgram.WaitForDetached(output);

		// The rest of the command sees the substitution's status as $?, as in bash.
		declare.Status = status;

		return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), status);
	}

	/// <summary>
	/// Gets the arguments that run <paramref name="text" /> in a copy of the shell.
	/// </summary>
	/// <param name="commands">The builtins and functions, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
	/// <param name="settings">The <c>set</c> options.</param>
	/// <param name="text">The commands the copy runs.</param>
	/// <param name="tested">
	/// <see langword="true" /> when the copy runs where <c>set -e</c> is ignored, as a stage of an <c>if</c>'s
	/// condition, which then leaves <c>set -e</c> off in the copy too, as bash ignores it in a subshell there.
	/// </param>
	/// <returns>
	/// <c>-C</c> while <c>set -C</c> is on, then <c>-c</c> with the text, then <c>$0</c> and the positional
	/// parameters.
	/// </returns>
	/// <remarks>
	/// The text starts with the shell's own state, so the copy has what a bash subshell would: the functions, the
	/// variables that aren't exported, the aliases, and the other <c>set</c> options, last so <c>set -x</c> doesn't
	/// show the rest. The exported variables and the folder come with the process.
	/// </remarks>
	internal static string[] CopyArguments(Dictionary<string, ICommand> commands, AliasCommand aliases, DeclareCommand declare, ShellSettings settings, string text, bool tested)
	{
		var state = new StringBuilder();

		foreach (var function in commands.Values.OfType<FunctionCommand>())
		{
			_ = state.Append(function.Name).Append("() ").Append(function.Body).Append('\n');
		}

		foreach (var (name, value) in declare.Variables.Where(variable => DeclareCommand.IsIdentifier(variable.Key) && Environment.GetEnvironmentVariable(variable.Key) is null))
		{
			_ = state.Append(name).Append('=').Append(CommandLine.Quote(value)).Append('\n');
		}

		foreach (var (name, value) in aliases.Aliases)
		{
			_ = state.Append(AliasCommand.Format(name, value)).Append('\n');
		}

		var ignoreErrExit = tested || settings.TestDepth > 0;

		foreach (var (name, _) in ShellSettings.Options.Where(option => option.Name != "noclobber" && settings.IsOn(option.Name) && !(ignoreErrExit && option.Name == "errexit")))
		{
			_ = state.Append("set -o ").Append(name).Append('\n');
		}

		string[] noClobber = settings.NoClobber ? ["-C"] : [];

		return [.. noClobber, "-c", state + text, CommandLine.Lookup(declare.Variables, "0"), .. declare.Positional];
	}

	/// <summary>
	/// Runs a compound command with the redirects typed after it, on top of the ones around it.
	/// </summary>
	/// <param name="commands">The builtins, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
	/// <param name="settings">The <c>set</c> options.</param>
	/// <param name="redirects">The redirects as typed, with their here-document bodies.</param>
	/// <param name="body">Runs the command.</param>
	/// <returns>
	/// The command's status, 1 when a file can't be opened, or <see cref="CommandList.UnsupportedStatus" /> for a
	/// redirect with nothing usable after it. The command doesn't run in either case.
	/// </returns>
	private static int Redirected(Dictionary<string, ICommand> commands, AliasCommand aliases, DeclareCommand declare, ShellSettings settings, Leaf redirects, Func<int> body)
	{
		var text = redirects.Text;
		Word? unset = null;
		var split = CommandLine.Split(text, declare.Variables, settings.NoUnset ? name => unset ??= name : null, list => Substitute(commands, aliases, declare, settings, list).Output);

		if (unset is { } missing)
		{
			return Program.ReportUnset(text, missing, settings);
		}

		var (words, list) = CommandLine.SplitRedirect(split);

		if (words is [var unusable, ..])
		{
			Program.ReportUnusable(text, unusable);

			return CommandList.UnsupportedStatus;
		}

		using var opened = Redirection.Open(list, text, settings.NoClobber, InheritedTargets);

		return opened is null ? 1 : WithTargets(opened.Targets, body);
	}

	/// <summary>
	/// Runs <paramref name="body" /> with <paramref name="targets" /> as where every command inside sends its streams
	/// before its own redirects.
	/// </summary>
	/// <param name="targets">The streams.</param>
	/// <param name="body">What to run.</param>
	/// <returns>
	/// The status <paramref name="body" /> gives.
	/// </returns>
	private static int WithTargets(StreamTarget[] targets, Func<int> body)
	{
		var outer = InheritedTargets;

		InheritedTargets = targets;

		try
		{
			return Program.WithStreams(targets, Program.StandardOutput, body, autoFlush: true);
		}
		finally
		{
			InheritedTargets = outer;
		}
	}

	/// <summary>
	/// Gets where one stage of a command's streams go before its own redirects.
	/// </summary>
	/// <param name="stage">The stage's place in the pipeline.</param>
	/// <param name="count">How many stages the pipeline has.</param>
	/// <returns>
	/// <see cref="InheritedTargets" />, except that a stage after the first reads the pipe and one before the last
	/// writes to it.
	/// </returns>
	internal static StreamTarget[] StageTargets(int stage, int count)
	{
		StreamTarget[] targets = [.. InheritedTargets];

		targets[0] = stage > 0 ? StreamTarget.Own(0) : targets[0];
		targets[1] = stage < count - 1 ? StreamTarget.Own(1) : targets[1];

		return targets;
	}

	/// <summary>
	/// Opens the redirects of each stage of a command before anything runs, so a redirect that fails runs nothing.
	/// </summary>
	/// <param name="parts">The stages, with their redirects taken out.</param>
	/// <param name="line">The command as typed, which an error report points into.</param>
	/// <param name="noClobber"><see langword="true" /> while <c>set -C</c> is on.</param>
	/// <param name="redirections">Gets each stage's opened redirects, which the caller disposes.</param>
	/// <returns>
	/// <see langword="false" /> after reporting the first redirect that failed.
	/// </returns>
	[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each opened Redirection goes into the list, which the caller disposes.")]
	internal static bool OpenStages((Word[] Words, Redirect[] Redirects)[] parts, string line, bool noClobber, List<Redirection> redirections)
	{
		for (var index = 0; index < parts.Length; index++)
		{
			if (Redirection.Open(parts[index].Redirects, line, noClobber, StageTargets(index, parts.Length)) is not { } opened)
			{
				return false;
			}

			redirections.Add(opened);
		}

		return true;
	}

	/// <summary>
	/// Replaces each stage of a pipeline that calls a function, or holds only <c>NAME=VALUE</c> words, with one that
	/// runs it in a copy of the shell, as bash runs every stage in a subshell.
	/// </summary>
	/// <param name="commands">The builtins and functions, by name.</param>
	/// <param name="aliases">The <c>alias</c> builtin.</param>
	/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
	/// <param name="settings">The <c>set</c> options.</param>
	/// <param name="stages">Each stage's command name and arguments.</param>
	/// <param name="copied">Whether each stage runs in a copy.</param>
	/// <returns>
	/// The stages, the copied ones now running the shell itself.
	/// </returns>
	internal static string[][] CopyStages(Dictionary<string, ICommand> commands, AliasCommand aliases, DeclareCommand declare, ShellSettings settings, string[][] stages, bool[] copied) =>
		[.. stages.Select((stage, index) => copied[index]
			? [Environment.ProcessPath!, .. CopyArguments(commands, aliases, declare, settings, string.Join(' ', stage.Select(CommandLine.Quote)), tested: false)]
			: stage)];
	/// <summary>
	/// Runs <paramref name="run" /> one <see cref="TraceLevel" /> deeper, for a command substitution, <c>eval</c> or a
	/// sourced file.
	/// </summary>
	/// <param name="run">What to run, which gives a status.</param>
	/// <returns>
	/// The status <paramref name="run" /> gives.
	/// </returns>
	internal static int Nested(Func<int> run)
	{
		TraceLevel++;

		try
		{
			return run();
		}
		finally
		{
			TraceLevel--;
		}
	}

	/// <summary>
	/// Prints each stage of a command to standard error before it runs, when <c>set -x</c> is on.
	/// </summary>
	/// <param name="declare">The <c>declare</c> builtin, which holds <c>PS4</c>.</param>
	/// <param name="settings">The <c>set</c> options.</param>
	/// <param name="stages">The stages, each starting with its <c>NAME=VALUE</c> words.</param>
	/// <remarks>
	/// Each <c>NAME=VALUE</c> word gets a line of its own, then the command, with the words quoted only where needed,
	/// the way bash prints them. Redirects aren't shown.
	/// </remarks>
	internal static void Trace(DeclareCommand declare, ShellSettings settings, IEnumerable<string[]> stages)
	{
		if (!settings.XTrace)
		{
			return;
		}

		foreach (var stage in stages)
		{
			var count = stage.TakeWhile(DeclareCommand.IsAssignment).Count();

			foreach (var parts in stage[..count].Select(assignment => assignment.Split('=', 2)))
			{
				// bash leaves an empty value as NAME= here, though an empty argument shows as ''.
				Trace(declare, settings, $"{parts[0]}={(parts[1].Length == 0 ? string.Empty : CommandLine.QuoteIfNeeded(parts[1]))}");
			}

			if (count < stage.Length)
			{
				Trace(declare, settings, string.Join(' ', stage[count..].Select(CommandLine.QuoteIfNeeded)));
			}
		}
	}

	/// <summary>
	/// Prints one line to standard error after <c>PS4</c>, when <c>set -x</c> is on.
	/// </summary>
	/// <param name="declare">The <c>declare</c> builtin, which holds <c>PS4</c>.</param>
	/// <param name="settings">The <c>set</c> options.</param>
	/// <param name="text">What runs, as it's shown.</param>
	/// <remarks>
	/// <c>PS4</c> is <c>+ </c> when it isn't set, and its variables and <c>$((...))</c> are expanded each time. Its first
	/// character is repeated once more for each <see cref="TraceLevel" />, so a line inside <c>$(...)</c> starts with
	/// <c>++ </c>, as in bash. shortcut: a <c>$(...)</c> in <c>PS4</c> isn't run, <c>unset PS4</c> still gives
	/// <c>+ </c> where bash shows nothing, and a copy of the shell starts back at level 0; run the substitution and pass
	/// the level on if a script needs them.
	/// </remarks>
	internal static void Trace(DeclareCommand declare, ShellSettings settings, string text)
	{
		if (!settings.XTrace)
		{
			return;
		}

		var prefix = CommandLine.ExpandBody(declare.Variables.GetValueOrDefault("PS4", "+ "), declare.Variables);

		Console.Error.WriteLine(prefix.Length == 0 ? text : new string(prefix[0], TraceLevel) + prefix + text);
	}
}
