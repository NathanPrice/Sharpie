using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Sharpie.Features.ManPages;
using Sharpie.Features.Terminal;
using Sharpie.Shell.Common;

namespace Sharpie.Tests.Features.Terminal;

/// <summary>
/// Drives a real <see cref="TerminalWindow" />, with its shell running in a real pseudo-terminal.
/// </summary>
/// <remarks>
/// The man page tests need <c>man</c> on <c>PATH</c>, and the prompt tests need <c>bash</c>. Each skips itself when
/// its program is missing, as on Windows.
/// </remarks>
public sealed class TerminalWindowTests : IDisposable
{
	// Shell output arrives on background reads, and the first prompt waits for fastfetch, so each wait allows a while.
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

	// Short, so the fastfetch banner and the prompts run past the bottom as they do in the real window.
	private readonly TerminalWindow window = new(useSavedHistory: false) { Height = 300 };

	/// <summary>
	/// Checks that the first tab's shell starts in the folder the window was started in, as a file manager's "Open
	/// Terminal Here" expects.
	/// </summary>
	[AvaloniaFact]
	public void Open_FirstTab_StartsInCurrentFolder()
	{
		// Act
		this.ShowAndWaitForPrompt();

		// Assert
		_ = this.window.View.Screen.WorkingDirectory.Should().Be(Directory.GetCurrentDirectory());
	}

	/// <summary>
	/// Checks that the tab is named after the folder the shell moves to.
	/// </summary>
	[AvaloniaFact]
	public void Cd_RenamesTabAfterFolder()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("sharpie-title-");

		try
		{
			this.ShowAndWaitForPrompt();

			// Act
			this.Enter($"cd {folder.FullName}");

			// Assert
			_ = this.window.Tabs[0].Title.Should().Be($"{folder.Name} · Sharpie");
		}
		finally
		{
			folder.Delete();
		}
	}

	/// <summary>
	/// Checks that quitting a page with the given key leaves the <c>man</c> line followed by exactly one empty prompt.
	/// </summary>
	/// <param name="key">The key that quits.</param>
	/// <param name="physical">Where the key sits on a QWERTY keyboard.</param>
	/// <param name="text">The text the key types, or <see langword="null" /> when it types none.</param>
	[AvaloniaTheory]
	[InlineData(Key.Q, PhysicalKey.Q, "q")]
	[InlineData(Key.Escape, PhysicalKey.Escape, null)]
	public void QuitManPage_LeavesOneEmptyPrompt(Key key, PhysicalKey physical, string? text)
	{
		// Arrange
		Assert.SkipWhen(PathFinder.FindExecutable("man") is null, "man isn't installed");
		this.ShowAndWaitForPrompt();

		// Act
		this.OpenAndQuit(key, physical, text);
		this.OpenAndQuit(key, physical, text);

		// Assert
		var screen = this.ScreenText;
		var after = screen[(screen.LastIndexOf("❯ man ls", StringComparison.Ordinal) + "❯ man ls".Length)..];

		_ = screen.Split("❯ man ls").Length.Should().Be(3, $"man ls was entered twice, and the screen ended with:\n{Tail(screen)}");
		_ = after.Count(character => character == '❯').Should().Be(1, $"the screen after the last man line was:\n{after}");
		_ = after.TrimEnd().Should().EndWith("❯", $"the new prompt should be empty, but the screen after the man line was:\n{after}");
		_ = this.window.View.ScrollOffset.Should().Be(0, "the prompt should be in view");
	}

	/// <summary>
	/// Checks that Up after entering a line brings that line back into the input.
	/// </summary>
	[AvaloniaFact]
	public void Up_AfterEnteringLine_RecallsIt()
	{
		// Arrange
		this.ShowAndWaitForPrompt();
		this.Enter("echo one");

		// Act
		this.Press(Key.Up, PhysicalKey.ArrowUp, null);
		WaitFor(() => this.ScreenText.EndsWith("❯ echo one", StringComparison.Ordinal), "Up should have recalled the line");

		// Assert
		_ = this.ScreenText.Should().EndWith("❯ echo one");
	}

	/// <summary>
	/// Checks that a program asking with <c>read -p</c>, as <c>cachy-update</c> does for <c>[Y/n]</c>, shows its prompt
	/// and gets the answer typed.
	/// </summary>
	[AvaloniaFact]
	public void ReadPrompt_ShowsPromptAndTakesAnswer()
	{
		// Arrange
		Assert.SkipWhen(PathFinder.FindExecutable("bash") is null, "bash isn't installed");
		this.ShowAndWaitForPrompt();

		// Act
		this.Type("bash -c 'read -p \"Proceed? [Y/n] \" a; echo answer=$a'");
		this.Press(Key.Enter, PhysicalKey.Enter, null);
		// The typed command holds the prompt text too, so only the end of the screen shows that bash printed it.
		WaitFor(() => this.ScreenText.TrimEnd().EndsWith("Proceed? [Y/n]", StringComparison.Ordinal), "the prompt should have shown");
		this.Enter("y");

		// Assert
		_ = this.ScreenText.Should().Contain("Proceed? [Y/n] y\nanswer=y");
	}

	/// <summary>
	/// Checks that what's typed at a password prompt, which turns the terminal's echo off, never shows on screen.
	/// </summary>
	[AvaloniaFact]
	public void PasswordPrompt_HidesWhatIsTyped()
	{
		// Arrange
		Assert.SkipWhen(PathFinder.FindExecutable("bash") is null, "bash isn't installed");
		this.ShowAndWaitForPrompt();

		// Act
		this.Type("bash -c 'read -s -p \"pw: \" a; echo; echo len=${#a}'");
		this.Press(Key.Enter, PhysicalKey.Enter, null);
		WaitFor(() => this.ScreenText.TrimEnd().EndsWith("pw:", StringComparison.Ordinal), "the password prompt should have shown");
		this.Enter("secret");

		// Assert
		_ = this.ScreenText.Should().Contain("len=6").And.NotContain("secret");
	}

	/// <summary>
	/// Checks that a click reaches a program that turned on mouse tracking, as an SGR report naming the button and
	/// the cell, the way htop and vim read clicks.
	/// </summary>
	[AvaloniaFact]
	public void MouseClick_WhenProgramTracksMouse_SendsReport()
	{
		// Arrange
		Assert.SkipWhen(PathFinder.FindExecutable("bash") is null, "bash isn't installed");
		this.ShowAndWaitForPrompt();
		this.Type("bash -c 'printf \"\\e[?1000h\\e[?1006hready\"; read -rsd M x; printf \"\\e[?1000l\"; echo; echo got:${x:1}M'");
		this.Press(Key.Enter, PhysicalKey.Enter, null);
		WaitFor(() => this.ScreenText.EndsWith("ready", StringComparison.Ordinal), "the program should have turned tracking on");

		// Act
		// The top-left cell sits inside the padding, past the title bar, so a point well into the view lands on a cell.
		var view = this.window.View;
		var point = view.TranslatePoint(new Point(TerminalView.Padding + 1, TerminalView.Padding + 1), this.window)!.Value;

		this.window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
		this.window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
		WaitFor(() => this.ScreenText.Contains("got:", StringComparison.Ordinal), "the program should have read the click");

		// Assert
		_ = this.ScreenText.Should().Contain("got:[<0;1;1M");
	}

	/// <summary>
	/// Checks that a right click sends a report to a program tracking the mouse, and no menu pops up over it.
	/// </summary>
	[AvaloniaFact]
	public void RightClick_WhenProgramTracksMouse_SendsReportNotMenu()
	{
		// Arrange
		Assert.SkipWhen(PathFinder.FindExecutable("bash") is null, "bash isn't installed");
		this.ShowAndWaitForPrompt();
		// Reads up to the release report's closing m, so tracking stays on until the release has been sent.
		this.Type("bash -c 'printf \"\\e[?1000h\\e[?1006hready\"; read -rsd m x; printf \"\\e[?1000l\"; echo; echo got:$x | tr -d \"\\033\"'");
		this.Press(Key.Enter, PhysicalKey.Enter, null);
		WaitFor(() => this.ScreenText.EndsWith("ready", StringComparison.Ordinal), "the program should have turned tracking on");

		// Act
		this.RightClick();
		WaitFor(() => this.ScreenText.Contains("got:", StringComparison.Ordinal), "the program should have read the click");

		// Assert
		_ = this.ScreenText.Should().Contain("got:[<2;1;1M[<2;1;1");
		_ = this.window.View.ContextMenu!.IsOpen.Should().BeFalse();
	}

	/// <summary>
	/// Checks that a right click opens the menu with Copy greyed out when nothing is selected.
	/// </summary>
	[AvaloniaFact]
	public void RightClick_WithNoSelection_OpensMenuWithCopyDisabled()
	{
		// Arrange
		this.ShowAndWaitForPrompt();

		// Act
		this.RightClick();

		// Assert
		_ = this.window.View.ContextMenu!.IsOpen.Should().BeTrue();
		_ = this.MenuItem("Copy").IsEnabled.Should().BeFalse();
		_ = this.MenuItem("Paste").IsEnabled.Should().BeTrue();
	}

	/// <summary>
	/// Checks that Copy is offered after dragging out a selection, so selecting and right clicking copies.
	/// </summary>
	[AvaloniaFact]
	public void RightClick_WithSelection_OpensMenuWithCopyEnabled()
	{
		// Arrange
		this.ShowAndWaitForPrompt();

		var view = this.window.View;
		var from = view.TranslatePoint(new Point(TerminalView.Padding + 1, TerminalView.Padding + 1), this.window)!.Value;
		var to = view.TranslatePoint(new Point(TerminalView.Padding + 60, TerminalView.Padding + 1), this.window)!.Value;

		this.window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
		this.window.MouseMove(to, RawInputModifiers.LeftMouseButton);
		this.window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);

		// Act
		this.RightClick();

		// Assert
		_ = this.MenuItem("Copy").IsEnabled.Should().BeTrue();
	}

	/// <summary>
	/// Checks that one wheel event with an enormous delta scrolls at most a screen's worth and returns at once,
	/// instead of looping once per notch and freezing the window.
	/// </summary>
	[AvaloniaFact]
	public void MouseWheel_WithHugeDelta_ReturnsQuickly()
	{
		// Arrange
		this.ShowAndWaitForPrompt();

		var view = this.window.View;
		var point = view.TranslatePoint(new Point(TerminalView.Padding + 1, TerminalView.Padding + 1), this.window)!.Value;
		var clock = Stopwatch.StartNew();

		// Act
		this.window.MouseWheel(point, new Vector(0, 1e9), RawInputModifiers.None);

		// Assert
		_ = clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
	}

	/// <summary>
	/// Checks that when everything fits, as the fastfetch banner and first prompt do in the real window, nothing has
	/// scrolled off the top.
	/// </summary>
	[AvaloniaFact]
	public void Startup_BannerFits_ShowsFromTop()
	{
		// Arrange
		// Taller than the real window's 840, because the headless platform's stand-in font has taller lines than the
		// real one, and the banner has to fit for this case.
		using var window = new TerminalWindow(useSavedHistory: false) { Height = 1600 };

		try
		{
			// Act
			window.Show();
			WaitFor(() => window.View.Screen.GetText().Contains('❯', StringComparison.Ordinal), "the first prompt should have shown");
			RunFor(TimeSpan.FromMilliseconds(500));

			// Assert
			_ = window.View.Screen.ScrollbackCount.Should().Be(0, "the banner should show from its first line");
		}
		finally
		{
			window.Close();
		}
	}

	/// <summary>
	/// Checks that Ctrl+T opens a second tab, shows the tab strip, and starts the new shell in the first tab's folder.
	/// </summary>
	[AvaloniaFact]
	public void CtrlT_OpensTabInActiveTabsFolder()
	{
		// Arrange
		var folder = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

		this.ShowAndWaitForPrompt();
		this.Enter($"cd '{folder}'");

		// Act
		this.OpenSecondTab();

		// Assert
		_ = this.window.Tabs.Should().HaveCount(2);
		_ = this.window.View.Should().BeSameAs(this.window.Tabs[1].View);
		_ = this.Strip.IsVisible.Should().BeTrue();
		_ = this.window.View.Screen.WorkingDirectory.Should().Be(folder);
	}

	/// <summary>
	/// Checks that <c>exit</c> in the second tab closes just that tab, leaving the window open on the first.
	/// </summary>
	[AvaloniaFact]
	public void Exit_InSecondTab_ClosesOnlyThatTab()
	{
		// Arrange
		this.ShowAndWaitForPrompt();
		this.OpenSecondTab();

		var first = this.window.Tabs[0];

		// Act
		this.Type("exit");
		this.Press(Key.Enter, PhysicalKey.Enter, null);
		WaitFor(() => this.window.Tabs.Count == 1, "the second tab should have closed");

		// Assert
		_ = this.window.IsVisible.Should().BeTrue();
		_ = this.window.View.Should().BeSameAs(first.View);
		_ = this.Strip.IsVisible.Should().BeFalse();
	}

	/// <summary>
	/// Checks that Ctrl+Shift+W closes the active tab and shows the one left.
	/// </summary>
	[AvaloniaFact]
	public void CtrlShiftW_ClosesActiveTab()
	{
		// Arrange
		this.ShowAndWaitForPrompt();
		this.OpenSecondTab();

		var first = this.window.Tabs[0];

		// Act
		this.Press(Key.W, PhysicalKey.W, null, RawInputModifiers.Control | RawInputModifiers.Shift);

		// Assert
		_ = this.window.Tabs.Should().ContainSingle().Which.Should().BeSameAs(first);
		_ = this.window.View.Should().BeSameAs(first.View);
	}

	/// <summary>
	/// Checks that each tab key, pressed on the second of two tabs, goes to the first.
	/// </summary>
	/// <param name="key">The key.</param>
	/// <param name="physical">Where the key sits on a QWERTY keyboard.</param>
	/// <param name="modifiers">The modifier keys held.</param>
	[AvaloniaTheory]
	[InlineData(Key.Tab, PhysicalKey.Tab, RawInputModifiers.Control)]
	[InlineData(Key.Tab, PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift)]
	[InlineData(Key.D1, PhysicalKey.Digit1, RawInputModifiers.Alt)]
	public void TabKey_OnSecondOfTwoTabs_SelectsFirst(Key key, PhysicalKey physical, RawInputModifiers modifiers)
	{
		// Arrange
		this.ShowAndWaitForPrompt();
		this.OpenSecondTab();

		// Act
		this.Press(key, physical, null, modifiers);

		// Assert
		_ = this.window.View.Should().BeSameAs(this.window.Tabs[0].View);
		_ = this.window.Tabs[1].IsVisible.Should().BeFalse();
	}

	// Presses Ctrl+T and waits for the new tab's first prompt.
	private void OpenSecondTab()
	{
		this.Press(Key.T, PhysicalKey.T, null, RawInputModifiers.Control);
		WaitFor(() => this.window.Tabs.Count == 2 && this.CountPrompts() > 0, "the second tab's prompt should have shown");
	}

	// Types man ls, waits for the page, quits it, and waits for the screen to come back with the shell's next prompt.
	private void OpenAndQuit(Key key, PhysicalKey physical, string? text)
	{
		var prompts = this.CountPrompts();

		this.Type("man ls");
		this.Press(Key.Enter, PhysicalKey.Enter, null);
		WaitFor(() => this.Pager.IsVisible, "the man page should have opened");

		this.Press(key, physical, text);
		WaitFor(() => !this.Pager.IsVisible && this.CountPrompts() > prompts, "the prompt should have come back");
		RunFor(TimeSpan.FromMilliseconds(500));
	}

	private static string Tail(string screen) => screen[Math.Max(0, screen.Length - 300)..];

	/// <inheritdoc />
	public void Dispose()
	{
		this.window.Close();
		this.window.Dispose();
	}

	private string ScreenText => this.window.View.Screen.GetText();

	private TabStrip Strip => this.window.GetVisualDescendants().OfType<TabStrip>().First();

	private ManPager Pager => this.window.GetVisualDescendants().OfType<ManPager>().First();

	private int CountPrompts() => this.ScreenText.Count(character => character == '❯');

	private void ShowAndWaitForPrompt()
	{
		this.window.Show();
		WaitFor(() => this.CountPrompts() > 0, "the first prompt should have shown");
	}

	// Types a line, presses Enter, and waits for the next prompt.
	private void Enter(string line)
	{
		var prompts = this.CountPrompts();

		this.Type(line);
		this.Press(Key.Enter, PhysicalKey.Enter, null);
		WaitFor(() => this.CountPrompts() > prompts, $"a new prompt should have shown after {line}");
	}

	private MenuItem MenuItem(string header) =>
		this.window.View.ContextMenu!.Items.OfType<MenuItem>().First(item => (string?)item.Header == header);

	// Clicks the right button on the top-left cell, which sits inside the padding past the title bar.
	private void RightClick()
	{
		var view = this.window.View;
		var point = view.TranslatePoint(new Point(TerminalView.Padding + 1, TerminalView.Padding + 1), this.window)!.Value;

		this.window.MouseDown(point, MouseButton.Right, RawInputModifiers.None);
		this.window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
	}

	private void Type(string text)
	{
		this.window.KeyTextInput(text);
		Dispatcher.UIThread.RunJobs();
	}

	private void Press(Key key, PhysicalKey physical, string? text, RawInputModifiers modifiers = RawInputModifiers.None)
	{
		this.window.KeyPress(key, modifiers, physical, text);

		if (text is not null)
		{
			this.window.KeyTextInput(text);
		}

		this.window.KeyRelease(key, modifiers, physical, text);
		Dispatcher.UIThread.RunJobs();
	}

	private static void WaitFor(Func<bool> condition, string what)
	{
		var clock = Stopwatch.StartNew();

		while (!condition())
		{
			_ = clock.Elapsed.Should().BeLessThan(Timeout, $"{what} by now");
			Dispatcher.UIThread.RunJobs();
			Thread.Sleep(20);
		}
	}

	private static void RunFor(TimeSpan duration)
	{
		var clock = Stopwatch.StartNew();

		while (clock.Elapsed < duration)
		{
			Dispatcher.UIThread.RunJobs();
			Thread.Sleep(20);
		}
	}
}
