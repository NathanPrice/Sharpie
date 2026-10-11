using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using FluentAssertions;
using Sharpie.Features.ManPages;

namespace Sharpie.Tests.Features.ManPages;

/// <summary>
/// Drives <see cref="ManPager" /> with real key events on the headless platform, set up the way the terminal window
/// uses it: hidden next to a focused screen that gets hidden when a page opens.
/// </summary>
public sealed class ManPagerTests
{
	// 200 lines, so the page is several screens long. "needle" is on lines 50 and 150, and line 0 has a bold heading
	// in man's overstrike form.
	private static readonly string Page = BuildPage();

	private readonly Window window;
	private readonly ManPager pager;
	private bool closed;

	/// <summary>
	/// Initializes a new instance of the <see cref="ManPagerTests" /> class, with the page open and laid out.
	/// </summary>
	public ManPagerTests()
	{
		var screen = new TextBox();

		this.pager = new ManPager(FontFamily.Default, 15, Brushes.White, Brushes.Gray, Brushes.Blue, _ => _ => { });
		this.pager.Closed += (_, _) => this.closed = true;
		this.window = new Window { Width = 800, Height = 400, Content = new Panel { Children = { screen, this.pager } } };
		this.window.Show();
		_ = screen.Focus();
		Dispatcher.UIThread.RunJobs();

		screen.IsVisible = false;
		this.pager.Open("ls", Page);
		Dispatcher.UIThread.RunJobs();
	}

	// Taken from the page itself, since the search line's text box has a scroll viewer of its own.
	private ScrollViewer Scroller => this.View.GetVisualDescendants().OfType<ScrollViewer>().First();

	private TextBlock Status =>
		this.pager.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text?.Contains(" · ", StringComparison.Ordinal) == true
		                                                                      || block.Text?.StartsWith("Pattern", StringComparison.Ordinal) == true);

	private TextEditor View => this.pager.GetVisualDescendants().OfType<TextEditor>().First();

	/// <summary>
	/// Checks that opening a page moves the keyboard focus into it.
	/// </summary>
	[AvaloniaFact]
	public void Open_GivesThePageKeyboardFocus()
	{
		// Assert
		_ = this.pager.IsKeyboardFocusWithin.Should().BeTrue();
	}

	/// <summary>
	/// Checks that the overstruck heading shows without its backspaces.
	/// </summary>
	[AvaloniaFact]
	public void Open_OverstrikeHeading_ShowsPlainText()
	{
		// Assert
		_ = this.View.Text.Should().StartWith("NAME\n");
	}

	/// <summary>
	/// Checks that Page Down scrolls the page.
	/// </summary>
	[AvaloniaFact]
	public void PageDown_ScrollsDown()
	{
		// Act
		this.Press(Key.PageDown, PhysicalKey.PageDown);

		// Assert
		_ = this.Scroller.Offset.Y.Should().BeGreaterThan(0);
	}

	/// <summary>
	/// Checks that Page Up after Page Down goes back to the top.
	/// </summary>
	[AvaloniaFact]
	public void PageUp_AfterPageDown_ScrollsBack()
	{
		// Arrange
		this.Press(Key.PageDown, PhysicalKey.PageDown);

		// Act
		this.Press(Key.PageUp, PhysicalKey.PageUp);

		// Assert
		_ = this.Scroller.Offset.Y.Should().Be(0);
	}

	/// <summary>
	/// Checks that the less keys for scrolling down move the page.
	/// </summary>
	/// <param name="key">The key pressed.</param>
	/// <param name="physical">Where the key sits on a QWERTY keyboard.</param>
	[AvaloniaTheory]
	[InlineData(Key.J, PhysicalKey.J)]
	[InlineData(Key.Down, PhysicalKey.ArrowDown)]
	[InlineData(Key.Space, PhysicalKey.Space)]
	[InlineData(Key.F, PhysicalKey.F)]
	public void ScrollDownKey_ScrollsDown(Key key, PhysicalKey physical)
	{
		// Act
		this.Press(key, physical);

		// Assert
		_ = this.Scroller.Offset.Y.Should().BeGreaterThan(0);
	}

	/// <summary>
	/// Checks that Shift+G jumps to the end of the page.
	/// </summary>
	[AvaloniaFact]
	public void ShiftG_JumpsToEnd()
	{
		// Act
		this.Press(Key.G, PhysicalKey.G, RawInputModifiers.Shift);

		// Assert
		_ = this.Scroller.Offset.Y.Should().BeApproximately(this.Scroller.Extent.Height - this.Scroller.Viewport.Height, 1);
	}

	/// <summary>
	/// Checks that a search typed after / selects the first match and scrolls it into view.
	/// </summary>
	[AvaloniaFact]
	public void Search_TypedPattern_SelectsFirstMatch()
	{
		// Act
		this.Search("NEEDLE");

		// Assert
		_ = this.View.SelectedText.Should().Be("needle");
		_ = this.View.TextArea.Selection.StartPosition.Line.Should().Be(51);
		_ = this.Scroller.Offset.Y.Should().BeGreaterThan(0);
	}

	/// <summary>
	/// Checks that n moves to the next match and wraps back to the first after the last.
	/// </summary>
	[AvaloniaFact]
	public void N_AfterSearch_MovesToNextMatchAndWraps()
	{
		// Arrange
		this.Search("needle");

		// Act
		this.Press(Key.N, PhysicalKey.N);
		var second = this.View.TextArea.Selection.StartPosition.Line;
		this.Press(Key.N, PhysicalKey.N);

		// Assert
		_ = second.Should().Be(151);
		_ = this.View.TextArea.Selection.StartPosition.Line.Should().Be(51);
	}

	/// <summary>
	/// Checks that a search with no match shows a message that the next key clears, as in less.
	/// </summary>
	[AvaloniaFact]
	public void Search_NoMatch_ShowsMessageUntilNextKey()
	{
		// Arrange
		this.Search("zzzz");
		var message = this.Status.Text;

		// Act
		this.Press(Key.J, PhysicalKey.J);

		// Assert
		_ = message.Should().Be("Pattern not found: zzzz");
		_ = this.Status.Text.Should().StartWith("ls · q quit");
	}

	/// <summary>
	/// Checks that typing q into the search line types it instead of quitting.
	/// </summary>
	[AvaloniaFact]
	public void Search_TypingQ_DoesNotQuit()
	{
		// Act
		this.window.KeyTextInput("/");
		Dispatcher.UIThread.RunJobs();
		this.Press(Key.Q, PhysicalKey.Q);
		this.window.KeyTextInput("q");
		Dispatcher.UIThread.RunJobs();

		// Assert
		_ = this.closed.Should().BeFalse();
		_ = this.pager.GetVisualDescendants().OfType<TextBox>().Single(box => box.IsVisible).Text.Should().Be("q");
	}

	/// <summary>
	/// Checks that q closes the page.
	/// </summary>
	[AvaloniaFact]
	public void Q_RaisesClosed()
	{
		// Act
		this.Press(Key.Q, PhysicalKey.Q);

		// Assert
		_ = this.closed.Should().BeTrue();
		_ = this.pager.IsVisible.Should().BeFalse();
	}

	private void Press(Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
	{
		this.window.KeyPress(key, modifiers, physical, null);
		this.window.KeyRelease(key, modifiers, physical, null);
		Dispatcher.UIThread.RunJobs();
	}

	private void Search(string pattern)
	{
		this.window.KeyTextInput("/");
		Dispatcher.UIThread.RunJobs();
		this.window.KeyTextInput(pattern);
		this.Press(Key.Enter, PhysicalKey.Enter);
	}

	/// <summary>
	/// Checks that Tab selects the page's links in turn and Enter follows the selected one.
	/// </summary>
	[AvaloniaFact]
	public void TabThenEnter_FollowsSecondLink()
	{
		// Arrange
		string? followed = null;

		this.pager.LinkClicked += (_, url) => followed = url;
		this.pager.Open("links", "see \e]8;;https://a.test/one\e\\One\e]8;;\e\\ and \e]8;;https://a.test/two\e\\Two\e]8;;\e\\\n");
		Dispatcher.UIThread.RunJobs();

		// Act
		this.Press(Key.Tab, PhysicalKey.Tab);
		this.Press(Key.Tab, PhysicalKey.Tab);
		var selected = this.View.SelectedText;
		this.Press(Key.Enter, PhysicalKey.Enter);

		// Assert
		_ = this.View.Text.Should().Be("see One and Two\n");
		_ = selected.Should().Be("Two");
		_ = followed.Should().Be("https://a.test/two");
	}

	/// <summary>
	/// Checks that Backspace goes back from a page a link led to, and does nothing on the first page.
	/// </summary>
	[AvaloniaFact]
	public void Backspace_AfterNavigate_ShowsPreviousPage()
	{
		// Arrange
		this.pager.Navigate("next", "the next page\n");
		Dispatcher.UIThread.RunJobs();

		// Act
		this.Press(Key.Back, PhysicalKey.Backspace);
		var back = this.View.Text;
		this.Press(Key.Back, PhysicalKey.Backspace);

		// Assert
		_ = back.Should().StartWith("NAME\n");
		_ = this.View.Text.Should().StartWith("NAME\n");
		_ = this.closed.Should().BeFalse();
	}

	/// <summary>
	/// Checks that the pointer is the plain arrow over text and turns into a hand over a link.
	/// </summary>
	[AvaloniaFact]
	public void PointerMove_OverLink_ShowsHandElsewhereArrow()
	{
		// Arrange
		this.pager.Open("links", "\e]8;;https://a.test/one\e\\One\e]8;;\e\\ plain text here\n");
		Dispatcher.UIThread.RunJobs();

		var textView = this.View.TextArea.TextView;

		// Act
		this.window.MouseMove(this.PointAt(1, 2));
		var overLink = textView.Cursor;
		this.window.MouseMove(this.PointAt(1, 12));

		// Assert
		_ = overLink.Should().BeSameAs(ManPager.HandCursor);
		_ = textView.Cursor.Should().BeSameAs(ManPager.ArrowCursor);
	}

	/// <summary>
	/// Checks that clicking a link follows it once and leaves no drag-select running, so moving the mouse afterwards
	/// doesn't select text.
	/// </summary>
	[AvaloniaFact]
	public void ClickLink_ThenMove_FollowsOnceWithoutSelecting()
	{
		// Arrange
		var followed = new List<string>();

		this.pager.LinkClicked += (_, url) => followed.Add(url);
		this.pager.Open("links", "\e]8;;https://a.test/one\e\\One\e]8;;\e\\ plain text here\nsecond line of plain text\n");
		Dispatcher.UIThread.RunJobs();

		// Act
		this.window.MouseDown(this.PointAt(1, 2), MouseButton.Left);
		this.window.MouseUp(this.PointAt(1, 2), MouseButton.Left);
		Dispatcher.UIThread.RunJobs();
		this.window.MouseMove(this.PointAt(2, 15));
		Dispatcher.UIThread.RunJobs();

		// Assert
		_ = followed.Should().Equal("https://a.test/one");
		_ = this.View.SelectionLength.Should().Be(0);
	}

	/// <summary>
	/// Checks that dragging from a link selects text to copy instead of following the link.
	/// </summary>
	[AvaloniaFact]
	public void DragFromLink_SelectsWithoutFollowing()
	{
		// Arrange
		var followed = false;

		this.pager.LinkClicked += (_, _) => followed = true;
		this.pager.Open("links", "\e]8;;https://a.test/one\e\\One\e]8;;\e\\ plain text here\n");
		Dispatcher.UIThread.RunJobs();

		// Act
		this.window.MouseDown(this.PointAt(1, 2), MouseButton.Left);
		this.window.MouseMove(this.PointAt(1, 12));
		this.window.MouseUp(this.PointAt(1, 12), MouseButton.Left);
		Dispatcher.UIThread.RunJobs();

		// Assert
		_ = this.View.SelectionLength.Should().BeGreaterThan(0);
		_ = followed.Should().BeFalse();
	}

	/// <summary>
	/// Checks that the page can't be scrolled past its last line, so the end of the page sits at the bottom of the
	/// reader, as in less.
	/// </summary>
	[AvaloniaFact]
	public void ShiftG_LastLineSitsAtBottom()
	{
		// Act
		this.Press(Key.G, PhysicalKey.G, RawInputModifiers.Shift);

		// Assert
		var textView = this.View.TextArea.TextView;
		var lastLineBottom = textView.DocumentHeight - this.Scroller.Offset.Y;

		_ = lastLineBottom.Should().BeGreaterThan(this.Scroller.Viewport.Height - (2 * textView.DefaultLineHeight));
	}

	/// <summary>
	/// Gets the window point in the middle of a character of the page.
	/// </summary>
	/// <param name="line">The line, from 1.</param>
	/// <param name="column">The column, from 1.</param>
	/// <returns>
	/// The point, in window coordinates.
	/// </returns>
	private Point PointAt(int line, int column)
	{
		var textView = this.View.TextArea.TextView;
		var visual = textView.GetVisualPosition(new TextViewPosition(line, column), VisualYPosition.LineMiddle) - textView.ScrollOffset;

		return textView.TranslatePoint(visual, this.window)!.Value;
	}

	private static string BuildPage()
	{
		var page = new StringBuilder("N\bNA\bAM\bME\bE\n");

		for (var line = 1; line < 200; line++)
		{
			_ = page.Append(line is 50 or 150 ? $"     line {line} has a needle in it\n" : $"     line {line}\n");
		}

		return page.ToString();
	}
}
