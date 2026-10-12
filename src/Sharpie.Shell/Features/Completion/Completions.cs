using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Completion;

/// <summary>
/// Finds the command and file names that a partly typed word can complete to.
/// </summary>
internal static class Completions
{
	/// <summary>
	/// Finds the names that the last word of <paramref name="partial" /> could complete to.
	/// </summary>
	/// <param name="partial">The text typed so far.</param>
	/// <param name="names">The builtin command names. Executables on <c>PATH</c> are added to these.</param>
	/// <returns>
	/// Each matching name once, in ordinal order, or an empty list when nothing matches. The first word matches
	/// command names. Any later word matches the names of files and directories in the working directory, or in the
	/// folder named by the word up to its last <c>/</c>, with that folder kept at the front of each match. Directory
	/// names end in <c>/</c>.
	/// </returns>
	public static IReadOnlyList<string> FindMatches(string partial, IEnumerable<string> names)
	{
		_ = partial.ArgumentNotNull();
		_ = names.ArgumentNotNull();

		var space = partial.LastIndexOf(' ');

		if (space >= 0)
		{
			var word = partial[(space + 1)..];
			var folder = word[..(word.LastIndexOf('/') + 1)];
			var prefix = word[folder.Length..];

			// A typo in the folder part must not crash the shell, so it just has no matches.
			if (folder.Length > 0 && !Directory.Exists(folder))
			{
				return [];
			}

			// Filtered here instead of passing a search pattern, because the pattern would treat * and ? in the typed
			// word as wildcards.
			return Directory.EnumerateFileSystemEntries(folder.Length > 0 ? folder : ".")
				.Where(entry => Path.GetFileName(entry.AsSpan()).StartsWith(prefix, StringComparison.Ordinal))
				.Select(entry => folder + Path.GetFileName(entry) + (Directory.Exists(entry) ? "/" : ""))
				.Order(StringComparer.Ordinal)
				.ToList();
		}

		// Distinct because echo and friends are builtins and also live in /usr/bin, and counting them twice would
		// look like two matches.
		return names
			.Where(name => name.StartsWith(partial, StringComparison.Ordinal))
			.Concat(PathFinder.FindExecutableNames(partial))
			.Distinct()
			.Order(StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Works out what Tab should add to <paramref name="partial" />, from its matches.
	/// </summary>
	/// <param name="partial">The text typed so far.</param>
	/// <param name="matches">The names <see cref="FindMatches" /> found for <paramref name="partial" />.</param>
	/// <returns>
	/// The rest of the only matching name followed by a space, or with no space when it's a directory, so the next Tab
	/// can go on into it. When several names match, the part they all share past the last word of
	/// <paramref name="partial" />, with no space because the name isn't finished. When they share nothing more but the
	/// last word is itself one of them, just a space. <see langword="null" /> when nothing matches or the matches share
	/// nothing more.
	/// </returns>
	/// <remarks>
	/// Takes the matches rather than finding them, so Tab, which also lists them, searches <c>PATH</c> only once.
	/// </remarks>
	public static string? Complete(string partial, IReadOnlyList<string> matches)
	{
		_ = partial.ArgumentNotNull();
		_ = matches.ArgumentNotNull();

		var typed = partial.Length - (partial.LastIndexOf(' ') + 1);

		switch (matches.Count)
		{
			case 0:
				return null;
			case 1:
				return matches[0].EndsWith('/') ? matches[0][typed..] : matches[0][typed..] + " ";
			default:
				break;
		}

		var shared = SharedPrefix(matches);

		if (shared.Length > typed)
		{
			return shared[typed..];
		}

		// zsh's REC_EXACT: a word that is already a whole name is finished, even when longer names like executable
		// .bak copies also match. Typing on, as in name.<Tab>, still reaches them.
		return matches.Contains(partial[^typed..]) ? " " : null;
	}

	/// <summary>
	/// Finds the longest text that every one of <paramref name="words" /> starts with.
	/// </summary>
	/// <param name="words">The words to compare. Must hold at least one.</param>
	/// <returns>
	/// The shared start, or an empty string when the first characters already differ.
	/// </returns>
	internal static string SharedPrefix(IReadOnlyList<string> words)
	{
		_ = words.ArgumentNotNull();

		return words.Aggregate((prefix, word) => prefix[..CommonLength(prefix, word)]);

		// Counts how many leading characters a and b have in common.
		static int CommonLength(string a, string b)
		{
			var length = 0;

			while (length < a.Length && length < b.Length && a[length] == b[length])
			{
				length++;
			}

			return length;
		}
	}
}
