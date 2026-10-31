using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Common;

/// <summary>
/// Runs the list of a <c>&lt;(...)</c> or <c>&gt;(...)</c> in a copy of the shell joined to a pipe, and gives the path
/// the command opens to read what the list writes, or to write what the list reads.
/// </summary>
/// <remarks>
/// The copy runs alongside the command, so <c>diff &lt;(sort a) &lt;(sort b)</c> streams and
/// <c>while read l; do ...; done &lt; &lt;(cmd)</c> reads as <c>cmd</c> writes. The shell doesn't wait for the copy,
/// as bash doesn't. Each pipe stays open until the pipeline it was expanded for ends, through
/// <see cref="Scoped" />, so a <c>for f in &lt;(...)</c> loop can still read it in its body.
/// </remarks>
internal static partial class ProcessSubstitution
{
	/// <summary>
	/// The pipes open now, oldest first.
	/// </summary>
	private static readonly List<Opened> Pipes = [];

	/// <summary>
	/// Gets or sets what gives the command line that runs a list in a copy of the shell.
	/// </summary>
	/// <value>
	/// Takes the list's text, and gives the program's path, then its arguments, which put the shell's functions,
	/// variables and options in front of the list. The shell sets it when it starts.
	/// </value>
	internal static Func<string, string[]>? CopyCommandLine { get; set; }

	/// <summary>
	/// Gets the highest descriptor a process substitution holds open, which every program started meanwhile gets.
	/// </summary>
	/// <value>
	/// The descriptor, or -1 when none is open.
	/// </value>
	internal static int HighestDescriptor => Pipes.Count == 0 ? -1 : Pipes.Max(pipe => pipe.Descriptor);

	/// <summary>
	/// Starts <paramref name="list" /> in a copy of the shell joined to a new pipe.
	/// </summary>
	/// <param name="list">The text between the parentheses.</param>
	/// <param name="writes">
	/// <see langword="true" /> for <c>&gt;(...)</c>, where the command writes and the list reads, or
	/// <see langword="false" /> for <c>&lt;(...)</c>, where the list writes and the command reads.
	/// </param>
	/// <returns>
	/// The path of the command's end of the pipe: <c>/dev/fd/N</c> on Linux and macOS, or a named pipe on Windows.
	/// </returns>
	/// <exception cref="InvalidOperationException"><see cref="CopyCommandLine" /> isn't set.</exception>
	/// <exception cref="CommandLine.ExpansionError">The copy couldn't be started.</exception>
	internal static string Start(string list, bool writes)
	{
		var command = (CopyCommandLine ?? throw new InvalidOperationException())(list);

		// One level down, since the copy raises SHLVL again and bash's subshell stays on the same level.
		string[] level = [$"SHLVL={DeclareCommand.ShellLevel - 1}"];

		var opened = OperatingSystem.IsWindows() ? StartNamedPipe(command, level, writes) : StartDescriptor(command, level, writes);

		Pipes.Add(opened);

		return opened.Path;
	}

	/// <summary>
	/// Runs <paramref name="run" />, then closes every pipe <see cref="Start" /> opened while it ran.
	/// </summary>
	/// <param name="run">Runs a pipeline, which gives its status.</param>
	/// <returns>
	/// The status <paramref name="run" /> gives.
	/// </returns>
	/// <remarks>
	/// Pipes opened before, by a pipeline around this one, stay open, so a <c>$(cat &lt;(x))</c> in a command's words
	/// closes only its own.
	/// </remarks>
	internal static int Scoped(Func<int> run)
	{
		var mark = Pipes.Count;

		try
		{
			return run();
		}
		finally
		{
			var done = Pipes[mark..];

			Pipes.RemoveRange(mark, done.Count);

			foreach (var pipe in done)
			{
				pipe.Close();
			}
		}
	}

	/// <summary>
	/// The error for a copy of the shell that couldn't be started.
	/// </summary>
	/// <param name="reason">Why it couldn't.</param>
	/// <returns>
	/// The error, which ends the command before it runs.
	/// </returns>
	private static CommandLine.ExpansionError CannotStart(string reason) =>
		new(new ShellError("sharpie::shell::cannot_start", $"Can't start the shell for a process substitution: {reason}"), fatal: false);

	/// <summary>
	/// A pipe a process substitution opened.
	/// </summary>
	/// <param name="Path">The path the command opens.</param>
	/// <param name="Descriptor">The shell's descriptor for its end on Linux and macOS, or -1 on Windows.</param>
	/// <param name="Close">Closes the shell's end.</param>
	private sealed record Opened(string Path, int Descriptor, Action Close);
}
