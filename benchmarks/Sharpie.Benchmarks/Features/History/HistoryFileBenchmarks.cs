using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Sharpie.Shell.Features.History;

namespace Sharpie.Benchmarks.Features.History;

/// <summary>
/// Measures <see cref="HistoryFile.Save" />, which encrypts and rewrites the whole history after every command, and
/// <see cref="HistoryFile.Load" />, which runs once at startup.
/// </summary>
[MemoryDiagnoser]
public class HistoryFileBenchmarks
{
	private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
	private List<string> lines = [];
	private DirectoryInfo folder = null!;
	private string path = string.Empty;

	/// <summary>
	/// Makes a full history and saves it once, so <see cref="Load" /> has a file to read.
	/// </summary>
	[GlobalSetup]
	public void Setup()
	{
		this.lines = [.. Enumerable.Range(0, HistoryCommand.MaxLines).Select(index => $"git commit -m \"change number {index}\" && dotnet test")];
		this.folder = Directory.CreateTempSubdirectory("sharpie-bench-");
		this.path = Path.Combine(this.folder.FullName, "history");
		_ = HistoryFile.Save(this.path, this.key, this.lines);
	}

	/// <summary>
	/// Deletes the temporary folder.
	/// </summary>
	[GlobalCleanup]
	public void Cleanup() => this.folder.Delete(recursive: true);

	/// <summary>
	/// Saves the full history, as every command does.
	/// </summary>
	/// <returns>
	/// Whether the save worked.
	/// </returns>
	[Benchmark(Baseline = true)]
	public bool Save() => HistoryFile.Save(this.path, this.key, this.lines);

	/// <summary>
	/// Loads the full history, as the shell does once at startup.
	/// </summary>
	/// <returns>
	/// The number of lines read.
	/// </returns>
	[Benchmark]
	public int? Load() => HistoryFile.Load(this.path, this.key)?.Count;
}
