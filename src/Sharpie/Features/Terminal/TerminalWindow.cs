using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Sharpie.Features.ManPages;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Features.Terminal;

/// <summary>
/// Shows the shell in a dark, translucent window, as a real terminal: keys go to the shell's pseudo-terminal, and the
/// terminal's output is drawn on a character grid.
/// </summary>
/// <remarks>
/// The shell runs as a separate child process because its builtins act on the whole process: <c>cd</c> changes the
/// process's working directory and <c>exit</c> ends the process. It runs in a pseudo-terminal, so the shell and the
/// programs it starts see a terminal, prompts like <c>[Y/n]</c> and <c>sudo</c>'s password show, and full-screen
/// programs work. The prompt header, the colored input and Tab completion are drawn by the shell's line editor.
/// </remarks>
internal sealed partial class TerminalWindow : Window, IDisposable
{
	private readonly TerminalView view;
	private readonly ScrollBar scrollBar;
	private readonly ManPager pager;
	private readonly WindowFrame frame;
	private readonly ShellConnection shell;

	/// <summary>
	/// Initializes a new instance of the <see cref="TerminalWindow" /> class and starts the shell.
	/// </summary>
	public TerminalWindow() : this(useSavedHistory: true)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="TerminalWindow" /> class and starts the shell.
	/// </summary>
	/// <param name="useSavedHistory">
	/// <see langword="false" /> to keep the shell away from the saved history, so tests never read or write the user's
	/// history.
	/// </param>
	internal TerminalWindow(bool useSavedHistory)
	{
		this.Title = "Sharpie";
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

		// The shell is started below, after the view knows its first size. Anything typed before then is dropped.
		this.view = new TerminalView(bytes => this.shell?.Write(bytes));
		this.view.GridResized += this.OnGridResized;
		this.view.Scrolled += this.OnViewScrolled;
		this.view.Screen.ManPageRequested += this.OnManPageRequested;

		// Fluent's scroll bar shrinks to a thin line until the pointer comes near, close to the old screen's.
		this.scrollBar = new ScrollBar
		{
			Orientation = Orientation.Vertical,
			AllowAutoHide = true,
			HorizontalAlignment = HorizontalAlignment.Right,
			IsVisible = false,
		};
		this.scrollBar.Scroll += this.OnScrollBarScroll;

		this.pager = new ManPager(TerminalView.Font, TerminalView.FontSize, Palette.Text, Palette.Muted, Palette.Selection, new StylePainter().For);
		this.pager.Closed += this.OnPagerClosed;

		var titleBar = new TitleBar(this);

		DockPanel.SetDock(titleBar, Dock.Top);

		this.frame = new WindowFrame(this, new DockPanel { Children = { titleBar, new Panel { Children = { this.view, this.scrollBar, this.pager } } } });
		this.Content = this.frame;

		this.shell = ShellConnection.Start(this.OnOutput, useSavedHistory, this.view.Screen.Columns, this.view.Screen.Rows);
		this.shell.Exited += this.OnShellExited;

		this.Opened += this.OnOpened;
		this.Closed += this.OnClosed;
	}

	/// <summary>
	/// Gets the terminal view, which tests read the screen from.
	/// </summary>
	/// <value>
	/// The view the shell's output is drawn in.
	/// </value>
	internal TerminalView View => this.view;

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
	/// Draws what the shell printed, and names the window after the shell's folder.
	/// </summary>
	/// <param name="output">The text the terminal printed.</param>
	private void OnOutput(string output)
	{
		this.view.Feed(output);

		if (this.view.Screen.WorkingDirectory is { Length: > 0 } directory)
		{
			this.Title = $"{PromptHeader.SplitForDisplay(directory).Name} · Sharpie";
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

		_ = this.view.Focus();
	}

	/// <summary>
	/// Tells the terminal its new size when the window is resized, so programs redraw to fit.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnGridResized(object? sender, EventArgs e) =>
		this.shell?.Resize(this.view.Screen.Columns, this.view.Screen.Rows);

	/// <summary>
	/// Moves the scroll bar to match the view, and shows it only when there's scrollback.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnViewScrolled(object? sender, EventArgs e)
	{
		var scrollback = this.view.Screen.ScrollbackCount;

		this.scrollBar.IsVisible = scrollback > 0;
		this.scrollBar.Maximum = scrollback;
		this.scrollBar.ViewportSize = this.view.Screen.Rows;
		this.scrollBar.Value = scrollback - this.view.ScrollOffset;
	}

	/// <summary>
	/// Scrolls the view when the scroll bar is dragged.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnScrollBarScroll(object? sender, ScrollEventArgs e) =>
		this.view.ScrollTo((int)Math.Round(this.scrollBar.Maximum - e.NewValue));

	/// <summary>
	/// Closes the window after the shell exits, for example after <c>exit</c>.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnShellExited(object? sender, EventArgs e) => Dispatcher.UIThread.Post(this.Close);

	/// <summary>
	/// Stops the shell, and anything it started, when the user closes the window.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnClosed(object? sender, EventArgs e)
	{
		this.shell.Exited -= this.OnShellExited;
		this.Dispose();
	}

	/// <summary>
	/// Ends the shell and everything it started.
	/// </summary>
	/// <remarks>
	/// Runs when the window closes. Calling it again does nothing.
	/// </remarks>
	public void Dispose() => this.shell.Dispose();
}
