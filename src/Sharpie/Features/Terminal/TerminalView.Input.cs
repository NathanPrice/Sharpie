using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Sharpie.Features.Vt;

namespace Sharpie.Features.Terminal;

/// <content>
/// Turns keys, the mouse, focus and the clipboard into input for the program.
/// </content>
internal sealed partial class TerminalView
{
	// How many rows one wheel notch scrolls, the usual three.
	private const int WheelRows = 3;

	// Set when a key already sent its text with ESC in front, so the text input that follows isn't sent again.
	private bool textSent;

	// The cell the pointer was last reported in, so moves inside one cell aren't reported again.
	private (int Column, int Row) lastMouseCell;

	/// <summary>
	/// Sends text to the program as UTF-8.
	/// </summary>
	/// <param name="text">The text.</param>
	public void SendText(string text)
	{
		this.send(Encoding.UTF8.GetBytes(text));
		this.anchor = null;
		this.ScrollTo(0);
		this.ShowCursor();
		this.InvalidateVisual();
	}

	/// <summary>
	/// Copies the selection to the clipboard and clears it.
	/// </summary>
	/// <returns>
	/// <see langword="true" /> when there was a selection to copy.
	/// </returns>
	public bool CopySelection()
	{
		if (this.SelectedText() is not { } text)
		{
			return false;
		}

		_ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
		this.anchor = null;
		this.InvalidateVisual();

		return true;
	}

	/// <summary>
	/// Sends the clipboard's text to the program, bracketed when it asked for that, so a shell or editor can tell a
	/// paste from typing.
	/// </summary>
	/// <returns>
	/// A task that completes once the text is sent.
	/// </returns>
	public async Task PasteAsync()
	{
		if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard
			|| await clipboard.TryGetTextAsync().ConfigureAwait(true) is not { Length: > 0 } text)
		{
			return;
		}

		// Terminals send pasted line breaks as Enter. The end marker is taken out of the text so a paste can't end the
		// bracket early and have the rest typed as commands.
		text = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');

		this.SendText(this.Screen.BracketedPaste
			? $"\e[200~{text.Replace("\e[201~", string.Empty, StringComparison.Ordinal)}\e[201~"
			: text);
	}

	/// <inheritdoc />
	protected override void OnKeyDown(KeyEventArgs e)
	{
		ArgumentNullException.ThrowIfNull(e);

		base.OnKeyDown(e);

		var control = (e.KeyModifiers & KeyModifiers.Control) != 0;
		var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

		e.Handled = true;

		// Ctrl+C copies when text is selected and interrupts otherwise, as in the old screen. Ctrl+Shift+C and V are
		// the usual terminal shortcuts, and Ctrl+V pastes too, as it did before.
		if (control && e.Key == Key.C && (shift || this.anchor is not null) && this.CopySelection())
		{
			return;
		}

		if (control && e.Key == Key.V)
		{
			_ = this.PasteAsync();

			return;
		}

		if (shift && e.Key is Key.PageUp or Key.PageDown)
		{
			this.ScrollTo(this.ScrollOffset + ((e.Key == Key.PageUp ? 1 : -1) * Math.Max(1, this.Screen.Rows - 1)));

			return;
		}

		if (KeyEncoder.Encode(e.Key, e.KeyModifiers, e.KeySymbol, this.Screen.ApplicationCursorKeys) is { } sequence)
		{
			this.textSent = (e.KeyModifiers & KeyModifiers.Alt) != 0;
			this.SendText(sequence);

			return;
		}

		// Plain typing arrives as text input next. Marking it unhandled lets that happen.
		e.Handled = false;
	}

	/// <inheritdoc />
	protected override void OnTextInput(TextInputEventArgs e)
	{
		ArgumentNullException.ThrowIfNull(e);

		base.OnTextInput(e);

		if (this.textSent)
		{
			this.textSent = false;
			e.Handled = true;

			return;
		}

		if (!string.IsNullOrEmpty(e.Text))
		{
			this.SendText(e.Text);
			e.Handled = true;
		}
	}

	/// <inheritdoc />
	protected override void OnGotFocus(FocusChangedEventArgs e)
	{
		base.OnGotFocus(e);

		if (this.Screen.FocusEvents)
		{
			this.send("\e[I"u8.ToArray());
		}
	}

	/// <inheritdoc />
	protected override void OnLostFocus(FocusChangedEventArgs e)
	{
		base.OnLostFocus(e);

		if (this.Screen.FocusEvents)
		{
			this.send("\e[O"u8.ToArray());
		}
	}

	/// <inheritdoc />
	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		ArgumentNullException.ThrowIfNull(e);

		base.OnPointerPressed(e);
		_ = this.Focus();

		var point = e.GetCurrentPoint(this);
		var button = point.Properties.PointerUpdateKind switch
		{
			PointerUpdateKind.LeftButtonPressed => 0,
			PointerUpdateKind.MiddleButtonPressed => 1,
			PointerUpdateKind.RightButtonPressed => 2,
			_ => -1,
		};

		if (this.ReportMouse(MouseAction.Press, button, e.KeyModifiers, point.Position))
		{
			e.Pointer.Capture(this);
			e.Handled = true;

			return;
		}

		if (button != 0)
		{
			return;
		}

		var cell = this.LineAt(point.Position);

		if (e.ClickCount == 2)
		{
			this.SelectWord(cell);
		}
		else
		{
			this.anchor = cell;
			this.caret = cell;
		}

		e.Pointer.Capture(this);
		e.Handled = true;
		this.InvalidateVisual();
	}

	/// <inheritdoc />
	protected override void OnPointerMoved(PointerEventArgs e)
	{
		ArgumentNullException.ThrowIfNull(e);

		base.OnPointerMoved(e);

		var point = e.GetCurrentPoint(this);
		var held = point.Properties.IsLeftButtonPressed ? 0
			: point.Properties.IsMiddleButtonPressed ? 1
			: point.Properties.IsRightButtonPressed ? 2
			: -1;

		if (this.ReportMouse(MouseAction.Move, held, e.KeyModifiers, point.Position))
		{
			return;
		}

		if (held == 0 && this.anchor is not null && e.Pointer.Captured == this)
		{
			this.caret = this.LineAt(point.Position);
			this.InvalidateVisual();
		}
	}

	/// <inheritdoc />
	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		ArgumentNullException.ThrowIfNull(e);

		base.OnPointerReleased(e);

		var button = e.InitialPressMouseButton switch
		{
			MouseButton.Left => 0,
			MouseButton.Middle => 1,
			MouseButton.Right => 2,
			_ => -1,
		};

		_ = this.ReportMouse(MouseAction.Release, button, e.KeyModifiers, e.GetPosition(this));
		e.Pointer.Capture(null);
	}

	/// <inheritdoc />
	protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
	{
		ArgumentNullException.ThrowIfNull(e);

		base.OnPointerWheelChanged(e);
		e.Handled = true;

		var notches = (int)Math.Round(e.Delta.Y);
		var up = notches > 0;

		for (var i = 0; i < Math.Abs(notches); i++)
		{
			if (this.ReportMouse(up ? MouseAction.WheelUp : MouseAction.WheelDown, -1, e.KeyModifiers, e.GetPosition(this)))
			{
				continue;
			}

			// The alternate screen has no scrollback, so the wheel sends arrow keys there instead, as xterm's
			// alternateScroll does, and less or man scroll their page.
			if (this.Screen.AlternateScreen)
			{
				var arrow = KeyEncoder.Encode(up ? Key.Up : Key.Down, KeyModifiers.None, null, this.Screen.ApplicationCursorKeys);

				this.send(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(arrow, WheelRows))));
			}
			else
			{
				this.ScrollTo(this.ScrollOffset + (up ? WheelRows : -WheelRows));
			}
		}
	}

	/// <summary>
	/// Sends a mouse report when the program is tracking the mouse and Shift isn't held.
	/// </summary>
	/// <param name="action">What the mouse did.</param>
	/// <param name="button">The button, or -1 for none.</param>
	/// <param name="modifiers">The modifier keys held.</param>
	/// <param name="position">Where the pointer is, in the control's coordinates.</param>
	/// <returns>
	/// <see langword="true" /> when the program tracks the mouse, so the event belongs to it even if nothing was
	/// sent; <see langword="false" /> when the view should handle it, for selecting or scrolling.
	/// </returns>
	/// <remarks>
	/// Holding Shift always selects text locally instead, as xterm does, so text can still be copied out of a
	/// program that takes the mouse.
	/// </remarks>
	private bool ReportMouse(MouseAction action, int button, KeyModifiers modifiers, Point position)
	{
		if (this.Screen.MouseTracking == MouseTracking.None || (modifiers & KeyModifiers.Shift) != 0)
		{
			return false;
		}

		var cell = this.CellAt(position);

		if (action == MouseAction.Move && cell == this.lastMouseCell)
		{
			return true;
		}

		this.lastMouseCell = cell;

		if (MouseEncoder.Encode(this.Screen.MouseTracking, this.Screen.MouseEncoding, action, button, modifiers, cell.Column, cell.Row) is { } report)
		{
			// Each character is one byte, which the original encoding needs for positions past 95.
			this.send(Encoding.Latin1.GetBytes(report));
		}

		return true;
	}

	/// <summary>
	/// Gets the screen cell under a point, counted from 1 as mouse reports count.
	/// </summary>
	/// <param name="position">The point in the control's coordinates.</param>
	/// <returns>
	/// The column and row, kept on screen.
	/// </returns>
	private (int Column, int Row) CellAt(Point position) =>
		(Math.Clamp((int)((position.X - Padding) / this.cellWidth) + 1, 1, this.Screen.Columns),
			Math.Clamp((int)((position.Y - Padding) / this.cellHeight) + 1, 1, this.Screen.Rows));

	/// <summary>
	/// Gets the place under a point among all rows, scrollback included, for selecting.
	/// </summary>
	/// <param name="position">The point in the control's coordinates.</param>
	/// <returns>
	/// The row among <see cref="VtScreen.LineCount" /> and the column, where the column can be one past the last so
	/// a whole row can be selected.
	/// </returns>
	private (int Line, int Column) LineAt(Point position)
	{
		var row = Math.Clamp((int)Math.Floor((position.Y - Padding) / this.cellHeight), 0, this.Screen.Rows - 1);
		var column = Math.Clamp((int)Math.Round((position.X - Padding) / this.cellWidth), 0, this.Screen.Columns);

		return (this.Screen.LineCount - this.Screen.Rows - this.ScrollOffset + row, column);
	}

	/// <summary>
	/// Selects the word around a place, for a double click.
	/// </summary>
	/// <param name="place">The row and column clicked.</param>
	private void SelectWord((int Line, int Column) place)
	{
		var cells = this.Screen.GetLine(place.Line).Cells;
		var start = Math.Min(place.Column, cells.Length - 1);
		var end = start;

		while (start > 0 && !char.IsWhiteSpace(cells[start - 1].Character))
		{
			start--;
		}

		while (end < cells.Length && !char.IsWhiteSpace(cells[end].Character))
		{
			end++;
		}

		this.anchor = (place.Line, start);
		this.caret = (place.Line, end);
	}
}
