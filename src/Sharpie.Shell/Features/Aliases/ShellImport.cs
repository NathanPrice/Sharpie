using System.Collections;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using DotNetTips.Spargine.Core;
using Serilog;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Aliases;

/// <summary>
/// Copies the exported variables and aliases of the user's own login shell, such as <c>PATH</c> from
/// <c>~/.zshrc</c>, into this shell.
/// </summary>
/// <remarks>
/// A window opened from the desktop menu gets the desktop's environment, which lacks whatever the user's shell
/// startup files add. Those files are written in that shell's own language, so this shell can't read them itself.
/// Instead it asks the shell once, the way VS Code does for apps started from the desktop.
/// </remarks>
internal static class ShellImport
{
	/// <summary>
	/// The argument that makes <c>Sharpie</c> print its environment and exit, which the imported shell runs to
	/// report what its startup files exported.
	/// </summary>
	/// <remarks>
	/// Done by this program rather than <c>env -0</c>, which only GNU has, or <c>export -p</c>, which each shell quotes
	/// in its own way.
	/// </remarks>
	internal const string PrintEnvironmentOption = "--print-environment";

	/// <summary>
	/// How long the user's shell gets to start up and answer before the import gives up.
	/// </summary>
	/// <remarks>
	/// The user's zsh setup answers in about a third of a second. The import holds back the first prompt of every new
	/// tab, so a startup file that hangs mustn't hold it back for long.
	/// </remarks>
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

	/// <summary>
	/// The POSIX shell that <see cref="RunProfile" /> loads the profile with.
	/// </summary>
	private const string ProfileShell = "/bin/sh";

	/// <summary>
	/// The variables that describe the asking shell itself rather than the user's setup, so they're never copied.
	/// </summary>
	private static readonly HashSet<string> Skipped = new(["PWD", "OLDPWD", "SHLVL", "_"], StringComparer.Ordinal);

	/// <summary>
	/// Syntax an imported alias value can't use here: command lists, <c>&amp;&amp;</c>, <c>||</c> and command
	/// substitution.
	/// </summary>
	/// <remarks>
	/// An alias is expanded after the line is cut into commands, so a <c>;</c>, <c>&amp;&amp;</c> or <c>||</c> in its
	/// value would be passed to the program as an argument. Command substitution isn't read at all.
	/// </remarks>
	private static readonly string[] UnsupportedSyntax = [";", "&&", "||", "$(", "`"];

	/// <summary>
	/// Runs <paramref name="shell" /> as an interactive login shell, then sets every variable it exported and defines
	/// every alias it has.
	/// </summary>
	/// <param name="shell">The user's shell from <c>$SHELL</c>, or <see langword="null" /> when it's unset.</param>
	/// <param name="self">The full path of this <c>Sharpie</c> program.</param>
	/// <param name="aliases">The <c>alias</c> builtin the aliases are added to.</param>
	/// <param name="builtins">The builtin command names, which an imported alias may start with.</param>
	/// <param name="timeout">How long to wait for the answer, or <see langword="null" /> for five seconds.</param>
	/// <remarks>
	/// Does nothing on Windows, which has no <c>$SHELL</c>, when <paramref name="shell" /> is empty, or when it is this
	/// shell. If the shell can't be started, doesn't answer in time, or answers in a form that can't be read, one line
	/// says so on standard error and nothing is imported.
	/// <para>
	/// The shell runs in a new session with no terminal. An interactive zsh opens the terminal and takes it over, and
	/// prompt themes like powerlevel10k draw on it directly; without its own session either would reach the window.
	/// Whatever its startup files print, such as a fastfetch banner, lands outside the markers and is dropped.
	/// </para>
	/// </remarks>
	public static void Run(string? shell, string self, AliasCommand aliases, IReadOnlyCollection<string> builtins, TimeSpan? timeout = null)
	{
		_ = self.ArgumentNotNull();
		_ = aliases.ArgumentNotNull();
		_ = builtins.ArgumentNotNull();

		if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(shell))
		{
			return;
		}

		var shellPath = Path.IsPathRooted(shell) ? shell : PathFinder.FindExecutable(shell);

		if (shellPath == self)
		{
			return;
		}

		Import(shell, shellPath, ["-l", "-i"], self, aliases, builtins, timeout);
	}

	/// <summary>
	/// Sets the variables that <c>/etc/profile</c> and <c>~/.profile</c> export, for a login shell.
	/// </summary>
	/// <param name="self">The full path of this <c>Sharpie</c> program.</param>
	/// <param name="aliases">The <c>alias</c> builtin, given any alias the profile defines.</param>
	/// <param name="builtins">The builtin command names, which an imported alias may start with.</param>
	/// <param name="timeout">How long to wait for the answer, or <see langword="null" /> for five seconds.</param>
	/// <remarks>
	/// <para>
	/// Those files are POSIX shell scripts, so <c>/bin/sh -l</c> runs them and reports back, the same way
	/// <see cref="Run" /> asks the user's shell. plasmalogin and SDDM load the profile this way for csh and fish,
	/// whose own language can't read it either. On macOS <c>/etc/profile</c> runs <c>path_helper</c>, which builds
	/// <c>PATH</c>.
	/// </para>
	/// <para>
	/// The shell isn't made interactive, as those login managers don't. Arch's <c>/etc/profile</c> then leaves out
	/// <c>/etc/bash.bashrc</c>, which is meant for an interactive bash. Does nothing on Windows, which has no profile.
	/// If <c>sh</c> fails, one line says so and the login goes on with the variables it was started with.
	/// </para>
	/// </remarks>
	public static void RunProfile(string self, AliasCommand aliases, IReadOnlyCollection<string> builtins, TimeSpan? timeout = null)
	{
		_ = self.ArgumentNotNull();
		_ = aliases.ArgumentNotNull();
		_ = builtins.ArgumentNotNull();

		if (!OperatingSystem.IsWindows())
		{
			Import(ProfileShell, ProfileShell, ["-l"], self, aliases, builtins, timeout);
		}
	}

	/// <summary>
	/// Runs <paramref name="shellPath" /> with <paramref name="flags" />, then sets every variable it exported and
	/// defines every alias it has.
	/// </summary>
	/// <param name="shell">The shell as the error message names it.</param>
	/// <param name="shellPath">The shell's full path, or <see langword="null" /> when it isn't on <c>PATH</c>.</param>
	/// <param name="flags">The options it starts with, before <c>-c</c>.</param>
	/// <param name="self">The full path of this <c>Sharpie</c> program.</param>
	/// <param name="aliases">The <c>alias</c> builtin the aliases are added to.</param>
	/// <param name="builtins">The builtin command names, which an imported alias may start with.</param>
	/// <param name="timeout">How long to wait for the answer, or <see langword="null" /> for five seconds.</param>
	private static void Import(string shell, string? shellPath, string[] flags, string self, AliasCommand aliases, IReadOnlyCollection<string> builtins, TimeSpan? timeout)
	{
		var marker = Guid.NewGuid().ToString("N");
		var script = $"printf %s {marker}; {CommandLine.Quote(self)} {PrintEnvironmentOption}; printf %s {marker}; alias; printf %s {marker}";
		string? output;

		try
		{
			output = shellPath is null ? null : Capture(shellPath, [shellPath, .. flags, "-c", script], marker, timeout ?? DefaultTimeout);
		}
		catch (Win32Exception exception)
		{
			Log.Warning(exception, "Can't start {Shell} to import its aliases and variables", shellPath);
			output = null;
		}

		if (output is null || Parse(output, marker) is not var (environment, found))
		{
			new ShellError("sharpie::import::failed", $"Couldn't read settings from `{shell}`").Write();

			return;
		}

		foreach (var (name, value) in environment)
		{
			Environment.SetEnvironmentVariable(name, value);
		}

		var imported = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var (name, value) in found)
		{
			imported[name] = value;
		}

		foreach (var (name, value) in imported.Where(entry => IsUsable(entry.Value, imported, builtins)))
		{
			aliases.Define(name, value);
		}
	}

	/// <summary>
	/// Checks whether this shell can run an imported alias value.
	/// </summary>
	/// <param name="value">The alias value.</param>
	/// <param name="imported">Every imported alias, by name, which <paramref name="value" /> may start with.</param>
	/// <param name="builtins">The builtin command names.</param>
	/// <returns>
	/// <see langword="true" /> when <paramref name="value" /> uses no syntax this shell lacks and starts with a builtin,
	/// a program, or another imported alias that does.
	/// </returns>
	/// <remarks>
	/// Startup files written for zsh define aliases that only work there. oh-my-zsh has <c>sudo='nocorrect sudo'</c>,
	/// where <c>nocorrect</c> is a zsh keyword, and <c>history='omz_history'</c>, which names a zsh function. Imported
	/// as they are, they would break the commands they're named after.
	/// </remarks>
	internal static bool IsUsable(string value, IReadOnlyDictionary<string, string> imported, IReadOnlyCollection<string> builtins)
	{
		_ = value.ArgumentNotNull();
		_ = imported.ArgumentNotNull();
		_ = builtins.ArgumentNotNull();

		if (UnsupportedSyntax.Any(syntax => value.Contains(syntax, StringComparison.Ordinal)))
		{
			return false;
		}

		var used = new HashSet<string>(StringComparer.Ordinal);

		while (CommandLine.Split(value) is [{ Text: var command }, ..])
		{
			if (builtins.Contains(command))
			{
				return true;
			}

			// An alias that starts with its own name, like ls='ls -p', runs the program of that name.
			if (!used.Add(command) || !imported.TryGetValue(command, out var next))
			{
				return PathFinder.FindExecutable(command) is not null;
			}

			value = next;
		}

		return false;
	}

	/// <summary>
	/// Writes this process's environment to standard output for <see cref="Run" /> to read.
	/// </summary>
	/// <remarks>
	/// Each variable is written as <c>NAME=VALUE</c> followed by a NUL character, the one character no name or value
	/// can hold.
	/// </remarks>
	public static void PrintEnvironment()
	{
		foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
		{
			Console.Out.Write($"{entry.Key}={entry.Value}\0");
		}

		Console.Out.Flush();
	}

	/// <summary>
	/// Reads the variables and aliases out of what the asked shell printed.
	/// </summary>
	/// <param name="output">Everything the shell printed.</param>
	/// <param name="marker">The text printed before the environment, between it and the aliases, and after them.</param>
	/// <returns>
	/// The variables to set and the aliases to define, in the order printed, or <see langword="null" /> when the three
	/// markers aren't all there.
	/// </returns>
	/// <remarks>
	/// zsh lists an alias as <c>name='value'</c>, and bash, dash and ksh as <c>alias name='value'</c>. Both quote the
	/// value the way <see cref="CommandLine.Split" /> reads. A line that doesn't come out as one name and one value,
	/// like fish's <c>alias name 'value'</c>, is skipped.
	/// </remarks>
	internal static (List<(string Name, string Value)> Environment, List<(string Name, string Value)> Aliases)? Parse(string output, string marker)
	{
		_ = output.ArgumentNotNull();

		var parts = output.Split(marker);

		if (parts.Length < 4)
		{
			return null;
		}

		var environment = new List<(string, string)>();

		foreach (var entry in parts[1].Split('\0', StringSplitOptions.RemoveEmptyEntries))
		{
			if (entry.Split('=', 2) is [{ Length: > 0 } name, var value] && !Skipped.Contains(name))
			{
				environment.Add((name, value));
			}
		}

		var aliases = new List<(string, string)>();

		foreach (var line in parts[2].Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var definition = line.TrimEnd('\r');

			if (definition.StartsWith("alias ", StringComparison.Ordinal))
			{
				definition = definition["alias ".Length..];
			}

			if (definition.Split('=', 2) is [{ Length: > 0 } name, var quoted] && CommandLine.Split(quoted) is [{ IsOperator: false, Text: var value }])
			{
				aliases.Add((name, value));
			}
		}

		return (environment, aliases);
	}

	/// <summary>
	/// Starts the shell in a new session and collects what it prints until the last marker.
	/// </summary>
	/// <param name="path">The shell's full path.</param>
	/// <param name="arguments">The shell's <c>argv</c>, starting with its name.</param>
	/// <param name="marker">The marker the script prints three times.</param>
	/// <param name="timeout">How long to wait.</param>
	/// <returns>
	/// The output, or <see langword="null" /> when the shell didn't print the last marker in time.
	/// </returns>
	/// <exception cref="Win32Exception">The shell couldn't be started.</exception>
	private static string? Capture(string path, string[] arguments, string marker, TimeSpan timeout)
	{
		using var devNull = new FileStream("/dev/null", FileMode.Open, FileAccess.ReadWrite);
		using var pipe = new AnonymousPipeServerStream(PipeDirection.In);
		var nullFile = (int)devNull.SafeFileHandle.DangerousGetHandle();

		var pid = PosixSpawn.Start(path, arguments, -1, nullFile, (int)pipe.ClientSafePipeHandle.DangerousGetHandle(), nullFile, newSession: true);

		// Without closing this copy the pipe would never report its end.
		pipe.DisposeLocalCopyOfClientHandle();

		var reading = Task.Run(() => ReadUntilLastMarker(pipe, marker));
		var answered = reading.Wait(timeout);

		// The shell leads its own group, so this also stops whatever its startup files left running in it, which could
		// otherwise keep the pipe open. A shell that already answered loses only its exit.
		_ = NativeMethods.Kill(-pid, 9);

		while (NativeMethods.WaitPid(pid, out _, 0) < 0 && Marshal.GetLastPInvokeError() == NativeMethods.Interrupted)
		{
		}

		return answered ? reading.Result : null;
	}

	/// <summary>
	/// Reads <paramref name="stream" /> until <paramref name="marker" /> has appeared three times or the stream ends.
	/// </summary>
	/// <param name="stream">The shell's output.</param>
	/// <param name="marker">The marker.</param>
	/// <returns>
	/// Everything read.
	/// </returns>
	/// <remarks>
	/// It doesn't wait for the end, because a program the startup files sent into the background can hold the pipe
	/// open long after the shell is done.
	/// </remarks>
	private static string ReadUntilLastMarker(Stream stream, string marker)
	{
		using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
		var text = new StringBuilder();
		var buffer = new char[4096];
		int count;

		while ((count = reader.Read(buffer)) > 0)
		{
			_ = text.Append(buffer, 0, count);

			if (text.ToString().Split(marker).Length >= 4)
			{
				break;
			}
		}

		return text.ToString();
	}
}
