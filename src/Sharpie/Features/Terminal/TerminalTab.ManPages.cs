using Avalonia.Controls;
using Sharpie.Features.ManPages;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Features.Terminal;

/// <content>
/// Shows the man pages the shell hands over in the window's own <see cref="ManPager" /> reader.
/// </content>
internal sealed partial class TerminalTab
{
	/// <summary>
	/// Opens the page for a <c>man</c> or <c>dman</c> line the shell sent instead of running it.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="line">The command line, like <c>man ls</c> or <c>dman String.Split</c>.</param>
	/// <remarks>
	/// The shell only sends lines whose page <c>man -w</c> or <c>dman</c> found, and has already printed its next prompt, so a page
	/// that still fails to load just leaves the prompt showing.
	/// </remarks>
	private void OnManPageRequested(object? sender, string line)
	{
		if (CommandLine.PagedCommand(line) is { } words)
		{
			_ = this.OpenManPageAsync(words);
		}
	}

	/// <summary>
	/// Builds the page with <c>man</c> or <see cref="DmanCommand" /> and opens it in the <see cref="ManPager" />.
	/// </summary>
	/// <param name="words">The command line's words, starting with <c>man</c> or <c>dman</c>.</param>
	/// <returns>
	/// A task that completes once the page is open, or there turned out to be no page.
	/// </returns>
	private async Task OpenManPageAsync(string[] words)
	{
		// One column short, since a line that exactly fills the reader gets a horizontal scrollbar. At least 20 so a
		// tiny window still gets a readable page.
		var columns = Math.Max(20, this.view.Screen.Columns - 1);
		var directory = this.view.Screen.WorkingDirectory;
		var arguments = words[1..];
		var output = await Task.Run(() => words[0] == "dman"
			? DmanCommand.Render(arguments, columns)
			: ManPageReader.Read(arguments, columns, directory)).ConfigureAwait(true);

		if (output is null)
		{
			return;
		}

		this.view.IsVisible = false;
		this.scrollBar.IsVisible = false;
		this.pager.Open(arguments[^1], output);
	}

	/// <summary>
	/// Opens a link the user followed in a page.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="url">The link's address.</param>
	private void OnPagerLinkClicked(object? sender, string url)
	{
		_ = this.FollowLinkAsync(url);
	}

	/// <summary>
	/// Shows a link's page in the reader when dman has it, such as an API or a C# article on learn.microsoft.com, and
	/// opens any other web address in the browser.
	/// </summary>
	/// <param name="url">The link's address.</param>
	/// <returns>
	/// A task that completes once the page is showing or the browser was asked to open it.
	/// </returns>
	/// <remarks>
	/// Only http and https addresses go to the browser. A page's links come from downloaded docs, and handing the
	/// system a <c>file:</c> or other address from them could start a program.
	/// </remarks>
	private async Task FollowLinkAsync(string url)
	{
		var columns = Math.Max(20, this.view.Screen.Columns - 1);
		var page = await Task.Run(() => DmanCommand.Render([url], columns)).ConfigureAwait(true);

		if (page is not null)
		{
			// The header line starts with the page name, like String.Split(3net).
			var parenthesis = page.IndexOf('(', StringComparison.Ordinal);

			this.pager.Navigate(parenthesis > 0 ? page[..parenthesis] : url, page);

			return;
		}

		if (Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme is "https" or "http"
			&& TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
		{
			_ = await launcher.LaunchUriAsync(address).ConfigureAwait(true);
		}
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
