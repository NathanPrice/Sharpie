using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Highlighting;

/// <summary>
/// Names what a colored part of the command line is.
/// </summary>
[SuppressMessage("Maintainability", "CA1515:Consider making public types internal", Justification = "The xUnit theories in Sharpie.Shell.Tests take it as a parameter, and xUnit only runs public test methods.")]
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
	// The characters CommandLine.Split treats specially in a word with no spaces, operators included. A word without
	// any of them is already what Split would give back.
	private static readonly SearchValues<char> ParserCharacters = SearchValues.Create("\\'\"#|&;<>()");

	/// <summary>
	/// Finds the parts of <paramref name="line" /> to color.
	/// </summary>
	/// <param name="line">The command line typed so far.</param>
	/// <param name="builtins">The builtin command names.</param>
	/// <param name="knownCommands">
	/// Whether each command name already looked up is a command, filled in as names are looked up, or
	/// <see langword="null" /> to look every name up. The line editor keeps one per line, so the command isn't
	/// searched for on <c>PATH</c> again on every key.
	/// </param>
	/// <returns>
	/// The colored parts in order, never overlapping. Text not covered by any token keeps the default color.
	/// </returns>
	/// <remarks>
	/// An argument is only checked on disk when it looks like a path: it contains <c>/</c> or starts with <c>~</c> or
	/// <c>.</c>. Otherwise <c>echo hello</c> would flag <c>hello</c>. The argument to <c>cd</c> is always checked and
	/// must be a folder. A redirect target only needs its folder to exist, since running the command creates the file.
	/// </remarks>
	public static List<Token> Highlight(string line, IReadOnlyCollection<string> builtins, Dictionary<string, bool>? knownCommands = null)
	{
		_ = line.ArgumentNotNull();
		_ = builtins.ArgumentNotNull();

		var tokens = new List<Token>();
		string? command = null;
		TargetKind? target = null;
		var argumentCount = 0;

		foreach (var (start, end, strings) in SplitRaw(line))
		{
			// Unquoted by the parser itself, so a quoted word is judged by what the shell will actually run.
			var word = !line.AsSpan(start, end - start).ContainsAny(ParserCharacters) ? new Word(line[start..end], IsOperator: false)
				: CommandLine.Split(line[start..end]) is [var first] ? first
				: new Word(string.Empty, IsOperator: false);
			var value = word.Text;
			TokenKind? kind;

			// Only a file name is checked, so the delimiter in <<EOF, the text in <<< x and the 1 in 2>&1 aren't flagged.
			if (target is { } redirected)
			{
				kind = redirected == TargetKind.File && !FolderExists(value) ? TokenKind.MissingPath : null;
				target = null;
			}
			else if (word.IsOperator && CommandLine.RedirectKind(word.Text) is { } redirect)
			{
				kind = TokenKind.Redirect;
				target = redirect;
			}
			else
			{
				switch (command)
				{
					// NAME=VALUE words in front of the command go into its environment, so they aren't the command.
					case null when DeclareCommand.IsAssignment(value):
						kind = null;
						break;
					case null:
						command = value;
						kind = IsCommand(value, builtins, knownCommands) ? TokenKind.Command : TokenKind.UnknownCommand;
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
				tokens.Add(new Token(start, end - start, wholeWord));
			}
			else if (strings is not null)
			{
				tokens.AddRange(strings);
			}
		}

		return tokens;
	}

	/// <summary>
	/// Finds where each word of <paramref name="line" /> starts and ends, and where its quoted parts are.
	/// </summary>
	/// <param name="line">The command line typed so far.</param>
	/// <returns>
	/// One entry per word and per operator, with its quoted parts as <see cref="TokenKind.Quoted" /> tokens, or
	/// <see langword="null" /> for a word with none. A quote that is never closed runs to the end of the line.
	/// </returns>
	/// <remarks>
	/// <see cref="CommandLine.Split" /> drops positions, which coloring needs, so this walks the same quote,
	/// backslash and operator rules only to find the boundaries.
	/// </remarks>
	private static List<(int Start, int End, List<Token>? Strings)> SplitRaw(string line)
	{
		var words = new List<(int Start, int End, List<Token>? Strings)>();

		// Made only for a word with quotes in it, since most words have none.
		List<Token>? strings = null;
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
					strings = null;
					start = -1;
				}

				continue;
			}

			if (quote is null && CommandLine.OperatorAt(line, index) is { } typed)
			{
				var number = start >= 0 && CommandLine.IsStreamNumber(line.AsSpan(start, index - start), typed);

				if (start >= 0 && !number)
				{
					words.Add((start, index, strings));
				}

				words.Add((number ? start : index, index + typed.Length, null));
				strings = null;
				start = -1;
				index += typed.Length - 1;

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
				(strings ??= []).Add(new Token(quoteStart, index - quoteStart + 1, TokenKind.Quoted));
				quote = null;
			}
		}

		if (quote is not null)
		{
			(strings ??= []).Add(new Token(quoteStart, line.Length - quoteStart, TokenKind.Quoted));
		}

		if (start >= 0)
		{
			words.Add((start, line.Length, strings));
		}

		return words;
	}

	/// <summary>
	/// Checks whether <paramref name="name" /> is a builtin or a program the shell can run.
	/// </summary>
	/// <param name="name">The command word, unquoted.</param>
	/// <param name="builtins">The builtin command names.</param>
	/// <param name="knownCommands">The answers so far, or <see langword="null" /> to always look.</param>
	/// <returns>
	/// <see langword="true" /> when the shell would find the command; otherwise, <see langword="false" />.
	/// </returns>
	private static bool IsCommand(string name, IReadOnlyCollection<string> builtins, Dictionary<string, bool>? knownCommands)
	{
		if (knownCommands is null)
		{
			return Look();
		}

		if (!knownCommands.TryGetValue(name, out var found))
		{
			found = Look();
			knownCommands[name] = found;
		}

		return found;

		// FindExecutable also finds absolute paths, because Path.Combine drops the PATH folder for them.
		bool Look() => builtins.Contains(name) || PathFinder.FindExecutable(name) is not null;
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
	/// <see langword="true" /> when the folder exists or the path is <c>/dev/null</c>, which works on Windows too;
	/// otherwise, <see langword="false" />, including for an empty or invalid path.
	/// </returns>
	private static bool FolderExists(string path)
	{
		try
		{
			return path == Redirection.NullDevice || (Path.GetDirectoryName(Path.GetFullPath(ExpandHome(path))) is { } folder && Directory.Exists(folder));
		}
		catch (ArgumentException)
		{
			return false;
		}
	}
}
