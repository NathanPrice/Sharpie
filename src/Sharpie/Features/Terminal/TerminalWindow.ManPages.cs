using Sharpie.Features.ManPages;
using Sharpie.Shell.Common;

namespace Sharpie.Features.Terminal;

/// <content>
/// Shows the man pages the shell hands over in the window's own <see cref="ManPager" /> reader.
/// </content>
internal sealed partial class TerminalWindow
{
	/// <summary>
	/// Opens the page for a <c>man</c> line the shell sent instead of running it.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="line">The command line, like <c>man ls</c>.</param>
	/// <remarks>
	/// The shell only sends lines whose page <c>man -w</c> found, and has already printed its next prompt, so a page
	/// that still fails to load just leaves the prompt showing.
	/// </remarks>
	private void OnManPageRequested(object? sender, string line)
	{
		if (CommandLine.ManArguments(line) is { } arguments)
		{
			_ = this.OpenManPageAsync(arguments);
		}
	}

	/// <summary>
	/// Runs <c>man</c> and opens its page in the <see cref="ManPager" />.
	/// </summary>
	/// <param name="arguments">The arguments for <c>man</c>.</param>
	/// <returns>
	/// A task that completes once the page is open, or <c>man</c> printed nothing.
	/// </returns>
	private async Task OpenManPageAsync(string[] arguments)
	{
		// One column short, since a line that exactly fills the reader gets a horizontal scrollbar. At least 20 so a
		// tiny window still gets a readable page.
		var columns = Math.Max(20, this.view.Screen.Columns - 1);
		var directory = this.view.Screen.WorkingDirectory;
		var output = await Task.Run(() => ManPageReader.Read(arguments, columns, directory)).ConfigureAwait(true);

		if (output is null)
		{
			return;
		}

		this.view.IsVisible = false;
		this.scrollBar.IsVisible = false;
		this.pager.Open(arguments[^1], output);
	}

	/// <summary>
	/// Shows the terminal again after the user quits a man page.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnPagerClosed(object? sender, EventArgs e)
	{
		this.view.IsVisible = true;
		this.OnViewScrolled(this, EventArgs.Empty);
		_ = this.view.Focus();
	}
}
