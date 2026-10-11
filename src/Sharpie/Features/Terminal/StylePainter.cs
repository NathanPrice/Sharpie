using Avalonia.Media;
using AvaloniaEdit.Rendering;
using DotNetTips.Spargine.Core;
using Sharpie.Features.Ansi;

namespace Sharpie.Features.Terminal;

/// <summary>
/// Turns colors and ANSI styles into the actions that AvaloniaEdit's line colorizers apply to text.
/// </summary>
internal sealed class StylePainter
{
	// Each distinct style gets one paint action, since a colorful program like fastfetch adds hundreds of spans in a
	// handful of styles.
	private readonly Dictionary<AnsiStyle, Action<VisualLineElement>> paints = [];

	/// <summary>
	/// Creates the action that colors text with <paramref name="brush" />.
	/// </summary>
	/// <param name="brush">The text color.</param>
	/// <param name="bold"><see langword="true" /> to draw the text bold; otherwise, <see langword="false" />.</param>
	/// <returns>
	/// An action to pass to <see cref="DocumentColorizingTransformer" />'s <c>ChangeLinePart</c>.
	/// </returns>
	/// <remarks>
	/// Built once when a span is added. A lambda written inside the colorizer would capture the span and allocate
	/// again for every visible line on every redraw.
	/// </remarks>
	public static Action<VisualLineElement> Paint(IBrush brush, bool bold)
	{
		_ = brush.ArgumentNotNull();

		return bold
			? element =>
			{
				element.TextRunProperties.SetForegroundBrush(brush);
				element.TextRunProperties.SetTypeface(
					new Typeface(element.TextRunProperties.Typeface.FontFamily, weight: FontWeight.Bold));
			}
		: element => element.TextRunProperties.SetForegroundBrush(brush);
	}

	/// <summary>
	/// Gets the paint action for <paramref name="style" />, creating it the first time the style is seen.
	/// </summary>
	/// <param name="style">The colors and weight a program asked for.</param>
	/// <returns>
	/// An action to pass to <see cref="DocumentColorizingTransformer" />'s <c>ChangeLinePart</c>.
	/// </returns>
	public Action<VisualLineElement> For(AnsiStyle style)
	{
		if (this.paints.TryGetValue(style, out var paint))
		{
			return paint;
		}

		var foreground = style.Foreground is { } fg ? Palette.For(fg) : null;
		var background = style.Background is { } bg ? Palette.For(bg) : null;
		var bold = style.Bold;

		paint = element =>
		{
			if (foreground is not null)
			{
				element.TextRunProperties.SetForegroundBrush(foreground);
			}

			if (background is not null)
			{
				element.TextRunProperties.SetBackgroundBrush(background);
			}

			if (bold)
			{
				element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, weight: FontWeight.Bold));
			}
		};

		this.paints[style] = paint;

		return paint;
	}
}
