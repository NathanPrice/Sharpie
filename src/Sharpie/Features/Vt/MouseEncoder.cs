using System.Globalization;
using Avalonia.Input;

namespace Sharpie.Features.Vt;

/// <summary>
/// What the mouse did.
/// </summary>
internal enum MouseAction
{
	/// <summary>
	/// A button went down.
	/// </summary>
	Press,

	/// <summary>
	/// A button came up.
	/// </summary>
	Release,

	/// <summary>
	/// The pointer moved to another cell.
	/// </summary>
	Move,

	/// <summary>
	/// The wheel turned up.
	/// </summary>
	WheelUp,

	/// <summary>
	/// The wheel turned down.
	/// </summary>
	WheelDown,
}

/// <summary>
/// Writes mouse reports the way xterm does, for programs that turned mouse tracking on.
/// </summary>
internal static class MouseEncoder
{
	// The largest column or row the original encoding can carry, since each is sent as one byte plus 32.
	private const int MaxDefaultPosition = 223;

	/// <summary>
	/// Gets the report for one mouse event, or nothing when the program didn't ask for that kind of event.
	/// </summary>
	/// <param name="tracking">Which events the program asked for.</param>
	/// <param name="encoding">How the program wants them written.</param>
	/// <param name="action">What the mouse did.</param>
	/// <param name="button">0 for left, 1 for middle, 2 for right, or -1 for none, as when moving with no button held.</param>
	/// <param name="modifiers">The modifier keys held.</param>
	/// <param name="column">The cell's column, counted from 1.</param>
	/// <param name="row">The cell's row, counted from 1.</param>
	/// <returns>
	/// The report, every character of which fits in one byte, or <see langword="null" /> when nothing is sent.
	/// </returns>
	/// <remarks>
	/// The original encoding sends positions as single bytes, so the caller has to send the report as Latin-1, not
	/// UTF-8. Positions past 223 are held at 223 there. Mode 9 reports presses only, without modifiers.
	/// </remarks>
	public static string? Encode(MouseTracking tracking, MouseEncoding encoding, MouseAction action, int button, KeyModifiers modifiers, int column, int row)
	{
		var wanted = tracking switch
		{
			MouseTracking.Press => action == MouseAction.Press && button >= 0,
			MouseTracking.PressRelease => action != MouseAction.Move,
			MouseTracking.Drag => action != MouseAction.Move || button >= 0,
			MouseTracking.AnyMove => true,
			_ => false,
		};

		if (!wanted || (action is MouseAction.Press or MouseAction.Release && button < 0))
		{
			return null;
		}

		var code = action switch
		{
			MouseAction.WheelUp => 64,
			MouseAction.WheelDown => 65,
			MouseAction.Release when encoding != MouseEncoding.Sgr => 3,
			MouseAction.Move => 32 + (button >= 0 ? button : 3),
			_ => button,
		};

		if (tracking != MouseTracking.Press)
		{
			code += ((modifiers & KeyModifiers.Shift) != 0 ? 4 : 0)
				+ ((modifiers & KeyModifiers.Alt) != 0 ? 8 : 0)
				+ ((modifiers & KeyModifiers.Control) != 0 ? 16 : 0);
		}

		return encoding switch
		{
			MouseEncoding.Sgr => string.Create(CultureInfo.InvariantCulture, $"\e[<{code};{column};{row}{(action == MouseAction.Release ? 'm' : 'M')}"),
			MouseEncoding.Urxvt => string.Create(CultureInfo.InvariantCulture, $"\e[{32 + code};{column};{row}M"),
			_ => string.Concat("\e[M", ((char)(32 + code)).ToString(), ((char)(32 + Math.Min(column, MaxDefaultPosition))).ToString(), ((char)(32 + Math.Min(row, MaxDefaultPosition))).ToString()),
		};
	}
}
