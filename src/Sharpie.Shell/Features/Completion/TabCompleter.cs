using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DotNetTips.Spargine.Core;
using Serilog;

namespace Sharpie.Shell.Features.Completion;

/// <summary>
/// Decides what each Tab press does to the line being typed, following bash: complete when possible, ring the bell,
/// list the matches on the next Tab, then cycle through them.
/// </summary>
/// <param name="names">The builtin command names. Executables on <c>PATH</c> are added to these.</param>
/// <param name="findCompleter">
/// Gives the completer script registered with <c>complete -C</c> for a command name, or <see langword="null" />. Left
/// out by callers that have no <c>complete</c> registrations, like tests.
/// </param>
/// <param name="variables">
/// The shell variables, for completing <c>$NAME</c>, expanding a <c>$NAME/</c> folder and reading
/// <c>COMP_WORDBREAKS</c>. Left out by callers that have none, like tests, which then see only the environment.
/// </param>
internal sealed class TabCompleter(IReadOnlyCollection<string> names, Func<string, string?>? findCompleter = null, IReadOnlyDictionary<string, string>? variables = null)
{
	private readonly IReadOnlyCollection<string> names = names.ArgumentNotNull();
	private readonly IReadOnlyDictionary<string, string> variables = variables ?? new Dictionary<string, string>();

	private bool tabbedOnce;
	private IReadOnlyList<string>? cycle;
	private List<string> inserts = [];
	private string head = "";
	private string tail = "";
	private int next;

	/// <summary>
	/// Works out the effect of one Tab press on <paramref name="line" />.
	/// </summary>
	/// <param name="line">The text typed so far.</param>
	/// <param name="cursor">
	/// Where the cursor is. Tab completes the word that ends there and keeps the text after it.
	/// </param>
	/// <returns>
	/// The new line and cursor, plus the matches to print above it when this press lists them, and whether to ring the
	/// bell.
	/// </returns>
	public TabResult Press(string line, int cursor)
	{
		_ = line.ArgumentNotNull();

		if (this.cycle is not null)
		{
			var selected = this.next;
			var text = this.inserts[selected];

			this.next = (this.next + 1) % this.cycle.Count;

			return new TabResult(this.head + text + this.tail, this.head.Length + text.Length, this.cycle, Bell: false, selected);
		}

		var before = line[..cursor];
		var words = CompletionWords.Split(before, this.variables.TryGetValue("COMP_WORDBREAKS", out var breaks) ? breaks : CompletionWords.DefaultBreaks);
		var word = words[^1];
		var raw = before[word.ReplaceFrom..];
		var after = line[cursor..];

		List<string>? candidates = null;

		if (words.Count > 1 && findCompleter?.Invoke(Text(words[0])) is { } script)
		{
			var found = RunCompleter(script, line, cursor, Text(words[0]), raw, Text(words[^2]));
			candidates = found.Count > 0 ? found : null;
		}

		if (candidates is [var only])
		{
			return Replace(only, " ");
		}

		// No trailing space, because the shared part isn't a finished word. When it adds nothing to what was typed,
		// this falls through to the bell, then the list on the next Tab.
		if (candidates is not null && Completions.SharedPrefix(candidates) is var shared && shared.Length > raw.Length)
		{
			return Replace(shared, "");
		}

		var matches = new Completions.Found(candidates ?? [], 0);

		if (candidates is null)
		{
			matches = Completions.FindMatches(raw, word.Value, word.Quote, command: words.Count == 1, this.names, this.variables);

			if (Completions.Complete(word.Value, matches.Matches) is (var value, var finished))
			{
				return Replace(Insert(value), finished ? $"{word.Quote} " : "");
			}
		}

		if (this.tabbedOnce && matches.Matches.Count > 1)
		{
			this.cycle = matches.Matches;
			this.inserts = candidates ?? [.. matches.Matches.Select(Insert)];
			this.head = before[..word.ReplaceFrom];
			this.tail = after;
			this.next = 0;

			return new TabResult(line, cursor, matches.Matches, Bell: false);
		}

		this.tabbedOnce = true;

		return new TabResult(line, cursor, null, Bell: true);

		// Puts text in place of the word, from where Tab replaces up to the cursor, and moves the cursor after it.
		// The closing quote and space of a finished word go in only at the end of the line, as readline does.
		TabResult Replace(string text, string finish)
		{
			this.Reset();
			text += after.Length == 0 ? finish : "";

			return new TabResult(before[..word.ReplaceFrom] + text + after, word.ReplaceFrom + text.Length, null, Bell: false);
		}

		// The word's text as typed, quotes included.
		string Text(CompletionWord other) => before[other.Start..other.End];

		// The stem was typed as is, so it's kept from the line, and only the rest is quoted for the shell.
		string Insert(string match)
		{
			var stem = matches.Stem;
			var typed = stem == 0 ? 0 : match[stem - 1] == '/' ? raw.LastIndexOf('/') + 1 : stem;

			return raw[..typed] + Completions.QuoteWord(match[stem..], word.Quote, atStart: stem == 0);
		}
	}

	/// <summary>
	/// Runs the completer script at <paramref name="script" /> and waits for it to exit.
	/// </summary>
	/// <param name="script">The path registered with <c>complete -C</c>.</param>
	/// <param name="line">The whole line, passed in <c>COMP_LINE</c>.</param>
	/// <param name="cursor">Where the cursor is, passed in <c>COMP_POINT</c> as a count of UTF-8 bytes.</param>
	/// <param name="command">The command name, passed to the script as its first argument.</param>
	/// <param name="current">The partial word at the cursor, passed as the second argument. Empty after a space.</param>
	/// <param name="previous">The word before <paramref name="current" />, passed as the third argument.</param>
	/// <returns>
	/// Each non-empty line the script printed, in ordinal order, or an empty list when it printed nothing or couldn't
	/// be started.
	/// </returns>
	private static List<string> RunCompleter(string script, string line, int cursor, string command, string current, string previous)
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

			// Set on the child only, so the shell's own environment never gets these. COMP_POINT is a byte index.
			startInfo.Environment["COMP_LINE"] = line;
			startInfo.Environment["COMP_POINT"] = Encoding.UTF8.GetByteCount(line.AsSpan(0, cursor)).ToString(CultureInfo.InvariantCulture);

			using var process = Process.Start(startInfo);

			if (process is null)
			{
				return [];
			}

			process.StandardInput.Close();

			// Errors are drained and dropped, or a script that writes many could fill the pipe and hang Tab.
			var errors = process.StandardError.ReadToEndAsync();

			// Read to the end before inserting anything, so a slow script can't leave a partial line.
			var output = process.StandardOutput.ReadToEnd();

			errors.Wait();
			process.WaitForExit();

			return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)];
		}
		// A missing or non-executable script must not end the shell, so Tab falls back to normal completion.
		catch (Win32Exception exception)
		{
			Log.Warning(exception, "Can't start completion script {Script}", script);

			return [];
		}
	}

	/// <summary>
	/// Forgets earlier Tab presses. Callers run this on any other key, so the next Tab starts over.
	/// </summary>
	public void Reset()
	{
		this.tabbedOnce = false;
		this.cycle = null;
	}

	/// <summary>
	/// The outcome of one Tab press.
	/// </summary>
	/// <param name="Line">The text the line should hold now.</param>
	/// <param name="Cursor">Where the cursor should be now.</param>
	/// <param name="Listed">
	/// The matches, or <see langword="null" /> when this press didn't list or cycle. Printed above the line when
	/// <paramref name="Selected" /> is <see langword="null" />, and already on screen otherwise.
	/// </param>
	/// <param name="Bell"><see langword="true" /> when Tab had nothing to add and the bell should ring.</param>
	/// <param name="Selected">
	/// The index in <paramref name="Listed" /> of the match now in the line, which should be highlighted, or
	/// <see langword="null" /> when not cycling.
	/// </param>
	internal readonly record struct TabResult(string Line, int Cursor, IReadOnlyList<string>? Listed, bool Bell, int? Selected = null);
}
