using System.Buffers;
using System.Text;
using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Common;

/// <summary>
/// Expands the wildcards <c>*</c>, <c>?</c> and <c>[...]</c> into the file names they match, as bash does.
/// </summary>
/// <remarks>
/// .NET's <c>FileSystemName.MatchesSimpleExpression</c> only knows <c>*</c> and <c>?</c>, so <c>[...]</c> needs this
/// matcher. <c>?</c> matches one UTF-16 unit, so a character outside the Basic Multilingual Plane, such as an emoji,
/// takes <c>??</c>; match on <see cref="Rune" /> values if file names like that need to work.
/// </remarks>
internal static class Glob
{
	/// <summary>
	/// The characters that make a word a pattern when they aren't escaped.
	/// </summary>
	private static readonly SearchValues<char> Wildcards = SearchValues.Create("*?[");

	/// <summary>
	/// Folder listing options that keep every entry the user can see.
	/// </summary>
	/// <remarks>
	/// The default skips hidden entries, and .NET marks every dotfile hidden on Unix, so <c>.*</c> would find nothing.
	/// </remarks>
	private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0 };

	/// <summary>
	/// Finds the paths that <paramref name="pattern" /> matches.
	/// </summary>
	/// <param name="pattern">
	/// A path whose folder and file names may hold wildcards. A backslash makes the next character literal.
	/// </param>
	/// <returns>
	/// The matching paths, written the way the pattern was, sorted in the current culture's order. An empty array
	/// means nothing matched or the pattern has no wildcard that isn't escaped.
	/// </returns>
	/// <remarks>
	/// Each part between slashes is matched against one folder's entries, so <c>*</c> never crosses a <c>/</c>. A name
	/// that starts with <c>.</c> only matches a part that starts with a literal <c>.</c>, as in bash, so <c>*</c> skips
	/// dotfiles. Folders that can't be read add nothing. Bash sorts by the locale's collation, which the current
	/// culture follows.
	/// </remarks>
	internal static string[] Expand(string pattern)
	{
		if (!HasWildcard(pattern.ArgumentNotNull()))
		{
			return [];
		}

		var parts = pattern.Split('/');
		List<string> paths = [string.Empty];

		for (var index = 0; index < parts.Length; index++)
		{
			var part = parts[index];
			var separator = index == 0 ? string.Empty : "/";

			// The first part is read from the current folder. After it, "/" also covers an absolute path, whose
			// first part is empty, and keeps C: on Windows meaning the drive's root.
			paths = HasWildcard(part)
				? [.. paths.SelectMany(path => Matches(index == 0 ? "." : path + "/", part).Select(name => path + separator + name))]
				: [.. paths.Select(path => path + separator + Unescape(part))];
		}

		// A literal last part, as in */x.cs or */, was never read from disk.
		string[] result = HasWildcard(parts[^1]) ? [.. paths] : [.. paths.Where(Path.Exists)];

		Array.Sort(result, StringComparer.CurrentCulture);

		return result;
	}

	/// <summary>
	/// Checks whether <paramref name="name" /> matches <paramref name="pattern" /> as a whole.
	/// </summary>
	/// <param name="name">A file or folder name.</param>
	/// <param name="pattern">
	/// The pattern: <c>*</c> matches any run of characters, <c>?</c> any one, and <c>[...]</c> one from the set, with
	/// <c>!</c> or <c>^</c> first to match one not in it. A backslash makes the next character literal.
	/// </param>
	/// <returns>
	/// <see langword="true" /> when the whole name matches.
	/// </returns>
	/// <remarks>
	/// This is the usual one-pass match that, when the rest fails, goes back to the last <c>*</c> and lets it take one
	/// more character. Going back further can't help, because a later <c>*</c> covers anything an earlier one could.
	/// </remarks>
	internal static bool IsMatch(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern)
	{
		var at = 0;
		var next = 0;

		// The pattern position just past the last * and the name position it covers up to, or 0 before any *.
		var starNext = 0;
		var starAt = 0;

		while (at < name.Length)
		{
			if (next < pattern.Length && pattern[next] == '*')
			{
				starNext = ++next;
				starAt = at;
			}
			else if (next < pattern.Length && MatchesOne(name[at], pattern[next..], out var length))
			{
				at++;
				next += length;
			}
			else if (starNext > 0)
			{
				next = starNext;
				at = ++starAt;
			}
			else
			{
				return false;
			}
		}

		return !pattern[next..].ContainsAnyExcept('*');
	}

	/// <summary>
	/// Checks whether <paramref name="part" /> holds a wildcard that isn't escaped.
	/// </summary>
	/// <param name="part">A pattern, or a part of one between slashes.</param>
	/// <returns>
	/// <see langword="true" /> when the part has to be matched against a folder's entries.
	/// </returns>
	private static bool HasWildcard(ReadOnlySpan<char> part)
	{
		for (var index = 0; index < part.Length; index++)
		{
			if (part[index] == '\\')
			{
				index++;
			}
			else if (Wildcards.Contains(part[index]))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Removes the escaping backslashes from a part with no wildcards.
	/// </summary>
	/// <param name="part">A part of a pattern between slashes.</param>
	/// <returns>
	/// The name the part stands for. A backslash at the very end is kept.
	/// </returns>
	private static string Unescape(string part)
	{
		var name = new StringBuilder(part.Length);

		for (var index = 0; index < part.Length; index++)
		{
			if (part[index] == '\\' && index + 1 < part.Length)
			{
				index++;
			}

			_ = name.Append(part[index]);
		}

		return name.ToString();
	}

	/// <summary>
	/// Lists the entries of <paramref name="folder" /> that <paramref name="part" /> matches.
	/// </summary>
	/// <param name="folder">The folder to read, ending in <c>/</c>, or <c>.</c> for the current folder.</param>
	/// <param name="part">A part of a pattern between slashes, with a wildcard in it.</param>
	/// <returns>
	/// The matching names, or none when the folder doesn't exist, isn't a folder, or can't be read.
	/// </returns>
	private static string[] Matches(string folder, string part)
	{
		var dotted = part.StartsWith('.') || part.StartsWith(@"\.", StringComparison.Ordinal);

		try
		{
			return [.. Directory.EnumerateFileSystemEntries(folder, "*", AllEntries)
				.Select(entry => Path.GetFileName(entry))
				.Where(name => (dotted || !name.StartsWith('.')) && IsMatch(name, part))];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	/// <summary>
	/// Checks whether <paramref name="character" /> matches the one-character item that starts
	/// <paramref name="pattern" />.
	/// </summary>
	/// <param name="character">A character of the name.</param>
	/// <param name="pattern">The rest of the pattern, starting at an item that isn't <c>*</c>.</param>
	/// <param name="length">How many pattern characters the item takes up.</param>
	/// <returns>
	/// <see langword="true" /> when the character matches the item.
	/// </returns>
	private static bool MatchesOne(char character, ReadOnlySpan<char> pattern, out int length)
	{
		length = 1;

		if (pattern[0] == '?')
		{
			return true;
		}

		if (pattern[0] == '\\' && pattern.Length > 1)
		{
			length = 2;

			return character == pattern[1];
		}

		if (pattern[0] == '[' && MatchesSet(character, pattern, out var setLength) is { } inSet)
		{
			length = setLength;

			return inSet;
		}

		return character == pattern[0];
	}

	/// <summary>
	/// Checks whether <paramref name="character" /> is in the <c>[...]</c> set that starts <paramref name="pattern" />.
	/// </summary>
	/// <param name="character">A character of the name.</param>
	/// <param name="pattern">The rest of the pattern, starting at the <c>[</c>.</param>
	/// <param name="length">How many pattern characters the set takes up, both brackets included.</param>
	/// <returns>
	/// Whether the character is in the set, or <see langword="null" /> when the <c>[</c> is never closed, which makes
	/// it a literal <c>[</c>, as in bash.
	/// </returns>
	/// <remarks>
	/// A <c>]</c> right after the <c>[</c>, or after the <c>!</c> or <c>^</c>, is part of the set, and so is a
	/// <c>-</c> at the end. <c>a-z</c> is a range by character code, like bash's default <c>globasciiranges</c>.
	/// <c>[:alpha:]</c> and the other POSIX classes work inside the brackets.
	/// </remarks>
	private static bool? MatchesSet(char character, ReadOnlySpan<char> pattern, out int length)
	{
		var index = 1;
		var negated = index < pattern.Length && pattern[index] is '!' or '^';
		var matched = false;

		if (negated)
		{
			index++;
		}

		for (var first = true; index < pattern.Length && (first || pattern[index] != ']'); first = false)
		{
			if (pattern[index..].StartsWith("[:") && pattern[(index + 2)..].IndexOf(":]") is var close and >= 0)
			{
				matched |= IsInClass(character, pattern.Slice(index + 2, close));
				index += close + 4;

				continue;
			}

			var low = ReadSetCharacter(pattern, ref index);
			var high = low;

			if (index + 1 < pattern.Length && pattern[index] == '-' && pattern[index + 1] != ']')
			{
				index++;
				high = ReadSetCharacter(pattern, ref index);
			}

			matched |= character >= low && character <= high;
		}

		length = index + 1;

		return index < pattern.Length ? matched != negated : null;
	}

	/// <summary>
	/// Reads one character of a <c>[...]</c> set, which a backslash can escape.
	/// </summary>
	/// <param name="pattern">The pattern.</param>
	/// <param name="index">The position to read at, moved past what was read.</param>
	/// <returns>
	/// The character.
	/// </returns>
	private static char ReadSetCharacter(ReadOnlySpan<char> pattern, ref int index)
	{
		if (pattern[index] == '\\' && index + 1 < pattern.Length)
		{
			index++;
		}

		return pattern[index++];
	}

	/// <summary>
	/// Checks whether <paramref name="character" /> is in the POSIX class <paramref name="name" />, as in
	/// <c>[[:digit:]]</c>.
	/// </summary>
	/// <param name="character">A character of the name.</param>
	/// <param name="name">The class name, such as <c>alpha</c>.</param>
	/// <returns>
	/// <see langword="true" /> when the character is in the class. An unknown class matches nothing.
	/// </returns>
	private static bool IsInClass(char character, ReadOnlySpan<char> name) => name switch
	{
		"alpha" => char.IsLetter(character),
		"digit" => char.IsAsciiDigit(character),
		"alnum" => char.IsLetterOrDigit(character),
		"upper" => char.IsUpper(character),
		"lower" => char.IsLower(character),
		"space" => char.IsWhiteSpace(character),
		"blank" => character is ' ' or '\t',
		"punct" => char.IsPunctuation(character) || char.IsSymbol(character),
		"xdigit" => char.IsAsciiHexDigit(character),
		"cntrl" => char.IsControl(character),
		"graph" => !char.IsWhiteSpace(character) && !char.IsControl(character),
		"print" => !char.IsControl(character),
		_ => false,
	};
}
