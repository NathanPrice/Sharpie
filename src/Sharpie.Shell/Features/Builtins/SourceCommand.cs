using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Runs the command lines of a file in the current shell, so the variables, aliases and folder it sets stay set.
/// </summary>
/// <param name="name">
/// The name the command is typed as: <c>.</c>, the POSIX name, or <c>source</c>, the one bash and zsh add.
/// </param>
/// <param name="runLine">Runs one command line, the same way the shell runs a typed one.</param>
/// <remarks>
/// The shell also runs <c>~/.sharpierc</c> with it at startup. Arguments after the file name are ignored, since
/// Sharpie has no positional parameters.
/// </remarks>
internal sealed class SourceCommand(string name, Action<string> runLine) : ICommand
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

	/// <inheritdoc />
	public string Name => name;

	/// <inheritdoc />
	public string Description => "Runs the commands in a file in the current shell";

	/// <inheritdoc />
	public void Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args is [])
		{
			Console.Error.WriteLine($"{this.Name}: filename argument required");

			return;
		}

		if (this.depth >= MaxDepth)
		{
			Console.Error.WriteLine($"{this.Name}: {args[0]}: files nested more than {MaxDepth} deep");

			return;
		}

		string[] lines;

		try
		{
			lines = File.ReadAllLines(Find(args[0]));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			Console.Error.WriteLine($"{this.Name}: {args[0]}: {exception.Message}");

			return;
		}

		this.depth++;

		try
		{
			foreach (var line in JoinContinuedLines(lines))
			{
				runLine(line);
			}
		}
		finally
		{
			this.depth--;
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

	/// <summary>
	/// Joins each line that ends in an unescaped backslash to the line after it, as POSIX does.
	/// </summary>
	/// <param name="lines">The lines of the file.</param>
	/// <returns>
	/// The command lines, with each joining backslash removed.
	/// </returns>
	/// <remarks>
	/// The backslash counts when there's an odd number of them at the end, so <c>\\</c>, an escaped backslash, doesn't
	/// join. A backslash inside single quotes joins too, where POSIX would keep it.
	/// </remarks>
	internal static IEnumerable<string> JoinContinuedLines(IEnumerable<string> lines)
	{
		_ = lines.ArgumentNotNull();

		var pending = string.Empty;

		foreach (var line in lines)
		{
			var joined = pending + line;

			// The run of backslashes the line ends with.
			if (joined[joined.TrimEnd('\\').Length..].Length % 2 == 1)
			{
				pending = joined[..^1];
			}
			else
			{
				pending = string.Empty;

				yield return joined;
			}
		}

		if (pending.Length > 0)
		{
			yield return pending;
		}
	}
}
