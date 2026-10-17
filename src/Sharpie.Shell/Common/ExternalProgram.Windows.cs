using System.ComponentModel;
using System.Diagnostics;
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
	/// <param name="listJob"><see langword="false" /> for a background program in a command substitution.</param>
	/// <returns>
	/// The status as <see cref="Run" /> gives it.
	/// </returns>
	[SupportedOSPlatform("windows")]
	private static int RunWithProcess(string commandName, string fullPath, string[] arguments, Stream? output, Stream? error, bool background, Stream? input, IReadOnlyList<string> assignments, bool listJob)
	{
		// An unlisted background program's output is copied into the substitution's stream, which outlives this call
		// until WaitForDetached. A file doesn't, so a background program's file output still goes to the terminal.
		var detached = background && !listJob && output is not (null or FileStream);

		var startInfo = CreateStartInfo(commandName, fullPath, arguments, assignments);

		// A background job's output goes to the terminal even when redirected. Nobody would read its pipe once
		// Run returns, so a chatty program would block when the pipe fills, and the caller closes the file
		// anyway. Redirecting it would need a copy that owns the file and outlives this call.
		startInfo.RedirectStandardOutput = output is not null && (!background || detached);
		startInfo.RedirectStandardError = error is not null && !background;
		startInfo.RedirectStandardInput = input is not null;

		try
		{
			var process = Process.Start(startInfo);

			// Returns before s_runningId is set, so Ctrl+C only reaches the foreground program. The Process object is
			// kept rather than disposed so jobs can ask it whether the program has exited.
			if (detached)
			{
				Detached.Add((output!, process is null ? Task.CompletedTask : process.StandardOutput.BaseStream.CopyToAsync(output!)));

				return 0;
			}

			// An unlisted one whose output goes to a file or the terminal is neither listed nor waited for.
			if (background)
			{
				if (listJob)
				{
					// Rebuilt from the split words, so quotes the user typed don't show up in jobs. Pass the raw line
					// instead if the listing ever has to match the input exactly.
					var job = new Job(0, process?.Id ?? 0, string.Join(' ', [.. assignments, commandName, .. arguments]), process);

					AddJob(job);
					Console.WriteLine($@"[{job.Number}] {job.ProcessId}");
				}

				return 0;
			}

			using (process)
			{
				sRunningId = process?.Id ?? 0;

				// Standard error is copied in the background. Copying the two pipes one after the other can deadlock:
				// a program that fills the pipe nobody is reading yet blocks, and the copy waiting on the other pipe
				// never finishes.
				var errorCopy = process is not null && error is not null
					? process.StandardError.BaseStream.CopyToAsync(error)
					: Task.CompletedTask;

				// Written in the background for the same reason, since the program may not read all its input before
				// it fills its output pipe.
				var inputCopy = process is not null && input is not null
					? Task.Run(() =>
					{
						try
						{
							input.CopyTo(process.StandardInput.BaseStream);

							// Closing the input is how the program learns there's no more, so wc can print.
							process.StandardInput.Close();
						}
						// The program exited without reading all of its input.
						catch (IOException)
						{
						}
					})
					: Task.CompletedTask;

				if (process is not null && output is not null)
				{
					// Copied as raw bytes so binary output reaches the file unchanged.
					process.StandardOutput.BaseStream.CopyTo(output);
				}

				errorCopy.Wait();
				process?.WaitForExit();
				inputCopy.Wait();

				return process?.ExitCode ?? CannotRun;
			}
		}
		catch (Win32Exception exception)
		{
			new ShellError("sharpie::shell::cannot_start", $"Can't start `{commandName}`: {exception.Message}").Write();

			return CannotRun;
		}
		finally
		{
			sRunningId = 0;
		}
	}

	/// <summary>
	/// Runs a pipeline as <see cref="RunPipeline" /> describes.
	/// </summary>
	/// <param name="stages">Each program's name followed by its arguments, in pipeline order.</param>
	/// <param name="paths">Where each program was found, in the same order.</param>
	/// <param name="assignments">Each stage's <c>NAME=VALUE</c> words, in the same order.</param>
	/// <param name="pipeFail">
	/// <see langword="true" /> for <c>set -o pipefail</c>. An earlier program killed because the last one exited
	/// counts as failed.
	/// </param>
	/// <param name="output">Where the last program writes, or <see langword="null" /> for the terminal.</param>
	/// <returns>
	/// The status as <see cref="RunPipeline" /> gives it.
	/// </returns>
	/// <remarks>
	/// <para>
	/// .NET's <see cref="Process" /> can't give a child a pipe it didn't create itself, so neighboring programs can't
	/// share one pipe the way <c>pipe</c> and <c>fork</c> would set them up. Each gets its own pipe instead, and
	/// the shell copies bytes from one to the next as they arrive.
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
	private static int RunPipelineWithProcesses(string[][] stages, string[] paths, string[][] assignments, bool pipeFail, Stream? output)
	{
		var processes = new List<Process>();

		try
		{
			for (var index = 0; index < stages.Length; index++)
			{
				var startInfo = CreateStartInfo(stages[index][0], paths[index], stages[index][1..], assignments[index]);

				// The first program reads the terminal and the last one writes to the terminal or output.
				startInfo.RedirectStandardInput = index > 0;
				startInfo.RedirectStandardOutput = index < stages.Length - 1 || output is not null;
				processes.Add(Process.Start(startInfo)!);
			}

			// Ctrl+C goes to the program whose output the user is watching.
			sRunningId = processes[^1].Id;

			var relays = processes.Zip(processes.Skip(1), (from, to) => Task.Run(() => Relay(from, to))).ToArray();

			// Copied as raw bytes, alongside the waits, so a program filling the pipe can't block.
			var outputCopy = output is null ? Task.CompletedTask : processes[^1].StandardOutput.BaseStream.CopyToAsync(output);

			processes[^1].WaitForExit();

			foreach (var process in processes)
			{
				if (!process.HasExited)
				{
					process.Kill();
				}

				process.WaitForExit();
			}

			Task.WaitAll(relays);
			outputCopy.Wait();

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

		// Copies the output of from into the input of to until from is done or to has exited.
		static void Relay(Process from, Process to)
		{
			try
			{
				from.StandardOutput.BaseStream.CopyTo(to.StandardInput.BaseStream);

				// Closing the next program's input is how it learns the one before is done, so wc can print.
				to.StandardInput.Close();
			}
			// The next program exited and closed its input.
			catch (Exception exception) when (exception is IOException or ObjectDisposedException)
			{
			}
		}
	}


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
