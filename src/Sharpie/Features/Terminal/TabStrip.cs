using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DotNetTips.Spargine.Core;

namespace Sharpie.Features.Terminal;

/// <summary>
/// The row of tabs under the title bar, one equal-width tab per shell, each with its title and a close button. Shown
/// only while the window has two or more tabs, as Ghostty does.
/// </summary>
internal sealed class TabStrip : UniformGrid
{
	private readonly Action<TerminalTab> select;
	private readonly Action<TerminalTab> close;

	/// <summary>
	/// Initializes a new instance of the <see cref="TabStrip" /> class.
	/// </summary>
	/// <param name="select">What a click on a tab does with that tab.</param>
	/// <param name="close">What a click on a tab's close button does with that tab.</param>
	public TabStrip(Action<TerminalTab> select, Action<TerminalTab> close)
	{
		this.select = select.ArgumentNotNull();
		this.close = close.ArgumentNotNull();

		this.Rows = 1;
		this.Height = 32;
		this.Margin = new Thickness(4, 0, 4, 2);
		this.IsVisible = false;
	}

	/// <summary>
	/// Shows <paramref name="tabs" /> with <paramref name="active" /> highlighted, or hides the strip when there's only
	/// one tab.
	/// </summary>
	/// <param name="tabs">The window's tabs, in order.</param>
	/// <param name="active">The tab being shown.</param>
	public void Show(IReadOnlyList<TerminalTab> tabs, TerminalTab active)
	{
		this.Children.Clear();
		this.IsVisible = tabs.ArgumentNotNull().Count > 1;

		foreach (var tab in tabs)
		{
			this.Children.Add(this.CreateTab(tab, tab == active));
		}
	}

	/// <summary>
	/// Builds the control for one tab: its title in the middle and a close button on the right.
	/// </summary>
	/// <param name="tab">The tab.</param>
	/// <param name="isActive"><see langword="true" /> when the tab is the one being shown.</param>
	/// <returns>
	/// The control, which selects the tab when clicked.
	/// </returns>
	private Border CreateTab(TerminalTab tab, bool isActive)
	{
		var closeButton = TitleBar.CreateCaptionButton("M0,0 L10,10 M10,0 L0,10", () => this.close(tab), hover: Palette.Red);

		closeButton.HorizontalAlignment = HorizontalAlignment.Right;

		var border = new Border
		{
			Margin = new Thickness(2, 0),
			CornerRadius = new CornerRadius(6),
			Background = isActive ? new SolidColorBrush(Colors.White, 0.08) : Brushes.Transparent,
			Child = new Panel
			{
				Children =
				{
					new TextBlock
					{
						Foreground = isActive ? Palette.Text : Palette.Muted,
						FontSize = 13,
						TextTrimming = TextTrimming.CharacterEllipsis,
						Margin = new Thickness(44, 0),
						HorizontalAlignment = HorizontalAlignment.Center,
						VerticalAlignment = VerticalAlignment.Center,
						[!TextBlock.TextProperty] = tab[!TerminalTab.TitleProperty],
					},
					closeButton,
				},
			},
		};

		// The close button handles its own press, so this only runs for presses elsewhere on the tab.
		border.PointerPressed += (_, e) =>
		{
			if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
			{
				this.select(tab);
			}
		};

		return border;
	}
}
