using BenchmarkDotNet.Attributes;
using Sharpie.Shell.Features.Aliases;

namespace Sharpie.Benchmarks.Common;

/// <summary>
/// Measures <see cref="Shell.Common.CommandLine.Split" /> and <see cref="AliasCommand.Expand" />, which every
/// command the shell runs goes through.
/// </summary>
[MemoryDiagnoser]
public class CommandLineBenchmarks
{
	private readonly AliasCommand aliases = new();

	private readonly Dictionary<string, string> variables = new(StringComparer.Ordinal) { ["NAME"] = "world" };

	/// <summary>
	/// Defines one alias, as a startup file would.
	/// </summary>
	[GlobalSetup]
	public void Setup() => _ = this.aliases.Run(["ll=ls -la --color=auto"]);

	/// <summary>
	/// Splits a short command.
	/// </summary>
	/// <returns>
	/// The number of words.
	/// </returns>
	[Benchmark(Baseline = true)]
	public int ShortLine() => Shell.Common.CommandLine.Split("ls -la", this.variables).Length;

	/// <summary>
	/// Splits a long line with quotes, a variable, a pipe and redirects.
	/// </summary>
	/// <returns>
	/// The number of words.
	/// </returns>
	[Benchmark]
	public int LongLine() => Shell.Common.CommandLine.Split(
		"git log --oneline \"fix the $NAME thing\" ./src 'single quoted' | grep -v merge > ~/out.txt 2>> ./errors.log",
		this.variables).Length;

	/// <summary>
	/// Splits a line that starts with an alias and expands it.
	/// </summary>
	/// <returns>
	/// The number of words after expansion.
	/// </returns>
	[Benchmark]
	public int AliasedLine() => this.aliases.Expand(Shell.Common.CommandLine.Split("ll ./src", this.variables), this.variables).Length;
}
