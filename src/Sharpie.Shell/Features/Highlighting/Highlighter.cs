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
		var previousEnd = 0;

		foreach (var (start, end, strings) in SplitRaw(line))
		{
			// A line break between words ends the command as ; does, so the next line's command word is colored too.
			if (line.AsSpan(previousEnd, start - previousEnd).Contains('\n'))
			{
				command = null;
				target = null;
				argumentCount = 0;
			}

			previousEnd = end;

			// Unquoted by the parser itself, so a quoted word is judged by what the shell will actually run.
			var word = !line.AsSpan(start, end - start).ContainsAny(ParserCharacters) ? new Word(line[start..end], IsOperator: false)
				: CommandLine.Split(line[start..end]) is [var first] ? first
				: new Word(string.Empty, IsOperator: false);
			var value = word.Text;
			TokenKind? kind;

			// A <( or >( starts a list, whose first word is a command, even right after a redirect, as in < <(cmd).
			if (line.AsSpan(start, end - start) is "<(" or ">(")
			{
				kind = null;
				command = null;
				target = null;
				argumentCount = 0;
			}
			// Only a file name is checked, so the delimiter in <<EOF, the text in <<< x and the 1 in 2>&1 aren't flagged.
			else if (target is { } redirected)
			{
				kind = redirected == TargetKind.File && !FolderExists(value) ? TokenKind.MissingPath : null;
				target = null;
			}
			else if (word.IsOperator && CommandLine.RedirectKind(word.Text) is { } redirect)
			{
				kind = TokenKind.Redirect;
				target = redirect;
			}
			else if (word.IsOperator)
			{
				// Each command of a list or pipeline starts with its own command word.
				kind = null;
				command = null;
				argumentCount = 0;
			}
			else
			{
				switch (command)
				{
					// NAME=VALUE words in front of the command go into its environment, so they aren't the command. A list
					// still being typed, as in a=(x, is one too.
					case null when DeclareCommand.IsAssignment(value) || DeclareCommand.IsAssignment(line[start..end]):
						kind = null;
						break;

					// time's -p is an option, so the command comes after it.
					case null when value == "-p" && line.AsSpan(0, start).TrimEnd().EndsWith("time", StringComparison.Ordinal):
						kind = null;
						break;

					// A reserved word is typed unquoted, and the word after it starts a command again.
					case null when IsKeyword(line[start..end]):
						kind = TokenKind.Command;
						break;
					case null:
						command = value;
						kind = CommandKind(line, start, end, value, builtins, knownCommands);
						break;
					case "cd" when argumentCount++ == 0:
						kind = Directory.Exists(ExpandHome(value)) ? null : TokenKind.MissingPath;
						break;
					default:
						kind = IsMissingPath(value) ? TokenKind.MissingPath : null;
						break;
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
	/// Picks the color of the command word from <paramref name="start" /> to <paramref name="end" />.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="start">Where the word starts.</param>
	/// <param name="end">One past where it ends.</param>
	/// <param name="value">The word with its quotes removed.</param>
	/// <param name="builtins">The builtin command names.</param>
	/// <param name="knownCommands">The command names already looked up, as <see cref="Highlight" /> takes them.</param>
	/// <returns>
	/// <see cref="TokenKind.Command" /> for a builtin or a program on <c>PATH</c>, <see langword="null" /> for a history
	/// reference, which is replaced by an earlier line before it runs, and <see cref="TokenKind.UnknownCommand" />
	/// otherwise.
	/// </returns>
	private static TokenKind? CommandKind(string line, int start, int end, string value, IReadOnlyCollection<string> builtins, Dictionary<string, bool>? knownCommands) =>
		line.AsSpan(start, end - start) is ['!', _, ..] || (start == 0 && line[0] == '^') ? null
		: value == "[[" || IsCommand(value, builtins, knownCommands) ? TokenKind.Command
		: TokenKind.UnknownCommand;

	/// <summary>
	/// Checks whether an argument looks like a path, because it contains <c>/</c> or starts with <c>~</c> or <c>.</c>,
	/// and nothing is there.
	/// </summary>
	/// <param name="value">The argument, unquoted.</param>
	/// <returns>
	/// <see langword="true" /> when it should be flagged.
	/// </returns>
	private static bool IsMissingPath(string value)
	{
		var path = ExpandHome(value);

		return (value.Contains('/', StringComparison.Ordinal) || value.StartsWith('~') || value.StartsWith('.')) && !File.Exists(path) && !Directory.Exists(path);
	}

	/// <summary>
	/// Checks whether a word as typed is a reserved word or a <c>((...))</c>, which is a command of its own, colored as
	/// <c>[[</c> is.
	/// </summary>
	/// <param name="typed">The word as typed.</param>
	/// <returns>
	/// <see langword="true" /> when it is.
	/// </returns>
	private static bool IsKeyword(string typed) => CommandParser.ReservedWords.Contains(typed) || typed.StartsWith("((", StringComparison.Ordinal);

	/// <summary>
	/// Finds where the arithmetic, <c>${...}</c> or array list at <paramref name="index" /> ends.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">The position.</param>
	/// <param name="start">Where the word being read starts, or -1 when none has started.</param>
	/// <returns>
	/// The position of the last <c>)</c> of a <c>$((...))</c>, of a <c>((...))</c> that starts a word, or of the list
	/// of a <c>NAME=(...)</c> word, which runs to the end of the line when it's never closed, or of the <c>}</c> of a
	/// <c>${...}</c>, or -1.
	/// </returns>
	/// <remarks>
	/// A quote inside <c>${...}</c> belongs to the expansion, as in <c>"${q/"'"/Q}"</c>, so it doesn't open or close one.
	/// A <c>(</c> inside quotes never starts a list, since the word up to it then holds the quote.
	/// </remarks>
	private static int SkipEnd(string line, int index, int start) =>
		line.AsSpan(index).StartsWith("${", StringComparison.Ordinal) ? CommandLine.BraceEnd(line, index)
		: line[index] == '(' && start >= 0 && CommandLine.IsCompoundStart(line.AsSpan(start, index - start)) ? CommandLine.CompoundEnd(line, index) is var listEnd and >= 0 ? listEnd : line.Length - 1
		: start >= 0 && line[index] != '$' ? -1 : CommandLine.ArithmeticEnd(line, index);

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
	internal static List<(int Start, int End, List<Token>? Strings)> SplitRaw(string line)
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

			// A ((...)), $((...)), ${...} or NAME=(...) list is part of its word, so what's inside isn't read as commands
			// or quotes.
			if (SkipEnd(line, index, start) is var close and >= 0)
			{
				start = start < 0 ? index : start;
				index = close;

				continue;
			}

			if (quote is null && character is ' ' or '\t' or '\n')
			{
				if (start >= 0)
				{
					words.Add((start, index, strings));
					strings = null;
					start = -1;
				}

				continue;
			}

			// A <( or >( is one entry, so its < or > isn't taken for a redirect.
			if (quote is null && (CommandLine.IsProcessSubstitutionStart(line, index) ? line.Substring(index, 2) : CommandLine.OperatorAt(line, index)) is { } typed)
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

			if (character == '\\')
			{
				escaped = true;
			}
			else if (quote is null && character == '\'')
			{
				// A single quote, $'...' too, is read whole, so a \' in $'it\'s' doesn't end it.
				var stop = CommandLine.QuoteEnd(line, index) is var closed and >= 0 ? closed + 1 : line.Length;
				(strings ??= []).Add(new Token(index, stop - index, TokenKind.Quoted));
				index = stop - 1;
			}
			else if (quote is null && character == '"')
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
	/// Replaces a leading <c>~</c> or <c>~user</c> in <paramref name="path" /> with that user's home folder, the way
	/// <c>cd</c> does.
	/// </summary>
	/// <param name="path">The path as typed.</param>
	/// <returns>
	/// The expanded path, or <paramref name="path" /> unchanged when it doesn't start with <c>~</c> or names an unknown
	/// user. Windows has no lookup of another user's home folder, so there <c>~user</c> stays as typed.
	/// </returns>
	private static string ExpandHome(string path)
	{
		if (!path.StartsWith('~'))
		{
			return path;
		}

		var slash = path.IndexOf('/', StringComparison.Ordinal);
		var user = slash < 0 ? path[1..] : path[1..slash];
		var home = user.Length == 0
			? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
			: OperatingSystem.IsWindows() ? null : NativeMethods.HomeOf(user);

		return home is null ? path : slash < 0 ? home : Path.Combine(home, path[(slash + 1)..]);
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
