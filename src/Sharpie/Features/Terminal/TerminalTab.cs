using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Sharpie.Features.ManPages;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Features.Terminal;

/// <summary>
/// One tab of a <see cref="TerminalWindow" />: a shell running in its own pseudo-terminal, drawn on a character grid,
/// with its scroll bar and man page reader.
/// </summary>
/// <remarks>
/// The shell runs as a separate child process because its builtins act on the whole process: <c>cd</c> changes the
/// process's working directory and <c>exit</c> ends the process. It runs in a pseudo-terminal, so the shell and the
/// programs it starts see a terminal, prompts like <c>[Y/n]</c> and <c>sudo</c>'s password show, and full-screen
/// programs work. The prompt header, the colored input and Tab completion are drawn by the shell's line editor.
/// </remarks>
internal sealed partial class TerminalTab : Panel, IDisposable
{
	/// <summary>
	/// Identifies the <see cref="Title" /> property.
	/// </summary>
	public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<TerminalTab, string>(nameof(Title), "Sharpie");

	private readonly ScrollBar scrollBar;
	private readonly ManPager pager;
	private readonly ShellConnection shell;

	/// <summary>
	/// Initializes a new instance of the <see cref="TerminalTab" /> class and starts its shell.
	/// </summary>
	/// <param name="useSavedHistory">
	/// <see langword="false" /> to keep the shell away from the saved history, so tests never read or write the user's
	/// history.
	/// </param>
	/// <param name="workingDirectory">The folder the shell starts in, or <see langword="null" /> for the home folder.</param>
	public TerminalTab(bool useSavedHistory, string? workingDirectory)
	{
		// The shell is started below, after the view knows its first size. Anything typed before then is dropped.
		this.View = new TerminalView(bytes => this.shell?.Write(bytes));
		this.View.GridResized += this.OnGridResized;
		this.View.Scrolled += this.OnViewScrolled;
		this.View.Screen.ManPageRequested += this.OnManPageRequested;

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
		this.pager.LinkClicked += this.OnPagerLinkClicked;

		this.Children.AddRange([this.View, this.scrollBar, this.pager]);

		this.shell = ShellConnection.Start(this.OnOutput, useSavedHistory, this.View.Screen.Columns, this.View.Screen.Rows, workingDirectory);
		this.shell.Exited += this.OnShellExited;
	}

	/// <summary>
	/// Occurs on the UI thread when the tab's shell ends, for example after <c>exit</c>.
	/// </summary>
	public event EventHandler? Exited;

	/// <summary>
	/// Gets the tab's title, which the window and the tab strip show.
	/// </summary>
	/// <value>
	/// The name of the shell's folder followed by <c>· Sharpie</c>, or just <c>Sharpie</c> until the shell reports a
	/// folder.
	/// </value>
	public string Title
	{
		get => this.GetValue(TitleProperty);
		private set => this.SetValue(TitleProperty, value);
	}

	/// <summary>
	/// Gets the terminal view, which tests read the screen from.
	/// </summary>
	/// <value>
	/// The view the shell's output is drawn in.
	/// </value>
	internal TerminalView View { get; }

	/// <summary>
	/// Gives the keyboard to the open man page, or to the terminal when no page is open.
	/// </summary>
	public void FocusInput()
	{
		if (this.pager.IsVisible)
		{
			this.pager.TakeFocus();
		}
		else
		{
			_ = this.View.Focus();
		}
	}

	/// <summary>
	/// Draws what the shell printed, and names the tab after the shell's folder.
	/// </summary>
	/// <param name="output">The text the terminal printed.</param>
	private void OnOutput(string output)
	{
		this.View.Feed(output);

		if (this.View.Screen.WorkingDirectory is { Length: > 0 } directory)
		{
			this.Title = $"{PromptHeader.SplitForDisplay(directory).Name} · Sharpie";
		}
	}

	/// <summary>
	/// Tells the terminal its new size when the tab is resized, so programs redraw to fit.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnGridResized(object? sender, EventArgs e) =>
		this.shell?.Resize(this.View.Screen.Columns, this.View.Screen.Rows);

	/// <summary>
	/// Moves the scroll bar to match the view, and shows it only when there's scrollback.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnViewScrolled(object? sender, EventArgs e)
	{
		var scrollback = this.View.Screen.ScrollbackCount;

		this.scrollBar.IsVisible = scrollback > 0;
		this.scrollBar.Maximum = scrollback;
		this.scrollBar.ViewportSize = this.View.Screen.Rows;
		this.scrollBar.Value = scrollback - this.View.ScrollOffset;
	}

	/// <summary>
	/// Scrolls the view when the scroll bar is dragged.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnScrollBarScroll(object? sender, ScrollEventArgs e) =>
		this.View.ScrollTo((int)Math.Round(this.scrollBar.Maximum - e.NewValue));

	/// <summary>
	/// Passes the shell's exit on as <see cref="Exited" />, on the UI thread.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnShellExited(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => this.Exited?.Invoke(this, EventArgs.Empty));

	/// <summary>
	/// Ends the shell and everything it started.
	/// </summary>
	/// <remarks>
	/// Runs when the tab or its window closes. Calling it again does nothing.
	/// </remarks>
	public void Dispose() => this.shell.Dispose();
}
