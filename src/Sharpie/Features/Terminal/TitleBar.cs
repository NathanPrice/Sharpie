using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DotNetTips.Spargine.Core;

namespace Sharpie.Features.Terminal;

/// <summary>
/// The title bar of a borderless window: the title in the middle and the minimize, maximize, and close buttons on the
/// right. Dragging it moves the window and double-clicking it maximizes or restores.
/// </summary>
internal sealed class TitleBar : Panel
{
	private readonly Window window;

	/// <summary>
	/// Initializes a new instance of the <see cref="TitleBar" /> class for <paramref name="window" />.
	/// </summary>
	/// <param name="window">The window the bar shows the title of and moves, sizes, and closes.</param>
	public TitleBar(Window window)
	{
		this.window = window.ArgumentNotNull();

		this.Height = 34;
		this.Background = Brushes.Transparent;

		this.Children.Add(new TextBlock
		{
			Foreground = Palette.Muted,
			FontSize = 13,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			[!TextBlock.TextProperty] = window[!Window.TitleProperty],
		});

		this.Children.Add(new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Children =
			{
				CreateCaptionButton("M0,5 L10,5", () => window.WindowState = WindowState.Minimized, hover: null),
				CreateCaptionButton("M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z", this.ToggleMaximized, hover: null),
				CreateCaptionButton("M0,0 L10,10 M10,0 L0,10", window.Close, hover: Palette.Red),
			},
		});

		this.PointerPressed += this.OnPointerPressed;
	}

	/// <summary>
	/// Builds one flat title bar button showing a 10 by 10 line drawing.
	/// </summary>
	/// <param name="glyph">The path data for the drawing.</param>
	/// <param name="click">What the button does when clicked.</param>
	/// <param name="hover">The background when the pointer is over it, or <see langword="null" /> for a faint highlight.</param>
	/// <returns>
	/// The button.
	/// </returns>
	private static Button CreateCaptionButton(string glyph, Action click, IBrush? hover)
	{
		var button = new Button
		{
			Width = 40,
			Height = 28,
			Margin = new Thickness(2, 0),
			CornerRadius = new CornerRadius(6),
			Background = Brushes.Transparent,
			HorizontalContentAlignment = HorizontalAlignment.Center,
			VerticalContentAlignment = VerticalAlignment.Center,
			Content = new Avalonia.Controls.Shapes.Path
			{
				Data = Geometry.Parse(glyph),
				Stroke = Palette.Text,
				StrokeThickness = 1.2,
			},
		};

		// Fluent reads these resources for its hover and pressed looks, which would otherwise be a light gray box.
		button.Resources["ButtonBackgroundPointerOver"] = hover ?? new SolidColorBrush(Colors.White, 0.08);
		button.Resources["ButtonBackgroundPressed"] = hover ?? new SolidColorBrush(Colors.White, 0.14);
		button.Click += (_, _) => click();

		return button;
	}

	/// <summary>
	/// Moves the window on a drag, or maximizes or restores it on a double-click.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
		{
			return;
		}

		if (e.ClickCount == 2)
		{
			this.ToggleMaximized();
		}
		else
		{
			this.window.BeginMoveDrag(e);
		}
	}

	/// <summary>
	/// Maximizes the window, or restores it when it's already maximized.
	/// </summary>
	private void ToggleMaximized() => this.window.WindowState = this.window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
