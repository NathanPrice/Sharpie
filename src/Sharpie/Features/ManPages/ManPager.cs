using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit;
using DotNetTips.Spargine.Core;
using Sharpie.Features.Ansi;

namespace Sharpie.Features.ManPages;

/// <summary>
/// Shows one man page over the whole window and lets the user page through and search it with the keys <c>less</c>
/// uses.
/// </summary>
/// <remarks>
/// <c>less</c> itself can't run in the window, because the shell and the programs it starts write to pipes rather
/// than a terminal. This stands in for it, the way less takes over the screen until <c>q</c>.
/// </remarks>
internal sealed partial class ManPager : DockPanel
{
	private readonly TextEditor view;
	private readonly TextBlock status;
	private readonly TextBox searchBox;
	private string title = string.Empty;

	// The quit key while it's held down, so the page closes on its release. Null otherwise.
	private Key? quitKey;

	// The less keys, by key. A lookup instead of a switch keeps OnKeyDown short, since a switch on Key has to list all
	// of the enum's members. Shift turns g into G and n into N, as in less.
	private static readonly Dictionary<Key, Action<ManPager, KeyEventArgs>> KeyActions = new()
	{
		// Closed when the key comes back up, in OnKeyUp. The q's text input arrives after this event, and closing now
		// moved focus to the shell screen first, which typed the q into the next prompt.
		[Key.Q] = static (pager, e) => pager.quitKey = e.Key,
		[Key.Escape] = static (pager, e) => pager.quitKey = e.Key,
		[Key.J] = static (pager, _) => pager.Scroller?.LineDown(),
		[Key.Down] = static (pager, _) => pager.Scroller?.LineDown(),
		[Key.Enter] = static (pager, _) =>
		{
			if (!pager.FollowSelectedLink())
			{
				pager.Scroller?.LineDown();
			}
		},
		[Key.Tab] = static (pager, e) => pager.SelectLink(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift)),
		[Key.Back] = static (pager, _) => pager.Back(),
		[Key.Left] = static (pager, e) =>
		{
			if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
			{
				pager.Back();
			}
		},
		[Key.K] = static (pager, _) => pager.Scroller?.LineUp(),
		[Key.Up] = static (pager, _) => pager.Scroller?.LineUp(),
		[Key.Space] = static (pager, _) => pager.Scroller?.PageDown(),
		[Key.F] = static (pager, _) => pager.Scroller?.PageDown(),
		[Key.PageDown] = static (pager, _) => pager.Scroller?.PageDown(),
		[Key.B] = static (pager, _) => pager.Scroller?.PageUp(),
		[Key.PageUp] = static (pager, _) => pager.Scroller?.PageUp(),
		[Key.End] = static (pager, _) => pager.view.ScrollToEnd(),
		[Key.Home] = static (pager, _) => pager.view.ScrollToHome(),
		[Key.G] = static (pager, e) =>
		{
			if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
			{
				pager.view.ScrollToEnd();
			}
			else
			{
				pager.view.ScrollToHome();
			}
		},
		[Key.N] = static (pager, e) => pager.Find(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift)),
	};

	// The last pattern entered after /, which n and N repeat. Empty until the first search.
	private string pattern = string.Empty;
	private readonly Func<AnsiStyle, Action<VisualLineElement>> paint;
	private readonly List<AnsiRun> runs = [];

	/// <summary>
	/// Initializes a new instance of the <see cref="ManPager" /> class.
	/// </summary>
	/// <param name="font">The font the window's screen uses, so the page looks like the rest of the shell.</param>
	/// <param name="fontSize">The font size the window's screen uses.</param>
	/// <param name="text">The default text color.</param>
	/// <param name="muted">The color of the status line.</param>
	/// <param name="selection">The brush for selected text and search matches.</param>
	/// <param name="paint">Turns a style the page printed into a paint action in the window's theme.</param>
	public ManPager(FontFamily font, double fontSize, IBrush text, IBrush muted, IBrush selection, Func<AnsiStyle, Action<VisualLineElement>> paint)
	{
		_ = font.ArgumentNotNull();
		_ = text.ArgumentNotNull();
		_ = muted.ArgumentNotNull();
		_ = selection.ArgumentNotNull();
		this.paint = paint.ArgumentNotNull();
		this.IsVisible = false;

		this.status = new TextBlock
		{
			Foreground = muted,
			FontFamily = font,
			FontSize = fontSize - 2,
			Margin = new Thickness(16, 4, 16, 8),
		};

		this.searchBox = new TextBox
		{
			IsVisible = false,
			PlaceholderText = "/search",
			Foreground = text,
			Background = Brushes.Transparent,
			BorderThickness = default,
			FontFamily = font,
			FontSize = fontSize - 2,
			Margin = new Thickness(12, 0, 12, 4),
		};
		this.searchBox.KeyDown += this.OnSearchBoxKeyDown;

		// Man formats each page to the width it was given, so wrapping it again would break the indented columns.
		this.view = new TextEditor
		{
			IsReadOnly = true,
			WordWrap = false,
			Background = Brushes.Transparent,
			Foreground = text,
			FontFamily = font,
			FontSize = fontSize,
			Padding = new Thickness(16),
		};

		// AvaloniaEdit lets an editor scroll a screen past the last line by default. A reader stops at the end, like less.
		this.view.Options.AllowScrollBelowDocument = false;

		// A reader has nothing to type into, so the caret is hidden. Selecting text to copy still works.
		this.view.TextArea.Caret.CaretBrush = Brushes.Transparent;

		// On the TextView, not the TextArea: AvaloniaEdit puts its own I-beam there, and the innermost cursor wins.
		this.view.TextArea.TextView.Cursor = ArrowCursor;
		this.view.TextArea.SelectionBrush = selection;
		this.view.TextArea.SelectionBorder = null;
		this.view.TextArea.TextView.LineTransformers.Add(new RunColorizer(this));
		this.view.Document.UndoStack.SizeLimit = 0;

		// On the pager rather than the text, so the keys work whichever part of the page has focus.
		this.AddHandler(KeyDownEvent, this.OnKeyDown, RoutingStrategies.Tunnel);
		this.view.TextArea.AddHandler(PointerReleasedEvent, this.OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
		// Bubble, so it runs after AvaloniaEdit's own handler, which puts the I-beam back on every move.
		this.view.TextArea.AddHandler(PointerMovedEvent, this.OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
		this.AddHandler(TextInputEvent, this.OnTextInput, RoutingStrategies.Tunnel);
		this.AddHandler(KeyUpEvent, this.OnKeyUp, RoutingStrategies.Tunnel);

		SetDock(this.status, Dock.Bottom);
		SetDock(this.searchBox, Dock.Bottom);
		this.Children.Add(this.status);
		this.Children.Add(this.searchBox);
		this.Children.Add(this.view);
	}

	/// <summary>
	/// Gets the scroll viewer around the page text.
	/// </summary>
	/// <value>
	/// The viewer, or <see langword="null" /> before the editor's template is applied.
	/// </value>
	/// <remarks>
	/// <see cref="TextEditor" /> has <c>LineDown</c> and <c>PageDown</c> methods, but they're empty in AvaloniaEdit 12,
	/// and the viewer they would use is internal. <see cref="TextEditor.ScrollToHome" /> and
	/// <see cref="TextEditor.ScrollToEnd" /> do work, so the top and end keys still use those.
	/// </remarks>
	private ScrollViewer? Scroller => this.view.TextArea.FindAncestorOfType<ScrollViewer>();

	/// <summary>
	/// Occurs when the user quits the page.
	/// </summary>
	public event EventHandler? Closed;

	/// <summary>
	/// Shows <paramref name="output" /> from the top as a new page, with no history to go back to, and gives the page
	/// the keyboard.
	/// </summary>
	/// <param name="title">The page name for the status line, such as <c>ls</c>.</param>
	/// <param name="output">What man printed, with its overstrike and color codes.</param>
	public void Open(string title, string output)
	{
		this.history.Clear();
		this.Show(title, output);
	}

	/// <summary>
	/// Shows a page from the top and gives it the keyboard.
	/// </summary>
	/// <param name="title">The page name for the status line.</param>
	/// <param name="output">The page, with its overstrike, color codes and links.</param>
	private void Show(string title, string output)
	{
		var plain = new StringBuilder();

		this.runs.Clear();
		this.links.Clear();
		new AnsiParser().Parse(output, plain, this.runs, this.links);

		this.output = output;
		this.view.Text = plain.ToString();
		this.view.ScrollToHome();
		this.view.CaretOffset = 0;
		this.title = title;
		this.quitKey = null;
		this.pattern = string.Empty;
		this.ShowHints();
		this.IsVisible = true;
		FocusAfterLayout(this.view.TextArea);
	}

	/// <summary>
	/// Gives the page the keyboard again, for when focus ended up somewhere else while the page is open.
	/// </summary>
	public void TakeFocus()
	{
		FocusAfterLayout(this.searchBox.IsVisible ? this.searchBox : this.view.TextArea);
	}

	/// <summary>
	/// Handles the less keys.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	/// <remarks>
	/// Only the keys used here are marked handled. Marking every key would also stop the text input for <c>/</c>,
	/// which <see cref="OnTextInput" /> needs. The text is read-only, so the other keys can't change anything.
	/// </remarks>
	private void OnKeyDown(object? sender, KeyEventArgs e)
	{
		// The search line's own handler takes Enter and Escape, and every other key is typed into it.
		if (this.searchBox.IsKeyboardFocusWithin)
		{
			return;
		}

		// Like less, a message such as "Pattern not found" lasts until the next key. A search with no match then puts
		// it back below.
		this.ShowHints();

		// Ctrl and Cmd shortcuts are left alone so Ctrl+C still copies a selection.
		if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 || !KeyActions.TryGetValue(e.Key, out var action))
		{
			return;
		}

		action(this, e);
		e.Handled = true;
	}

	/// <summary>
	/// Closes the page when the quit key that went down on it comes back up.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnKeyUp(object? sender, KeyEventArgs e)
	{
		if (e.Key != this.quitKey)
		{
			return;
		}

		this.quitKey = null;
		this.IsVisible = false;
		e.Handled = true;
		this.Closed?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// Opens the search line when <c>/</c> is typed, and keeps all other typed text off the page.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	/// <remarks>
	/// Done on the typed text rather than in <see cref="OnKeyDown" />, because <c>/</c> sits on a different key in
	/// many keyboard layouts.
	/// </remarks>
	private void OnTextInput(object? sender, TextInputEventArgs e)
	{
		if (this.searchBox.IsKeyboardFocusWithin)
		{
			return;
		}

		// The page is read-only, so no typed text is wanted there. It's swallowed so a quitting q can't carry on into
		// the shell's prompt.
		e.Handled = true;

		if (e.Text != "/")
		{
			return;
		}

		this.status.IsVisible = false;
		this.searchBox.Text = string.Empty;
		this.searchBox.IsVisible = true;
		FocusAfterLayout(this.searchBox);
	}

	/// <summary>
	/// Searches for the typed pattern on Enter, or closes the search line on Escape.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key is not (Key.Enter or Key.Escape))
		{
			return;
		}

		var pattern = this.searchBox.Text ?? string.Empty;

		this.searchBox.IsVisible = false;
		this.ShowHints();

		_ = this.view.TextArea.Focus();

		e.Handled = true;

		if (e.Key != Key.Enter || pattern.Length <= 0)
		{
			return;
		}

		this.pattern = pattern;

		// A new pattern starts from the top of the screen, like less, not from the old match.
		this.view.Select(this.TopOffset(), 0);
		this.Find(forward: true);
	}

	/// <summary>
	/// Selects the next or previous match of the last pattern, wrapping around the ends, and scrolls to it.
	/// </summary>
	/// <param name="forward">
	/// <see langword="true" /> to search after the current match; <see langword="false" /> to search before it.
	/// </param>
	/// <remarks>
	/// Matching ignores case, since man pages capitalize the same word differently in headings and text. Does nothing
	/// before a pattern has been entered.
	/// </remarks>
	private void Find(bool forward)
	{
		if (this.pattern.Length == 0)
		{
			return;
		}

		var text = this.view.Text ?? string.Empty;
		var start = this.view.SelectionStart;
		int match;

		if (forward)
		{
			match = text.IndexOf(this.pattern, start + this.view.SelectionLength, StringComparison.OrdinalIgnoreCase);

			if (match < 0)
			{
				match = text.IndexOf(this.pattern, StringComparison.OrdinalIgnoreCase);
			}
		}
		else
		{
			match = start > 0 ? text.LastIndexOf(this.pattern, start - 1, StringComparison.OrdinalIgnoreCase) : -1;

			if (match < 0)
			{
				match = text.LastIndexOf(this.pattern, StringComparison.OrdinalIgnoreCase);
			}
		}

		if (match < 0)
		{
			this.status.Text = $"Pattern not found: {this.pattern}";
			return;
		}

		this.view.Select(match, this.pattern.Length);
		this.ScrollTo(match);
	}

	/// <summary>
	/// Scrolls so the line holding an offset sits a third of the way down, so the lines just before it stay in view.
	/// </summary>
	/// <param name="offset">The document offset.</param>
	private void ScrollTo(int offset)
	{
		if (this.Scroller is not { } scroller)
		{
			return;
		}

		var line = this.view.Document.GetLineByOffset(offset).LineNumber;
		var top = this.view.TextArea.TextView.GetVisualTopByDocumentLine(line);

		scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, top - (scroller.Viewport.Height / 3)));
	}

	/// <summary>
	/// Gets the offset of the first line showing at the top of the page.
	/// </summary>
	/// <returns>
	/// The document offset where that line starts, or 0 before the page is laid out.
	/// </returns>
	private int TopOffset()
	{
		var textView = this.view.TextArea.TextView;

		return textView.GetDocumentLineByVisualTop(this.Scroller?.Offset.Y ?? 0)?.Offset ?? 0;
	}

	/// <summary>
	/// Gives <paramref name="control" /> the keyboard once the layout pass that follows has run.
	/// </summary>
	/// <param name="control">The control to focus.</param>
	/// <remarks>
	/// The pager and its search line start hidden, so until they're laid out once their templates aren't applied and
	/// focusing them silently does nothing. That left the keyboard on the hidden shell screen, and no pager key
	/// worked.
	/// </remarks>
	private static void FocusAfterLayout(InputElement control) => Dispatcher.UIThread.Post(() => control.Focus(), DispatcherPriority.Loaded);

	/// <summary>
	/// Puts the page name and the key hints back on the status line.
	/// </summary>
	private void ShowHints()
	{
		this.status.Text = $"{this.title} · q quit · / search · n/N next/prev · space/b page · g/G top/end"
			+ (this.links.Count > 0 ? " · Tab/Enter links" : string.Empty)
			+ (this.history.Count > 0 ? " · Backspace back" : string.Empty);
		this.status.IsVisible = true;
	}

	/// <summary>
	/// Colors the page with the runs <see cref="AnsiParser" /> found in it, and draws its links cyan and underlined.
	/// </summary>
	/// <param name="pager">The pager whose runs are drawn.</param>
	private sealed class RunColorizer(ManPager pager) : DocumentColorizingTransformer
	{
		private static readonly AnsiStyle LinkStyle = new(new AnsiColor(6, 0), null, Bold: false);

		/// <inheritdoc />
		protected override void ColorizeLine(DocumentLine line)
		{
			var runs = pager.runs;

			for (var index = FirstReaching(runs.Count, item => runs[item].Start + runs[item].Length, line.Offset); index < runs.Count && runs[index].Start < line.EndOffset; index++)
			{
				var run = runs[index];
				var start = Math.Max(run.Start, line.Offset);
				var end = Math.Min(run.Start + run.Length, line.EndOffset);

				if (start < end)
				{
					this.ChangeLinePart(start, end, pager.paint(run.Style));
				}
			}

			var links = pager.links;

			// Cyan, since man draws bold in the same blue most links are written in, and the underline alone was easy
			// to miss. Nothing else in man or dman prose is cyan; code examples use it, but code never has links.
			var link = pager.paint(LinkStyle);

			for (var index = FirstReaching(links.Count, item => links[item].Start + links[item].Length, line.Offset); index < links.Count && links[index].Start < line.EndOffset; index++)
			{
				var start = Math.Max(links[index].Start, line.Offset);
				var end = Math.Min(links[index].Start + links[index].Length, line.EndOffset);

				if (start < end)
				{
					this.ChangeLinePart(start, end, element =>
					{
						link(element);
						element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
					});
				}
			}
		}

		/// <summary>
		/// Finds the first item, in a list sorted by position, that reaches past an offset.
		/// </summary>
		/// <param name="count">The number of items.</param>
		/// <param name="end">Gets where an item ends.</param>
		/// <param name="offset">The offset, like the start of a line.</param>
		/// <returns>
		/// The index of that item, or <paramref name="count" /> when none does.
		/// </returns>
		/// <remarks>
		/// A binary search, since a long page has thousands of runs and only the few on the line being drawn matter.
		/// </remarks>
		private static int FirstReaching(int count, Func<int, int> end, int offset)
		{
			var low = 0;
			var high = count;

			while (low < high)
			{
				var middle = (low + high) / 2;

				if (end(middle) <= offset)
				{
					low = middle + 1;
				}
				else
				{
					high = middle;
				}
			}

			return low;
		}
	}
}
