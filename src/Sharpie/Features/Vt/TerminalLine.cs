namespace Sharpie.Features.Vt;

/// <summary>
/// One cell of the terminal grid.
/// </summary>
/// <param name="Character">The character, or the high half of a surrogate pair, or <c>\0</c> for the second column of a wide character.</param>
/// <param name="Low">The low half of a surrogate pair, or <c>\0</c>.</param>
/// <param name="Width">2 for the first column of a wide character, 0 for its second column, and 1 otherwise.</param>
/// <param name="Style">The style's number in the screen's style table, where 0 is the default.</param>
internal readonly record struct Cell(char Character, char Low, byte Width, int Style)
{
	/// <summary>
	/// Creates an empty cell drawn in <paramref name="style" />, which keeps the background color a program erased
	/// with.
	/// </summary>
	/// <param name="style">The style number.</param>
	/// <returns>
	/// A space one column wide.
	/// </returns>
	public static Cell Blank(int style) => new(' ', '\0', 1, style);

	/// <summary>
	/// Gets the text the cell shows.
	/// </summary>
	/// <returns>
	/// The character, both halves of a surrogate pair, or an empty string for a wide character's second column.
	/// </returns>
	public string Text() => this.Width == 0 ? string.Empty : this.Low == '\0' ? this.Character.ToString() : string.Concat(this.Character, this.Low);
}

/// <summary>
/// One row of the terminal grid, on screen or in the scrollback.
/// </summary>
/// <param name="cells">The row's cells.</param>
internal sealed class TerminalLine(Cell[] cells)
{
	/// <summary>
	/// Gets or sets the row's cells.
	/// </summary>
	/// <value>
	/// As many cells as the screen had columns when the row was last on screen.
	/// </value>
	public Cell[] Cells { get; set; } = cells;

	/// <summary>
	/// Gets or sets a value indicating whether the text ran past the end of this row onto the next one.
	/// </summary>
	/// <value>
	/// <see langword="true" /> for a wrapped row, so copying joins it to the next without a line break.
	/// </value>
	public bool Wrapped { get; set; }

	/// <summary>
	/// Creates a row of blank cells.
	/// </summary>
	/// <param name="columns">The number of cells.</param>
	/// <param name="style">The style the blanks are drawn in.</param>
	/// <returns>
	/// The row.
	/// </returns>
	public static TerminalLine Blank(int columns, int style)
	{
		var cells = new Cell[columns];

		Array.Fill(cells, Cell.Blank(style));

		return new TerminalLine(cells);
	}

	/// <summary>
	/// Gets the row's text, without the spaces at its end.
	/// </summary>
	/// <param name="start">The first column.</param>
	/// <param name="end">The column after the last one.</param>
	/// <returns>
	/// The text of the columns from <paramref name="start" /> up to <paramref name="end" />.
	/// </returns>
	public string Text(int start = 0, int end = int.MaxValue)
	{
		end = Math.Min(end, this.Cells.Length);

		var text = new System.Text.StringBuilder();

		for (var column = Math.Max(0, start); column < end; column++)
		{
			_ = text.Append(this.Cells[column].Text());
		}

		return text.ToString().TrimEnd(' ');
	}
}

/// <summary>
/// The rows that scrolled off the top of the screen, oldest first, keeping only the newest ones once full.
/// </summary>
/// <param name="capacity">The most rows kept.</param>
/// <remarks>
/// A ring rather than a list, because a list would shift every row to drop the oldest one, and a program that prints
/// a lot drops one row for every row it prints.
/// </remarks>
internal sealed class Scrollback(int capacity)
{
	private readonly TerminalLine[] rows = new TerminalLine[capacity];
	private int first;

	/// <summary>
	/// Gets the number of rows kept.
	/// </summary>
	/// <value>
	/// From 0 up to the capacity.
	/// </value>
	public int Count { get; private set; }

	/// <summary>
	/// Gets the row at <paramref name="index" />, where 0 is the oldest.
	/// </summary>
	/// <param name="index">The row's position.</param>
	/// <returns>
	/// The row.
	/// </returns>
	public TerminalLine this[int index] => this.rows[(this.first + index) % this.rows.Length];

	/// <summary>
	/// Adds a row as the newest, dropping the oldest when full.
	/// </summary>
	/// <param name="row">The row.</param>
	/// <returns>
	/// The oldest row, which no longer belongs to the scrollback, or <see langword="null" /> while there's still room.
	/// </returns>
	public TerminalLine? Add(TerminalLine row)
	{
		if (this.Count < this.rows.Length)
		{
			this.rows[(this.first + this.Count) % this.rows.Length] = row;
			this.Count++;

			return null;
		}

		var dropped = this.rows[this.first];

		this.rows[this.first] = row;
		this.first = (this.first + 1) % this.rows.Length;

		return dropped;
	}

	/// <summary>
	/// Removes and returns the newest row, which a screen that grows taller takes back.
	/// </summary>
	/// <returns>
	/// The row.
	/// </returns>
	public TerminalLine RemoveNewest()
	{
		this.Count--;

		return this.rows[(this.first + this.Count) % this.rows.Length];
	}

	/// <summary>
	/// Drops every row.
	/// </summary>
	public void Clear()
	{
		Array.Clear(this.rows);
		this.first = 0;
		this.Count = 0;
	}
}
