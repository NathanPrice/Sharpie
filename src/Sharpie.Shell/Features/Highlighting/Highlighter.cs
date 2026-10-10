using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Highlighting;

/// <summary>
/// Names what a colored part of the command line is.
/// </summary>
public enum TokenKind
{
	/// <summary>
	/// A builtin or a program the shell can find.
	/// </summary>
	Command,

	/// <summary>
	/// A command word that matches no builtin and no program on <c>PATH</c>.
	/// </summary>
	UnknownCommand,

	/// <summary>
	/// A quoted part of a word, quotes included.
	/// </summary>
	Quoted,

	/// <summary>
	/// A redirect operator such as <c>&gt;</c> or <c>2&gt;&gt;</c>.
	/// </summary>
	Redirect,

	/// <summary>
	/// A path argument that doesn't exist, or a redirect target whose folder doesn't exist.
	/// </summary>
	MissingPath,
}

/// <summary>
/// A colored part of the command line.
/// </summary>
/// <param name="Start">The index of the first character in the line.</param>
/// <param name="Length">The number of characters covered.</param>
/// <param name="Kind">What the characters are.</param>
internal readonly record struct Token(int Start, int Length, TokenKind Kind);

/// <summary>
/// Colors a command line while it's typed and flags commands, files, and folders that don't exist.
/// </summary>
internal static class Highlighter
{
	/// <summary>
	/// Finds the parts of <paramref name="line" /> to color.
	/// </summary>
	/// <param name="line">The command line typed so far.</param>
	/// <param name="builtins">The builtin command names.</param>
	/// <returns>
	/// The colored parts in order, never overlapping. Text not covered by any token keeps the default color.
	/// </returns>
	/// <remarks>
	/// An argument is only checked on disk when it looks like a path: it contains <c>/</c> or starts with <c>~</c> or
	/// <c>.</c>. Otherwise <c>echo hello</c> would flag <c>hello</c>. The argument to <c>cd</c> is always checked and
	/// must be a folder. A redirect target only needs its folder to exist, since running the command creates the file.
	/// </remarks>
	public static IReadOnlyList<Token> Highlight(string line, IReadOnlyCollection<string> builtins)
	{
		_ = line.ArgumentNotNull();
		_ = builtins.ArgumentNotNull();

		var tokens = new List<Token>();
		string? command = null;
		var afterRedirect = false;
		var argumentCount = 0;

		foreach (var word in SplitRaw(line))
		{
			// Unquoted by the parser itself, so a quoted word is judged by what the shell will actually run.
			var value = CommandLine.Split(line[word.Start..word.End]).FirstOrDefault() ?? string.Empty;
			TokenKind? kind;

			if (afterRedirect)
			{
				kind = FolderExists(value) ? null : TokenKind.MissingPath;
				afterRedirect = false;
			}
			// The same operators CommandLine.SplitRedirect takes out.
			else if (value is ">" or "1>" or ">>" or "1>>" or "2>" or "2>>")
			{
				kind = TokenKind.Redirect;
				afterRedirect = true;
			}
			else
			{
				switch (command)
				{
					case null:
						command = value;

						// FindExecutable also finds absolute paths, because Path.Combine drops the PATH folder for them.
						kind = builtins.Contains(value) || PathFinder.FindExecutable(value) is not null
							? TokenKind.Command
							: TokenKind.UnknownCommand;
						break;
					case "cd" when argumentCount++ == 0:
						kind = Directory.Exists(ExpandHome(value)) ? null : TokenKind.MissingPath;
						break;
					default:
						{
							var looksLikePath = value.Contains('/', StringComparison.Ordinal) || value.StartsWith('~') || value.StartsWith('.');
							var path = ExpandHome(value);

							kind = looksLikePath && !File.Exists(path) && !Directory.Exists(path) ? TokenKind.MissingPath : null;
							break;
						}
				}
			}

			if (kind is { } wholeWord)
			{
				tokens.Add(new Token(word.Start, word.End - word.Start, wholeWord));
			}
			else
			{
				tokens.AddRange(word.Strings);
			}
		}

		return tokens;
	}

	/// <summary>
	/// Finds where each word of <paramref name="line" /> starts and ends, and where its quoted parts are.
	/// </summary>
	/// <param name="line">The command line typed so far.</param>
	/// <returns>
	/// One entry per word, with its quoted parts as <see cref="TokenKind.Quoted" /> tokens. A quote that is never
	/// closed runs to the end of the line.
	/// </returns>
	/// <remarks>
	/// <see cref="CommandLine.Split" /> drops positions, which coloring needs, so this walks the same quote and
	/// backslash rules only to find the boundaries.
	/// </remarks>
	private static List<(int Start, int End, List<Token> Strings)> SplitRaw(string line)
	{
		var words = new List<(int Start, int End, List<Token> Strings)>();
		var strings = new List<Token>();
		var start = -1;
		char? quote = null;
		var quoteStart = 0;
		var escaped = false;

		for (var index = 0; index < line.Length; index++)
		{
			var character = line[index];

			if (escaped)
			{
				escaped = false;

				continue;
			}

			if (quote is null && character is ' ' or '\t')
			{
				if (start >= 0)
				{
					words.Add((start, index, strings));
					strings = [];
					start = -1;
				}

				continue;
			}

			if (start < 0)
			{
				start = index;
			}

			if (character == '\\' && quote != '\'')
			{
				escaped = true;
			}
			else if (quote is null && character is '\'' or '"')
			{
				quote = character;
				quoteStart = index;
			}
			else if (character == quote)
			{
				strings.Add(new Token(quoteStart, index - quoteStart + 1, TokenKind.Quoted));
				quote = null;
			}
		}

		if (quote is not null)
		{
			strings.Add(new Token(quoteStart, line.Length - quoteStart, TokenKind.Quoted));
		}

		if (start >= 0)
		{
			words.Add((start, line.Length, strings));
		}

		return words;
	}

	/// <summary>
	/// Replaces a leading <c>~</c> in <paramref name="path" /> with the user's home folder, the way <c>cd</c> does.
	/// </summary>
	/// <param name="path">The path as typed.</param>
	/// <returns>
	/// The expanded path, or <paramref name="path" /> unchanged when it doesn't start with <c>~</c> or <c>~/</c>.
	/// </returns>
	private static string ExpandHome(string path)
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

		return path == "~" ? home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) : path;
	}

	/// <summary>
	/// Checks whether the folder a redirect would write <paramref name="path" /> into exists.
	/// </summary>
	/// <param name="path">The redirect target as typed.</param>
	/// <returns>
	/// <see langword="true" /> when the folder exists; otherwise, <see langword="false" />, including for an empty or
	/// invalid path.
	/// </returns>
	private static bool FolderExists(string path)
	{
		try
		{
			return Path.GetDirectoryName(Path.GetFullPath(ExpandHome(path))) is { } folder && Directory.Exists(folder);
		}
		catch (ArgumentException)
		{
			return false;
		}
	}
}
