using System.Diagnostics.CodeAnalysis;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using BenchmarkDotNet.Attributes;
using Sharpie.Features.Terminal;

namespace Sharpie.Benchmarks.Features.Terminal;

/// <summary>
/// Measures one frame of <see cref="TerminalView.Render" />, drawn with Skia into a bitmap the size of a 1080p window.
/// </summary>
/// <remarks>
/// Headless drawing is turned off so text is really shaped and drawn, as it is on screen.
/// </remarks>
[MemoryDiagnoser]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet needs an unsealed class and frees the bitmap through GlobalCleanup.")]
public class TerminalViewBenchmarks
{
	private static readonly PixelSize WindowSize = new(1920, 1080);

	private TerminalView blank = null!;
	private TerminalView plain = null!;
	private TerminalView colored = null!;
	private RenderTargetBitmap bitmap = null!;

	/// <summary>
	/// Starts the app on the headless platform and fills three views: one empty, one with plain text, and one with
	/// colored text.
	/// </summary>
	[GlobalSetup]
	public void Setup()
	{
		_ = AppBuilder.Configure<App>()
			.UseSkia()
			.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
			.SetupWithoutStarting();

		this.bitmap = new RenderTargetBitmap(WindowSize);
		this.blank = CreateView(string.Empty);
		this.plain = CreateView(Lines(row => $"{row,8} INFO  request handled in 12 ms for /api/items/{row} with status 200 OK and some more text to fill the row up\r\n"));
		this.colored = CreateView(Lines(row =>
			$"\e[01;34mfolder{row}\e[0m  \e[01;32mscript.sh\e[0m  readme.md  \e[38;2;{row * 4 % 256};120;200mtruecolor{row}\e[0m  \e[1;31merror\e[0m  \e[4munderlined\e[0m  \e[7minverse\e[0m  \e[42;30m bg \e[0m\r\n"));
	}

	/// <summary>
	/// Releases the bitmap the frames are drawn into.
	/// </summary>
	[GlobalCleanup]
	public void Cleanup() => this.bitmap.Dispose();

	/// <summary>
	/// Draws an empty screen, which is what a cursor blink at an idle prompt costs.
	/// </summary>
	[Benchmark(Baseline = true)]
	public void BlankScreen() => this.bitmap.Render(this.blank);

	/// <summary>
	/// Draws a screen full of plain text.
	/// </summary>
	[Benchmark]
	public void PlainScreen() => this.bitmap.Render(this.plain);

	/// <summary>
	/// Draws a screen full of colored, bold, underlined and inverse text.
	/// </summary>
	[Benchmark]
	public void ColoredScreen() => this.bitmap.Render(this.colored);

	/// <summary>
	/// Creates a view sized to the window and feeds it <paramref name="output" />.
	/// </summary>
	/// <param name="output">The program output to show.</param>
	/// <returns>
	/// The laid-out view.
	/// </returns>
	private static TerminalView CreateView(string output)
	{
		var view = new TerminalView(_ => { });
		var size = WindowSize.ToSize(1);

		view.Measure(size);
		view.Arrange(new Rect(size));
		view.Feed(output);

		return view;
	}

	/// <summary>
	/// Joins enough numbered lines to fill a tall screen.
	/// </summary>
	/// <param name="line">Makes the line for a row number.</param>
	/// <returns>
	/// The joined lines.
	/// </returns>
	private static string Lines(Func<int, string> line)
	{
		var text = new StringBuilder();

		for (var row = 0; row < 100; row++)
		{
			_ = text.Append(line(row));
		}

		return text.ToString();
	}
}
