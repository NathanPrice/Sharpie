using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Serilog;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Completion;

/// <content>
/// Generates the words of a spec, as <c>compgen</c> prints them.
/// </content>
internal sealed partial class CompletionEngine
{
	/// <summary>
	/// Generates the words <paramref name="spec" /> gives for <paramref name="word" />.
	/// </summary>
	/// <param name="spec">The spec.</param>
	/// <param name="word">The word typed so far.</param>
	/// <param name="programmed">
	/// Runs the spec's <c>-F</c> function and <c>-C</c> command, for Tab, or <see langword="null" /> to leave them
	/// out, as <c>compgen</c> does here.
	/// </param>
	/// <returns>
	/// The words, in bash's order: the actions, the <c>-G</c> files and the <c>-W</c> words that start with
	/// <paramref name="word" />, then the function's and command's, less those <c>-X</c> drops, each with the
	/// <c>-P</c> prefix and <c>-S</c> suffix.
	/// </returns>
	/// <remarks>
	/// shortcut: the actions <c>binding</c>, <c>disabled</c>, <c>enabled</c>, <c>group</c>, <c>helptopic</c>,
	/// <c>hostname</c>, <c>job</c>, <c>running</c>, <c>service</c>, <c>shopt</c>, <c>signal</c> and <c>stopped</c>
	/// give no words, and <c>compgen</c> doesn't run <c>-F</c> or <c>-C</c>; add them when a script needs them.
	/// </remarks>
	public List<string> Generate(CompletionSpec spec, string word, Func<List<string>>? programmed = null)
	{
		var found = new List<string>();

		foreach (var (_, action) in CompletionSpec.ActionNames.Where(pair => spec.Actions.Contains(pair.Name)))
		{
			found.AddRange(this.ActionWords(action, word));
		}

		if (spec.Glob is { } pattern)
		{
			found.AddRange(Glob.Expand(pattern));
		}

		if (spec.WordList is { } list)
		{
			found.AddRange(this.WordListWords(list).Where(candidate => candidate.StartsWith(word, StringComparison.Ordinal)));
		}

		found.AddRange(programmed?.Invoke() ?? []);

		if (spec.Filter is { Length: > 0 } filter)
		{
			var keep = filter[0] == '!';
			var filterPattern = (keep ? filter[1..] : filter).Replace(@"\&", "\0", StringComparison.Ordinal).Replace("&", word, StringComparison.Ordinal).Replace("\0", "&", StringComparison.Ordinal);

			_ = found.RemoveAll(candidate => Glob.IsMatch(candidate, filterPattern) != keep);
		}

		return [.. found.Select(candidate => spec.Prefix + candidate + spec.Suffix)];
	}

	/// <summary>
	/// Gives the words one action generates for <paramref name="word" />.
	/// </summary>
	/// <param name="action">The action's name.</param>
	/// <param name="word">The word typed so far.</param>
	/// <returns>
	/// The words that start with <paramref name="word" />, in ordinal order. File and folder names come without the
	/// <c>/</c> Tab adds to a folder, as in bash.
	/// </returns>
	private IEnumerable<string> ActionWords(string action, string word)
	{
		var names = action switch
		{
			"alias" => aliases.Keys,
			"arrayvar" => declare.ArrayNames,
			"builtin" => commands.Where(pair => pair.Value is not FunctionCommand).Select(pair => pair.Key),
			"command" => Completions.FindCommands(word, commands.Keys.Concat(aliases.Keys).Concat(CommandParser.ReservedWords)),
			"directory" => Completions.FindFiles(word, declare.Variables).Where(name => name.EndsWith('/')).Select(name => name[..^1]),
			"export" => Environment.GetEnvironmentVariables().Keys.Cast<string>(),
			"file" => Completions.FindFiles(word, declare.Variables).Select(name => name.TrimEnd('/')),
			"function" => commands.Values.OfType<FunctionCommand>().Select(function => function.Name),
			"keyword" => CommandParser.ReservedWords,
			"setopt" => ShellSettings.Options.Select(option => option.Name),
			"user" => OperatingSystem.IsWindows() ? [] : NativeMethods.UserNames(),
			"variable" => declare.Variables.Keys.Concat(declare.ArrayNames).Concat(Environment.GetEnvironmentVariables().Keys.Cast<string>()).Where(DeclareCommand.IsIdentifier),
			_ => [],
		};

		return names.Where(name => name.StartsWith(word, StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal);
	}

	/// <summary>
	/// Splits a <c>-W</c> word list on <c>IFS</c> and expands each word, as bash does.
	/// </summary>
	/// <param name="list">The list as given.</param>
	/// <returns>
	/// The words. A separator inside quotes, or after a backslash, doesn't split, and the quotes go when the word is
	/// expanded. shortcut: a <c>$(...)</c> in a word stays as typed; run it if a script needs that.
	/// </returns>
	private List<string> WordListWords(string list)
	{
		var separators = CommandLine.FieldSeparators(declare.Variables);
		var words = new List<string>();
		var current = new StringBuilder();
		char? quote = null;

		for (var index = 0; index < list.Length; index++)
		{
			var character = list[index];

			if (quote is null && separators.Contains(character, StringComparison.Ordinal))
			{
				Flush();

				continue;
			}

			// A backslash at the very end escapes nothing, and bash drops it.
			if (character == '\\' && quote != '\'' && index + 1 == list.Length)
			{
				break;
			}

			_ = current.Append(character);

			if (character == '\\' && quote != '\'')
			{
				_ = current.Append(list[++index]);
			}
			else if (character is '\'' or '"' && (quote is null || quote == character))
			{
				quote = quote is null ? character : null;
			}
		}

		Flush();

		return words;

		void Flush()
		{
			if (current.Length > 0)
			{
				words.Add(CommandLine.ExpandText(current.ToString(), declare.Variables));
				_ = current.Clear();
			}
		}
	}

	/// <summary>
	/// Runs a <c>complete -C</c> command and waits for it to exit.
	/// </summary>
	/// <param name="script">The command, a program's path or name.</param>
	/// <param name="line">The command's text, passed in <c>COMP_LINE</c>.</param>
	/// <param name="point">Where the cursor is in it, passed in <c>COMP_POINT</c> as a count of UTF-8 bytes.</param>
	/// <param name="command">The command name, passed as the first argument.</param>
	/// <param name="current">The word being completed, passed as the second argument.</param>
	/// <param name="previous">The word before it, passed as the third argument.</param>
	/// <returns>
	/// Each line the program printed, in ordinal order, or none when it couldn't be started.
	/// </returns>
	private static List<string> RunCompleter(string script, string line, int point, string command, string current, string previous)
	{
		try
		{
			var startInfo = new ProcessStartInfo(script, [command, current, previous])
			{
				UseShellExecute = false,

				// All three are redirected. .NET treats a child with any of them on the terminal as one that uses it,
				// and its terminal handling for that child stops the shell ignoring SIGTTOU. The shell is then stopped
				// when it takes the terminal back from the next program, and the next prompt crashes.
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			// Set on the child only, so the shell's own environment never gets these.
			startInfo.Environment["COMP_LINE"] = line;
			startInfo.Environment["COMP_POINT"] = Encoding.UTF8.GetByteCount(line.AsSpan(0, point)).ToString(CultureInfo.InvariantCulture);

			using var process = Process.Start(startInfo);

			if (process is null)
			{
				return [];
			}

			process.StandardInput.Close();

			// Errors are drained and dropped, or a program that writes many could fill the pipe and hang Tab.
			var errors = process.StandardError.ReadToEndAsync();

			// Read to the end before inserting anything, so a slow program can't leave a partial line.
			var output = process.StandardOutput.ReadToEnd();

			errors.Wait();
			process.WaitForExit();

			return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)];
		}
		// A missing or non-executable program must not end the shell, so Tab falls back to the usual completion.
		catch (Win32Exception exception)
		{
			Log.Warning(exception, "Can't start completion command {Script}", script);

			return [];
		}
	}
}
