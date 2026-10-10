using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Sharpie.Features.Vt;

namespace Sharpie.Benchmarks.Features.Vt;

/// <summary>
/// Measures <see cref="VtScreen.Feed" /> on about 1 MB of the kinds of output real programs print.
/// </summary>
/// <remarks>
/// The screens live across iterations, so the scrollback is full after the first one and every run measures the steady
/// state of a busy terminal rather than a fresh one.
/// </remarks>
[MemoryDiagnoser]
public class VtScreenBenchmarks
{
	private const int Columns = 200;
	private const int Rows = 50;
	private const int TargetLength = 1_000_000;

	private string plain = string.Empty;
	private string colored = string.Empty;
	private string redraw = string.Empty;
	private string wide = string.Empty;
	private VtScreen mainScreen = null!;
	private VtScreen alternateScreen = null!;

	/// <summary>
	/// Builds the output samples and two screens, one switched to the alternate screen as full-screen programs do.
	/// </summary>
	[GlobalSetup]
	public void Setup()
	{
		this.plain = Repeat(row => $"{row,8} INFO  request handled in 12 ms for /api/items/{row} with status 200 OK and nothing else to say here\r\n");
		this.colored = Repeat(row =>
			$"\e[01;34mfolder{row}\e[0m  \e[01;32mscript.sh\e[0m  readme.md  \e[38;2;{row % 256};120;200mtruecolor{row}\e[0m  \e[1;31merror\e[0m  \e[4munderlined\e[0m\r\n");
		this.redraw = Repeat(frame =>
		{
			var screen = new StringBuilder("\e[H");

			for (var row = 1; row <= Rows; row++)
			{
				_ = screen.Append(CultureInfo.InvariantCulture, $"\e[{row};1H\e[K\e[32m{frame + row,6}\e[0m \e[1mcpu\e[0m \e[33m{row % 100,3}%\e[0m ")
					.Append('|', row % 60)
					.Append(CultureInfo.InvariantCulture, $" \e[36m/usr/bin/process-{row}\e[0m");
			}

			return screen.ToString();
		});
		this.wide = Repeat(row => $"{row} 日本語のテキストと絵文字 🚀🎉✨ 中文字符 한국어 텍스트 {row}\r\n");
		this.mainScreen = new VtScreen(Columns, Rows, _ => { });
		this.alternateScreen = new VtScreen(Columns, Rows, _ => { });
		this.alternateScreen.Feed("\e[?1049h");
	}

	/// <summary>
	/// Feeds plain ASCII log lines, like <c>cat</c> on a big log file.
	/// </summary>
	/// <returns>
	/// The cursor row, so the work can't be optimized away.
	/// </returns>
	[Benchmark(Baseline = true)]
	public int PlainText()
	{
		this.mainScreen.Feed(this.plain);

		return this.mainScreen.CursorRow;
	}

	/// <summary>
	/// Feeds SGR-colored lines with truecolor, like <c>ls --color</c> or fastfetch.
	/// </summary>
	/// <returns>
	/// The cursor row, so the work can't be optimized away.
	/// </returns>
	[Benchmark]
	public int ColoredText()
	{
		this.mainScreen.Feed(this.colored);

		return this.mainScreen.CursorRow;
	}

	/// <summary>
	/// Feeds whole-screen redraws with cursor moves and erases on the alternate screen, like htop.
	/// </summary>
	/// <returns>
	/// The cursor row, so the work can't be optimized away.
	/// </returns>
	[Benchmark]
	public int FullScreenRedraw()
	{
		this.alternateScreen.Feed(this.redraw);

		return this.alternateScreen.CursorRow;
	}

	/// <summary>
	/// Feeds CJK text and emoji, which take two columns and surrogate pairs.
	/// </summary>
	/// <returns>
	/// The cursor row, so the work can't be optimized away.
	/// </returns>
	[Benchmark]
	public int WideCharacters()
	{
		this.mainScreen.Feed(this.wide);

		return this.mainScreen.CursorRow;
	}

	/// <summary>
	/// Joins numbered chunks until the text reaches about <see cref="TargetLength" /> characters.
	/// </summary>
	/// <param name="chunk">Makes the chunk for a number.</param>
	/// <returns>
	/// The joined text.
	/// </returns>
	private static string Repeat(Func<int, string> chunk)
	{
		var text = new StringBuilder(TargetLength + 4096);

		for (var index = 0; text.Length < TargetLength; index++)
		{
			_ = text.Append(chunk(index));
		}

		return text.ToString();
	}
}
