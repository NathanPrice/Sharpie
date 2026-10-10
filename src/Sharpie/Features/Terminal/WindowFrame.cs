using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DotNetTips.Spargine.Core;

namespace Sharpie.Features.Terminal;

/// <summary>
/// The rounded, nearly opaque frame of a borderless window, which also resizes the window when its edge is dragged.
/// </summary>
internal sealed class WindowFrame : Border
{
	// How far in from the window's edge a press starts a resize. The frame is padded by this much, so the content
	// never covers it.
	private const double ResizeBand = 5;

	// Made once, since this runs on every pointer move and each Cursor holds a native handle.
	private static readonly Cursor DiagonalCursor = new(StandardCursorType.TopLeftCorner);
	private static readonly Cursor AntiDiagonalCursor = new(StandardCursorType.TopRightCorner);
	private static readonly Cursor VerticalCursor = new(StandardCursorType.SizeNorthSouth);
	private static readonly Cursor HorizontalCursor = new(StandardCursorType.SizeWestEast);

	private readonly Window window;

	/// <summary>
	/// Initializes a new instance of the <see cref="WindowFrame" /> class around <paramref name="content" />.
	/// </summary>
	/// <param name="window">The window the frame resizes.</param>
	/// <param name="content">What the frame holds.</param>
	public WindowFrame(Window window, Control content)
	{
		this.window = window.ArgumentNotNull();

		// Only just see-through. At 94% the wallpaper still showed through too much, since nothing blurs it.
		this.Background = new SolidColorBrush(Palette.Background, 0.97);
		this.ClipToBounds = true;
		this.BorderBrush = Palette.Edge;
		this.BorderThickness = new Thickness(1);
		this.CornerRadius = new CornerRadius(10);
		this.Padding = new Thickness(ResizeBand - 1);
		this.Child = content.ArgumentNotNull();

		this.AddHandler(PointerMovedEvent, this.OnFramePointerMoved, RoutingStrategies.Tunnel);
		this.AddHandler(PointerPressedEvent, this.OnFramePointerPressed, RoutingStrategies.Tunnel);
	}

	/// <summary>
	/// Squares the corners and drops the edge line while the window is maximized, since it has no edges to round or
	/// to grab then.
	/// </summary>
	/// <param name="maximized"><see langword="true" /> when the window is maximized.</param>
	public void ShowMaximized(bool maximized)
	{
		this.CornerRadius = new CornerRadius(maximized ? 0 : 10);
		this.BorderThickness = new Thickness(maximized ? 0 : 1);
	}

	/// <summary>
	/// Makes the frame square and opaque, for a system that can't show the window translucent.
	/// </summary>
	/// <remarks>
	/// Without any transparency the clear corners would show black.
	/// </remarks>
	public void ShowOpaque()
	{
		this.Background = new SolidColorBrush(Palette.Background);
		this.CornerRadius = default;
	}

	/// <summary>
	/// Finds which window edge or corner <paramref name="point" /> is on.
	/// </summary>
	/// <param name="point">The pointer position, relative to the frame.</param>
	/// <param name="size">The frame's size.</param>
	/// <returns>
	/// The edge or corner, or <see langword="null" /> when the point is inside the resize band on no side.
	/// </returns>
	private static WindowEdge? EdgeAt(Point point, Size size)
	{
		var left = point.X < ResizeBand;
		var right = point.X > size.Width - ResizeBand;
		var top = point.Y < ResizeBand;
		var bottom = point.Y > size.Height - ResizeBand;

		return (top, bottom, left, right) switch
		{
			(true, _, true, _) => WindowEdge.NorthWest,
			(true, _, _, true) => WindowEdge.NorthEast,
			(_, true, true, _) => WindowEdge.SouthWest,
			(_, true, _, true) => WindowEdge.SouthEast,
			(true, _, _, _) => WindowEdge.North,
			(_, true, _, _) => WindowEdge.South,
			(_, _, true, _) => WindowEdge.West,
			(_, _, _, true) => WindowEdge.East,
			_ => null,
		};
	}

	/// <summary>
	/// Shows a resize cursor while the pointer is over the edge of the frame.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnFramePointerMoved(object? sender, PointerEventArgs e)
	{
		var edge = this.window.WindowState == WindowState.Normal ? EdgeAt(e.GetPosition(this), this.Bounds.Size) : null;

		this.Cursor = edge switch
		{
			WindowEdge.NorthWest or WindowEdge.SouthEast => DiagonalCursor,
			WindowEdge.NorthEast or WindowEdge.SouthWest => AntiDiagonalCursor,
			WindowEdge.North or WindowEdge.South => VerticalCursor,
			WindowEdge.West or WindowEdge.East => HorizontalCursor,
			_ => null,
		};
	}

	/// <summary>
	/// Starts resizing the window when the frame is pressed on an edge, since there's no system frame to grab.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnFramePointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (this.window.WindowState != WindowState.Normal || EdgeAt(e.GetPosition(this), this.Bounds.Size) is not { } edge)
		{
			return;
		}

		this.window.BeginResizeDrag(edge, e);
		e.Handled = true;
	}
}
