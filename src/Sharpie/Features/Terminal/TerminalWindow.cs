using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;

namespace Sharpie.Features.Terminal;

/// <summary>
/// Shows shells in a dark, translucent window, one per <see cref="TerminalTab" />, with a <see cref="TabStrip" /> to
/// switch between them once there are two or more.
/// </summary>
internal sealed class TerminalWindow : Window, IDisposable
{
	private readonly List<TerminalTab> tabs = [];
	private readonly Panel pages;
	private readonly TabStrip strip;
	private readonly WindowFrame frame;
	private readonly bool useSavedHistory;
	private TerminalTab active;

	/// <summary>
	/// Initializes a new instance of the <see cref="TerminalWindow" /> class and starts the shell in its first tab.
	/// </summary>
	/// <param name="useSavedHistory">
	/// <see langword="false" /> to keep the shell away from the saved history, so tests never read or write the user's
	/// history.
	/// </param>
	/// <param name="command">
	/// A program and its arguments to run in the first tab instead of an interactive shell, or <see langword="null" />
	/// for the shell.
	/// </param>
	internal TerminalWindow(bool useSavedHistory, string[]? command = null)
	{
		// Wide and tall enough for the fastfetch banner, about 133 columns by 31 lines at font size 15, to fit with the
		// first prompt below it and no wrapping.
		this.Width = 1300;
		this.Height = 840;
		this.Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Sharpie/Assets/icon.png")));

		// The window draws its own title bar and frame, because KDE's frame stays light whatever the window looks like.
		// Avalonia can draw one itself, but on X11 (and XWayland, which Avalonia uses on Wayland) that's behind a flag
		// Avalonia marks experimental, so this sticks to stable APIs. The window itself is clear so the frame can have
		// rounded corners.
		this.WindowDecorations = WindowDecorations.None;
		this.Background = Brushes.Transparent;

		// Plain transparency only, for the rounded corners. KDE's blur and Windows' Mica cover the whole window
		// rectangle, so they showed as a square patch outside each rounded corner. When the system can't do
		// transparency at all, the frame goes square and opaque once the window opens.
		this.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

		this.strip = new TabStrip(this.SelectTab, this.CloseTab);
		this.pages = new Panel();

		var titleBar = new TitleBar(this);

		DockPanel.SetDock(titleBar, Dock.Top);
		DockPanel.SetDock(this.strip, Dock.Top);

		this.frame = new WindowFrame(this, new DockPanel { Children = { titleBar, this.strip, this.pages } });
		this.Content = this.frame;

		this.useSavedHistory = useSavedHistory;

		// The folder the window was started in, which file managers set for "Open Terminal Here". A launch from the
		// menu starts in the home folder, which the desktop file sets.
		this.OpenTab(Directory.GetCurrentDirectory(), command);

		// Tunneling, so these keys are seen before the terminal, which marks every key handled, or an open man page.
		this.AddHandler(KeyDownEvent, this.OnTabKeyDown, RoutingStrategies.Tunnel);
		this.Opened += this.OnOpened;
		this.Closed += this.OnClosed;
	}

	/// <summary>
	/// Gets the active tab's terminal view, which tests read the screen from.
	/// </summary>
	/// <value>
	/// The view the active tab's shell output is drawn in.
	/// </value>
	internal TerminalView View => this.active.View;

	/// <summary>
	/// Gets the window's tabs, in the order the tab strip shows them.
	/// </summary>
	/// <value>
	/// The open tabs, never empty while the window is open.
	/// </value>
	internal IReadOnlyList<TerminalTab> Tabs => this.tabs;

	/// <inheritdoc />
	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);

		// Runs during construction too, before the frame exists.
		if (change.Property == WindowStateProperty && this.frame is not null)
		{
			this.frame.ShowMaximized(this.WindowState == WindowState.Maximized);
		}
	}

	/// <summary>
	/// Focuses the terminal, and makes the background opaque when the system can't show it translucent.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnOpened(object? sender, EventArgs e)
	{
		if (this.ActualTransparencyLevel == WindowTransparencyLevel.None)
		{
			this.frame.ShowOpaque();
		}

		this.active.FocusInput();
	}

	/// <summary>
	/// Starts a shell in a new tab after the others and shows it.
	/// </summary>
	/// <param name="workingDirectory">The folder the shell starts in, or <see langword="null" /> for the home folder.</param>
	/// <param name="command">
	/// A program and its arguments to run instead of an interactive shell, or <see langword="null" /> for the shell.
	/// </param>
	[MemberNotNull(nameof(active))]
	private void OpenTab(string? workingDirectory, string[]? command = null)
	{
		var tab = new TerminalTab(this.useSavedHistory, workingDirectory, command);

		tab.Exited += (_, _) => this.CloseTab(tab);
		this.tabs.Add(tab);
		this.pages.Children.Add(tab);
		this.SelectTab(tab);
	}

	/// <summary>
	/// Shows <paramref name="tab" />, names the window after it, and gives it the keyboard.
	/// </summary>
	/// <param name="tab">The tab to show.</param>
	[MemberNotNull(nameof(active))]
	private void SelectTab(TerminalTab tab)
	{
		foreach (var each in this.tabs)
		{
			each.IsVisible = each == tab;
		}

		this.active = tab;
		this[!TitleProperty] = tab[!TerminalTab.TitleProperty];
		this.strip.Show(this.tabs, tab);
		tab.FocusInput();
	}

	/// <summary>
	/// Ends the shell in <paramref name="tab" /> and removes the tab, closing the window after the last one.
	/// </summary>
	/// <param name="tab">The tab to close.</param>
	/// <remarks>
	/// A tab that's already gone is skipped, since its shell's exit can arrive after the tab or window was closed.
	/// </remarks>
	private void CloseTab(TerminalTab tab)
	{
		var index = this.tabs.IndexOf(tab);

		if (index < 0)
		{
			return;
		}

		this.tabs.RemoveAt(index);
		_ = this.pages.Children.Remove(tab);
		tab.Dispose();

		if (this.tabs.Count == 0)
		{
			this.Close();

			return;
		}

		// Closing the active tab shows the one that took its place, or the new last tab, as Ghostty does.
		this.SelectTab(tab == this.active ? this.tabs[Math.Min(index, this.tabs.Count - 1)] : this.active);
	}

	/// <summary>
	/// Handles the tab keys: Ctrl+T opens a tab, Ctrl+Tab and Ctrl+Shift+Tab go to the next and previous tab,
	/// Ctrl+Shift+W closes the tab, and Alt+1 to Alt+9 go to a tab by number.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnTabKeyDown(object? sender, KeyEventArgs e)
	{
		var count = this.tabs.Count;
		var index = this.tabs.IndexOf(this.active);

		switch (e.KeyModifiers, e.Key)
		{
			// Programs no longer see Ctrl+T, such as readline's swap-characters.
			case (KeyModifiers.Control, Key.T):
				this.OpenTab(this.active.View.Screen.WorkingDirectory);
				break;

			case (KeyModifiers.Control, Key.Tab):
				this.SelectTab(this.tabs[(index + 1) % count]);
				break;

			case (KeyModifiers.Control | KeyModifiers.Shift, Key.Tab):
				this.SelectTab(this.tabs[(index + count - 1) % count]);
				break;

			// Plain Ctrl+W stays with the shell, which uses it to delete a word.
			case (KeyModifiers.Control | KeyModifiers.Shift, Key.W):
				this.CloseTab(this.active);
				break;

			// Alt+9 is the last tab, as in Ghostty. A number with no tab is left for the program.
			case (KeyModifiers.Alt, >= Key.D1 and <= Key.D9):
				var number = e.Key == Key.D9 ? count : e.Key - Key.D1 + 1;

				if (number > count)
				{
					return;
				}

				this.SelectTab(this.tabs[number - 1]);
				break;

			default:
				return;
		}

		e.Handled = true;
	}

	/// <summary>
	/// Stops every tab's shell, and anything they started, when the user closes the window.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnClosed(object? sender, EventArgs e) => this.Dispose();

	/// <summary>
	/// Ends every tab's shell and everything they started.
	/// </summary>
	/// <remarks>
	/// Runs when the window closes. Calling it again does nothing.
	/// </remarks>
	public void Dispose()
	{
		foreach (var tab in this.tabs)
		{
			tab.Dispose();
		}

		// Emptied so a shell exit already on its way finds no tab to close.
		this.tabs.Clear();
	}
}
