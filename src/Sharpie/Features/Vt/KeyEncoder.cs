using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Input;

namespace Sharpie.Features.Vt;

/// <summary>
/// Turns key presses into the bytes xterm sends for them, so programs read the keys they expect.
/// </summary>
internal static class KeyEncoder
{
	/// <summary>
	/// Gets what a key press sends to the program.
	/// </summary>
	/// <param name="key">The key.</param>
	/// <param name="modifiers">The modifier keys held.</param>
	/// <param name="symbol">The text the key types, like <c>a</c> or <c>A</c>, or <see langword="null" />.</param>
	/// <param name="applicationCursorKeys">
	/// <see langword="true" /> while the program asked for the application forms of the arrow keys.
	/// </param>
	/// <param name="disambiguateKeys">
	/// <see langword="true" /> while the program turned on the kitty keyboard protocol's first flag, so keys that
	/// legacy encoding sends alike, such as Enter and Shift+Enter, reach it as separate keys.
	/// </param>
	/// <returns>
	/// The text to send, or <see langword="null" /> when the key only types text, which arrives as text input instead.
	/// </returns>
	/// <remarks>
	/// Keys with modifiers use xterm's <c>1;m</c> form, where <c>m</c> is 1 plus 1 for Shift, 2 for Alt and 4 for
	/// Ctrl. Alt with a key that types text sends ESC first, as xterm does with its meta key sending escape.
	/// With <paramref name="disambiguateKeys" />, Escape, modified Enter, Tab and Backspace, and Ctrl or Alt with a
	/// letter, digit or Space send the kitty form <c>ESC [ code ; m u</c> instead. Plain Enter, Tab and Backspace keep
	/// their usual bytes, as the protocol asks, so a shell left with the flag on by a crashed program still runs
	/// <c>reset</c>.
	/// </remarks>
	[SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "Avalonia's Key enum has a member for every key on every keyboard. Only the keys a terminal sends need an arm.")]
	public static string? Encode(Key key, KeyModifiers modifiers, string? symbol, bool applicationCursorKeys, bool disambiguateKeys)
	{
		var shift = (modifiers & KeyModifiers.Shift) != 0;
		var alt = (modifiers & KeyModifiers.Alt) != 0;
		var control = (modifiers & KeyModifiers.Control) != 0;
		var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);

		if (Cursor(key) is { } letter)
		{
			return modifier > 1 ? $"\e[1;{modifier}{letter}"
				: applicationCursorKeys ? $"\eO{letter}"
				: $"\e[{letter}";
		}

		if (Function(key) is { } function)
		{
			return function.Number is { } number
				? modifier > 1 ? $"\e[{number};{modifier}~" : $"\e[{number}~"
				: modifier > 1 ? $"\e[1;{modifier}{function.Letter}" : $"\eO{function.Letter}";
		}

		if (disambiguateKeys && KittyCode(key, modifier > 1, alt || control) is { } code)
		{
			return modifier > 1 ? $"\e[{code};{modifier}u" : $"\e[{code}u";
		}

		var escape = alt ? "\e" : string.Empty;

		return key switch
		{
			Key.Enter => escape + "\r",
			Key.Tab => shift ? "\e[Z" : escape + "\t",
			Key.Back => escape + (control ? "\b" : "\x7F"),
			Key.Escape => "\e",
			_ when control && ControlCharacter(key) is { } character => escape + character,
			_ when alt && !string.IsNullOrEmpty(symbol) && !char.IsControl(symbol[0]) => escape + symbol,
			_ => null,
		};
	}

	/// <summary>
	/// Gets the number the kitty keyboard protocol sends for a key that legacy encoding can't tell apart.
	/// </summary>
	/// <param name="key">The key.</param>
	/// <param name="modified"><see langword="true" /> when any modifier is held.</param>
	/// <param name="altOrControl"><see langword="true" /> when Alt or Ctrl is held.</param>
	/// <returns>
	/// The key's Unicode code point, lowercase for letters, or <see langword="null" /> when the key keeps its legacy
	/// bytes.
	/// </returns>
	[SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "Avalonia's Key enum has a member for every key on every keyboard. Only the keys a terminal sends need an arm.")]
	private static int? KittyCode(Key key, bool modified, bool altOrControl) => key switch
	{
		Key.Escape => 27,
		Key.Enter when modified => 13,
		Key.Tab when modified => 9,
		Key.Back when modified => 127,
		>= Key.A and <= Key.Z when altOrControl => 'a' + (key - Key.A),
		>= Key.D0 and <= Key.D9 when altOrControl => '0' + (key - Key.D0),
		Key.Space when altOrControl => ' ',
		_ => null,
	};

	/// <summary>
	/// Gets the letter that ends the sequence for a cursor key.
	/// </summary>
	/// <param name="key">The key.</param>
	/// <returns>
	/// The letter, or <see langword="null" /> for other keys.
	/// </returns>
	[SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "Avalonia's Key enum has a member for every key on every keyboard. Only the keys a terminal sends need an arm.")]
	private static char? Cursor(Key key) => key switch
	{
		Key.Up => 'A',
		Key.Down => 'B',
		Key.Right => 'C',
		Key.Left => 'D',
		Key.Home => 'H',
		Key.End => 'F',
		_ => null,
	};

	/// <summary>
	/// Gets how xterm numbers the editing and function keys.
	/// </summary>
	/// <param name="key">The key.</param>
	/// <returns>
	/// The number for keys sent as <c>ESC [ n ~</c>, or the letter for F1 to F4, sent as <c>ESC O</c> and a letter.
	/// <see langword="null" /> for other keys.
	/// </returns>
	[SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "Avalonia's Key enum has a member for every key on every keyboard. Only the keys a terminal sends need an arm.")]
	private static (int? Number, char Letter)? Function(Key key) => key switch
	{
		Key.Insert => (2, '\0'),
		Key.Delete => (3, '\0'),
		Key.PageUp => (5, '\0'),
		Key.PageDown => (6, '\0'),
		Key.F1 => (null, 'P'),
		Key.F2 => (null, 'Q'),
		Key.F3 => (null, 'R'),
		Key.F4 => (null, 'S'),
		Key.F5 => (15, '\0'),
		Key.F6 => (17, '\0'),
		Key.F7 => (18, '\0'),
		Key.F8 => (19, '\0'),
		Key.F9 => (20, '\0'),
		Key.F10 => (21, '\0'),
		Key.F11 => (23, '\0'),
		Key.F12 => (24, '\0'),
		_ => null,
	};

	/// <summary>
	/// Gets the control character Ctrl and <paramref name="key" /> type.
	/// </summary>
	/// <param name="key">The key held with Ctrl.</param>
	/// <returns>
	/// The character, such as 0x01 for Ctrl+A or 0x1B for Ctrl+[, or <see langword="null" /> when the key has none.
	/// </returns>
	[SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "Avalonia's Key enum has a member for every key on every keyboard. Only the keys a terminal sends need an arm.")]
	private static string? ControlCharacter(Key key) => key switch
	{
		>= Key.A and <= Key.Z => ((char)(key - Key.A + 1)).ToString(CultureInfo.InvariantCulture),
		Key.Space or Key.D2 => "\0",
		Key.OemOpenBrackets or Key.D3 => "\e",
		Key.OemPipe or Key.OemBackslash or Key.D4 => "\x1C",
		Key.OemCloseBrackets or Key.D5 => "\x1D",
		Key.D6 => "\x1E",
		Key.OemMinus or Key.OemQuestion or Key.D7 => "\x1F",
		Key.D8 => "\x7F",
		_ => null,
	};
}
