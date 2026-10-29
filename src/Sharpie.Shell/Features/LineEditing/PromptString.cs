using System.Globalization;
using System.Net;
using System.Text;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Time;

namespace Sharpie.Shell.Features.LineEditing;

/// <summary>
/// Turns a <c>PS1</c> value into the prompt it shows, as bash does.
/// </summary>
/// <remarks>
/// The backslash escapes are replaced first, then <c>$NAME</c>, <c>$(...)</c> and the rest are expanded as inside
/// double quotes, with the quotes themselves kept. Each escape's value is backslash-quoted before that, so a folder
/// named <c>a$(rm x)</c> shown by <c>\w</c> stays text and never runs.
/// </remarks>
internal static class PromptString
{
	/// <summary>
	/// Expands <paramref name="ps1" /> into the prompt.
	/// </summary>
	/// <param name="ps1">The value of <c>PS1</c>.</param>
	/// <param name="directory">The shell's working directory, for <c>\w</c> and <c>\W</c>.</param>
	/// <param name="variables">The shell variables.</param>
	/// <param name="substitute">Runs a <c>$(...)</c> or backquotes for its output.</param>
	/// <param name="historyNumber">This command's number in the history, for <c>\!</c>.</param>
	/// <param name="commandNumber">How many commands this shell has read, counting this one, for <c>\#</c>.</param>
	/// <returns>
	/// The prompt, still holding <c>\x01</c> and <c>\x02</c> around the text that <c>\[</c> and <c>\]</c> marked.
	/// </returns>
	/// <remarks>
	/// An escape bash doesn't have stays as typed, as do <c>\D{...}</c> and <c>\l</c>, which this shell doesn't
	/// support.
	/// </remarks>
	internal static string Expand(string ps1, string directory, IReadOnlyDictionary<string, string> variables, Func<string, string> substitute, int historyNumber, int commandNumber)
	{
		var decoded = new StringBuilder();

		for (var index = 0; index < ps1.Length; index++)
		{
			if (ps1[index] != '\\' || index + 1 == ps1.Length)
			{
				_ = decoded.Append(ps1[index]);

				continue;
			}

			var code = ps1[++index];

			// bash reads exactly three octal digits, and like \\ leaves the character unquoted, so \044 starts an
			// expansion.
			if (index + 3 <= ps1.Length && !ps1.AsSpan(index, 3).ContainsAnyExceptInRange('0', '7'))
			{
				_ = decoded.Append((char)Convert.ToInt32(ps1.Substring(index, 3), 8));
				index += 2;

				continue;
			}

			_ = Escape(code, directory, variables, historyNumber, commandNumber) switch
			{
				null => decoded.Append('\\').Append(code),
				var value when code == '\\' => decoded.Append(value),
				var value => decoded.Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)),
			};
		}

		return CommandLine.ExpandBody(decoded.ToString(), variables, substitute: substitute);
	}

	/// <summary>
	/// Removes the <c>\[</c> and <c>\]</c> markers from an expanded prompt and measures what's left.
	/// </summary>
	/// <param name="prompt">The prompt from <see cref="Expand" />.</param>
	/// <returns>
	/// The prompt to write, and how many columns it takes: every character outside the markers counts as one.
	/// </returns>
	internal static (string Text, int Width) Visible(string prompt)
	{
		var text = new StringBuilder(prompt.Length);
		var width = 0;
		var hidden = false;

		foreach (var character in prompt)
		{
			if (character is '\x01' or '\x02')
			{
				hidden = character == '\x01';

				continue;
			}

			_ = text.Append(character);
			width += hidden ? 0 : 1;
		}

		return (text.ToString(), width);
	}

	/// <summary>
	/// Splits an expanded prompt into the lines printed once and the last line, which the line editor redraws.
	/// </summary>
	/// <param name="expanded">The prompt from <see cref="Expand" />.</param>
	/// <returns>
	/// Every line but the last, with its markers removed and each line break written as <c>\r\n</c> since the editor
	/// writes to a raw terminal, and the last line, markers kept.
	/// </returns>
	internal static (string Header, string Prompt) Split(string expanded)
	{
		var lastBreak = expanded.LastIndexOf('\n');

		return (Visible(expanded[..(lastBreak + 1)]).Text.Replace("\n", "\r\n", StringComparison.Ordinal), expanded[(lastBreak + 1)..]);
	}

	// The value of one backslash escape, or null for one bash doesn't have.
	private static string? Escape(char code, string directory, IReadOnlyDictionary<string, string> variables, int historyNumber, int commandNumber) => code switch
	{
		'u' => Environment.UserName,
		'h' => Dns.GetHostName().Split('.')[0],
		'H' => Dns.GetHostName(),
		'w' => PromptHeader.ShortenHome(directory),
		'W' => PromptHeader.SplitForDisplay(directory).Name,
		'$' => Environment.IsPrivilegedProcess ? "#" : "$",
		's' => Path.GetFileName(CommandLine.Lookup(variables, "0")),
		'v' => typeof(PromptString).Assembly.GetName().Version?.ToString(2),
		'V' => typeof(PromptString).Assembly.GetName().Version?.ToString(3),
		'j' => ExternalProgram.Jobs.Count.ToString(CultureInfo.InvariantCulture),
		'!' => historyNumber.ToString(CultureInfo.InvariantCulture),
		'#' => commandNumber.ToString(CultureInfo.InvariantCulture),
		'd' => ShellTimeZone.Now().ToString("ddd MMM dd", CultureInfo.InvariantCulture),
		't' => ShellTimeZone.Now().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
		'T' => ShellTimeZone.Now().ToString("hh:mm:ss", CultureInfo.InvariantCulture),
		'@' => ShellTimeZone.Now().ToString("hh:mm tt", CultureInfo.InvariantCulture),
		'A' => ShellTimeZone.Now().ToString("HH:mm", CultureInfo.InvariantCulture),
		'n' => "\n",
		'r' => "\r",
		'a' => "\a",
		'e' => "\e",
		'\\' => "\\",

		// bash keeps \" for the expansion, which drops the backslash as inside double quotes.
		'"' => "\"",
		'[' => "\x01",
		']' => "\x02",
		_ => null,
	};
}
