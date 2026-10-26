using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Runs the command lines of a file in the current shell, so the variables, aliases and folder it sets stay set.
/// </summary>
/// <param name="name">
/// The name the command is typed as: <c>.</c>, the POSIX name, or <c>source</c>, the one bash and zsh add.
/// </param>
/// <param name="declare">The <c>declare</c> builtin, which holds the positional parameters.</param>
/// <param name="runLine">Runs one command line, the same way the shell runs a typed one, and gives its status.</param>
/// <remarks>
/// The shell also runs <c>~/.sharpierc</c> and script files with it. Arguments after the file name become the
/// positional parameters while the file runs, and the old ones come back after, as in bash, unless the file changed
/// them with <c>set</c> outside a function. With none, the file sees the caller's. One leading <c>--</c> is skipped.
/// </remarks>
internal sealed class SourceCommand(string name, DeclareCommand declare, Func<string, int> runLine) : ICommand
{
	/// <summary>
	/// How many files may be running inside one another before the next one is refused.
	/// </summary>
	/// <remarks>
	/// Stops a file that runs itself with an error instead of a stack overflow, which would end the shell and, for
	/// <c>~/.sharpierc</c>, every new tab with it.
	/// </remarks>
	internal const int MaxDepth = 100;

	/// <summary>
	/// How many files are running right now, one inside another.
	/// </summary>
	private int depth;

	/// <summary>
	/// Gets a value indicating whether this builtin is running a file, which <c>return</c> may end.
	/// </summary>
	/// <value>
	/// <see langword="true" /> from the file's first line to its last.
	/// </value>
	internal bool Running => this.depth > 0;

	/// <inheritdoc />
	public string Name => name;

	/// <inheritdoc />
	public string Description => "Runs the commands in a file in the current shell";

	/// <inheritdoc />
	/// <remarks>
	/// The status is the last line's, 0 for a file with no commands, 2 with no file named, or 1 when the file can't be
	/// run. A quote still open at the end of the file is reported with status 2, and that last command doesn't run, as
	/// in bash.
	/// <c>return</c> ends the file early with its own status.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		args = args is ["--", .. var rest] ? rest : args;

		if (args is [])
		{
			new ShellError("sharpie::source::missing_file", $"`{this.Name}` needs a file to run").Write();

			return 2;
		}

		if (this.depth >= MaxDepth)
		{
			new ShellError("sharpie::source::too_deep", $"`{args[0]}` is nested more than {MaxDepth} files deep")
			{
				Help = "a file that runs itself, directly or through another file, never ends",
			}.Write();

			return 1;
		}

		string[] lines;

		try
		{
			lines = File.ReadAllLines(Find(args[0]));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			new ShellError("sharpie::source::unreadable", $"Can't read `{args[0]}`: {exception.Message}").Write();

			return 1;
		}

		var caller = declare.Positional;
		var sets = declare.PositionalSetCount;

		if (args.Length > 1)
		{
			declare.SetPositional(args[1..]);
		}

		this.depth++;

		try
		{
			return ReturnCommand.Catch(() =>
			{
				var status = 0;

				foreach (var (number, line) in CommandList.JoinContinuedLines(lines))
				{
					declare.LineNumber = number;
					status = runLine(line);
				}

				return status;
			});
		}
		finally
		{
			this.depth--;

			if (args.Length > 1 && declare.PositionalSetCount == sets)
			{
				declare.SetPositional(caller);
			}
		}
	}

	/// <summary>
	/// Finds the file that <paramref name="file" /> names.
	/// </summary>
	/// <param name="file">The file name as typed.</param>
	/// <returns>
	/// The first match in a <c>PATH</c> folder when <paramref name="file" /> has no folder in it. Otherwise, and when
	/// <c>PATH</c> is unset or has no match, <paramref name="file" /> itself.
	/// </returns>
	/// <remarks>
	/// POSIX searches <c>PATH</c> for a name with no slash, and the file needn't be executable. bash then also tries the
	/// current folder, which is what returning <paramref name="file" /> unchanged does.
	/// </remarks>
	internal static string Find(string file)
	{
		_ = file.ArgumentNotNull();

		if (Path.GetFileName(file) != file || Environment.GetEnvironmentVariable("PATH") is not { } path)
		{
			return file;
		}

		var folders = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

		return folders.Select(folder => Path.Combine(folder, file)).FirstOrDefault(File.Exists) ?? file;
	}
}
