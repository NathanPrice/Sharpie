using BenchmarkDotNet.Attributes;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.History;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Benchmarks.Features.LineEditing;

/// <summary>
/// Measures typing a whole line through <see cref="LineEditor.ReadLine" />, which highlights, suggests from history
/// and redraws after every key.
/// </summary>
/// <remarks>
/// The typed line matches no history entry, so every key scans all <see cref="HistoryCommand.MaxLines" /> lines for a
/// suggestion, the slowest case.
/// </remarks>
[MemoryDiagnoser]
public class LineEditorBenchmarks
{
	private const string Typed = "git commit -m \"benchmark every keystroke\"";

	private ConsoleKeyInfo[] keys = [];
	private int next;
	private LineEditor editor = null!;

	/// <summary>
	/// Gets the number of keys each run presses, including Enter.
	/// </summary>
	/// <value>
	/// Divide a run's time by this to get the cost of one key.
	/// </value>
	public static int KeyCount => Typed.Length + 1;

	/// <summary>
	/// Builds the keys to type and an editor with a full history.
	/// </summary>
	[GlobalSetup]
	public void Setup()
	{
		var builtins = Shell.Program.CreateBuiltins([]).Select(command => command.Name).ToList();
		var history = Enumerable.Range(0, HistoryCommand.MaxLines).Select(index => $"ls -la ./folder{index} && cat notes{index}.txt").ToList();

		this.keys = [.. Typed.Select(character => new ConsoleKeyInfo(character, 0, shift: false, alt: false, control: false)),
			new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false)];
		this.editor = new LineEditor(
			() => this.keys[this.next++],
			TextWriter.Null,
			() => 200,
			new TabCompleter(builtins),
			history,
			builtins);
	}

	/// <summary>
	/// Types the line and presses Enter.
	/// </summary>
	/// <returns>
	/// The line the editor returns.
	/// </returns>
	[Benchmark]
	public string? TypeLine()
	{
		this.next = 0;

		return this.editor.ReadLine(string.Empty);
	}
}
