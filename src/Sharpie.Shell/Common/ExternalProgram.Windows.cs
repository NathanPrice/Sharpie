using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Sharpie.Shell.Features.Jobs;

namespace Sharpie.Shell.Common;

/// <content>
/// Starts programs through <see cref="Process" />, for Windows.
/// </content>
internal static partial class ExternalProgram
{
	/// <summary>
	/// Runs the program at <paramref name="fullPath" /> as <see cref="Run" /> describes.
	/// </summary>
	/// <param name="commandName">The program name as the user typed it.</param>
	/// <param name="fullPath">Where <see cref="PathFinder.FindExecutable" /> found the program.</param>
	/// <param name="arguments">The arguments to pass to the program.</param>
	/// <param name="output">Where standard output goes, or <see langword="null" /> for the terminal.</param>
	/// <param name="error">Where standard error goes, or <see langword="null" /> for the terminal.</param>
	/// <param name="background"><see langword="true" /> to return without waiting; otherwise, <see langword="false" />.</param>
	/// <param name="input">What the program reads, or <see langword="null" /> for the terminal.</param>
	/// <param name="assignments">The <c>NAME=VALUE</c> words only this program gets in its environment.</param>
	/// <param name="redirection">The program's redirects, or <see langword="null" /> for none.</param>
	/// <param name="listJob"><see langword="false" /> for a background program in a command substitution.</param>
	/// <returns>
	/// The status as <see cref="Run" /> gives it.
	/// </returns>
	[SupportedOSPlatform("windows")]
	private static int RunWithProcess(string commandName, string fullPath, string[] arguments, Stream? output, Stream? error, bool background, Stream? input, IReadOnlyList<string> assignments, Redirection? redirection, bool listJob) =>
		RunProcesses([[commandName, .. arguments]], [fullPath], [[.. assignments]], [redirection?.Targets ?? Redirection.Standard], [input, output, error], background ? redirection : null, background, listJob);

	/// <summary>
	/// Runs a pipeline as <see cref="RunPipeline" /> describes.
	/// </summary>
	/// <param name="stages">Each program's name followed by its arguments, in pipeline order.</param>
	/// <param name="paths">Where each program was found, in the same order.</param>
	/// <param name="assignments">Each stage's <c>NAME=VALUE</c> words, in the same order.</param>
	/// <param name="targets">Where each stage's streams go, from its redirects, in the same order.</param>
	/// <param name="pipeFail">
	/// <see langword="true" /> for <c>set -o pipefail</c>. An earlier program killed because the last one exited
	/// counts as failed.
	/// </param>
	/// <param name="output">Where the last program writes, or <see langword="null" /> for the terminal.</param>
	/// <returns>
	/// The status as <see cref="RunPipeline" /> gives it.
	/// </returns>
	[SupportedOSPlatform("windows")]
	private static int RunPipelineWithProcesses(string[][] stages, string[] paths, string[][] assignments, StreamTarget[][] targets, bool pipeFail, Stream? output) =>
		RunProcesses(stages, paths, assignments, targets, [null, output, null], null, background: false, pipeFail: pipeFail);

	/// <summary>
	/// Starts each stage's program, copies its streams to and from where its redirects send them, and waits for all of
	/// them unless it's a background job.
	/// </summary>
	/// <param name="stages">Each program's name followed by its arguments, in pipeline order.</param>
	/// <param name="paths">Where each program was found, in the same order.</param>
	/// <param name="assignments">Each stage's <c>NAME=VALUE</c> words, in the same order.</param>
	/// <param name="targets">Where each stage's streams go, in the same order.</param>
	/// <param name="ends">
	/// The first stage's input, then the last stage's output, then every stage's errors, each <see langword="null" />
	/// for the console.
	/// </param>
	/// <param name="owned">
	/// The redirects of a background job, whose files are closed once the program and its copies are done, or
	/// <see langword="null" />.
	/// </param>
	/// <param name="background"><see langword="true" /> to return without waiting, for one program.</param>
	/// <param name="listJob">
	/// <see langword="false" /> to leave a background program out of the job table, for one inside a command
	/// substitution.
	/// </param>
	/// <param name="pipeFail"><see langword="true" /> for <c>set -o pipefail</c>.</param>
	/// <returns>
	/// The status as <see cref="ExternalProgram.PipelineStatus" /> gives it, 0 for a background job, or 126 when a
	/// program couldn't start.
	/// </returns>
	/// <remarks>
	/// <para>
	/// .NET's <see cref="Process" /> can't give a child a pipe or file it didn't create itself, so neighboring programs
	/// can't share one pipe the way <c>pipe</c> and <c>fork</c> would set them up, and a redirect can't hand a program
	/// its file. A program's stream stays on the console only when it is the console stream with its own number.
	/// Every other stream gets its own pipe, and the shell copies bytes between it and where it goes as they arrive:
	/// a file, the other console stream, or the next program. Two copies into one place, as in <c>2&gt;&amp;1 |</c>,
	/// write through a lock so their writes don't interleave mid-write, and a program's input closes once every copy
	/// into it is done. Windows programs have only streams 0 to 2, so a file opened for <c>3&gt;</c> isn't given to
	/// the program.
	/// </para>
	/// <para>
	/// As in bash, the shell waits for every program in the pipeline, not only the last. When a later program
	/// exits first, as <c>head</c> does in <c>tail -f file | head -n 5</c>, bash's earlier program gets a broken pipe
	/// and ends. Closing the shell's end of the pipe doesn't do that here, because .NET keeps the handle open while
	/// the copy is blocked reading it, so once the last program exits every earlier one still running is killed
	/// instead. An earlier program that should outlive the last, like <c>sleep 2 | echo hi</c>, ends early as a
	/// result.
	/// </para>
	/// </remarks>
	[SupportedOSPlatform("windows")]
	private static int RunProcesses(string[][] stages, string[] paths, string[][] assignments, StreamTarget[][] targets, Stream?[] ends, Redirection? owned, bool background, bool listJob = true, bool pipeFail = false)
	{
		var processes = new List<Process>();

		try
		{
			for (var index = 0; index < stages.Length; index++)
			{
				var startInfo = CreateStartInfo(stages[index][0], paths[index], stages[index][1..], assignments[index]);

				startInfo.RedirectStandardInput = !OnConsole(targets, ends, index, 0);
				startInfo.RedirectStandardOutput = !OnConsole(targets, ends, index, 1);
				startInfo.RedirectStandardError = !OnConsole(targets, ends, index, 2);
				processes.Add(Process.Start(startInfo)!);
			}

			var copies = CopyStreams(processes, targets, ends);

			if (background)
			{
				// Rebuilt from the split words, so quotes the user typed don't show up in jobs. Pass the raw line
				// instead if the listing ever has to match the input exactly.
				StartJob(processes[0], string.Join(' ', [.. assignments[0], .. stages[0]]), copies, owned, listJob ? null : ends[1] ?? Stream.Null);
				processes.Clear();

				return 0;
			}

			// Ctrl+C goes to the program whose output the user is watching.
			sRunningId = processes[^1].Id;
			processes[^1].WaitForExit();

			foreach (var process in processes)
			{
				if (!process.HasExited)
				{
					process.Kill();
				}

				process.WaitForExit();
			}

			Task.WaitAll(copies);

			return PipelineStatus([.. processes.Select(process => process.ExitCode)], pipeFail);
		}
		catch (Win32Exception exception)
		{
			new ShellError("sharpie::shell::cannot_start", $"Can't start `{stages[processes.Count][0]}`: {exception.Message}").Write();

			return CannotRun;
		}
		finally
		{
			foreach (var process in processes)
			{
				// A later program failed to start. The ones already running would otherwise be left behind, the
				// first of them still reading the terminal.
				if (!process.HasExited)
				{
					process.Kill();
				}

				process.Dispose();
			}

			sRunningId = 0;
		}
	}

	/// <summary>
	/// Adds a background program to the job table, and closes its redirect files once it and its copies are done.
	/// </summary>
	/// <param name="process">The program, which the job keeps so <c>jobs</c> can ask whether it has exited.</param>
	/// <param name="command">The command line as <c>jobs</c> shows it.</param>
	/// <param name="copies">The copies to and from its streams.</param>
	/// <param name="owned">Its redirects, or <see langword="null" />.</param>
	/// <param name="unlisted">
	/// The command substitution's stream, for a program left out of the job table, whose copies
	/// <see cref="WaitForDetached" /> waits for. <see langword="null" /> to list the job.
	/// </param>
	/// <remarks>
	/// It returns before sRunningId is set, so Ctrl+C only reaches the foreground program. shortcut: an unlisted
	/// program is waited for until it exits, so <c>$(cmd &gt; f &amp;)</c> waits for cmd, which bash doesn't;
	/// wait only for the copies into the substitution's stream if that matters.
	/// </remarks>
	[SupportedOSPlatform("windows")]
	[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The task disposes the files once the program and its copies are done.")]
	private static void StartJob(Process process, string command, Task[] copies, Redirection? owned, Stream? unlisted)
	{
		var files = owned?.Detach();

		LastBackground = process.Id;

		var done = Task.Run(async () =>
		{
			await process.WaitForExitAsync().ConfigureAwait(false);
			await Task.WhenAll(copies).ConfigureAwait(false);
			files?.Dispose();
		});

		if (unlisted is not null)
		{
			Detached.Add((unlisted, done));

			return;
		}

		var job = new Job(0, process.Id, command, process);

		AddJob(job);
		Console.WriteLine($@"[{job.Number}] {job.ProcessId}");
	}

	/// <summary>
	/// Starts every copy between the programs' redirected streams and where those streams go.
	/// </summary>
	/// <param name="processes">The started programs, in pipeline order.</param>
	/// <param name="targets">Where each one's streams go.</param>
	/// <param name="ends">The pipeline's own input, output and errors, each <see langword="null" /> for the console.</param>
	/// <returns>
	/// The tasks to wait for, including the ones that close each program's input once every copy into it is done.
	/// </returns>
	[SupportedOSPlatform("windows")]
	private static Task[] CopyStreams(List<Process> processes, StreamTarget[][] targets, Stream?[] ends)
	{
		var last = processes.Count - 1;
		var console = new Stream?[3];
		var locked = new Dictionary<Stream, Stream>();
		var feeds = processes.Select(_ => new List<Task>()).ToArray();
		var tasks = new List<Task>();

		for (var index = 0; index < processes.Count; index++)
		{
			foreach (var stream in (ReadOnlySpan<int>)[1, 2])
			{
				if (!OnConsole(targets, ends, index, stream))
				{
					var (to, next) = Destination(index, targets[index][stream]);
					var copy = Pump(stream == 1 ? processes[index].StandardOutput.BaseStream : processes[index].StandardError.BaseStream, Locked(to));

					tasks.Add(copy);

					if (next >= 0)
					{
						feeds[next].Add(copy);
					}
				}
			}
		}

		for (var index = 0; index < processes.Count; index++)
		{
			if (!OnConsole(targets, ends, index, 0))
			{
				var input = processes[index].StandardInput;

				// The copies from the stage before are already in feeds; a file or the caller's input is copied here.
				if ((targets[index][0].Opened ?? (index == 0 && targets[index][0] == StreamTarget.Own(0) ? ends[0] : null)) is { } source)
				{
					feeds[index].Add(Pump(source, input.BaseStream));
				}

				tasks.Add(Task.WhenAll(feeds[index]).ContinueWith(_ => Close(input), TaskScheduler.Default));
			}
		}

		return [.. tasks];

		// Where a stage's output or errors go, and the stage whose input that is, or -1.
		(Stream To, int Next) Destination(int index, StreamTarget target) => target switch
		{
			{ Opened: { } file } => (file, -1),
			{ Closed: true } or { Inherited: 0 } => (Stream.Null, -1),
			{ Inherited: 1 } when index < last => targets[index + 1][0] == StreamTarget.Own(0) ? (processes[index + 1].StandardInput.BaseStream, index + 1) : (Stream.Null, -1),
			{ Inherited: var own } => (End(ends, last, index, own) ?? (console[own] ??= own == 1 ? Console.OpenStandardOutput() : Console.OpenStandardError()), -1),
		};

		Stream Locked(Stream stream) => locked.TryGetValue(stream, out var wrapped) ? wrapped : locked[stream] = Stream.Synchronized(stream);

		// Closing the input is how the program learns there's no more, so wc can print.
		static void Close(StreamWriter input)
		{
			try
			{
				input.Close();
			}
			// The program exited without reading all of its input.
			catch (IOException)
			{
			}
		}

		// Copies everything from one stream to another in the background, until either end is done.
		static Task Pump(Stream from, Stream to) => Task.Run(() =>
		{
			try
			{
				from.CopyTo(to);
			}
			// The program on the other end exited and closed its input.
			catch (Exception exception) when (exception is IOException or ObjectDisposedException)
			{
			}
		});
	}

	/// <summary>
	/// Checks whether a stage's stream stays on the console, which it does only when it's the console stream with its
	/// own number.
	/// </summary>
	/// <param name="targets">Where each stage's streams go.</param>
	/// <param name="ends">The pipeline's own input, output and errors.</param>
	/// <param name="index">The stage.</param>
	/// <param name="stream">0, 1 or 2.</param>
	/// <returns>
	/// <see langword="true" /> when the program can use the console directly, with no pipe.
	/// </returns>
	private static bool OnConsole(StreamTarget[][] targets, Stream?[] ends, int index, int stream) =>
		targets[index][stream] == StreamTarget.Own(stream) && IsEnd(targets.Length - 1, index, stream) && ends[stream] is null;

	/// <summary>
	/// Gets the pipeline's own stream that is a stage's own stream, if there is one.
	/// </summary>
	/// <param name="ends">The pipeline's own input, output and errors.</param>
	/// <param name="last">The last stage's index.</param>
	/// <param name="index">The stage.</param>
	/// <param name="stream">0, 1 or 2.</param>
	/// <returns>
	/// The caller's stream, or <see langword="null" /> for the console or a pipe between stages.
	/// </returns>
	private static Stream? End(Stream?[] ends, int last, int index, int stream) => IsEnd(last, index, stream) ? ends[stream] : null;

	/// <summary>
	/// Checks whether a stage's own stream is one of the pipeline's ends rather than a pipe to a neighbor.
	/// </summary>
	/// <param name="last">The last stage's index.</param>
	/// <param name="index">The stage.</param>
	/// <param name="stream">0, 1 or 2.</param>
	/// <returns>
	/// <see langword="true" /> for every stage's errors, the first stage's input and the last stage's output.
	/// </returns>
	private static bool IsEnd(int last, int index, int stream) => stream == 2 || index == (stream == 0 ? 0 : last);


	/// <summary>
	/// Builds the start settings for <paramref name="commandName" /> with no streams redirected.
	/// </summary>
	/// <param name="commandName">The program name as the user typed it.</param>
	/// <param name="fullPath">Where <see cref="PathFinder.FindExecutable" /> found the program on <c>PATH</c>.</param>
	/// <param name="arguments">The arguments to pass to the program, each one as a separate argument.</param>
	/// <param name="assignments">The <c>NAME=VALUE</c> words only this program gets in its environment.</param>
	/// <returns>
	/// Settings that start the program directly, without a shell in between.
	/// </returns>
	private static ProcessStartInfo CreateStartInfo(string commandName, string fullPath, string[] arguments, IReadOnlyList<string> assignments)
	{
		// The typed name is passed rather than fullPath because .NET has no other way to make it the program's
		// argv[0], which is what bash passes too. But on Unix, Process.Start looks for a bare name in the current
		// folder and the shell's own folder before PATH, so a file planted there would run in place of the PATH
		// program. In that case the full path is passed instead, and argv[0] becomes the full path.
		// The same goes for an assigned PATH, since Process.Start searches the shell's own PATH.
		var shadowed = File.Exists(commandName) || File.Exists(Path.Combine(AppContext.BaseDirectory, commandName)) || AssignedPath(assignments) is not null;

		var startInfo = new ProcessStartInfo
		{
			FileName = shadowed ? fullPath : commandName,
			UseShellExecute = false,
		};

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		foreach (var assignment in assignments)
		{
			var parts = assignment.Split('=', 2);

			startInfo.Environment[parts[0]] = parts[1];
		}

		return startInfo;
	}
}
