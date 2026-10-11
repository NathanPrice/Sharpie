using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Sharpie.Features.Vt;

namespace Sharpie.Features.Terminal;

/// <summary>
/// A stretch of one row's text, laid out and ready to draw.
/// </summary>
/// <param name="Text">The laid-out text.</param>
/// <param name="Column">The first column it covers.</param>
/// <param name="Width">How many columns it covers, for the underline.</param>
/// <param name="Underline">The pen for the underline, or <see langword="null" /> when the text isn't underlined.</param>
internal readonly record struct ShapedRun(TextLayout Text, int Column, int Width, IPen? Underline);

/// <summary>
/// Keeps the laid-out text of the rows drawn in the last frame, so a row is laid out again only when its cells
/// change.
/// </summary>
/// <remarks>
/// Laying out text is most of what a frame costs, and the cursor blink redraws the whole screen twice a second.
/// Comparing a row's cells with a copy is enough to spot any change, because a cell holds its style as a number and
/// <see cref="VtScreen" />'s style table only grows, so a number always means the same colors. Rows are looked up by
/// <see cref="TerminalLine" /> rather than by their place in the view, so rows that only moved up when output
/// scrolled are drawn again without a new layout. Only the last frame's rows are kept, which holds memory to about
/// two screens instead of the whole scrollback. It holds <see cref="TextLayout" /> rather than
/// <see cref="FormattedText" />, because <see cref="FormattedText" /> lays its text out again every time it's drawn.
/// </remarks>
internal sealed class RowCache
{
	private Dictionary<TerminalLine, (Cell[] Cells, IReadOnlyList<ShapedRun> Runs)> current = [];
	private Dictionary<TerminalLine, (Cell[] Cells, IReadOnlyList<ShapedRun> Runs)> previous = [];

	/// <summary>
	/// Starts a new frame, keeping the rows drawn in the frame that just ended and dropping any older ones.
	/// </summary>
	public void NextFrame()
	{
		foreach (var (_, runs) in this.previous.Values)
		{
			Dispose(runs);
		}

		(this.previous, this.current) = (this.current, this.previous);
		this.current.Clear();
	}

	/// <summary>
	/// Gets a row's text as laid out in the last frame, when its cells haven't changed since.
	/// </summary>
	/// <param name="line">The row.</param>
	/// <param name="cells">The row's cells as they are now.</param>
	/// <returns>
	/// The laid-out text, or <see langword="null" /> when the row wasn't drawn last frame or its cells changed.
	/// </returns>
	public IReadOnlyList<ShapedRun>? Find(TerminalLine line, ReadOnlySpan<Cell> cells)
	{
		if (!this.previous.Remove(line, out var entry))
		{
			return null;
		}

		if (!cells.SequenceEqual(entry.Cells))
		{
			Dispose(entry.Runs);

			return null;
		}

		this.current[line] = entry;

		return entry.Runs;
	}

	/// <summary>
	/// Keeps a row's newly laid-out text for the next frame.
	/// </summary>
	/// <param name="line">The row.</param>
	/// <param name="cells">The cells the text was laid out from.</param>
	/// <param name="runs">The laid-out text.</param>
	public void Add(TerminalLine line, ReadOnlySpan<Cell> cells, IReadOnlyList<ShapedRun> runs) =>
		this.current[line] = (cells.ToArray(), runs);

	/// <summary>
	/// Frees the layouts of a row that's no longer kept.
	/// </summary>
	/// <param name="runs">The row's laid-out text.</param>
	/// <remarks>
	/// Disposing hands the layouts' buffers back to Avalonia's pools, as its own <c>TextBlock</c> does when its text
	/// changes.
	/// </remarks>
	private static void Dispose(IReadOnlyList<ShapedRun> runs)
	{
		foreach (var run in runs)
		{
			run.Text.Dispose();
		}
	}
}
