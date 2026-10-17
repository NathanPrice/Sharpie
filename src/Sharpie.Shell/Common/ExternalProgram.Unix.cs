using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Sharpie.Shell.Features.Jobs;

namespace Sharpie.Shell.Common;

/// <content>
/// Starts programs with <see cref="PosixSpawn" />, for Linux and macOS.
/// </content>
internal static partial class ExternalProgram
{
	// SIGKILL is 9 on both Linux and macOS.
	private const int Kill = 9;

	/// <summary>
	/// Runs the program at <paramref name="fullPath" /> as <see cref="Run" /> describes.
	/// </summary>
	/// <param name="commandName">The program name as the user typed it, passed on as its <c>argv[0]</c>.</param>
	/// <param name="fullPath">Where <see cref="PathFinder.FindExecutable" /> found the program.</param>
	/// <param name="arguments">The arguments to pass to the program.</param>
	/// <param name="output">Where standard output goes, or <see langword="null" /> for the terminal.</param>
	/// <param name="error">Where standard error goes, or <see langword="null" /> for the terminal.</param>
	/// <param name="background"><see langword="true" /> to return without waiting; otherwise, <see langword="false" />.</param>
	/// <param name="input">What the program reads, or <see langword="null" /> for the terminal.</param>
	/// <param name="assignments">The <c>NAME=VALUE</c> words only this program gets in its environment.</param>
	/// <param name="targets">Where each of the program's streams goes, from its redirects.</param>
	/// <param name="listJob"><see langword="false" /> for a background program in a command substitution.</param>
	/// <returns>
	/// The status as <see cref="Run" /> gives it.
	/// </returns>
	/// <remarks>
	/// A file is handed to the program as is. Any other stream goes through a pipe the shell copies, except for a
	/// listed background job, whose output then goes to the terminal: nobody would read the pipe once this returns.
	/// An unlisted one's copies go on in <see cref="Detached" />, and another thread collects its exit.
	/// </remarks>
	private static int RunWithSpawn(string commandName, string fullPath, string[] arguments, Stream? output, Stream? error, bool background, Stream? input, IReadOnlyList<string> assignments, StreamTarget[] targets, bool listJob)
	{
		using var plumbing = new Plumbing();

		var detached = background && !listJob;
		var (streams, closed) = Descriptors(targets, stream => stream switch
		{
			0 => plumbing.Reader(input),
			1 => plumbing.Writer(output, copy: !background || detached),
			_ => plumbing.Writer(error, copy: !background || detached),
		});

		// Rebuilt from the split words, so quotes the user typed don't show up in jobs. Pass the raw line instead if
		// the listing ever has to match the input exactly.
		var command = string.Join(' ', [.. assignments, commandName, .. arguments]);
		int pid;

		try
		{
			pid = PosixSpawn.Start(fullPath, [commandName, .. arguments], JobControl.Enabled ? 0 : -1, streams, closed, assignments: assignments);
		}
		catch (Win32Exception exception)
		{
			new ShellError("sharpie::shell::cannot_start", $"Can't start `{commandName}`: {exception.Message}").Write();

			return CannotRun;
		}

		plumbing.Started();

		var job = new Job(0, pid, command);

		job.Add(pid);

		if (detached)
		{
			Detached.Add((output ?? Stream.Null, plumbing.Detach()));

			// Collected on its own thread, since the shell never waits on it and it would stay a zombie otherwise.
			// The shell's own waits only take a foreground job's group or given ids, so they can't take this one.
			_ = Task.Run(() =>
			{
				while (NativeMethods.WaitPid(pid, out _, 0) < 0 && Marshal.GetLastPInvokeError() == NativeMethods.Interrupted)
				{
				}
			});

			return 0;
		}

		if (background)
		{
			AddJob(job);
			Console.WriteLine($@"[{job.Number}] {job.ProcessId}");

			return 0;
		}

		WaitInForeground(job);

		// A stopped job may never finish, so its copies are dropped with the pipes.
		if (job.State != JobState.Stopped)
		{
			plumbing.Finish();
		}

		return job.ExitCode;
	}

	/// <summary>
	/// Runs a pipeline as <see cref="RunPipeline" /> describes.
	/// </summary>
	/// <param name="stages">Each program's name followed by its arguments, in pipeline order.</param>
	/// <param name="paths">Where each program was found, in the same order.</param>
	/// <param name="assignments">Each stage's <c>NAME=VALUE</c> words, in the same order.</param>
	/// <param name="targets">Where each stage's streams go, from its redirects, in the same order.</param>
	/// <param name="pipeFail"><see langword="true" /> for <c>set -o pipefail</c>.</param>
	/// <param name="output">Where the last program writes, or <see langword="null" /> for the terminal.</param>
	/// <returns>
	/// The status as <see cref="RunPipeline" /> gives it.
	/// </returns>
	/// <remarks>
	/// Neighboring programs share a real pipe and the shell copies nothing, except into an <paramref name="output" />
	/// that isn't a file. With job control they all join the first
	/// program's process group, so Ctrl+C and Ctrl+Z reach the whole pipeline. When a later program exits early, as
	/// <c>head</c> does in <c>yes | head -n 3</c>, the earlier one gets SIGPIPE on its next write and ends, as in any
	/// shell.
	/// </remarks>
	private static int RunPipelineWithSpawn(string[][] stages, string[] paths, string[][] assignments, StreamTarget[][] targets, bool pipeFail, Stream? output)
	{
		using var plumbing = new Plumbing();

		var lastOutput = plumbing.Writer(output, copy: true);
		var pipes = new AnonymousPipeServerStream[stages.Length - 1];
		Job? job = null;
		var failed = false;

		try
		{
			for (var index = 0; index < pipes.Length; index++)
			{
				pipes[index] = new AnonymousPipeServerStream(PipeDirection.In);
			}

			for (var index = 0; index < stages.Length; index++)
			{
				// The first program reads the terminal and the last one writes to the terminal or output.
				var input = index > 0 ? (int)pipes[index - 1].SafePipeHandle.DangerousGetHandle() : -1;
				var stageOutput = index < pipes.Length ? (int)pipes[index].ClientSafePipeHandle.DangerousGetHandle() : lastOutput;
				var group = !JobControl.Enabled ? -1 : job?.ProcessId ?? 0;
				var (streams, closed) = Descriptors(targets[index], stream => stream switch
				{
					0 => input,
					1 => stageOutput,
					_ => -1,
				});

				try
				{
					var pid = PosixSpawn.Start(paths[index], stages[index], group, streams, closed, assignments: assignments[index]);

					job ??= new Job(0, pid, string.Join(" | ", stages.Select((stage, stageIndex) => string.Join(' ', [.. assignments[stageIndex], .. stage]))));
					job.Add(pid);
				}
				catch (Win32Exception exception)
				{
					new ShellError("sharpie::shell::cannot_start", $"Can't start `{stages[index][0]}`: {exception.Message}").Write();

					// The ones already running would otherwise be left behind, the first of them still reading the
					// terminal.
					foreach (var started in job?.Members ?? [])
					{
						_ = NativeMethods.Kill(started, Kill);
					}

					failed = true;

					break;
				}
			}
		}
		finally
		{
			// The programs hold their own copies. The shell's copies have to go, or a reader would never see the end
			// of its input. Disposing the server end alone leaves the client end open once it has been read.
			foreach (var pipe in pipes)
			{
				pipe?.DisposeLocalCopyOfClientHandle();
				pipe?.Dispose();
			}
		}

		plumbing.Started();

		if (job is not null)
		{
			WaitInForeground(job);
		}

		// A stopped job may never finish, so its copy is dropped with the pipe.
		if (job?.State != JobState.Stopped)
		{
			plumbing.Finish();
		}

		// The killed programs' statuses would otherwise stand in for the one that never ran.
		return failed ? CannotRun : PipelineStatus(job!.ExitCodes, pipeFail);
	}

	/// <summary>
	/// Works out which of the shell's open files each of a program's descriptors gets.
	/// </summary>
	/// <param name="targets">Where each of the program's streams goes, by number.</param>
	/// <param name="own">
	/// Gives the shell's descriptor for the command's own stream 0, 1 or 2, or -1 for the shell's descriptor with that
	/// number.
	/// </param>
	/// <returns>
	/// The pairs for <see cref="PosixSpawn.Start(string, IReadOnlyList{string}, int, IReadOnlyList{ValueTuple{int, int}}, IReadOnlyList{int}, bool, IReadOnlyList{string})" />,
	/// and the descriptors to close.
	/// </returns>
	/// <remarks>
	/// A closed number above 2 needs no step, since .NET opens every file close-on-exec and the program never gets it.
	/// macOS fails the whole start on a close step for a descriptor that isn't open.
	/// </remarks>
	private static (List<(int Source, int Target)> Streams, List<int> Closed) Descriptors(StreamTarget[] targets, Func<int, int> own)
	{
		var streams = new List<(int Source, int Target)>();
		var closed = new List<int>();

		for (var fd = 0; fd < targets.Length; fd++)
		{
			if (targets[fd].Opened is { } file)
			{
				streams.Add(((int)file.SafeFileHandle.DangerousGetHandle(), fd));
			}
			else if (!targets[fd].Closed)
			{
				var inherited = targets[fd].Inherited;

				streams.Add((own(inherited) is var source and >= 0 ? source : inherited, fd));
			}
			else if (fd <= 2)
			{
				closed.Add(fd);
			}
		}

		return (streams, closed);
	}

	/// <summary>
	/// Waits for every program in <paramref name="job" />, handing it the terminal when job control is on, and adds it
	/// to the job table if Ctrl+Z stopped it.
	/// </summary>
	/// <param name="job">The job, just started.</param>
	private static void WaitInForeground(Job job)
	{
		if (JobControl.Enabled)
		{
			JobControl.WaitInForeground(job, resume: false);

			if (job.State == JobState.Stopped)
			{
				AddJob(job);
				Console.WriteLine($@"{Environment.NewLine}[{job.Number}]+  {"Stopped",-24}{job.Command}");
			}

			return;
		}

		// Ctrl+C goes to the program whose output the user is watching.
		sRunningId = job.Members[^1];

		try
		{
			while (job.Members.Count > 0)
			{
				var pid = NativeMethods.WaitPid(job.Members[0], out var status, 0);

				if (pid > 0)
				{
					job.Record(pid, status);
				}
				else if (Marshal.GetLastPInvokeError() != NativeMethods.Interrupted)
				{
					break;
				}
			}
		}
		finally
		{
			sRunningId = 0;
		}
	}

	/// <summary>
	/// Turns the streams <see cref="Run" /> takes into open files a program can be given, and copies data through a
	/// pipe for streams that aren't files.
	/// </summary>
	private sealed class Plumbing : IDisposable
	{
		private readonly List<AnonymousPipeServerStream> pipes = [];
		private readonly List<Func<Task>> copiers = [];
		private readonly List<Task> copies = [];

		// One pipe per stream, so 2>&1 into one stream doesn't start two copies into it.
		private readonly Dictionary<Stream, int> ends = [];

		/// <summary>
		/// Gets the file a program should write to for <paramref name="stream" />.
		/// </summary>
		/// <param name="stream">Where the output goes, or <see langword="null" /> for the terminal.</param>
		/// <param name="copy">
		/// <see langword="false" /> to send output that isn't headed for a file to the terminal instead of a pipe.
		/// </param>
		/// <returns>
		/// The file descriptor, or -1 to keep the shell's.
		/// </returns>
		public int Writer(Stream? stream, bool copy)
		{
			if (stream is FileStream file)
			{
				return (int)file.SafeFileHandle.DangerousGetHandle();
			}

			if (stream is null || !copy)
			{
				return -1;
			}

			if (this.ends.TryGetValue(stream, out var end))
			{
				return end;
			}

			var pipe = new AnonymousPipeServerStream(PipeDirection.In);

			this.pipes.Add(pipe);

			// The pipe's end only arrives once every program holding it has exited, so the copy runs alongside.
			this.copiers.Add(() => pipe.CopyToAsync(stream));

			return this.ends[stream] = (int)pipe.ClientSafePipeHandle.DangerousGetHandle();
		}

		/// <summary>
		/// Gets the file a program should read from for <paramref name="stream" />.
		/// </summary>
		/// <param name="stream">What the program reads, or <see langword="null" /> for the terminal.</param>
		/// <returns>
		/// The file descriptor, or -1 to keep the shell's.
		/// </returns>
		public int Reader(Stream? stream)
		{
			if (stream is FileStream file)
			{
				return (int)file.SafeFileHandle.DangerousGetHandle();
			}

			if (stream is null)
			{
				return -1;
			}

			if (this.ends.TryGetValue(stream, out var end))
			{
				return end;
			}

			var pipe = new AnonymousPipeServerStream(PipeDirection.Out);

			this.pipes.Add(pipe);

			// Written in the background, since the program may not read all its input before it fills its output
			// pipe. Closing the pipe is how it learns there's no more, so wc can print.
			this.copiers.Add(() => Task.Run(() =>
			{
				try
				{
					stream.CopyTo(pipe);
					pipe.Dispose();
				}
				// The program exited without reading all of its input.
				catch (IOException)
				{
				}
			}));

			return this.ends[stream] = (int)pipe.ClientSafePipeHandle.DangerousGetHandle();
		}

		/// <summary>
		/// Closes the shell's copies of the program's pipe ends and starts copying.
		/// </summary>
		public void Started()
		{
			foreach (var pipe in this.pipes)
			{
				pipe.DisposeLocalCopyOfClientHandle();
			}

			this.copies.AddRange(this.copiers.Select(start => start()));
		}

		/// <summary>
		/// Waits until every copy is done, after the program has exited.
		/// </summary>
		public void Finish() => Task.WaitAll(this.copies);

		/// <summary>
		/// Hands the copies and pipes to a task, for a program the shell doesn't wait on, after <see cref="Started" />.
		/// </summary>
		/// <returns>
		/// A task that ends once every copy is done, after closing the pipes. Disposing this object no longer
		/// closes them.
		/// </returns>
		public Task Detach()
		{
			var pipes = this.pipes.ToArray();
			var copies = this.copies.ToArray();

			this.pipes.Clear();

			return Task.Run(() =>
			{
				try
				{
					Task.WaitAll(copies);
				}
				finally
				{
					foreach (var pipe in pipes)
					{
						pipe.Dispose();
					}
				}
			});
		}

		/// <inheritdoc />
		public void Dispose()
		{
			foreach (var pipe in this.pipes)
			{
				pipe.DisposeLocalCopyOfClientHandle();
				pipe.Dispose();
			}
		}
	}
}
