using BenchmarkDotNet.Running;

namespace Sharpie.Benchmarks;

/// <summary>
/// Runs the benchmarks picked on the command line, such as <c>--filter '*VtScreen*'</c>, or asks which to run.
/// </summary>
internal static class Program
{
	/// <summary>
	/// Hands the command-line arguments to BenchmarkDotNet.
	/// </summary>
	/// <param name="args">The command-line arguments, such as <c>--filter</c>.</param>
	private static void Main(string[] args) => _ = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
