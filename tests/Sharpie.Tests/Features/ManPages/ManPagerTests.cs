using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
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
