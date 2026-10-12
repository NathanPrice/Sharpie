using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
	private TerminalView changing = null!;
	private string[] redraws = [];
	private int frame;
	private RenderTargetBitmap bitmap = null!;

	/// <summary>
	/// Starts the app on the headless platform and fills four views: one empty, one with plain text, one with colored
	/// text, and one that two whole-screen redraws take turns on.
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
		this.redraws = [Redraw(0), Redraw(1)];
		this.changing = CreateView(this.redraws[1]);
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
	/// Redraws every row with new numbers and draws the frame, like htop, so no row's layout can be reused.
	/// </summary>
	[Benchmark]
	public void ChangedScreen()
	{
		this.changing.Feed(this.redraws[this.frame++ % 2]);
		this.bitmap.Render(this.changing);
	}

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
	/// Builds one whole-screen redraw that moves to each row, erases it and writes colored numbers, as htop does.
	/// </summary>
	/// <param name="frame">The frame number, which changes every row's numbers.</param>
	/// <returns>
	/// The output for the frame.
	/// </returns>
	private static string Redraw(int frame)
	{
		var text = new StringBuilder("\e[H");

		for (var row = 1; row <= 60; row++)
		{
			_ = text.Append(CultureInfo.InvariantCulture, $"\e[{row};1H\e[K\e[32m{frame + row,6}\e[0m \e[1mcpu\e[0m \e[33m{(frame + row) % 100,3}%\e[0m ")
				.Append('|', row % 60)
				.Append(CultureInfo.InvariantCulture, $" \e[36m/usr/bin/process-{row + frame}\e[0m");
		}

		return text.ToString();
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
