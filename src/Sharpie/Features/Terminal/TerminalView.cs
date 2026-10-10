using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Sharpie.Features.Ansi;
using Sharpie.Features.Vt;

namespace Sharpie.Features.Terminal;

/// <summary>
/// Draws a <see cref="VtScreen" /> in the window's font and colors, and turns keys and the mouse into input for the
/// program running in it.
/// </summary>
/// <remarks>
/// The grid is drawn straight onto the control rather than through a text editor, because programs move the cursor
/// anywhere and rewrite any cell. Text is drawn a run of same-styled cells at a time, and each character outside
/// ASCII on its own at its column, since a fallback font's glyphs can be wider or narrower than the main font's cells.
/// </remarks>
internal sealed partial class TerminalView : Control
{
	/// <summary>
	/// The space between the window frame and the text, as the old screen had.
	/// </summary>
	public const double Padding = 16;

	/// <summary>
	/// The font size, which the man page reader uses too.
	/// </summary>
	public const double FontSize = 15;

	/// <summary>
	/// The font, which the man page reader uses too.
	/// </summary>
	/// <remarks>
	/// Adwaita Mono is the chosen font. It usually isn't installed on Windows or macOS, so the rest of the list covers
	/// those.
	/// </remarks>
	public static readonly FontFamily Font = new("Adwaita Mono, Cascadia Mono, Menlo, Consolas, monospace");

	// How long the cursor stays on and off while blinking, close to AvaloniaEdit's caret.
	private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(530);

	private readonly Typeface regular = new(Font);
	private readonly Typeface bold = new(Font, weight: FontWeight.Bold);
	private readonly Action<byte[]> send;
	private readonly DispatcherTimer blink;
	private readonly double cellWidth;
	private readonly double cellHeight;

	private bool cursorOn = true;

	// The selection's two ends as (row among VtScreen.LineCount, column), or null when nothing is selected.
	private (int Line, int Column)? anchor;
	private (int Line, int Column) caret;

	/// <summary>
	/// Initializes a new instance of the <see cref="TerminalView" /> class.
	/// </summary>
	/// <param name="send">Sends input to the program, as bytes ready for its terminal.</param>
	public TerminalView(Action<byte[]> send)
	{
		ArgumentNullException.ThrowIfNull(send);

		this.send = send;
		this.Focusable = true;
		this.ClipToBounds = true;

		var sample = this.Format("M", this.regular, Palette.Text);

		this.cellWidth = sample.WidthIncludingTrailingWhitespace;
		this.cellHeight = sample.Height;
		this.Screen = new VtScreen(80, 24, reply => this.send(Encoding.UTF8.GetBytes(reply)))
		{
			CellPixels = ((int)Math.Round(this.cellWidth), (int)Math.Round(this.cellHeight)),
		};

		this.blink = new DispatcherTimer(BlinkInterval, DispatcherPriority.Background, (_, _) =>
		{
			this.cursorOn = !this.cursorOn;
			this.InvalidateVisual();
		});
	}

	/// <summary>
	/// Occurs when the grid's size in cells changes, so the program's terminal can be resized to match.
	/// </summary>
	public event EventHandler? GridResized;

	/// <summary>
	/// Occurs when the view scrolls or the scrollback grows, so a scroll bar can follow.
	/// </summary>
	public event EventHandler? Scrolled;

	/// <summary>
	/// Gets the screen the view draws.
	/// </summary>
	/// <value>
	/// The terminal state program output is fed into.
	/// </value>
	public VtScreen Screen { get; }

	/// <summary>
	/// Gets how many rows the view is scrolled back from the newest output.
	/// </summary>
	/// <value>
	/// 0 when the bottom of the screen shows, up to <see cref="VtScreen.ScrollbackCount" />.
	/// </value>
	public int ScrollOffset { get; private set; }

	/// <summary>
	/// Shows program output, scrolling back to the bottom as a terminal does when something new is printed.
	/// </summary>
	/// <param name="text">The output.</param>
	public void Feed(string text)
	{
		this.Screen.Feed(text);
		this.ScrollOffset = 0;
		this.ShowCursor();
		this.InvalidateVisual();
		this.Scrolled?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// Scrolls so the given number of rows of scrollback sit below the top of the view.
	/// </summary>
	/// <param name="offset">How far back from the newest output, in rows.</param>
	public void ScrollTo(int offset)
	{
		var clamped = Math.Clamp(offset, 0, this.Screen.ScrollbackCount);

		if (clamped != this.ScrollOffset)
		{
			this.ScrollOffset = clamped;
			this.InvalidateVisual();
			this.Scrolled?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>
	/// Gets the selected text.
	/// </summary>
	/// <returns>
	/// The text, or <see langword="null" /> when nothing is selected.
	/// </returns>
	public string? SelectedText()
	{
		if (this.Ordered() is not var ((startLine, startColumn), (endLine, endColumn)))
		{
			return null;
		}

		return this.Screen.GetText(startLine, startColumn, endLine, endColumn);
	}

	/// <inheritdoc />
	public override void Render(DrawingContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		// A clear fill, so the whole control takes pointer input even where nothing is drawn.
		context.FillRectangle(Brushes.Transparent, new Rect(this.Bounds.Size));

		var first = this.Screen.LineCount - this.Screen.Rows - this.ScrollOffset;

		for (var row = 0; row < this.Screen.Rows; row++)
		{
			var line = first + row;

			this.DrawBackgrounds(context, this.Screen.GetLine(line), row);
			this.DrawSelection(context, line, row);
			this.DrawText(context, this.Screen.GetLine(line), row);
		}

		this.DrawCursor(context);
	}

	/// <inheritdoc />
	protected override Size ArrangeOverride(Size finalSize)
	{
		var columns = (int)((finalSize.Width - (2 * Padding)) / this.cellWidth);
		var rows = (int)((finalSize.Height - (2 * Padding)) / this.cellHeight);

		if (columns > 0 && rows > 0 && (columns != this.Screen.Columns || rows != this.Screen.Rows))
		{
			this.Screen.Resize(columns, rows);
			this.GridResized?.Invoke(this, EventArgs.Empty);
			this.Scrolled?.Invoke(this, EventArgs.Empty);
		}

		return base.ArrangeOverride(finalSize);
	}

	/// <inheritdoc />
	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		this.blink.Start();
	}

	/// <inheritdoc />
	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		this.blink.Stop();
		base.OnDetachedFromVisualTree(e);
	}

	/// <summary>
	/// Turns the cursor on and restarts its blink, so it doesn't vanish while the user types.
	/// </summary>
	private void ShowCursor()
	{
		this.cursorOn = true;

		if (this.blink.IsEnabled)
		{
			this.blink.Stop();
			this.blink.Start();
		}
	}

	/// <summary>
	/// Fills the cells that have a background color.
	/// </summary>
	/// <param name="context">Where to draw.</param>
	/// <param name="line">The row's cells.</param>
	/// <param name="row">The row's place in the view.</param>
	private void DrawBackgrounds(DrawingContext context, TerminalLine line, int row)
	{
		var cells = line.Cells;
		var count = Math.Min(cells.Length, this.Screen.Columns);

		for (var column = 0; column < count;)
		{
			var end = column + 1;

			while (end < count && cells[end].Style == cells[column].Style)
			{
				end++;
			}

			if (this.Colors(this.Screen.GetStyle(cells[column].Style)).Background is { } background)
			{
				context.FillRectangle(background, this.CellRect(row, column, end - column));
			}

			column = end;
		}
	}

	/// <summary>
	/// Draws the selection highlight on one row, as the old screen did, with rounded corners under the text.
	/// </summary>
	/// <param name="context">Where to draw.</param>
	/// <param name="line">The row's place among all rows.</param>
	/// <param name="row">The row's place in the view.</param>
	private void DrawSelection(DrawingContext context, int line, int row)
	{
		if (this.Ordered() is not var ((startLine, startColumn), (endLine, endColumn)) || line < startLine || line > endLine)
		{
			return;
		}

		var from = line == startLine ? startColumn : 0;
		var to = line == endLine ? endColumn : this.Screen.Columns;

		if (to > from)
		{
			context.DrawRectangle(Palette.Selection, null, this.CellRect(row, from, to - from), 3, 3);
		}
	}

	/// <summary>
	/// Draws one row's characters.
	/// </summary>
	/// <param name="context">Where to draw.</param>
	/// <param name="line">The row's cells.</param>
	/// <param name="row">The row's place in the view.</param>
	private void DrawText(DrawingContext context, TerminalLine line, int row)
	{
		var cells = line.Cells;
		var count = Math.Min(cells.Length, this.Screen.Columns);
		var run = new StringBuilder();

		for (var column = 0; column < count;)
		{
			var style = this.Screen.GetStyle(cells[column].Style);

			// A run is same-styled ASCII, which the main font always has at exactly one cell per character.
			if (cells[column].Character is >= ' ' and < '\x7F')
			{
				var end = column;

				_ = run.Clear();

				while (end < count && cells[end].Style == cells[column].Style && cells[end].Character is >= ' ' and < '\x7F')
				{
					_ = run.Append(cells[end].Character);
					end++;
				}

				if (run.ToString().Trim().Length > 0 || style.Underline)
				{
					this.DrawRun(context, run.ToString(), style, row, column, end - column);
				}

				column = end;
			}
			else
			{
				if (cells[column].Width > 0)
				{
					this.DrawRun(context, cells[column].Text(), style, row, column, cells[column].Width);
				}

				column++;
			}
		}
	}

	/// <summary>
	/// Draws text in a style starting at a cell.
	/// </summary>
	/// <param name="context">Where to draw.</param>
	/// <param name="text">The text.</param>
	/// <param name="style">The style.</param>
	/// <param name="row">The row in the view.</param>
	/// <param name="column">The first column.</param>
	/// <param name="width">How many columns the text covers, for the underline.</param>
	private void DrawRun(DrawingContext context, string text, AnsiStyle style, int row, int column, int width)
	{
		if (style.Hidden)
		{
			return;
		}

		var rect = this.CellRect(row, column, width);
		var foreground = this.Colors(style).Foreground;

		context.DrawText(this.Format(text, style.Bold ? this.bold : this.regular, foreground), rect.TopLeft);

		if (style.Underline)
		{
			context.DrawLine(new Pen(foreground), new Point(rect.Left, rect.Bottom - 1.5), new Point(rect.Right, rect.Bottom - 1.5));
		}
	}

	/// <summary>
	/// Draws the cursor in the window's accent color, in the shape the program asked for, when it's on screen.
	/// </summary>
	/// <param name="context">Where to draw.</param>
	private void DrawCursor(DrawingContext context)
	{
		if (!this.Screen.CursorVisible || !this.cursorOn)
		{
			return;
		}

		var row = this.Screen.CursorRow + this.ScrollOffset;

		if (row >= this.Screen.Rows)
		{
			return;
		}

		var cell = this.CellRect(row, this.Screen.CursorColumn, 1);
		var shape = this.Screen.CursorShape switch
		{
			CursorShape.Block => cell,
			CursorShape.Underline => new Rect(cell.Left, cell.Bottom - 2, cell.Width, 2),
			_ => new Rect(cell.Left, cell.Top, 2, cell.Height),
		};

		context.FillRectangle(this.Screen.CursorShape == CursorShape.Block ? Palette.Selection : Palette.Accent, shape);
	}

	/// <summary>
	/// Works out the brushes a style is drawn with, swapping them for inverse video.
	/// </summary>
	/// <param name="style">The style.</param>
	/// <returns>
	/// The text brush, and the background brush or <see langword="null" /> for the window's own background.
	/// </returns>
	private (IBrush Foreground, IBrush? Background) Colors(AnsiStyle style)
	{
		var foreground = style.Foreground is { } fg ? Palette.For(fg) : Palette.Text;
		var background = style.Background is { } bg ? Palette.For(bg) : null;

		return style.Inverse ? (background ?? Palette.BackgroundBrush, foreground) : (foreground, background);
	}

	/// <summary>
	/// Gets the rectangle covering cells on one row of the view.
	/// </summary>
	/// <param name="row">The row in the view.</param>
	/// <param name="column">The first column.</param>
	/// <param name="width">How many columns.</param>
	/// <returns>
	/// The rectangle in the control's coordinates.
	/// </returns>
	private Rect CellRect(int row, int column, int width) =>
		new(Padding + (column * this.cellWidth), Padding + (row * this.cellHeight), width * this.cellWidth, this.cellHeight);

	/// <summary>
	/// Lays out text in the window's font.
	/// </summary>
	/// <param name="characters">The characters.</param>
	/// <param name="typeface">Regular or bold.</param>
	/// <param name="brush">The text color.</param>
	/// <returns>
	/// The text, ready to draw.
	/// </returns>
	private FormattedText Format(string characters, Typeface typeface, IBrush brush) =>
		new(characters, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, brush);

	/// <summary>
	/// Gets the selection with its start before its end.
	/// </summary>
	/// <returns>
	/// The start and the end, the end's column being one past the last selected, or <see langword="null" /> when
	/// nothing is selected.
	/// </returns>
	private ((int Line, int Column) Start, (int Line, int Column) End)? Ordered()
	{
		if (this.anchor is not { } start || start == this.caret)
		{
			return null;
		}

		return start.CompareTo(this.caret) < 0 ? (start, this.caret) : (this.caret, start);
	}
}
