using System.Buffers;
using System.Text;
using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Common;

/// <summary>
/// A word of a command line, or an operator such as <c>|</c> or <c>&gt;</c>.
/// </summary>
/// <param name="Text">The word with its quotes removed, or the operator as typed.</param>
/// <param name="IsOperator">
/// <see langword="true" /> for an operator. A quoted or escaped one, like <c>'|'</c>, is a plain word.
/// </param>
/// <param name="Start">Where the word starts in the typed text, quotes included, for an error to point at.</param>
/// <param name="End">Where the word ends in the typed text, one past its last character.</param>
internal readonly record struct Word(string Text, bool IsOperator, int Start = 0, int End = 0);

/// <summary>
/// Breaks a typed command line into words the way a POSIX shell does.
/// </summary>
internal static class CommandLine
{
	/// <summary>
	/// The operators bash's parser reads, longest first so that <c>&gt;&gt;</c> isn't read as two <c>&gt;</c>.
	/// </summary>
	private static readonly string[] Operators =
		["<<<", "<<-", "&>>", ";;&", "&&", "||", ";;", ";&", "|&", "<<", ">>", "<&", ">&", "<>", ">|", "&>", "|", "&", ";", "<", ">", "(", ")"];

	/// <summary>
	/// The first characters of <see cref="Operators" />.
	/// </summary>
	private static readonly SearchValues<char> OperatorStarts = SearchValues.Create("|&;<>()");

	/// <summary>
	/// Splits <paramref name="line" /> into words on spaces and tabs and around operators, keeping everything inside
	/// single or double quotes literally.
	/// </summary>
	/// <param name="line">The command line as the user typed it.</param>
	/// <param name="variables">
	/// The shell variables that <c>$NAME</c> expands from, or <see langword="null" /> to leave every <c>$</c> as typed.
	/// </param>
	/// <returns>
	/// The command name followed by its arguments, with the quote characters removed, and each operator as its own
	/// <see cref="Word" />. Quoted and unquoted text that touch with no space between join into one word. An empty
	/// array means the line was blank.
	/// </returns>
	/// <remarks>
	/// An unquoted operator from <see cref="Operators" /> ends the word before it even with no space, so
	/// <c>ls|wc</c> gives <c>ls</c>, <c>|</c> and <c>wc</c>. Quoting or a backslash turns an operator into plain text,
	/// so <c>'|'</c> and <c>\&gt;</c> are ordinary words. Digits that make up the whole word right before a redirect
	/// join it, as in <c>2&gt;</c>, and <c>2&gt;&amp;1</c> gives <c>2&gt;&amp;</c> and the word <c>1</c>, as in bash.
	/// <para>
	/// Inside one kind of quote the other kind is an ordinary character, so <c>"it's"</c> gives <c>it's</c>.
	/// Outside quotes a backslash makes the next character literal and is itself dropped, so <c>a\ b</c> gives
	/// <c>a b</c>. Inside double quotes a backslash only escapes <c>"</c>, <c>\</c>, <c>$</c>, <c>`</c> and a
	/// newline; before any other character it stays, so <c>"a\b"</c> gives <c>a\b</c>. Inside single quotes
	/// backslashes are always kept as typed. A quote that is never
	/// closed takes the rest of the line literally, and a backslash at the very end is kept as is. A real shell
	/// would ask for another line in both cases.
	/// </para>
	/// <para>
	/// An unquoted <c>#</c> at the start of a word begins a comment, which runs to the end of the line.
	/// </para>
	/// <para>
	/// Outside single quotes, <c>$NAME</c> is replaced by the shell variable's value, or the environment variable's when
	/// no shell variable has that name, and a name set in neither gives nothing. An
	/// unquoted <c>$NAME</c> that comes out empty adds no argument, like bash. A <c>$</c> not followed by a letter or
	/// underscore stays as typed. <c>${NAME}</c> marks where the name ends, so <c>${A}b</c> expands <c>A</c> and keeps
	/// <c>b</c>; a <c>${</c> that is never closed stays as typed. <c>$?</c> is the last command's exit status. Values
	/// aren't split on spaces.
	/// </para>
	/// </remarks>
	public static Word[] Split(string line, IReadOnlyDictionary<string, string>? variables = null)
	{
		_ = line.ArgumentNotNull();

		var words = new List<Word>();
		var word = new StringBuilder();

		// The quote character that's currently open, or null outside quotes.
		char? quote = null;

		// True right after an escaping backslash, so the next character goes into the word with no special meaning.
		var escaped = false;

		// Tracked apart from word.Length because '' on its own is still an argument, just an empty one.
		var hasWord = false;

		// Where the current word starts in the line, so digits typed right before a redirect can be told apart.
		var wordStart = 0;

		for (var index = 0; index < line.Length; index++)
		{
			var character = line[index];

			if (!hasWord)
			{
				wordStart = index;
			}

			if (escaped)
			{
				// Inside double quotes bash only escapes these; before anything else the backslash stays.
				if (quote == '"' && character is not ('"' or '\\' or '$' or '`' or '\n'))
				{
					_ = word.Append('\\');
				}

				_ = word.Append(character);
				escaped = false;
			}
			else
			{
				switch (quote)
				{
					case null or '"' when character == '\\':
						escaped = true;
						hasWord = true;

						break;
					case null or '"' when character == '$' && variables is not null && TryExpandVariable(line, index, variables, out var value, out var end):
						_ = word.Append(value);
						hasWord |= value.Length > 0;
						index = end;

						break;
					// POSIX starts a comment only at the start of a word, so the # in a#b and \#x is kept.
					case null when character == '#' && !hasWord:
						index = line.Length;

						break;
					case null when character is '\'' or '"':
						quote = character;
						hasWord = true;

						break;
					default:
						{
							if (character == quote)
							{
								quote = null;
							}
							else if (quote is null && OperatorAt(line, index) is { } typed)
							{
								var stream = hasWord && IsStreamNumber(line.AsSpan(wordStart, index - wordStart), typed) ? TakeWord() : string.Empty;
								var start = stream.Length > 0 ? wordStart : index;

								EndWord(index);
								words.Add(new Word(stream + typed, IsOperator: true, start, index + typed.Length));
								index += typed.Length - 1;
							}
							else if (quote is null && character is ' ' or '\t')
							{
								EndWord(index);
							}
							else
							{
								_ = word.Append(character);

								hasWord = true;
							}

							break;
						}
				}
			}
		}

		if (escaped)
		{
			_ = word.Append('\\');
		}

		EndWord(line.Length);

		return [.. words];

		// Gives the word read so far and starts a new one.
		string TakeWord()
		{
			var text = word.ToString();

			_ = word.Clear();
			hasWord = false;

			return text;
		}

		// Adds the word read so far, if there is one, ending at end.
		void EndWord(int end)
		{
			if (hasWord)
			{
				words.Add(new Word(TakeWord(), IsOperator: false, wordStart, end));
			}
		}
	}

	/// <summary>
	/// Reads the operator that starts at <paramref name="index" />, which the caller has checked is outside quotes and
	/// not escaped.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="index">The position to look at.</param>
	/// <returns>
	/// The longest operator in <see cref="Operators" /> that starts there, or <see langword="null" /> when none does.
	/// </returns>
	internal static string? OperatorAt(string line, int index)
	{
		if (!OperatorStarts.Contains(line.ArgumentNotNull()[index]))
		{
			return null;
		}

		foreach (var candidate in Operators)
		{
			if (line.AsSpan(index).StartsWith(candidate, StringComparison.Ordinal))
			{
				return candidate;
			}
		}

		return null;
	}

	/// <summary>
	/// Checks whether <paramref name="typed" />, the raw text of the word just before <paramref name="typedOperator" />,
	/// names the stream that the redirect works on, as the <c>2</c> in <c>2&gt;</c> does.
	/// </summary>
	/// <param name="typed">The word's characters as typed, quotes and backslashes included.</param>
	/// <param name="typedOperator">The operator right after the word.</param>
	/// <returns>
	/// <see langword="true" /> when <paramref name="typedOperator" /> starts with <c>&lt;</c> or <c>&gt;</c> and
	/// <paramref name="typed" /> is only digits, so the digits belong to the operator.
	/// </returns>
	/// <remarks>
	/// Bash reads such digits as an IO_NUMBER only when they are the whole word and unquoted, so <c>a2&gt;f</c>,
	/// <c>'2'&gt;f</c> and <c>\2&gt;f</c> keep <c>2</c> as part of an argument. Checking the raw text covers the
	/// quoted and escaped cases.
	/// </remarks>
	internal static bool IsStreamNumber(ReadOnlySpan<char> typed, string typedOperator) =>
		typed.Length > 0 && typedOperator.ArgumentNotNull()[0] is '<' or '>' && !typed.ContainsAnyExceptInRange('0', '9');

	/// <summary>
	/// Reads the <c>$NAME</c>, <c>${NAME}</c> or <c>$?</c> that starts at the <c>$</c> at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The command line being split.</param>
	/// <param name="index">The position of the <c>$</c>.</param>
	/// <param name="variables">
	/// The shell variables. A name in neither them nor the environment expands to an empty string.
	/// </param>
	/// <param name="value">The variable's value, or an empty string when no name follows.</param>
	/// <param name="end">The position of the last character the expansion used.</param>
	/// <returns>
	/// <see langword="false" /> when no form follows the <c>$</c>, so it stays as typed.
	/// </returns>
	private static bool TryExpandVariable(string line, int index, IReadOnlyDictionary<string, string> variables, out string value, out int end)
	{
		if (index + 1 < line.Length && line[index + 1] == '?')
		{
			value = Lookup(variables, "?");
			end = index + 1;

			return true;
		}

		if (index + 1 < line.Length && line[index + 1] == '{' && line.IndexOf('}', index + 2) is var close and > 0)
		{
			value = Lookup(variables, line[(index + 2)..close]);
			end = close;

			return true;
		}

		if (index + 1 < line.Length && (char.IsAsciiLetter(line[index + 1]) || line[index + 1] == '_'))
		{
			end = index + 1;

			while (end + 1 < line.Length && (char.IsAsciiLetterOrDigit(line[end + 1]) || line[end + 1] == '_'))
			{
				end++;
			}

			value = Lookup(variables, line[(index + 1)..(end + 1)]);

			return true;
		}

		value = string.Empty;
		end = index;

		return false;
	}

	/// <summary>
	/// Quotes <paramref name="text" /> as one word for a POSIX shell, which <see cref="Split" /> also reads back as one
	/// word.
	/// </summary>
	/// <param name="text">The text, such as a path.</param>
	/// <returns>
	/// The text in single quotes, with each <c>'</c> inside written as <c>'\''</c>.
	/// </returns>
	internal static string Quote(string text) => $"'{text.ArgumentNotNull().Replace("'", @"'\''", StringComparison.Ordinal)}'";

	/// <summary>
	/// Gets the value of the variable <paramref name="name" />.
	/// </summary>
	/// <param name="variables">The shell variables, which win over the environment.</param>
	/// <param name="name">The variable name.</param>
	/// <returns>
	/// The shell variable, else the environment variable, else an empty string.
	/// </returns>
	/// <remarks>
	/// POSIX makes every environment variable a shell variable when the shell starts, so <c>$PATH</c> and <c>$HOME</c>
	/// expand without being declared first.
	/// </remarks>
	internal static string Lookup(IReadOnlyDictionary<string, string> variables, string name) =>
		variables.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name) ?? string.Empty;

	/// <summary>
	/// Takes the redirections out of <paramref name="words" />: <c>&gt;</c> or <c>1&gt;</c> to write standard output,
	/// <c>&gt;&gt;</c> or <c>1&gt;&gt;</c> to append it, and <c>2&gt;</c> or <c>2&gt;&gt;</c> to write or append
	/// standard error, each followed by a file name.
	/// </summary>
	/// <param name="words">The words from <see cref="Split" />.</param>
	/// <returns>
	/// The words left for the command, plus the file name word for each stream, kept whole so an error can point at
	/// it. A path is <see langword="null" /> when that stream isn't redirected. <c>AppendOutput</c> and <c>AppendError</c> are <see langword="true" /> when that
	/// stream should go on the end of its file instead of replacing it. An operator with no file name after it, or
	/// another operator in its place, stays in the words. If the same stream is redirected twice, the last operator
	/// and file name win.
	/// </returns>
	public static (Word[] Words, Word? OutputPath, bool AppendOutput, Word? ErrorPath, bool AppendError)
		SplitRedirect(Word[] words)
	{
		_ = words.ArgumentNotNull();

		var remaining = new List<Word>();
		Word? outputPath = null;
		var appendOutput = false;
		Word? errorPath = null;
		var appendError = false;

		for (var index = 0; index < words.Length; index++)
		{
			var hasPath = index + 1 < words.Length && !words[index + 1].IsOperator;

			switch (hasPath)
			{
				case true when words[index] is { IsOperator: true, Text: ">" or "1>" }:
					outputPath = words[++index];
					appendOutput = false;

					break;
				case true when words[index] is { IsOperator: true, Text: ">>" or "1>>" }:
					outputPath = words[++index];
					appendOutput = true;

					break;
				case true when words[index] is { IsOperator: true, Text: "2>" }:
					errorPath = words[++index];
					appendError = false;

					break;
				case true when words[index] is { IsOperator: true, Text: "2>>" }:
					errorPath = words[++index];
					appendError = true;

					break;
				default:
					remaining.Add(words[index]);

					break;
			}
		}

		return ([.. remaining], outputPath, appendOutput, errorPath, appendError);
	}

	/// <summary>
	/// Splits <paramref name="words" /> at every <c>|</c> operator into the commands of a pipeline.
	/// </summary>
	/// <param name="words">The words from <see cref="Split" />, with redirects already taken out.</param>
	/// <returns>
	/// Each command's name followed by its arguments, in order. With no <c>|</c> this is one command. An empty array
	/// means the words can't run: some command was empty, as in <c>a |</c> or <c>a | | b</c>, or another operator was
	/// left over, such as <c>&lt;</c> or a <c>&gt;</c> with no file name.
	/// </returns>
	public static string[][] SplitPipeline(Word[] words)
	{
		_ = words.ArgumentNotNull();

		var stages = new List<string[]>();
		var start = 0;

		for (var index = 0; index <= words.Length; index++)
		{
			if (index != words.Length && words[index] is not { IsOperator: true, Text: "|" })
			{
				if (words[index].IsOperator)
				{
					return [];
				}

				continue;
			}

			if (index == start)
			{
				return [];
			}

			stages.Add([.. words[start..index].Select(word => word.Text)]);
			start = index + 1;
		}

		return [.. stages];
	}

	/// <summary>
	/// Gets the words of a line that only runs <c>man</c> or <c>dman</c>, which the terminal window shows in its own
	/// reader.
	/// </summary>
	/// <param name="line">The line the user entered.</param>
	/// <returns>
	/// The words, starting with <c>man</c> or <c>dman</c>, or <see langword="null" /> when the line is anything else,
	/// including either command with no page, with a redirect, pipe or other operator, or with other commands after
	/// <c>;</c>, <c>&amp;&amp;</c> or <c>||</c>. The shell runs those itself, and the page prints as text.
	/// </returns>
	public static string[]? PagedCommand(string line)
	{
		if (CommandList.Split(line.ArgumentNotNull()) is not { Count: 1 })
		{
			return null;
		}

		var (words, outputPath, _, errorPath, _) = SplitRedirect(Split(line));

		return words is [{ IsOperator: false, Text: "man" or "dman" }, _, ..] && outputPath is null && errorPath is null
			&& !Array.Exists(words, word => word.IsOperator)
			? [.. words.Select(word => word.Text)]
			: null;
	}
}
