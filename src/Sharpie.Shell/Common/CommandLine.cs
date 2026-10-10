using System.Text;
using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Common;

/// <summary>
/// Breaks a typed command line into words the way a POSIX shell does.
/// </summary>
internal static class CommandLine
{
	/// <summary>
	/// Splits <paramref name="line" /> into words on spaces and tabs, keeping everything inside single or double
	/// quotes literally.
	/// </summary>
	/// <param name="line">The command line as the user typed it.</param>
	/// <param name="variables">
	/// The shell variables that <c>$NAME</c> expands from, or <see langword="null" /> to leave every <c>$</c> as typed.
	/// </param>
	/// <returns>
	/// The command name followed by its arguments, with the quote characters removed. Quoted and unquoted text that
	/// touch with no space between join into one word. An empty array means the line was blank.
	/// </returns>
	/// <remarks>
	/// Inside one kind of quote the other kind is an ordinary character, so <c>"it's"</c> gives <c>it's</c>.
	/// Outside quotes a backslash makes the next character literal and is itself dropped, so <c>a\ b</c> gives
	/// <c>a b</c>. Inside double quotes a backslash only escapes <c>"</c>, <c>\</c>, <c>$</c>, <c>`</c> and a
	/// newline; before any other character it stays, so <c>"a\b"</c> gives <c>a\b</c>. Inside single quotes
	/// backslashes are always kept as typed. A quote that is never
	/// closed takes the rest of the line literally, and a backslash at the very end is kept as is. A real shell
	/// would ask for another line in both cases.
	/// <para>
	/// Outside single quotes, <c>$NAME</c> is replaced by the variable's value, and an unset name gives nothing. An
	/// unquoted <c>$NAME</c> that comes out empty adds no argument, like bash. A <c>$</c> not followed by a letter or
	/// underscore stays as typed. <c>${NAME}</c> marks where the name ends, so <c>${A}b</c> expands <c>A</c> and keeps
	/// <c>b</c>; a <c>${</c> that is never closed stays as typed. Values aren't split on spaces.
	/// </para>
	/// </remarks>
	public static string[] Split(string line, IReadOnlyDictionary<string, string>? variables = null)
	{
		_ = line.ArgumentNotNull();

		var words = new List<string>();
		var word = new StringBuilder();

		// The quote character that's currently open, or null outside quotes.
		char? quote = null;

		// True right after an escaping backslash, so the next character goes into the word with no special meaning.
		var escaped = false;

		// Tracked apart from word.Length because '' on its own is still an argument, just an empty one.
		var hasWord = false;

		for (var index = 0; index < line.Length; index++)
		{
			var character = line[index];

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
							else if (quote is null && character is ' ' or '\t')
							{
								if (hasWord)
								{
									words.Add(word.ToString());
									_ = word.Clear();

									hasWord = false;
								}
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

		if (hasWord)
		{
			words.Add(word.ToString());
		}

		return [.. words];
	}

	/// <summary>
	/// Reads the <c>$NAME</c> or <c>${NAME}</c> that starts at the <c>$</c> at <paramref name="index" />.
	/// </summary>
	/// <param name="line">The command line being split.</param>
	/// <param name="index">The position of the <c>$</c>.</param>
	/// <param name="variables">The shell variables. An unset name expands to an empty string.</param>
	/// <param name="value">The variable's value, or an empty string when no name follows.</param>
	/// <param name="end">The position of the last character the expansion used.</param>
	/// <returns>
	/// <see langword="false" /> when neither form follows the <c>$</c>, so it stays as typed.
	/// </returns>
	private static bool TryExpandVariable(string line, int index, IReadOnlyDictionary<string, string> variables, out string value, out int end)
	{
		if (index + 1 < line.Length && line[index + 1] == '{' && line.IndexOf('}', index + 2) is var close and > 0)
		{
			value = variables.GetValueOrDefault(line[(index + 2)..close], string.Empty);
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

			value = variables.GetValueOrDefault(line[(index + 1)..(end + 1)], string.Empty);

			return true;
		}

		value = string.Empty;
		end = index;

		return false;
	}

	/// <summary>
	/// Takes the redirections out of <paramref name="words" />: <c>&gt;</c> or <c>1&gt;</c> to write standard output,
	/// <c>&gt;&gt;</c> or <c>1&gt;&gt;</c> to append it, and <c>2&gt;</c> or <c>2&gt;&gt;</c> to write or append
	/// standard error, each followed by a file name.
	/// </summary>
	/// <param name="words">The words from <see cref="Split" />.</param>
	/// <returns>
	/// The words left for the command, plus the file name for each stream. A path is <see langword="null" /> when
	/// that stream isn't redirected. <c>AppendOutput</c> and <c>AppendError</c> are <see langword="true" /> when that
	/// stream should go on the end of its file instead of replacing it. An operator with no file name after it stays in the words as
	/// plain text. If the same stream is redirected twice, the last operator and file name win.
	/// </returns>
	/// <remarks>
	/// This works on words that are already split, so a quoted <c>'&gt;'</c> also counts as a redirect, and
	/// <c>echo hi&gt;file</c> with no spaces isn't seen. Fixing both means having <see cref="Split" /> return
	/// operators as their own tokens. The test inputs always put spaces around the operator.
	/// </remarks>
	public static (string[] Words, string? OutputPath, bool AppendOutput, string? ErrorPath, bool AppendError)
		SplitRedirect(string[] words)
	{
		_ = words.ArgumentNotNull();

		var remaining = new List<string>();
		string? outputPath = null;
		var appendOutput = false;
		string? errorPath = null;
		var appendError = false;

		for (var index = 0; index < words.Length; index++)
		{
			var hasPath = index + 1 < words.Length;

			switch (hasPath)
			{
				case true when words[index] is ">" or "1>":
					outputPath = words[++index];
					appendOutput = false;

					break;
				case true when words[index] is ">>" or "1>>":
					outputPath = words[++index];
					appendOutput = true;

					break;
				case true when words[index] == "2>":
					errorPath = words[++index];
					appendError = false;

					break;
				case true when words[index] == "2>>":
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
	/// Splits <paramref name="words" /> at every <c>|</c> into the commands of a pipeline.
	/// </summary>
	/// <param name="words">The words from <see cref="Split" />.</param>
	/// <returns>
	/// Each command's name followed by its arguments, in order. With no <c>|</c> this is one command. An empty array
	/// means some command was empty, as in <c>a |</c> or <c>a | | b</c>, and the line isn't a pipeline.
	/// </returns>
	/// <remarks>
	/// Like <see cref="SplitRedirect" />, this works on words that are already split, so a quoted <c>'|'</c> also
	/// counts as a bar.
	/// </remarks>
	public static string[][] SplitPipeline(string[] words)
	{
		_ = words.ArgumentNotNull();

		var stages = new List<string[]>();
		var start = 0;

		for (var index = 0; index <= words.Length; index++)
		{
			if (index != words.Length && words[index] != "|")
			{
				continue;
			}

			if (index == start)
			{
				return [];
			}

			stages.Add(words[start..index]);
			start = index + 1;
		}

		return [.. stages];
	}

	/// <summary>
	/// Gets the arguments of a line that only runs <c>man</c>, which the terminal window shows in its own reader.
	/// </summary>
	/// <param name="line">The line the user entered.</param>
	/// <returns>
	/// The arguments after <c>man</c>, or <see langword="null" /> when the line is anything else, including
	/// <c>man</c> with no page or with a redirect or pipe. The shell runs those itself, and the page prints as text.
	/// </returns>
	public static string[]? ManArguments(string line)
	{
		var (words, outputPath, _, errorPath, _) = SplitRedirect(Split(line.ArgumentNotNull()));

		return words is ["man", _, ..] && outputPath is null && errorPath is null && !words.Contains("|")
			? words[1..]
			: null;
	}
}
