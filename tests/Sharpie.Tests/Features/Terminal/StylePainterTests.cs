using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using FluentAssertions;
using Sharpie.Features.Ansi;
using Sharpie.Features.ManPages;
using Sharpie.Features.Terminal;

namespace Sharpie.Tests.Features.Terminal;

/// <summary>
/// Tests for <see cref="StylePainter" /> and the brushes <see cref="Palette" /> gives it, read back from text a
/// <see cref="ManPager" /> painted with them.
/// </summary>
public sealed class StylePainterTests
{
	/// <summary>
	/// Checks that a style paints its palette or exact color, its background, and bold, and leaves everything else
	/// alone.
	/// </summary>
	[AvaloniaFact]
	public void For_Style_PaintsColorsAndWeight()
	{
		// Act
		var painted = Painted(new StylePainter().For, "\e[1;31;44mx\e[0m\e[38;2;1;2;3my\e[0mz\n");

		// Assert
		_ = painted.Should().Equal(
			("x", ColorOf(Palette.For(new AnsiColor(1, 0))), ColorOf(Palette.For(new AnsiColor(4, 0))), FontWeight.Bold),
			("y", Color.FromUInt32(0xFF010203), null, FontWeight.Normal),
			("z", Colors.White, null, FontWeight.Normal));
	}

	/// <summary>
	/// Checks that the same style gets the same paint action each time.
	/// </summary>
	[AvaloniaFact]
	public void For_SameStyleTwice_ReusesPaint()
	{
		// Arrange
		var painter = new StylePainter();
		var style = new AnsiStyle(new AnsiColor(2, 0), null, Bold: true);

		// Act
		var first = painter.For(style);
		var second = painter.For(style);

		// Assert
		_ = second.Should().BeSameAs(first);
	}

	/// <summary>
	/// Checks that an exact color becomes an opaque brush of that color, made once.
	/// </summary>
	[AvaloniaFact]
	public void PaletteFor_ExactColor_IsOpaqueAndReused()
	{
		// Act
		var brush = Palette.For(new AnsiColor(null, 0x123456));

		// Assert
		_ = ((ISolidColorBrush)brush).Color.Should().Be(Color.FromUInt32(0xFF123456));
		_ = Palette.For(new AnsiColor(null, 0x123456)).Should().BeSameAs(brush);
	}

	/// <summary>
	/// Checks the fixed paint for one brush: bold text gets the brush and a bold face, other text only the brush.
	/// </summary>
	[AvaloniaFact]
	public void Paint_BrushAndBold_PaintsBrushAndWeight()
	{
		// Act
		var painted = Painted(style => StylePainter.Paint(Brushes.Red, style.Bold), "\e[1mb\e[0m \e[4mu\e[0m\n");

		// Assert
		_ = painted.Where(each => each.Text.Trim().Length > 0).Should().Equal(
			("b", Colors.Red, null, FontWeight.Bold),
			("u", Colors.Red, null, FontWeight.Normal));
	}

	/// <summary>
	/// Opens a page in a pager that paints with <paramref name="paint" />, and reads each piece of text back.
	/// </summary>
	/// <param name="paint">The paint function to give the pager.</param>
	/// <param name="output">The page, with its escape sequences.</param>
	/// <returns>
	/// Each piece of the first line with its foreground and background colors and its weight.
	/// </returns>
	private static List<(string Text, Color? Foreground, Color? Background, FontWeight Weight)> Painted(Func<AnsiStyle, Action<VisualLineElement>> paint, string output)
	{
		var pager = new ManPager(FontFamily.Default, 15, Brushes.White, Brushes.Gray, Brushes.Blue, paint);
		var window = new Window { Width = 800, Height = 400, Content = pager };

		window.Show();
		pager.Open("page", output);
		Dispatcher.UIThread.RunJobs();

		var editor = pager.GetVisualDescendants().OfType<TextEditor>().Single();
		var textView = editor.TextArea.TextView;

		textView.EnsureVisualLines();

		var line = textView.VisualLines[0];

		return [.. line.Elements.Select(element =>
			(editor.Document.GetText(line.FirstDocumentLine.Offset + element.RelativeTextOffset, element.DocumentLength),
				ColorOf(element.TextRunProperties.ForegroundBrush),
				ColorOf(element.TextRunProperties.BackgroundBrush),
				element.TextRunProperties.Typeface.Weight))];
	}

	/// <summary>
	/// Gets a brush's color, since the text keeps its own copies of the brushes it's painted with.
	/// </summary>
	/// <param name="brush">The brush, or <see langword="null" />.</param>
	/// <returns>
	/// The color, or <see langword="null" /> for no brush.
	/// </returns>
	private static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;
}
