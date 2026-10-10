using Avalonia.Media;
using Sharpie.Features.Ansi;

namespace Sharpie.Features.Terminal;

/// <summary>
/// The window's dark theme colors.
/// </summary>
internal static class Palette
{
	/// <summary>
	/// The background behind the text.
	/// </summary>
	public static readonly Color Background = Color.Parse("#15171C");

	/// <summary>
	/// The normal text color.
	/// </summary>
	public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#DADDE3"));

	/// <summary>
	/// Dimmed text, like the parent folder in the prompt header and the window title.
	/// </summary>
	public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6B7280"));

	/// <summary>
	/// The highlight color, used for the folder name, the prompt symbol, and the caret.
	/// </summary>
	public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#7AA8F0"));

	/// <summary>
	/// The see-through accent behind selected text.
	/// </summary>
	public static readonly IBrush Selection = new SolidColorBrush(Color.Parse("#7AA8F0"), 0.35);

	/// <summary>
	/// Green, for commands that exist.
	/// </summary>
	public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#86D18A"));

	/// <summary>
	/// Yellow, for quoted text and slow-command times.
	/// </summary>
	public static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#E8C872"));

	/// <summary>
	/// Cyan, for redirect operators.
	/// </summary>
	public static readonly IBrush Cyan = new SolidColorBrush(Color.Parse("#6CCFD8"));

	/// <summary>
	/// Red, for standard error, unknown commands, and the close button.
	/// </summary>
	public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#F0717B"));

	/// <summary>
	/// Purple, for the git branch.
	/// </summary>
	public static readonly IBrush Branch = new SolidColorBrush(Color.Parse("#C29DF2"));

	/// <summary>
	/// The thin line around the window frame.
	/// </summary>
	public static readonly IBrush Edge = new SolidColorBrush(Color.Parse("#2A2E37"));

	/// <summary>
	/// The 16 standard terminal colors, in ANSI order.
	/// </summary>
	/// <remarks>
	/// Matched to the window's theme so program output fits in. Black is the muted gray, since true black would vanish
	/// on the dark background.
	/// </remarks>
	public static readonly IBrush[] Standard =
	[
		Muted, Red, Green, Yellow, Accent, Branch, Cyan, Text,
		Brush("#8B93A1"), Brush("#FF8A93"), Brush("#A3E4A6"), Brush("#F2D88E"),
		Brush("#9BC0F5"), Brush("#D6B8F7"), Brush("#8EE0E7"), Brush("#F5F6F8"),
	];

	/// <summary>
	/// The background as a brush, for text drawn in inverse video.
	/// </summary>
	public static readonly IBrush BackgroundBrush = new SolidColorBrush(Background);

	// Brushes for exact colors programs ask for, made once each.
	private static readonly Dictionary<uint, IBrush> RgbBrushes = [];

	/// <summary>
	/// Gets the brush for a terminal color.
	/// </summary>
	/// <param name="color">The color a program chose.</param>
	/// <returns>
	/// The theme's brush for the 16 standard colors, so program output fits in, or a brush of the exact color.
	/// </returns>
	/// <remarks>
	/// Only the UI thread calls this, so the cache needs no lock.
	/// </remarks>
	public static IBrush For(AnsiColor color)
	{
		if (color.Index is { } index)
		{
			return Standard[index];
		}

		if (!RgbBrushes.TryGetValue(color.Rgb, out var brush))
		{
			brush = new SolidColorBrush(Color.FromUInt32(0xFF000000 | color.Rgb));
			RgbBrushes[color.Rgb] = brush;
		}

		return brush;
	}

	/// <summary>
	/// Creates a solid brush from a <c>#RRGGBB</c> string.
	/// </summary>
	/// <param name="hex">The color.</param>
	/// <returns>
	/// The brush.
	/// </returns>
	private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
