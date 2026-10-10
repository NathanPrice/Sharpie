using BenchmarkDotNet.Attributes;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Benchmarks.Features.LineEditing;

/// <summary>
/// Measures <see cref="PromptHeader.Build" />, which runs before every prompt and looks for a git branch.
/// </summary>
[MemoryDiagnoser]
public class PromptHeaderBenchmarks
{
	private readonly string outsideRepository = Path.GetTempPath();
	private readonly string insideRepository = AppContext.BaseDirectory;

	/// <summary>
	/// Builds the header for a folder outside any repository, which walks up to the root.
	/// </summary>
	/// <returns>
	/// The header.
	/// </returns>
	[Benchmark(Baseline = true)]
	public string OutsideRepository() => PromptHeader.Build(this.outsideRepository, TimeSpan.Zero, 200, blankLine: true);

	/// <summary>
	/// Builds the header for a folder deep in this repository's build output, which finds the branch.
	/// </summary>
	/// <returns>
	/// The header.
	/// </returns>
	[Benchmark]
	public string InsideRepository() => PromptHeader.Build(this.insideRepository, TimeSpan.Zero, 200, blankLine: true);
}
