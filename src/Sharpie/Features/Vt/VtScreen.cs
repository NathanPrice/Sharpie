using System.Text;
using Sharpie.Features.Ansi;

namespace Sharpie.Features.Vt;

/// <summary>
/// Which mouse events a program asked to receive.
/// </summary>
internal enum MouseTracking
{
	/// <summary>
	/// None. The mouse selects text and scrolls.
	/// </summary>
	None,

	/// <summary>
	/// Mode 9: presses only, with no modifiers.
	/// </summary>
	Press,

	/// <summary>
	/// Mode 1000: presses, releases and the wheel.
	/// </summary>
	PressRelease,

	/// <summary>
	/// Mode 1002: also moves while a button is held.
	/// </summary>
	Drag,

	/// <summary>
	/// Mode 1003: every move.
	/// </summary>
	AnyMove,
}

/// <summary>
/// How mouse reports are written.
/// </summary>
internal enum MouseEncoding
{
	/// <summary>
	/// The original bytes, each value plus 32, which can't go past column or row 223.
	/// </summary>
	Default,

	/// <summary>
	/// Mode 1006: decimal numbers, with <c>M</c> for a press and <c>m</c> for a release.
	/// </summary>
	Sgr,

	/// <summary>
	/// Mode 1015: decimal numbers, with the button still plus 32.
	/// </summary>
	Urxvt,
}

/// <summary>
/// The shape the cursor is drawn in, which programs like vim change with <c>DECSCUSR</c>.
/// </summary>
internal enum CursorShape
{
	/// <summary>
	/// A thin bar at the left of the cell, the window's usual caret.
	/// </summary>
	Bar,

	/// <summary>
	/// The whole cell.
	/// </summary>
	Block,

	/// <summary>
	/// A line under the cell.
	/// </summary>
	Underline,
}

/// <summary>
/// The terminal's screen: a grid of styled cells, the cursor, the scrollback, and the modes programs set with escape
/// sequences, following xterm.
/// </summary>
/// <remarks>
/// <see cref="Feed" /> takes program output, and anything the terminal has to answer, like a cursor position report,
/// goes back through the reply callback. Rows and columns are counted from 0 here, though escape sequences count from
/// 1.
/// </remarks>
internal sealed partial class VtScreen : IVtHandler
{
	/// <summary>
	/// The most rows the scrollback keeps.
	/// </summary>
	public const int ScrollbackRows = 10_000;

	// The style table's first size limit. Every distinct truecolor shade adds a style, so past the limit the table is
	// cut down to the styles still in use instead of growing as long as the tab is open.
	private const int InitialStyleLimit = 4096;

	// A bound so a program that pushes keyboard flags without popping them can't grow the stack forever. The kitty
	// keyboard protocol says to drop the oldest entry once the stack is full.
	private const int KeyboardFlagLimit = 16;

	// Only the first kitty keyboard flag, which sends keys like Shift+Enter apart from Enter, is supported. Masking
	// with it keeps the query's answer to what Sharpie actually does.
	private const int SupportedKeyboardFlags = 1;

	private readonly VtParser parser;
	private readonly Action<string> reply;
	private readonly List<AnsiStyle> styles = [default];
	private readonly Dictionary<AnsiStyle, int> styleIds = new() { [default] = 0 };

	// The kitty keyboard protocol keeps one stack for each screen, so a full-screen program's flags end with it.
	private readonly List<int> mainKeyboardFlags = [];
	private readonly List<int> alternateKeyboardFlags = [];

	private TerminalLine[] main;
	private TerminalLine[] alternate;
	private bool[] tabStops = [];

	private AnsiStyle style;
	private int styleId;
	private int styleLimit = InitialStyleLimit;

	// Set after a character lands in the last column. The next one wraps first, so a row can be filled exactly.
	private bool pendingWrap;

	private int top;
	private int bottom;
	private char highSurrogate;
	private int lastPrinted = ' ';
	private bool lineDrawing;
	private SavedCursor saved;
	private SavedCursor savedMain;

	/// <summary>
	/// Initializes a new instance of the <see cref="VtScreen" /> class with a blank screen.
	/// </summary>
	/// <param name="columns">The width in columns.</param>
	/// <param name="rows">The height in rows.</param>
	/// <param name="reply">Sends text back to the program, for reports it asks the terminal for.</param>
	public VtScreen(int columns, int rows, Action<string> reply)
	{
		ArgumentNullException.ThrowIfNull(reply);

		this.Columns = Math.Max(1, columns);
		this.Rows = Math.Max(1, rows);
		this.reply = reply;
		this.parser = new VtParser(this);
		this.main = BlankLines(this.Columns, this.Rows);
		this.alternate = BlankLines(this.Columns, this.Rows);
		this.Reset();
	}

	/// <summary>
	/// Occurs when a program rings the bell.
	/// </summary>
	public event EventHandler? Bell;

	/// <summary>
	/// Occurs when a program sets the window title with OSC 0 or 2.
	/// </summary>
	public event EventHandler? TitleChanged;

	/// <summary>
	/// Occurs when the shell asks the window to show a man page, with the command line in the event's text.
	/// </summary>
	public event EventHandler<string>? ManPageRequested;

	/// <summary>
	/// Occurs when the style table drops unused styles and gives the rest new numbers, so a cell's style number may now
	/// mean another style than before.
	/// </summary>
	public event EventHandler? StylesRenumbered;

	/// <summary>
	/// Gets the width in columns.
	/// </summary>
	/// <value>
	/// At least 1.
	/// </value>
	public int Columns { get; private set; }

	/// <summary>
	/// Gets the height in rows.
	/// </summary>
	/// <value>
	/// At least 1.
	/// </value>
	public int Rows { get; private set; }

	/// <summary>
	/// Gets the cursor's row on screen.
	/// </summary>
	/// <value>
	/// From 0 to <see cref="Rows" /> minus 1.
	/// </value>
	public int CursorRow { get; private set; }

	/// <summary>
	/// Gets the cursor's column.
	/// </summary>
	/// <value>
	/// From 0 to <see cref="Columns" /> minus 1.
	/// </value>
	public int CursorColumn { get; private set; }

	/// <summary>
	/// Gets a value indicating whether the cursor is shown.
	/// </summary>
	/// <value>
	/// <see langword="false" /> after a program hides it with mode 25.
	/// </value>
	public bool CursorVisible { get; private set; }

	/// <summary>
	/// Gets the cursor's shape.
	/// </summary>
	/// <value>
	/// <see cref="CursorShape.Bar" /> unless a program asked for another.
	/// </value>
	public CursorShape CursorShape { get; private set; }

	/// <summary>
	/// Gets a value indicating whether the alternate screen, which full-screen programs draw on, is showing.
	/// </summary>
	/// <value>
	/// <see langword="true" /> between modes 1049, 1047 or 47 being set and reset.
	/// </value>
	public bool AlternateScreen { get; private set; }

	/// <summary>
	/// Gets a value indicating whether the arrow keys send their application forms, <c>ESC O A</c> and so on.
	/// </summary>
	/// <value>
	/// <see langword="true" /> while mode 1 is set.
	/// </value>
	public bool ApplicationCursorKeys { get; private set; }

	/// <summary>
	/// Gets the kitty keyboard protocol flags the program asked for, with <c>ESC [ &gt; flags u</c> and the like.
	/// </summary>
	/// <value>
	/// 1 while keys that legacy encoding sends alike, such as Enter and Shift+Enter, are sent as separate
	/// <c>ESC [ code ; modifiers u</c> sequences; otherwise 0.
	/// </value>
	public int KeyboardFlags => this.KeyboardFlagStack is [.., var top] ? top : 0;

	/// <summary>
	/// Gets a value indicating whether pasted text is wrapped in <c>ESC [200~</c> and <c>ESC [201~</c>.
	/// </summary>
	/// <value>
	/// <see langword="true" /> while mode 2004 is set.
	/// </value>
	public bool BracketedPaste { get; private set; }

	/// <summary>
	/// Gets a value indicating whether the window reports gaining and losing focus.
	/// </summary>
	/// <value>
	/// <see langword="true" /> while mode 1004 is set.
	/// </value>
	public bool FocusEvents { get; private set; }

	/// <summary>
	/// Gets which mouse events the program wants.
	/// </summary>
	/// <value>
	/// <see cref="MouseTracking.None" /> unless a program turned tracking on.
	/// </value>
	public MouseTracking MouseTracking { get; private set; }

	/// <summary>
	/// Gets how mouse reports are written.
	/// </summary>
	/// <value>
	/// <see cref="MouseEncoding.Default" /> unless a program chose another.
	/// </value>
	public MouseEncoding MouseEncoding { get; private set; }

	/// <summary>
	/// Gets the title a program last set.
	/// </summary>
	/// <value>
	/// The title, or <see langword="null" /> when none was set.
	/// </value>
	public string? Title { get; private set; }

	/// <summary>
	/// Gets the folder the shell last reported with OSC 7.
	/// </summary>
	/// <value>
	/// The full path, or <see langword="null" /> until the first prompt.
	/// </value>
	public string? WorkingDirectory { get; private set; }

	/// <summary>
	/// Gets or sets the size of one cell in pixels, which programs can ask for.
	/// </summary>
	/// <value>
	/// The width and height. The view sets it once it knows its font.
	/// </value>
	public (int Width, int Height) CellPixels { get; set; } = (8, 16);

	/// <summary>
	/// Gets the number of rows in the scrollback and on screen together.
	/// </summary>
	/// <value>
	/// Only the screen's rows while the alternate screen shows, since it has no scrollback.
	/// </value>
	public int LineCount => this.ScrollbackCount + this.Rows;

	/// <summary>
	/// Gets the number of scrollback rows above the screen.
	/// </summary>
	/// <value>
	/// 0 while the alternate screen shows.
	/// </value>
	public int ScrollbackCount => this.AlternateScreen ? 0 : this.History.Count;

	/// <summary>
	/// Gets the number of styles in the style table.
	/// </summary>
	/// <value>
	/// At least 1, for the default style.
	/// </value>
	public int StyleCount => this.styles.Count;

	/// <summary>
	/// Gets the rows that scrolled off the top of the main screen.
	/// </summary>
	/// <value>
	/// At most <see cref="ScrollbackRows" /> rows.
	/// </value>
	private Scrollback History { get; } = new(ScrollbackRows);

	/// <summary>
	/// Gets the screen being drawn on.
	/// </summary>
	/// <value>
	/// The alternate screen's rows while it shows; otherwise, the main screen's.
	/// </value>
	private TerminalLine[] Lines => this.AlternateScreen ? this.alternate : this.main;

	/// <summary>
	/// Gets the keyboard flag stack for the screen showing.
	/// </summary>
	/// <value>
	/// The alternate screen's stack while it shows; otherwise, the main screen's. The last entry is in effect.
	/// </value>
	private List<int> KeyboardFlagStack => this.AlternateScreen ? this.alternateKeyboardFlags : this.mainKeyboardFlags;

	/// <summary>
	/// Reads the next chunk of program output.
	/// </summary>
	/// <param name="text">The chunk.</param>
	public void Feed(ReadOnlySpan<char> text) => this.parser.Feed(text);

	/// <summary>
	/// Gets a row by its place among <see cref="LineCount" /> rows, scrollback first.
	/// </summary>
	/// <param name="index">The row's position, 0 for the oldest scrollback row.</param>
	/// <returns>
	/// The row.
	/// </returns>
	public TerminalLine GetLine(int index) =>
		index < this.ScrollbackCount ? this.History[index] : this.Lines[index - this.ScrollbackCount];

	/// <summary>
	/// Gets the style a cell is drawn in.
	/// </summary>
	/// <param name="id">The cell's style number.</param>
	/// <returns>
	/// The style.
	/// </returns>
	public AnsiStyle GetStyle(int id) => this.styles[id];

	/// <summary>
	/// Gets the text of the scrollback and screen, one row per line, with wrapped rows joined.
	/// </summary>
	/// <returns>
	/// The text, without spaces at the end of rows or blank rows at the end.
	/// </returns>
	public string GetText() => this.GetText(0, 0, this.LineCount - 1, this.Columns);

	/// <summary>
	/// Gets the text between two points, as copying a selection does.
	/// </summary>
	/// <param name="startLine">The first row, counted as in <see cref="GetLine" />.</param>
	/// <param name="startColumn">The first column on that row.</param>
	/// <param name="endLine">The last row.</param>
	/// <param name="endColumn">The column after the last one on that row.</param>
	/// <returns>
	/// The text, with a line break after each row that didn't wrap, and blank rows at the end left out.
	/// </returns>
	public string GetText(int startLine, int startColumn, int endLine, int endColumn)
	{
		var text = new StringBuilder();

		for (var index = startLine; index <= endLine && index < this.LineCount; index++)
		{
			var line = this.GetLine(index);

			_ = text.Append(line.Text(index == startLine ? startColumn : 0, index == endLine ? endColumn : int.MaxValue));

			if (index < endLine && !line.Wrapped)
			{
				_ = text.Append('\n');
			}
		}

		return text.ToString().TrimEnd('\n');
	}

	/// <summary>
	/// Changes the screen's size, keeping the cursor's row on screen.
	/// </summary>
	/// <param name="columns">The new width in columns.</param>
	/// <param name="rows">The new height in rows.</param>
	/// <remarks>
	/// Rows aren't rewrapped to the new width; their ends are cut off or padded. When the screen gets shorter, rows
	/// above the cursor move into the scrollback, and when it gets taller they come back from it, as in xterm.
	/// </remarks>
	public void Resize(int columns, int rows)
	{
		columns = Math.Max(1, columns);
		rows = Math.Max(1, rows);

		if (columns == this.Columns && rows == this.Rows)
		{
			return;
		}

		this.main = this.ResizeLines(this.main, columns, rows, keepHistory: true);
		this.alternate = this.ResizeLines(this.alternate, columns, rows, keepHistory: false);
		this.Columns = columns;
		this.Rows = rows;
		this.top = 0;
		this.bottom = rows - 1;
		this.CursorColumn = Math.Min(this.CursorColumn, columns - 1);
		this.pendingWrap = false;
		this.ResetTabStops();
	}

	/// <summary>
	/// Builds a screen's rows for a new size.
	/// </summary>
	/// <param name="lines">The rows now.</param>
	/// <param name="columns">The new width.</param>
	/// <param name="rows">The new height.</param>
	/// <param name="keepHistory">
	/// <see langword="true" /> for the main screen, whose rows go to and come back from the scrollback.
	/// </param>
	/// <returns>
	/// The rows at the new size.
	/// </returns>
	private TerminalLine[] ResizeLines(TerminalLine[] lines, int columns, int rows, bool keepHistory)
	{
		var list = lines.ToList();
		var onThisScreen = keepHistory != this.AlternateScreen;
		var cursorRow = this.CursorRow;

		while (list.Count > rows)
		{
			// Rows below the cursor are dropped first, since they're usually empty. Then rows above it scroll off.
			if (!onThisScreen || cursorRow < list.Count - 1)
			{
				list.RemoveAt(list.Count - 1);
			}
			else
			{
				if (keepHistory)
				{
					_ = this.History.Add(list[0]);
				}

				list.RemoveAt(0);
				cursorRow--;
			}
		}

		while (list.Count < rows)
		{
			if (keepHistory && this.History.Count > 0)
			{
				list.Insert(0, this.History.RemoveNewest());
				cursorRow++;
			}
			else
			{
				list.Add(TerminalLine.Blank(columns, 0));
			}
		}

		foreach (var line in list)
		{
			if (line.Cells.Length != columns)
			{
				var cells = new Cell[columns];

				Array.Fill(cells, Cell.Blank(0));
				Array.Copy(line.Cells, cells, Math.Min(columns, line.Cells.Length));
				line.Cells = cells;
			}
		}

		if (onThisScreen)
		{
			this.CursorRow = Math.Clamp(cursorRow, 0, rows - 1);
		}

		return [.. list];
	}

	/// <inheritdoc />
	public void Print(char character)
	{
		if (char.IsHighSurrogate(character))
		{
			this.highSurrogate = character;

			return;
		}

		var low = '\0';

		if (char.IsLowSurrogate(character) && this.highSurrogate != '\0')
		{
			low = character;
			character = this.highSurrogate;
		}

		this.highSurrogate = '\0';

		if (this.lineDrawing && low == '\0')
		{
			character = LineDrawing(character);
		}

		var codePoint = low == '\0' ? character : char.ConvertToUtf32(character, low);
		var width = CellWidth.Of(codePoint);

		// Combining marks and joiners would need to be drawn over the character before. They're dropped instead, so
		// the base character still shows and the columns stay right.
		if (width == 0)
		{
			return;
		}

		this.lastPrinted = codePoint;

		if (this.pendingWrap || (width == 2 && this.CursorColumn == this.Columns - 1 && this.AutoWrap))
		{
			this.WrapToNextRow();
		}

		var cells = this.Lines[this.CursorRow].Cells;

		if (this.InsertMode)
		{
			this.InsertBlanks(width);
		}

		SplitWide(cells, this.CursorColumn);
		cells[this.CursorColumn] = new Cell(character, low, (byte)width, this.styleId);

		if (width == 2 && this.CursorColumn + 1 < this.Columns)
		{
			SplitWide(cells, this.CursorColumn + 1);
			cells[this.CursorColumn + 1] = new Cell('\0', '\0', 0, this.styleId);
		}

		if (this.CursorColumn + width >= this.Columns)
		{
			this.CursorColumn = this.Columns - 1;
			this.pendingWrap = this.AutoWrap;
		}
		else
		{
			this.CursorColumn += width;
		}
	}

	/// <summary>
	/// Marks the cursor's row as wrapped and moves to the start of the next row, scrolling at the bottom.
	/// </summary>
	private void WrapToNextRow()
	{
		this.Lines[this.CursorRow].Wrapped = true;
		this.CursorColumn = 0;
		this.Index();
	}

	/// <summary>
	/// Blanks the other half of a wide character before one of its columns is overwritten, so no half is left
	/// behind.
	/// </summary>
	/// <param name="cells">The row.</param>
	/// <param name="column">The column about to change.</param>
	private static void SplitWide(Cell[] cells, int column)
	{
		if (cells[column].Width == 0 && column > 0)
		{
			cells[column - 1] = Cell.Blank(cells[column - 1].Style);
		}
		else if (cells[column].Width == 2 && column + 1 < cells.Length)
		{
			cells[column + 1] = Cell.Blank(cells[column + 1].Style);
		}
	}

	/// <summary>
	/// Moves the cursor down a row, scrolling the scroll region up when it's on the region's last row.
	/// </summary>
	private void Index()
	{
		this.pendingWrap = false;

		if (this.CursorRow == this.bottom)
		{
			this.ScrollUp(1);
		}
		else if (this.CursorRow < this.Rows - 1)
		{
			this.CursorRow++;
		}
	}

	/// <summary>
	/// Moves the cursor up a row, scrolling the scroll region down when it's on the region's first row.
	/// </summary>
	private void ReverseIndex()
	{
		this.pendingWrap = false;

		if (this.CursorRow == this.top)
		{
			this.ScrollDown(1);
		}
		else if (this.CursorRow > 0)
		{
			this.CursorRow--;
		}
	}

	/// <summary>
	/// Scrolls the scroll region up, adding blank rows at its bottom.
	/// </summary>
	/// <param name="count">How many rows.</param>
	/// <remarks>
	/// Rows scrolled off the top of the main screen go to the scrollback when the region starts at the top, which
	/// keeps a status line a program pins to the bottom out of the scrollback.
	/// </remarks>
	private void ScrollUp(int count)
	{
		var lines = this.Lines;

		count = Math.Clamp(count, 0, this.bottom - this.top + 1);

		for (var i = 0; i < count; i++)
		{
			var dropped = lines[this.top];

			if (this.top == 0 && !this.AlternateScreen)
			{
				dropped = this.History.Add(dropped);
			}

			Array.Copy(lines, this.top + 1, lines, this.top, this.bottom - this.top);
			lines[this.bottom] = this.BlankLine(dropped);
		}
	}

	/// <summary>
	/// Scrolls the scroll region down, adding blank rows at its top.
	/// </summary>
	/// <param name="count">How many rows.</param>
	private void ScrollDown(int count)
	{
		var lines = this.Lines;

		count = Math.Clamp(count, 0, this.bottom - this.top + 1);

		for (var i = 0; i < count; i++)
		{
			var dropped = lines[this.bottom];

			Array.Copy(lines, this.top, lines, this.top + 1, this.bottom - this.top);
			lines[this.top] = this.BlankLine(dropped);
		}
	}

	/// <summary>
	/// Makes a blank row in the current background color, reusing a row that just left the screen or scrollback.
	/// </summary>
	/// <param name="dropped">The row nothing holds anymore, or <see langword="null" /> when there isn't one.</param>
	/// <returns>
	/// <paramref name="dropped" /> wiped, or a new row when it's missing or was cut for another width.
	/// </returns>
	/// <remarks>
	/// Reusing the row saves a new row and its cells for every row printed. Once the scrollback is full, every dropped
	/// row has lived long enough to reach the older garbage collector generations, so heavy output like a build log
	/// would keep triggering expensive collections.
	/// </remarks>
	private TerminalLine BlankLine(TerminalLine? dropped)
	{
		// Scrollback rows keep the width they had when they left the screen, so a resize can leave them the wrong size.
		if (dropped is null || dropped.Cells.Length != this.Columns)
		{
			return TerminalLine.Blank(this.Columns, this.EraseStyle());
		}

		Array.Fill(dropped.Cells, Cell.Blank(this.EraseStyle()));
		dropped.Wrapped = false;

		return dropped;
	}

	/// <summary>
	/// Gets the style erased cells take: the current background color and nothing else, as xterm does.
	/// </summary>
	/// <returns>
	/// The style number.
	/// </returns>
	private int EraseStyle() => this.style.Background is null && !this.style.Inverse
		? 0
		: this.StyleId(new AnsiStyle(null, this.style.Background, Bold: false, Inverse: this.style.Inverse));

	/// <summary>
	/// Blanks the cells from <paramref name="start" /> up to <paramref name="end" /> on one row.
	/// </summary>
	/// <param name="row">The row on screen.</param>
	/// <param name="start">The first column.</param>
	/// <param name="end">The column after the last one.</param>
	private void Erase(int row, int start, int end)
	{
		var cells = this.Lines[row].Cells;

		start = Math.Clamp(start, 0, cells.Length);
		end = Math.Clamp(end, 0, cells.Length);

		if (start < end)
		{
			SplitWide(cells, start);
			SplitWide(cells, end - 1);
			Array.Fill(cells, Cell.Blank(this.EraseStyle()), start, end - start);
		}

		this.Lines[row].Wrapped &= end < cells.Length;
	}

	/// <summary>
	/// Shifts the cells from the cursor to the end of its row right, opening blank cells at the cursor.
	/// </summary>
	/// <param name="count">How many cells to open.</param>
	private void InsertBlanks(int count)
	{
		var cells = this.Lines[this.CursorRow].Cells;

		count = Math.Clamp(count, 0, this.Columns - this.CursorColumn);
		Array.Copy(cells, this.CursorColumn, cells, this.CursorColumn + count, this.Columns - this.CursorColumn - count);
		Array.Fill(cells, Cell.Blank(this.EraseStyle()), this.CursorColumn, count);
	}

	/// <summary>
	/// Removes cells at the cursor, shifting the rest of the row left and filling its end with blanks.
	/// </summary>
	/// <param name="count">How many cells to remove.</param>
	private void DeleteCells(int count)
	{
		var cells = this.Lines[this.CursorRow].Cells;

		count = Math.Clamp(count, 0, this.Columns - this.CursorColumn);
		Array.Copy(cells, this.CursorColumn + count, cells, this.CursorColumn, this.Columns - this.CursorColumn - count);
		Array.Fill(cells, Cell.Blank(this.EraseStyle()), this.Columns - count, count);
	}

	/// <summary>
	/// Gets the number for <paramref name="value" /> in the style table, adding it the first time it's used.
	/// </summary>
	/// <param name="value">The style.</param>
	/// <returns>
	/// The style number cells store.
	/// </returns>
	private int StyleId(AnsiStyle value)
	{
		if (!this.styleIds.TryGetValue(value, out var id))
		{
			if (this.styles.Count >= this.styleLimit)
			{
				this.CompactStyles();
			}

			id = this.styles.Count;
			this.styles.Add(value);
			this.styleIds[value] = id;
		}

		return id;
	}

	/// <summary>
	/// Drops the styles no cell uses anymore and numbers the rest from 0 again.
	/// </summary>
	/// <remarks>
	/// The default style keeps number 0, and the style being printed in is kept even when no cell has it yet. The next
	/// limit is twice the styles kept, so output that keeps every style in use, like a rainbow of truecolor text, makes
	/// this run less and less often instead of on every new style.
	/// </remarks>
	private void CompactStyles()
	{
		var renumbered = new int[this.styles.Count];
		var kept = new List<AnsiStyle>();

		Array.Fill(renumbered, -1);
		_ = Renumber(0);
		this.styleId = Renumber(this.styleId);

		foreach (var line in this.main.Concat(this.alternate))
		{
			RenumberCells(line.Cells);
		}

		for (var index = 0; index < this.History.Count; index++)
		{
			RenumberCells(this.History[index].Cells);
		}

		this.styles.Clear();
		this.styles.AddRange(kept);
		this.styleIds.Clear();

		for (var id = 0; id < kept.Count; id++)
		{
			this.styleIds[kept[id]] = id;
		}

		this.styleLimit = Math.Max(InitialStyleLimit, kept.Count * 2);
		this.StylesRenumbered?.Invoke(this, EventArgs.Empty);

		void RenumberCells(Cell[] cells)
		{
			for (var column = 0; column < cells.Length; column++)
			{
				cells[column] = cells[column] with { Style = Renumber(cells[column].Style) };
			}
		}

		int Renumber(int id)
		{
			if (renumbered[id] < 0)
			{
				renumbered[id] = kept.Count;
				kept.Add(this.styles[id]);
			}

			return renumbered[id];
		}
	}

	/// <summary>
	/// Sets the style for text printed from now on.
	/// </summary>
	/// <param name="value">The style.</param>
	private void SetStyle(AnsiStyle value)
	{
		this.style = value;
		this.styleId = this.StyleId(value);
	}

	/// <summary>
	/// Puts back the default tab stops, every eight columns.
	/// </summary>
	private void ResetTabStops()
	{
		this.tabStops = new bool[this.Columns];

		for (var column = 8; column < this.Columns; column += 8)
		{
			this.tabStops[column] = true;
		}
	}

	/// <summary>
	/// Makes a screen's worth of blank rows.
	/// </summary>
	/// <param name="columns">The width.</param>
	/// <param name="rows">The height.</param>
	/// <returns>
	/// The rows.
	/// </returns>
	private static TerminalLine[] BlankLines(int columns, int rows) =>
		[.. Enumerable.Range(0, rows).Select(_ => TerminalLine.Blank(columns, 0))];

	/// <summary>
	/// Turns a character into its DEC line drawing form, which programs select with <c>ESC ( 0</c> to draw boxes.
	/// </summary>
	/// <param name="character">The character as sent.</param>
	/// <returns>
	/// The box drawing or symbol character for <c>`</c> through <c>~</c>, or <paramref name="character" /> itself.
	/// </returns>
	private static char LineDrawing(char character) => character is >= '`' and <= '~'
		? "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·"[character - '`']
		: character;

	/// <summary>
	/// The cursor state <c>ESC 7</c> saves and <c>ESC 8</c> restores.
	/// </summary>
	/// <param name="Row">The row.</param>
	/// <param name="Column">The column.</param>
	/// <param name="Style">The text style.</param>
	/// <param name="OriginMode">Whether cursor positions count from the scroll region's top.</param>
	/// <param name="LineDrawing">Whether the line drawing characters are selected.</param>
	private readonly record struct SavedCursor(int Row, int Column, AnsiStyle Style, bool OriginMode, bool LineDrawing);
}
