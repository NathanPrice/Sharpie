using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Common;

/// <summary>
/// The way the shell was started, read from its name and arguments as <c>sh</c> reads them.
/// </summary>
/// <param name="Login">
/// <see langword="true" /> for a login shell, which first loads <c>/etc/profile</c> and <c>~/.profile</c>.
/// </param>
/// <param name="Interactive"><see langword="true" /> when <c>-i</c> asked for an interactive shell.</param>
/// <param name="NoRc"><see langword="true" /> when <c>--norc</c> asked to skip <c>~/.sharpierc</c>.</param>
/// <param name="Command">The command string after <c>-c</c>, or <see langword="null" /> without <c>-c</c>.</param>
/// <param name="ScriptFile">The file of commands to run, or <see langword="null" /> when there's none.</param>
/// <param name="Arguments">
/// The words after the command or script file. With <c>-c</c> the first sets <c>$0</c> and the rest are the positional
/// parameters; with a script file they're all positional parameters.
/// </param>
/// <param name="NoClobber">
/// <see langword="true" /> when <c>-C</c> asked for <c>set -C</c> from the start, so <c>&gt;</c> won't replace a file.
/// </param>
internal sealed record ShellOptions(bool Login, bool Interactive, bool NoRc, string? Command, string? ScriptFile, IReadOnlyList<string> Arguments, bool NoClobber = false)
{
	/// <summary>
	/// Reads the options the way POSIX <c>sh</c> and bash do.
	/// </summary>
	/// <param name="programName">
	/// The shell's <c>argv[0]</c>, which starts with <c>-</c> for a login shell, or <see langword="null" /> when it
	/// isn't known.
	/// </param>
	/// <param name="args">The arguments after the program name.</param>
	/// <param name="error">The reason the arguments were refused, or an empty string when they weren't.</param>
	/// <returns>
	/// The options, or <see langword="null" /> for an unknown option or a <c>-c</c> with no command after it.
	/// </returns>
	/// <remarks>
	/// Single-letter options can be joined, so <c>-lc</c> is <c>-l -c</c>, as login tools and editors pass them.
	/// <c>--</c> or a lone <c>-</c> ends the options. The first word after them is the command with <c>-c</c> and the
	/// script file without it. The words after that go into <see cref="Arguments" />.
	/// </remarks>
	public static ShellOptions? Parse(string? programName, IReadOnlyList<string> args, out string error)
	{
		_ = args.ArgumentNotNull();

		var login = programName?.StartsWith('-') == true;
		var interactive = false;
		var noRc = false;
		var noClobber = false;
		var command = false;
		var index = 0;

		error = string.Empty;

		for (; index < args.Count && args[index].StartsWith('-'); index++)
		{
			var arg = args[index];

			if (arg is "-" or "--")
			{
				index++;

				break;
			}

			if (arg == "--login")
			{
				login = true;

				continue;
			}

			if (arg == Program.NoRcOption)
			{
				noRc = true;

				continue;
			}

			if (arg.StartsWith("--", StringComparison.Ordinal))
			{
				error = $"{arg}: invalid option";

				return null;
			}

			foreach (var letter in arg[1..])
			{
				switch (letter)
				{
					case 'c':
						command = true;

						break;
					case 'i':
						interactive = true;

						break;
					case 'l':
						login = true;

						break;
					case 'C':
						noClobber = true;

						break;
					default:
						error = $"-{letter}: invalid option";

						return null;
				}
			}
		}

		var operand = index < args.Count ? args[index] : null;

		if (command && operand is null)
		{
			error = "-c: option requires an argument";

			return null;
		}

		return new ShellOptions(login, interactive, noRc, command ? operand : null, command ? null : operand, [.. args.Skip(index + 1)], noClobber);
	}

	/// <summary>
	/// Decides whether the shell is interactive, by the POSIX rule.
	/// </summary>
	/// <param name="terminal">
	/// <see langword="true" /> when standard input and standard error are both terminals.
	/// </param>
	/// <returns>
	/// <see langword="true" /> with <c>-i</c>, or with no command or script file to run while on a terminal.
	/// </returns>
	public bool IsInteractive(bool terminal) => this.Interactive || (this.Command is null && this.ScriptFile is null && terminal);
}
