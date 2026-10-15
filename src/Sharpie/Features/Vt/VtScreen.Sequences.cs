using System.Globalization;
using System.Text;
using Serilog;
using Sharpie.Features.Ansi;

namespace Sharpie.Features.Vt;

/// <content>
/// Acts on control characters and escape sequences.
/// </content>
internal sealed partial class VtScreen
{
	// The colors OSC 10 and 11 report for text and background, the window's Palette.Text and Palette.Background.
	private const string ForegroundReport = "rgb:dada/dddd/e3e3";
	private const string BackgroundReport = "rgb:1515/1717/1c1c";

	/// <summary>
	/// Gets a value indicating whether printing past the last column wraps to the next row.
	/// </summary>
	/// <value>
	/// <see langword="true" /> unless a program resets mode 7.
	/// </value>
	public bool AutoWrap { get; private set; }

	/// <summary>
	/// Gets a value indicating whether printing shifts the rest of the row right instead of overwriting it.
	/// </summary>
	/// <value>
	/// <see langword="true" /> while mode 4 is set.
	/// </value>
	private bool InsertMode { get; set; }

	/// <summary>
	/// Gets a value indicating whether cursor positions count from the top of the scroll region.
	/// </summary>
	/// <value>
	/// <see langword="true" /> while mode 6 is set.
	/// </value>
	private bool OriginMode { get; set; }

	/// <inheritdoc />
	public void Execute(char control)
	{
		switch (control)
		{
			case '\a':
				this.Bell?.Invoke(this, EventArgs.Empty);
				break;
			case '\b':
				this.CursorColumn = Math.Max(0, this.CursorColumn - 1);
				this.pendingWrap = false;
				break;
			case '\t':
				this.TabForward(1);
				break;
			case '\n' or '\v' or '\f':
				this.Index();
				break;
			case '\r':
				this.CursorColumn = 0;
				this.pendingWrap = false;
				break;
			case '\x0E':
				this.lineDrawing = true;
				break;
			case '\x0F':
				this.lineDrawing = false;
				break;
			default:
				break;
		}
	}

	/// <inheritdoc />
	public void Escape(char intermediate, char final)
	{
		if (intermediate == '(')
		{
			this.lineDrawing = final == '0';

			return;
		}

		if (intermediate != '\0')
		{
			return;
		}

		switch (final)
		{
			case '7':
				this.SaveCursor();
				break;
			case '8':
				this.RestoreCursor();
				break;
			case 'D':
				this.Index();
				break;
			case 'E':
				this.CursorColumn = 0;
				this.Index();
				break;
			case 'H':
				this.tabStops[this.CursorColumn] = true;
				break;
			case 'M':
				this.ReverseIndex();
				break;
			case 'c':
				this.Reset();
				break;
			default:
				break;
		}
	}

	/// <inheritdoc />
	public void Csi(char marker, ReadOnlySpan<int> parameters, char intermediate, char final)
	{
		var first = parameters[0];
		var count = Math.Max(1, first);

		if (marker == '?')
		{
			if (final is 'h' or 'l')
			{
				foreach (var mode in parameters)
				{
					this.SetPrivateMode(mode, final == 'h');
				}
			}

			return;
		}

		if (marker == '>')
		{
			// Secondary device attributes: a VT220-class terminal with no firmware version to speak of.
			if (final == 'c')
			{
				this.reply("\e[>1;10;0c");
			}

			return;
		}

		if (intermediate == ' ' && final == 'q')
		{
			this.CursorShape = first switch
			{
				3 or 4 => CursorShape.Underline,
				1 or 2 => CursorShape.Block,
				_ => CursorShape.Bar,
			};

			return;
		}

		if (intermediate != '\0' || marker != '\0')
		{
			return;
		}

		// Cursor moves and edits cancel a wrap waiting at the end of the row. Styles, reports and modes leave it.
		this.pendingWrap &= final is 'm' or 'n' or 'c' or 't' or 'h' or 'l';

		// The two handle different final characters, so each ignores the other's.
		this.MoveCursor(final, count, parameters);
		this.Edit(final, first, count, parameters);
	}

	/// <summary>
	/// Acts on a CSI sequence that moves the cursor.
	/// </summary>
	/// <param name="final">The sequence's final character.</param>
	/// <param name="count">The first parameter, at least 1.</param>
	/// <param name="parameters">All the parameters, with -1 for one left empty.</param>
	private void MoveCursor(char final, int count, ReadOnlySpan<int> parameters)
	{
		switch (final)
		{
			case 'A':
				this.CursorRow = this.RowAbove(count);
				break;
			case 'B' or 'e':
				this.CursorRow = this.RowBelow(count);
				break;
			case 'C' or 'a':
				this.CursorColumn = Math.Min(this.Columns - 1, this.CursorColumn + count);
				break;
			case 'D':
				this.CursorColumn = Math.Max(0, this.CursorColumn - count);
				break;
			case 'E':
				this.CursorColumn = 0;
				this.CursorRow = this.RowBelow(count);
				break;
			case 'F':
				this.CursorColumn = 0;
				this.CursorRow = this.RowAbove(count);
				break;
			case 'G' or '`':
				this.CursorColumn = Math.Clamp(count - 1, 0, this.Columns - 1);
				break;
			case 'H' or 'f':
				this.MoveTo(count - 1, Math.Max(1, parameters.Length > 1 ? parameters[1] : 1) - 1);
				break;
			case 'I':
				this.TabForward(count);
				break;
			case 'Z':
				this.TabBack(count);
				break;
			case 'd':
				this.MoveTo(count - 1, this.CursorColumn);
				break;
			default:
				break;
		}
	}

	/// <summary>
	/// Finds the row a cursor move up ends on.
	/// </summary>
	/// <param name="count">How many rows to move.</param>
	/// <returns>
	/// The row, stopped at the scroll region's top when the cursor starts in or below the region, or at the screen's
	/// top when it starts above it.
	/// </returns>
	private int RowAbove(int count) => Math.Max(this.CursorRow >= this.top ? this.top : 0, this.CursorRow - count);

	/// <summary>
	/// Finds the row a cursor move down ends on.
	/// </summary>
	/// <param name="count">How many rows to move.</param>
	/// <returns>
	/// The row, stopped at the scroll region's bottom when the cursor starts in or above the region, or at the
	/// screen's bottom when it starts below it.
	/// </returns>
	private int RowBelow(int count) => Math.Min(this.CursorRow <= this.bottom ? this.bottom : this.Rows - 1, this.CursorRow + count);

	/// <summary>
	/// Acts on a CSI sequence that edits the screen, sets a mode or style, or asks for a report.
	/// </summary>
	/// <param name="final">The sequence's final character.</param>
	/// <param name="first">The first parameter, or -1 when it was left empty.</param>
	/// <param name="count">The first parameter, at least 1.</param>
	/// <param name="parameters">All the parameters, with -1 for one left empty.</param>
	private void Edit(char final, int first, int count, ReadOnlySpan<int> parameters)
	{
		switch (final)
		{
			case '@':
				this.InsertBlanks(count);
				break;
			case 'J':
				this.EraseInDisplay(Math.Max(0, first));
				break;
			case 'K':
				this.EraseInLine(Math.Max(0, first));
				break;
			case 'L':
				this.InsertLines(count);
				break;
			case 'M':
				this.DeleteLines(count);
				break;
			case 'P':
				this.DeleteCells(count);
				break;
			case 'S':
				this.ScrollUp(count);
				break;
			case 'T':
				this.ScrollDown(count);
				break;
			case 'X':
				this.Erase(this.CursorRow, this.CursorColumn, this.CursorColumn + count);
				break;
			case 'b':
				this.Repeat(count);
				break;
			case 'c':
				// Primary device attributes: a VT220 with ANSI color.
				this.reply("\e[?62;22c");
				break;
			case 'g':
				this.ClearTabStops(Math.Max(0, first));
				break;
			case 'h' or 'l':
				this.InsertMode = parameters.Contains(4) ? final == 'h' : this.InsertMode;
				break;
			case 'm':
				this.SetStyle(AnsiParser.ApplySgr(this.style, parameters));
				break;
			case 'n':
				this.Report(first);
				break;
			case 'r':
				this.SetScrollRegion(count - 1, (parameters.Length > 1 && parameters[1] > 0 ? parameters[1] : this.Rows) - 1);
				break;
			case 's':
				this.SaveCursor();
				break;
			case 't':
				this.ReportWindow(first);
				break;
			case 'u':
				this.RestoreCursor();
				break;
			default:
				break;
		}
	}

	/// <inheritdoc />
	public void Osc(string text)
	{
		ArgumentNullException.ThrowIfNull(text);

		var separator = text.IndexOf(';', StringComparison.Ordinal);
		var command = separator < 0 ? text : text[..separator];
		var value = separator < 0 ? string.Empty : text[(separator + 1)..];

		switch (command)
		{
			case "0" or "2":
				this.Title = value;
				this.TitleChanged?.Invoke(this, EventArgs.Empty);
				break;
			case "7":
				this.WorkingDirectory = Uri.TryCreate(value, UriKind.Absolute, out var url) ? url.LocalPath : value;
				break;
			case "10" when value == "?":
				this.reply($"\e]10;{ForegroundReport}\e\\");
				break;
			case "11" when value == "?":
				this.reply($"\e]11;{BackgroundReport}\e\\");
				break;
			default:
				if (text.StartsWith(Shell.Program.ManPageOsc, StringComparison.Ordinal))
				{
					this.RequestManPage(text[Shell.Program.ManPageOsc.Length..]);
				}

				break;
		}
	}

	/// <summary>
	/// Puts the terminal back as it was when it started, as <c>ESC c</c> asks, keeping the scrollback.
	/// </summary>
	private void Reset()
	{
		this.AlternateScreen = false;
		this.main = BlankLines(this.Columns, this.Rows);
		this.alternate = BlankLines(this.Columns, this.Rows);
		this.SetStyle(default);
		this.CursorRow = 0;
		this.CursorColumn = 0;
		this.pendingWrap = false;
		this.top = 0;
		this.bottom = this.Rows - 1;
		this.AutoWrap = true;
		this.InsertMode = false;
		this.OriginMode = false;
		this.CursorVisible = true;
		this.CursorShape = CursorShape.Bar;
		this.ApplicationCursorKeys = false;
		this.BracketedPaste = false;
		this.FocusEvents = false;
		this.MouseTracking = MouseTracking.None;
		this.MouseEncoding = MouseEncoding.Default;
		this.lineDrawing = false;
		this.saved = default;
		this.savedMain = default;
		this.ResetTabStops();
	}

	/// <summary>
	/// Sets or resets one DEC private mode, the <c>ESC [ ? n h</c> and <c>ESC [ ? n l</c> sequences.
	/// </summary>
	/// <param name="mode">The mode number.</param>
	/// <param name="on"><see langword="true" /> to set it; <see langword="false" /> to reset it.</param>
	private void SetPrivateMode(int mode, bool on)
	{
		switch (mode)
		{
			case 1:
				this.ApplicationCursorKeys = on;
				break;
			case 6:
				this.OriginMode = on;
				this.MoveTo(0, 0);
				break;
			case 7:
				this.AutoWrap = on;
				break;
			case 25:
				this.CursorVisible = on;
				break;
			case 9:
				this.MouseTracking = on ? MouseTracking.Press : MouseTracking.None;
				break;
			case 1000:
				this.MouseTracking = on ? MouseTracking.PressRelease : MouseTracking.None;
				break;
			case 1002:
				this.MouseTracking = on ? MouseTracking.Drag : MouseTracking.None;
				break;
			case 1003:
				this.MouseTracking = on ? MouseTracking.AnyMove : MouseTracking.None;
				break;
			case 1004:
				this.FocusEvents = on;
				break;
			case 1006:
				this.MouseEncoding = on ? MouseEncoding.Sgr : MouseEncoding.Default;
				break;
			case 1015:
				this.MouseEncoding = on ? MouseEncoding.Urxvt : MouseEncoding.Default;
				break;
			case 47 or 1047:
				this.SwitchScreen(on, clear: mode == 1047 && !on);
				break;
			case 1048:
				if (on)
				{
					this.SaveCursor();
				}
				else
				{
					this.RestoreCursor();
				}

				break;
			case 1049:
				this.SwitchScreen(on, clear: true);
				break;
			case 2004:
				this.BracketedPaste = on;
				break;
			default:
				break;
		}
	}

	/// <summary>
	/// Shows the alternate screen or goes back to the main one.
	/// </summary>
	/// <param name="alternateScreen"><see langword="true" /> to show the alternate screen.</param>
	/// <param name="clear"><see langword="true" /> to blank the alternate screen as it's entered or left.</param>
	/// <remarks>
	/// The main screen's cursor is kept aside while the alternate one shows, so a full-screen program leaves the
	/// prompt where it was.
	/// </remarks>
	private void SwitchScreen(bool alternateScreen, bool clear)
	{
		if (alternateScreen == this.AlternateScreen)
		{
			return;
		}

		if (alternateScreen)
		{
			this.savedMain = this.CurrentCursor();
		}

		if (clear)
		{
			this.alternate = BlankLines(this.Columns, this.Rows);
		}

		this.AlternateScreen = alternateScreen;
		this.top = 0;
		this.bottom = this.Rows - 1;

		if (!alternateScreen)
		{
			this.Restore(this.savedMain);
		}
	}

	/// <summary>
	/// Erases part or all of the screen, the <c>ED</c> sequence.
	/// </summary>
	/// <param name="mode">0 from the cursor to the end, 1 from the start to the cursor, 2 the whole screen, 3 the scrollback.</param>
	private void EraseInDisplay(int mode)
	{
		switch (mode)
		{
			case 0:
				this.Erase(this.CursorRow, this.CursorColumn, this.Columns);

				for (var row = this.CursorRow + 1; row < this.Rows; row++)
				{
					this.Erase(row, 0, this.Columns);
				}

				break;
			case 1:
				for (var row = 0; row < this.CursorRow; row++)
				{
					this.Erase(row, 0, this.Columns);
				}

				this.Erase(this.CursorRow, 0, this.CursorColumn + 1);
				break;
			case 2:
				for (var row = 0; row < this.Rows; row++)
				{
					this.Erase(row, 0, this.Columns);
				}

				break;
			case 3:
				this.History.Clear();
				break;
			default:
				break;
		}
	}

	/// <summary>
	/// Erases part or all of the cursor's row, the <c>EL</c> sequence.
	/// </summary>
	/// <param name="mode">0 from the cursor to the end, 1 from the start to the cursor, 2 the whole row.</param>
	private void EraseInLine(int mode)
	{
		var (start, end) = mode switch
		{
			1 => (0, this.CursorColumn + 1),
			2 => (0, this.Columns),
			_ => (this.CursorColumn, this.Columns),
		};

		this.Erase(this.CursorRow, start, end);
	}

	/// <summary>
	/// Opens blank rows at the cursor, pushing the rows below down within the scroll region.
	/// </summary>
	/// <param name="count">How many rows.</param>
	private void InsertLines(int count)
	{
		if (this.CursorRow < this.top || this.CursorRow > this.bottom)
		{
			return;
		}

		var regionTop = this.top;

		this.top = this.CursorRow;
		this.ScrollDown(count);
		this.top = regionTop;
		this.CursorColumn = 0;
	}

	/// <summary>
	/// Removes rows at the cursor, pulling the rows below up within the scroll region.
	/// </summary>
	/// <param name="count">How many rows.</param>
	private void DeleteLines(int count)
	{
		if (this.CursorRow < this.top || this.CursorRow > this.bottom)
		{
			return;
		}

		var regionTop = this.top;

		// Rows deleted inside the screen never belong in the scrollback, which ScrollUp only feeds from row 0.
		this.top = this.CursorRow;

		var lines = this.Lines;
		count = Math.Clamp(count, 0, this.bottom - this.top + 1);

		for (var i = 0; i < count; i++)
		{
			var dropped = lines[this.top];

			Array.Copy(lines, this.top + 1, lines, this.top, this.bottom - this.top);
			lines[this.bottom] = this.BlankLine(dropped);
		}

		this.top = regionTop;
		this.CursorColumn = 0;
	}

	/// <summary>
	/// Prints the last printed character again, the <c>REP</c> sequence.
	/// </summary>
	/// <param name="count">How many times.</param>
	private void Repeat(int count)
	{
		var text = char.ConvertFromUtf32(this.lastPrinted);

		for (var i = 0; i < Math.Min(count, this.Columns * this.Rows); i++)
		{
			foreach (var character in text)
			{
				this.Print(character);
			}
		}
	}

	/// <summary>
	/// Moves the cursor to a row and column, counted from the scroll region's top in origin mode.
	/// </summary>
	/// <param name="row">The row.</param>
	/// <param name="column">The column.</param>
	private void MoveTo(int row, int column)
	{
		var offset = this.OriginMode ? this.top : 0;
		var last = this.OriginMode ? this.bottom : this.Rows - 1;

		this.CursorRow = Math.Clamp(row + offset, offset, last);
		this.CursorColumn = Math.Clamp(column, 0, this.Columns - 1);
		this.pendingWrap = false;
	}

	/// <summary>
	/// Sets the rows that scroll, the <c>DECSTBM</c> sequence, and moves the cursor home.
	/// </summary>
	/// <param name="first">The region's first row.</param>
	/// <param name="last">The region's last row.</param>
	private void SetScrollRegion(int first, int last)
	{
		last = Math.Min(last, this.Rows - 1);

		if (first >= last)
		{
			return;
		}

		this.top = first;
		this.bottom = last;
		this.MoveTo(0, 0);
	}

	/// <summary>
	/// Moves the cursor to the next tab stop, or to the last column when there's none.
	/// </summary>
	/// <param name="count">How many stops.</param>
	private void TabForward(int count)
	{
		for (var i = 0; i < count; i++)
		{
			do
			{
				this.CursorColumn++;
			}
			while (this.CursorColumn < this.Columns - 1 && !this.tabStops[this.CursorColumn]);
		}

		this.CursorColumn = Math.Min(this.CursorColumn, this.Columns - 1);
	}

	/// <summary>
	/// Moves the cursor back to the previous tab stop, or to the first column.
	/// </summary>
	/// <param name="count">How many stops.</param>
	private void TabBack(int count)
	{
		for (var i = 0; i < count; i++)
		{
			do
			{
				this.CursorColumn--;
			}
			while (this.CursorColumn > 0 && !this.tabStops[this.CursorColumn]);
		}

		this.CursorColumn = Math.Max(0, this.CursorColumn);
	}

	/// <summary>
	/// Clears tab stops, the <c>TBC</c> sequence.
	/// </summary>
	/// <param name="mode">0 for the stop at the cursor, 3 for all of them.</param>
	private void ClearTabStops(int mode)
	{
		if (mode == 0)
		{
			this.tabStops[this.CursorColumn] = false;
		}
		else if (mode == 3)
		{
			Array.Clear(this.tabStops);
		}
	}

	/// <summary>
	/// Answers a device status request, the <c>DSR</c> sequence.
	/// </summary>
	/// <param name="request">5 asks whether the terminal is fine, 6 where the cursor is.</param>
	private void Report(int request)
	{
		if (request == 5)
		{
			this.reply("\e[0n");
		}
		else if (request == 6)
		{
			var row = this.CursorRow - (this.OriginMode ? this.top : 0) + 1;

			this.reply(string.Create(CultureInfo.InvariantCulture, $"\e[{row};{this.CursorColumn + 1}R"));
		}
	}

	/// <summary>
	/// Answers the window size requests of <c>XTWINOPS</c>. Requests to move or resize the window are ignored.
	/// </summary>
	/// <param name="request">14 for the text area in pixels, 16 for a cell in pixels, 18 for the text area in cells.</param>
	private void ReportWindow(int request)
	{
		var (width, height) = this.CellPixels;
		var answer = request switch
		{
			14 => $"\e[4;{height * this.Rows};{width * this.Columns}t",
			16 => $"\e[6;{height};{width}t",
			18 => $"\e[8;{this.Rows};{this.Columns}t",
			_ => null,
		};

		if (answer is not null)
		{
			this.reply(answer);
		}
	}

	/// <summary>
	/// Decodes the shell's request to show a man page and raises <see cref="ManPageRequested" />.
	/// </summary>
	/// <param name="encoded">The command line, base64 encoded UTF-8 so no character in it can end the OSC string.</param>
	private void RequestManPage(string encoded)
	{
		try
		{
			this.ManPageRequested?.Invoke(this, Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
		}
		// Not from the shell, since the shell always sends valid base64. Ignored like any unknown OSC.
		catch (FormatException exception)
		{
			Log.Debug(exception, "Ignored a man page request that isn't valid base64");
		}
	}

	/// <summary>
	/// Saves the cursor for <see cref="RestoreCursor" />.
	/// </summary>
	private void SaveCursor() => this.saved = this.CurrentCursor();

	/// <summary>
	/// Moves the cursor back to where <see cref="SaveCursor" /> left it, with the style it had.
	/// </summary>
	private void RestoreCursor() => this.Restore(this.saved);

	/// <summary>
	/// Captures the cursor state.
	/// </summary>
	/// <returns>
	/// The position, style and modes that go with the cursor.
	/// </returns>
	private SavedCursor CurrentCursor() => new(this.CursorRow, this.CursorColumn, this.style, this.OriginMode, this.lineDrawing);

	/// <summary>
	/// Puts back a captured cursor state, kept on screen if the screen got smaller since.
	/// </summary>
	/// <param name="cursor">The state.</param>
	private void Restore(SavedCursor cursor)
	{
		this.CursorRow = Math.Clamp(cursor.Row, 0, this.Rows - 1);
		this.CursorColumn = Math.Clamp(cursor.Column, 0, this.Columns - 1);
		this.SetStyle(cursor.Style);
		this.OriginMode = cursor.OriginMode;
		this.lineDrawing = cursor.LineDrawing;
		this.pendingWrap = false;
	}
}
