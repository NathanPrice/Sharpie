using BenchmarkDotNet.Attributes;
using Sharpie.Shell.Features.Completion;

namespace Sharpie.Benchmarks.Features.Completion;

/// <summary>
/// Measures one Tab press through <see cref="TabCompleter.Press" />, against the real <c>PATH</c> and a folder of 200
/// files.
/// </summary>
[MemoryDiagnoser]
public class TabCompleterBenchmarks
{
	private TabCompleter completer = null!;
	private DirectoryInfo folder = null!;
	private string pathLine = string.Empty;

	/// <summary>
	/// Creates the completer and a temporary folder of files to complete.
	/// </summary>
	[GlobalSetup]
	public void Setup()
	{
		this.completer = new TabCompleter(Shell.Program.CreateBuiltins([]).Select(command => command.Name).ToList());
		this.folder = Directory.CreateTempSubdirectory("sharpie-bench-");

		for (var index = 0; index < 200; index++)
		{
			File.WriteAllText(Path.Combine(this.folder.FullName, $"file{index}.txt"), string.Empty);
		}

		this.pathLine = $"cat {this.folder.FullName}{Path.DirectorySeparatorChar}file1";
	}

	/// <summary>
	/// Deletes the temporary folder.
	/// </summary>
	[GlobalCleanup]
	public void Cleanup() => this.folder.Delete(recursive: true);

	/// <summary>
	/// Completes a command name, which searches every <c>PATH</c> folder.
	/// </summary>
	/// <returns>
	/// The completed line.
	/// </returns>
	[Benchmark(Baseline = true)]
	public string CommandName()
	{
		this.completer.Reset();

		return this.completer.Press("gi").Line;
	}

	/// <summary>
	/// Completes a file name in a folder of 200 files.
	/// </summary>
	/// <returns>
	/// The completed line.
	/// </returns>
	[Benchmark]
	public string FilePath()
	{
		this.completer.Reset();

		return this.completer.Press(this.pathLine).Line;
	}
}
