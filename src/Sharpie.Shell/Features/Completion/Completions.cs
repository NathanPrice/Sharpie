using System.Buffers;
using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Completion;

/// <summary>
/// Finds the command, file, variable and user names that a partly typed word can complete to.
/// </summary>
internal static class Completions
{
	// bash's default_filename_quote_characters, less ~ and #, which only matter at the start of a word.
	private const string FileNameSpecials = " \t\n\\\"'@<>=;|&()$`?*[!:{";

	private static readonly SearchValues<char> NameCharacters = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_");

	/// <summary>
	/// Finds what the word Tab is completing could become.
	/// </summary>
	/// <param name="raw">The word as typed, from just after a quote that is still open.</param>
	/// <param name="value">The same text with quotes and backslashes taken out.</param>
	/// <param name="quote">The quote still open at the end of the word, or <see langword="null" />.</param>
	/// <param name="command"><see langword="true" /> when the word is in the command's place.</param>
	/// <param name="names">The builtin command names. Executables on <c>PATH</c> are added to these.</param>
	/// <param name="variables">The shell variables. The environment's are added to these.</param>
	/// <returns>
	/// The matches, each the whole new value of the word, in ordinal order. A word starting <c>$</c> or <c>${</c>
	/// matches variable names, and <c>~</c> with no <c>/</c> matches user names as <c>~name/</c>. Otherwise the
	/// command's place matches command names, and any other word, or a command word with a <c>/</c>, matches files.
	/// The stem is how much of each match was typed as is and must not be quoted again: the folder, <c>$</c>,
	/// <c>${</c> or <c>~</c>.
	/// </returns>
	public static Found FindMatches(string raw, string value, char? quote, bool command, IEnumerable<string> names, IReadOnlyDictionary<string, string> variables)
	{
		_ = raw.ArgumentNotNull();
		_ = value.ArgumentNotNull();
		_ = names.ArgumentNotNull();
		_ = variables.ArgumentNotNull();

		// Single quotes keep $ as typed, so there's no variable to complete.
		if (quote != '\'' && VariableStem(raw) is { } stem)
		{
			return new Found(FindVariables(value[stem..], braced: stem == 2, variables), stem);
		}

		if (quote is null && raw.StartsWith('~') && !raw.Contains('/', StringComparison.Ordinal))
		{
			return new Found(FindUsers(value[1..]), 1);
		}

		var folder = value.LastIndexOf('/') + 1;

		return new Found(command && folder == 0 ? FindCommands(value, names) : FindFiles(value, variables), folder);
	}

	/// <summary>
	/// Finds the command names that start with <paramref name="prefix" />.
	/// </summary>
	/// <param name="prefix">The command word typed so far.</param>
	/// <param name="names">The builtin command names. Executables on <c>PATH</c> are added to these.</param>
	/// <returns>
	/// Each matching name once, in ordinal order.
	/// </returns>
	public static IReadOnlyList<string> FindCommands(string prefix, IEnumerable<string> names)
	{
		_ = prefix.ArgumentNotNull();
		_ = names.ArgumentNotNull();

		// Distinct because echo and friends are builtins and also live in /usr/bin, and counting them twice would
		// look like two matches.
		return names
			.Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
			.Concat(PathFinder.FindExecutableNames(prefix))
			.Distinct()
			.Order(StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Works out what Tab should make of the word <paramref name="typed" />, from its matches.
	/// </summary>
	/// <param name="typed">The word typed so far, without quotes.</param>
	/// <param name="matches">The matches <see cref="FindMatches" /> found for <paramref name="typed" />.</param>
	/// <returns>
	/// The only match, finished unless it's a directory, so the next Tab can go on into it. When several match, the
	/// part they all share, unfinished because the name isn't. When they share nothing more but
	/// <paramref name="typed" /> is itself one of them, <paramref name="typed" /> finished. <see langword="null" /> when
	/// nothing matches or the matches share nothing more. A finished word gets a space after it.
	/// </returns>
	/// <remarks>
	/// Takes the matches rather than finding them, so Tab, which also lists them, searches <c>PATH</c> only once.
	/// </remarks>
	public static (string Value, bool Finished)? Complete(string typed, IReadOnlyList<string> matches)
	{
		_ = typed.ArgumentNotNull();
		_ = matches.ArgumentNotNull();

		switch (matches.Count)
		{
			case 0:
				return null;
			case 1:
				return (matches[0], !matches[0].EndsWith('/'));
			default:
				break;
		}

		var shared = SharedPrefix(matches);

		if (shared.Length > typed.Length)
		{
			return (shared, false);
		}

		// zsh's REC_EXACT: a word that is already a whole name is finished, even when longer names like executable
		// .bak copies also match. Typing on, as in name.<Tab>, still reaches them.
		return matches.Contains(typed) ? (typed, true) : null;
	}

	/// <summary>
	/// Quotes the completed part of a word so the shell reads it back as typed, the way bash quotes file names.
	/// </summary>
	/// <param name="text">The text Tab adds after the stem.</param>
	/// <param name="quote">The quote open around it, or <see langword="null" />.</param>
	/// <param name="atStart">
	/// <see langword="true" /> when <paramref name="text" /> starts the word, where <c>~</c> and <c>#</c> mean something.
	/// </param>
	/// <returns>
	/// The text with each <c>'</c> written as <c>'\''</c> inside single quotes, a backslash before <c>"</c>,
	/// <c>\</c>, <c>$</c> and <c>`</c> inside double quotes, and a backslash before each character bash quotes in file
	/// names otherwise.
	/// </returns>
	public static string QuoteWord(string text, char? quote, bool atStart)
	{
		_ = text.ArgumentNotNull();

		if (quote == '\'')
		{
			return text.Replace("'", @"'\''", StringComparison.Ordinal);
		}

		var special = quote == '"' ? "\"\\$`" : FileNameSpecials;
		var quoted = new StringBuilder(text.Length);

		for (var index = 0; index < text.Length; index++)
		{
			if (special.Contains(text[index], StringComparison.Ordinal) || (quote is null && atStart && index == 0 && text[0] is '~' or '#'))
			{
				_ = quoted.Append('\\');
			}

			_ = quoted.Append(text[index]);
		}

		return quoted.ToString();
	}

	/// <summary>
	/// Finds the files and directories that <paramref name="value" /> could complete to.
	/// </summary>
	/// <param name="value">The word typed so far, without quotes.</param>
	/// <param name="variables">The shell variables, for a folder that starts with <c>$NAME</c>.</param>
	/// <returns>
	/// The names in the working directory, or in the folder named by the word up to its last <c>/</c>, with that folder
	/// kept at the front of each match as typed, in ordinal order. Directory names end in <c>/</c>.
	/// </returns>
	private static List<string> FindFiles(string value, IReadOnlyDictionary<string, string> variables)
	{
		var folder = value[..(value.LastIndexOf('/') + 1)];
		var prefix = value[folder.Length..];
		var lookup = ExpandFolder(folder, variables);

		try
		{
			// Filtered here instead of passing a search pattern, because the pattern would treat * and ? in the typed
			// word as wildcards.
			return
			[
				.. Directory.EnumerateFileSystemEntries(folder.Length > 0 ? lookup : ".")
					.Where(entry => Path.GetFileName(entry.AsSpan()).StartsWith(prefix, StringComparison.Ordinal))
					.Select(entry => folder + Path.GetFileName(entry) + (Directory.Exists(entry) ? "/" : ""))
					.Order(StringComparer.Ordinal),
			];
		}
		// A typo in the folder part, or a folder the user can't read like /root, must not end the shell, so it just
		// has no matches.
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or ArgumentException)
		{
			return [];
		}
	}

	/// <summary>
	/// Expands a leading <c>~</c>, <c>~user</c>, <c>$NAME</c> or <c>${NAME}</c> in <paramref name="folder" />, so Tab can
	/// look inside it while the line keeps what was typed.
	/// </summary>
	/// <param name="folder">The folder part of the word, ending in <c>/</c>, or empty.</param>
	/// <param name="variables">The shell variables.</param>
	/// <returns>
	/// The folder to look in, or <paramref name="folder" /> unchanged when it starts with none of these or names an
	/// unknown user.
	/// </returns>
	private static string ExpandFolder(string folder, IReadOnlyDictionary<string, string> variables)
	{
		if (folder.StartsWith('~'))
		{
			var user = folder[1..folder.IndexOf('/', StringComparison.Ordinal)];

			// Windows has no lookup of another user's home folder, as in ~user expansion.
			var home = user.Length == 0
				? Lookup(variables, "HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
				: OperatingSystem.IsWindows() ? null : NativeMethods.HomeOf(user);

			return home is null ? folder : home + folder[(user.Length + 1)..];
		}

		if (folder.StartsWith('$'))
		{
			var braced = folder.StartsWith("${", StringComparison.Ordinal);
			var nameStart = braced ? 2 : 1;
			var nameEnd = nameStart;

			while (nameEnd < folder.Length && IsNameCharacter(folder[nameEnd]))
			{
				nameEnd++;
			}

			return braced && (nameEnd == folder.Length || folder[nameEnd] != '}')
				? folder
				: (Lookup(variables, folder[nameStart..nameEnd]) ?? string.Empty) + folder[(braced ? nameEnd + 1 : nameEnd)..];
		}

		return folder;
	}

	/// <summary>
	/// Finds the variable names that start with <paramref name="prefix" />.
	/// </summary>
	/// <param name="prefix">The name typed so far, after <c>$</c> or <c>${</c>.</param>
	/// <param name="braced"><see langword="true" /> after <c>${</c>, so each match gets its <c>}</c>.</param>
	/// <param name="variables">The shell variables. The environment's are added to these.</param>
	/// <returns>
	/// Each match as <c>$NAME</c> or <c>${NAME}</c>, in ordinal order, with a <c>/</c> after a name whose value is a
	/// directory, as bash does.
	/// </returns>
	private static List<string> FindVariables(string prefix, bool braced, IReadOnlyDictionary<string, string> variables) =>
	[
		.. variables.Keys
			.Concat(Environment.GetEnvironmentVariables().Keys.Cast<string>())
			.Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
			.Distinct()
			.Select(name => (braced ? $"${{{name}}}" : $"${name}") + (Lookup(variables, name) is { Length: > 0 } folder && Directory.Exists(folder) ? "/" : ""))
			.Order(StringComparer.Ordinal),
	];

	/// <summary>
	/// Finds the user names that start with <paramref name="prefix" />.
	/// </summary>
	/// <param name="prefix">The name typed so far, after <c>~</c>.</param>
	/// <returns>
	/// Each match as <c>~name/</c>, in ordinal order. Empty on Windows, which has no <c>~user</c>.
	/// </returns>
	private static List<string> FindUsers(string prefix) => OperatingSystem.IsWindows()
		? []
		: [.. NativeMethods.UserNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal).Select(name => $"~{name}/")];

	/// <summary>
	/// Finds how much of <paramref name="raw" /> is the <c>$</c> or <c>${</c> before a variable name.
	/// </summary>
	/// <param name="raw">The word as typed.</param>
	/// <returns>
	/// 1 or 2 when the word is <c>$</c> or <c>${</c> followed only by name characters, or <see langword="null" />.
	/// </returns>
	private static int? VariableStem(string raw)
	{
		var stem = raw.StartsWith("${", StringComparison.Ordinal) ? 2 : raw.StartsWith('$') ? 1 : 0;

		return stem > 0 && raw.AsSpan(stem).IndexOfAnyExcept(NameCharacters) < 0 ? stem : null;
	}

	/// <summary>
	/// Reads a variable from the shell, else from the environment.
	/// </summary>
	/// <param name="variables">The shell variables.</param>
	/// <param name="name">The name.</param>
	/// <returns>
	/// The value, or <see langword="null" /> when neither has it.
	/// </returns>
	private static string? Lookup(IReadOnlyDictionary<string, string> variables, string name) =>
		variables.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name);

	/// <summary>
	/// Checks whether <paramref name="character" /> can be part of a variable name.
	/// </summary>
	/// <param name="character">The character.</param>
	/// <returns>
	/// <see langword="true" /> for an ASCII letter, digit or <c>_</c>.
	/// </returns>
	private static bool IsNameCharacter(char character) => NameCharacters.Contains(character);

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

	/// <summary>
	/// The matches for a word, and how much of each was typed as is.
	/// </summary>
	/// <param name="Matches">The whole new values the word could take, in ordinal order.</param>
	/// <param name="Stem">The length of the start each match shares with the typed word and keeps unquoted.</param>
	internal readonly record struct Found(IReadOnlyList<string> Matches, int Stem);
}
