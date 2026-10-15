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
internal sealed class TabCompleter(IReadOnlyCollection<string> names, Func<string, string?>? findCompleter = null)
{
	private readonly IReadOnlyCollection<string> names = names.ArgumentNotNull();

	private bool tabbedOnce;
	private IReadOnlyList<string>? cycle;
	private string head = "";
	private int next;

	/// <summary>
	/// Works out the effect of one Tab press on <paramref name="line" />.
	/// </summary>
	/// <param name="line">The text typed so far.</param>
	/// <returns>
	/// The new line, plus the matches to print above it when this press lists them, and whether to ring the bell.
	/// </returns>
	public TabResult Press(string line)
	{
		if (this.cycle is not null)
		{
			var selected = this.next;

			this.next = (this.next + 1) % this.cycle.Count;

			return new TabResult(this.head + this.cycle[selected], this.cycle, Bell: false, selected);
		}

		var space = line.LastIndexOf(' ');

		List<string>? candidates = null;

		if (space >= 0)
		{
			var command = line[..line.IndexOf(' ', StringComparison.Ordinal)];

			if (findCompleter?.Invoke(command) is { } script)
			{
				var found = RunCompleter(script, line, command, line[(space + 1)..], PreviousWord(line[..space]));
				candidates = found.Count > 0 ? found : null;
			}
		}

		if (candidates is [var only])
		{
			this.Reset();

			return new TabResult(line[..(space + 1)] + only + " ", null, Bell: false);
		}

		// No trailing space, because the shared part isn't a finished word. When it adds nothing to what was typed,
		// this falls through to the bell, then the list on the next Tab.
		if (candidates is not null && Completions.SharedPrefix(candidates) is var shared && shared.Length > line.Length - (space + 1))
		{
			this.Reset();

			return new TabResult(line[..(space + 1)] + shared, null, Bell: false);
		}

		var matches = candidates ?? Completions.FindMatches(line, this.names);

		if (candidates is null && Completions.Complete(line, matches) is { } rest)
		{
			this.Reset();

			return new TabResult(line + rest, null, Bell: false);
		}

		if (this.tabbedOnce && matches.Count > 1)
		{
			this.cycle = matches;

			// The matches are single words, so cycling swaps only the last word and keeps what was typed before it.
			this.head = line[..(line.LastIndexOf(' ') + 1)];
			this.next = 0;

			return new TabResult(line, matches, Bell: false);
		}

		this.tabbedOnce = true;

		return new TabResult(line, null, Bell: true);
	}

	/// <summary>
	/// Runs the completer script at <paramref name="script" /> and waits for it to exit.
	/// </summary>
	/// <param name="script">The path registered with <c>complete -C</c>.</param>
	/// <param name="line">The whole line, passed in <c>COMP_LINE</c> with its UTF-8 length in <c>COMP_POINT</c>.</param>
	/// <param name="command">The command name, passed to the script as its first argument.</param>
	/// <param name="current">The partial word at the cursor, passed as the second argument. Empty after a space.</param>
	/// <param name="previous">The word before <paramref name="current" />, passed as the third argument.</param>
	/// <returns>
	/// Each non-empty line the script printed, in ordinal order, or an empty list when it printed nothing or couldn't
	/// be started.
	/// </returns>
	private static List<string> RunCompleter(string script, string line, string command, string current, string previous)
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

			// Set on the child only, so the shell's own environment never gets these. COMP_POINT is a byte index, and
			// Tab always completes the whole line, so the point is at its end.
			startInfo.Environment["COMP_LINE"] = line;
			startInfo.Environment["COMP_POINT"] = Encoding.UTF8.GetByteCount(line).ToString(CultureInfo.InvariantCulture);

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
	/// Finds the word just before the one being completed.
	/// </summary>
	/// <param name="before">The line up to the last space, so the word being completed is already cut off.</param>
	/// <returns>
	/// The last word of <paramref name="before" />, which is the command name itself for <c>git &lt;TAB&gt;</c>, as
	/// bash passes it, or an empty string when there's no word.
	/// </returns>
	private static string PreviousWord(string before) => before.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [.., var last] ? last : string.Empty;

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
	/// <param name="Listed">
	/// The matches, or <see langword="null" /> when this press didn't list or cycle. Printed above the line when
	/// <paramref name="Selected" /> is <see langword="null" />, and already on screen otherwise.
	/// </param>
	/// <param name="Bell"><see langword="true" /> when Tab had nothing to add and the bell should ring.</param>
	/// <param name="Selected">
	/// The index in <paramref name="Listed" /> of the match now in the line, which should be highlighted, or
	/// <see langword="null" /> when not cycling.
	/// </param>
	internal readonly record struct TabResult(string Line, IReadOnlyList<string>? Listed, bool Bell, int? Selected = null);
}
