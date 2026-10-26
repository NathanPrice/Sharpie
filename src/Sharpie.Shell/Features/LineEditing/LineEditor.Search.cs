using System.Diagnostics.CodeAnalysis;

namespace Sharpie.Shell.Features.LineEditing;

/// <summary>
/// The Ctrl+R and Ctrl+S history search, which works like readline's incremental search in bash.
/// </summary>
internal sealed partial class LineEditor
{
	// What's been typed since Ctrl+R or Ctrl+S, or null when no search is running.
	private string? query;

	// Kept across lines, so Ctrl+R with nothing typed searches for it again, as in bash.
	private string lastQuery = string.Empty;

	private bool reverse;
	private bool failed;

	// The history index of the line on screen, where history.Count stands for the line from before the search.
	private int found;

	private string original = string.Empty;
	private int originalCursor;

	/// <summary>
	/// Gets the prompt bash shows during a search.
	/// </summary>
	/// <value>
	/// Something like <c>(reverse-i-search)`ls': </c>, with <c>failed </c> in front when nothing matches.
	/// </value>
	private string SearchPrompt => $"({(this.failed ? "failed " : string.Empty)}{(this.reverse ? "reverse-" : string.Empty)}i-search)`{this.query}': ";

	/// <summary>
	/// Starts a search with nothing typed yet.
	/// </summary>
	/// <param name="reverse"><see langword="true" /> for Ctrl+R, which searches older lines, or <see langword="false" /> for Ctrl+S.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome StartSearch(bool reverse)
	{
		this.query = string.Empty;
		this.reverse = reverse;
		this.failed = false;
		this.found = history.Count;
		this.original = this.line.ToString();
		this.originalCursor = this.cursor;

		// So Up after the search starts a new prefix search from the line it found.
		this.recalled = history.Count;

		return Outcome.Continue;
	}

	/// <summary>
	/// Applies a key that belongs to the search, or ends the search for any other key.
	/// </summary>
	/// <param name="key">The key pressed.</param>
	/// <returns>
	/// What the key did, or <see langword="null" /> when the key ended the search and should still do its usual job,
	/// as Enter, Ctrl+C, the arrows and Tab do in bash.
	/// </returns>
	[SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "ConsoleKey has a member for every key on the keyboard. Only the keys the search handles need an arm.")]
	private Outcome? Search(ConsoleKeyInfo key)
	{
		var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
		var plain = (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0;

		Outcome? outcome = key.Key switch
		{
			ConsoleKey.R or ConsoleKey.S when control => this.Step(reverse: key.Key == ConsoleKey.R),
			ConsoleKey.G when control => this.AbortSearch(),
			ConsoleKey.Backspace when plain => this.Shorten(),

			// Swallowed, so ending a search with Escape doesn't make the next key count as Alt.
			ConsoleKey.Escape when plain => this.EndSearch(),
			_ when plain && !char.IsControl(key.KeyChar) => this.Extend(key.KeyChar),
			_ => null,
		};

		if (outcome is null)
		{
			_ = this.EndSearch();
		}

		return outcome;
	}

	/// <summary>
	/// Moves to the next match in the direction given, for a repeated Ctrl+R or Ctrl+S.
	/// </summary>
	/// <param name="reverse"><see langword="true" /> for Ctrl+R, <see langword="false" /> for Ctrl+S.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	/// <remarks>
	/// Switching direction only turns the search around without moving, as in bash, so the match on screen is checked
	/// again in the new direction first. With nothing typed, the last search's text is used, and with no last search
	/// nothing happens.
	/// </remarks>
	private Outcome Step(bool reverse)
	{
		var again = this.reverse == reverse;

		this.reverse = reverse;

		if (this.query!.Length == 0)
		{
			this.query = this.lastQuery;

			if (this.query.Length == 0)
			{
				return Outcome.Continue;
			}
		}

		return this.Find(again);
	}

	/// <summary>
	/// Adds a typed character to the search text and finds the text again from the match on screen.
	/// </summary>
	/// <param name="character">The typed character.</param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome Extend(char character)
	{
		this.query += character;

		return this.Find(again: false);
	}

	/// <summary>
	/// Drops the last character of the search text, keeping the match on screen.
	/// </summary>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	/// <remarks>
	/// With no text left to drop, it rings the bell as bash does. When the search had failed, bash goes back to the
	/// next older line instead. That case is rare, so here the shorter text is just found again from the line on screen.
	/// </remarks>
	private Outcome Shorten()
	{
		if (this.query!.Length == 0)
		{
			output.Write('\a');

			return Outcome.Continue;
		}

		this.query = this.query[..^1];

		return this.query.Length == 0 ? Outcome.Continue : this.Find(again: false);
	}

	/// <summary>
	/// Ends the search for Ctrl+G and puts back the line and cursor from before it.
	/// </summary>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome AbortSearch()
	{
		_ = this.line.Clear().Append(this.original);
		this.cursor = this.originalCursor;

		return this.EndSearch();
	}

	/// <summary>
	/// Ends the search, leaving the match on screen as the line being edited.
	/// </summary>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	private Outcome EndSearch()
	{
		if (this.query is { Length: > 0 })
		{
			this.lastQuery = this.query;
		}

		this.query = null;

		return Outcome.Continue;
	}

	/// <summary>
	/// Finds the search text in the line on screen, past the cursor, and then in the lines after it in the search's
	/// direction. The first match goes on screen with the cursor at its start.
	/// </summary>
	/// <param name="again">
	/// <see langword="true" /> to skip a match at the cursor, for a repeated Ctrl+R or Ctrl+S, or
	/// <see langword="false" /> to keep it, so typing another character leaves the cursor where it is if the longer
	/// text still matches there.
	/// </param>
	/// <returns>
	/// <see cref="Outcome.Continue" />.
	/// </returns>
	/// <remarks>
	/// The line from before the search is searched too, as bash does. A line the same as the one on screen is skipped,
	/// so repeated history lines don't need extra presses. Matching is case-sensitive, like bash. With no match, the
	/// line on screen stays and the bell rings.
	/// </remarks>
	private Outcome Find(bool again)
	{
		var text = this.query!;
		var step = this.reverse ? -1 : 1;

		for (var i = this.found; i >= 0 && i <= history.Count; i += step)
		{
			var candidate = i == history.Count ? this.original : history[i];

			if (i != this.found && this.line.Equals(candidate.AsSpan()))
			{
				continue;
			}

			var from = i == this.found ? this.cursor + (again ? step : 0) : this.reverse ? candidate.Length : 0;

			// Slices that stop at the line's ends, so a start past either end finds nothing instead of throwing.
			var start = this.reverse ? 0 : Math.Min(from, candidate.Length);
			var at = this.reverse
				? candidate.AsSpan(0, Math.Clamp(from + text.Length, 0, candidate.Length)).LastIndexOf(text)
				: candidate.AsSpan(start).IndexOf(text);

			if (at >= 0)
			{
				_ = this.line.Clear().Append(candidate);
				this.cursor = start + at;
				this.found = i;
				this.failed = false;

				return Outcome.Continue;
			}
		}

		this.failed = true;
		output.Write('\a');

		return Outcome.Continue;
	}
}
