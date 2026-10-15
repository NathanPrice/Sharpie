using BenchmarkDotNet.Attributes;
using Sharpie.Shell.Common;

namespace Sharpie.Benchmarks.Common;

/// <summary>
/// Measures <see cref="ShellError" />: drawing a report, and finding the "did you mean" name for a command that isn't
/// found, against the real <c>PATH</c>.
/// </summary>
[MemoryDiagnoser]
public class ShellErrorBenchmarks
{
	private readonly ShellError withSpan = new("sharpie::shell::command_not_found", "Command `gti` not found")
	{
		Source = "gti status --short",
		Start = 0,
		End = 3,
		Label = "not a builtin, alias or program on PATH",
		Help = "did you mean `git`?",
	};

	private readonly ShellError withoutSpan = new("sharpie::history::file_not_found", "No such file: `/tmp/history`");

	private string[] builtins = [];

	/// <summary>
	/// Collects the shell's builtin names, which the suggestion also searches.
	/// </summary>
	[GlobalSetup]
	public void Setup() => this.builtins = [.. Shell.Program.CreateBuiltins([]).Select(command => command.Name)];

	/// <summary>
	/// Draws a report that points into the typed line.
	/// </summary>
	/// <returns>
	/// The report's length.
	/// </returns>
	[Benchmark]
	public int RenderWithSpan() => this.withSpan.Render(color: true).Length;

	/// <summary>
	/// Draws a report with no position, as the builtins give.
	/// </summary>
	/// <returns>
	/// The report's length.
	/// </returns>
	[Benchmark]
	public int RenderWithoutSpan() => this.withoutSpan.Render(color: true).Length;

	/// <summary>
	/// Finds the suggestion the way an obvious first version would: check the execute bit of every file on
	/// <c>PATH</c>, then compare names.
	/// </summary>
	/// <returns>
	/// The suggestion, or <see langword="null" /> when nothing is close.
	/// </returns>
	[Benchmark(Baseline = true)]
	public string? SuggestFromExecutables() =>
		ShellError.DidYouMean("gti", this.builtins.Concat(PathFinder.FindExecutableNames(string.Empty))) is [var first, ..] ? first : null;

	/// <summary>
	/// Finds the suggestion the way the shell does: compare plain file names, then check only the close ones.
	/// </summary>
	/// <returns>
	/// The suggestion, or <see langword="null" /> when nothing is close.
	/// </returns>
	[Benchmark]
	public string? SuggestFromFileNames() =>
		ShellError.DidYouMean("gti", this.builtins.Concat(PathFinder.FindFileNames()))
			.FirstOrDefault(match => this.builtins.Contains(match) || PathFinder.FindExecutable(match) is not null);
}
