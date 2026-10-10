using BenchmarkDotNet.Attributes;
using Sharpie.Shell.Features.Highlighting;

namespace Sharpie.Benchmarks.Features.Highlighting;

/// <summary>
/// Measures <see cref="Highlighter.Highlight" />, which the line editor runs on every keystroke, against the real
/// <c>PATH</c> and file system.
/// </summary>
[MemoryDiagnoser]
public class HighlighterBenchmarks
{
	private HashSet<string> builtins = [];

	/// <summary>
	/// Collects the shell's builtin names, as the line editor gets them.
	/// </summary>
	[GlobalSetup]
	public void Setup() => this.builtins = [.. Shell.Program.CreateBuiltins([]).Select(command => command.Name)];

	/// <summary>
	/// Highlights a short command found on <c>PATH</c>.
	/// </summary>
	/// <returns>
	/// The number of colored tokens.
	/// </returns>
	[Benchmark(Baseline = true)]
	public int ShortCommand() => Highlighter.Highlight("ls -la", this.builtins).Count;

	/// <summary>
	/// Highlights a builtin, which skips the <c>PATH</c> search.
	/// </summary>
	/// <returns>
	/// The number of colored tokens.
	/// </returns>
	[Benchmark]
	public int Builtin() => Highlighter.Highlight("echo hello world", this.builtins).Count;

	/// <summary>
	/// Highlights a long line with quotes, paths and redirects.
	/// </summary>
	/// <returns>
	/// The number of colored tokens.
	/// </returns>
	[Benchmark]
	public int LongLine() => Highlighter.Highlight(
		"git log --oneline \"fix the thing\" ./src ~/notes.txt 'single quoted' > ~/out.txt 2>> ./errors.log",
		this.builtins).Count;

	/// <summary>
	/// Highlights a command that isn't on <c>PATH</c>, so every folder is checked.
	/// </summary>
	/// <returns>
	/// The number of colored tokens.
	/// </returns>
	[Benchmark]
	public int UnknownCommand() => Highlighter.Highlight("notacommand foo bar", this.builtins).Count;
}
