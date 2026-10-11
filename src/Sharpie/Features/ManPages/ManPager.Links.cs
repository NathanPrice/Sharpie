using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Sharpie.Features.Ansi;

namespace Sharpie.Features.ManPages;

/// <content>
/// Follows the links in a page, by mouse or keyboard, and goes back through the pages followed.
/// </content>
internal sealed partial class ManPager
{
	/// <summary>
	/// The pointer over a link. Made once, since a cursor holds a native resource.
	/// </summary>
	internal static readonly Cursor HandCursor = new(StandardCursorType.Hand);

	/// <summary>
	/// The pointer everywhere else on the page, the plain arrow rather than a text cursor, since the page is read,
	/// not edited.
	/// </summary>
	internal static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

	// The links in the page showing, in text order.
	private readonly List<AnsiLink> links = [];

	// The pages followed away from, newest on top, each with where it was scrolled to.
	private readonly Stack<(string Title, string Output, double Offset)> history = new();

	// The page showing as it was given, so it can be shown again after going back.
	private string output = string.Empty;

	/// <summary>
	/// Occurs when the user follows a link, with the link's address.
	/// </summary>
	public event EventHandler<string>? LinkClicked;

	/// <summary>
	/// Shows a page that a link led to, keeping the current one to go back to.
	/// </summary>
	/// <param name="title">The page name for the status line.</param>
	/// <param name="output">The page, with its overstrike, color codes and links.</param>
	public void Navigate(string title, string output)
	{
		this.history.Push((this.title, this.output, this.Scroller?.Offset.Y ?? 0));
		this.Show(title, output);
	}

	/// <summary>
	/// Goes back to the page the last link was followed from, where it was scrolled to. Does nothing on the first page.
	/// </summary>
	private void Back()
	{
		if (!this.history.TryPop(out var previous))
		{
			return;
		}

		this.Show(previous.Title, previous.Output);

		// After layout, since the new text has no height to scroll through until then.
		Dispatcher.UIThread.Post(
			() =>
			{
				if (this.Scroller is { } scroller)
				{
					scroller.Offset = new Vector(scroller.Offset.X, previous.Offset);
				}
			},
			DispatcherPriority.Loaded);
	}

	/// <summary>
	/// Selects the next or previous link from the selection, wrapping around the ends, and scrolls to it, so links can
	/// be reached without a mouse.
	/// </summary>
	/// <param name="forward"><see langword="true" /> for the next link; <see langword="false" /> for the previous one.</param>
	private void SelectLink(bool forward)
	{
		if (this.links.Count == 0)
		{
			return;
		}

		var start = this.view.SelectionLength > 0 ? this.view.SelectionStart : this.TopOffset() - (forward ? 1 : 0);
		var link = forward
			? this.links.FirstOrDefault(each => each.Start > start, this.links[0])
			: this.links.LastOrDefault(each => each.Start < start, this.links[^1]);

		this.view.Select(link.Start, link.Length);
		this.ScrollTo(link.Start);
	}

	/// <summary>
	/// Follows the link that's selected, for Enter after Tab.
	/// </summary>
	/// <returns>
	/// <see langword="true" /> when the selection was a link; otherwise, <see langword="false" />, so Enter scrolls as
	/// usual.
	/// </returns>
	private bool FollowSelectedLink()
	{
		var start = this.view.SelectionStart;
		var length = this.view.SelectionLength;
		var link = this.links.FirstOrDefault(each => each.Start == start && each.Length == length);

		if (length == 0 || link.Uri is null)
		{
			return false;
		}

		this.LinkClicked?.Invoke(this, link.Uri);

		return true;
	}

	/// <summary>
	/// Follows a link clicked with the left button. A press that dragged out a selection is a copy, not a click.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	/// <remarks>
	/// The release is left unhandled and the link followed only after it: AvaloniaEdit has to see the button come up
	/// to end its drag-select. When this took the release first, every later mouse move went on selecting text.
	/// </remarks>
	private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (e.InitialPressMouseButton != MouseButton.Left || this.view.SelectionLength > 0 || this.LinkAt(e) is not { } link)
		{
			return;
		}

		Dispatcher.UIThread.Post(() => this.LinkClicked?.Invoke(this, link.Uri));
	}

	/// <summary>
	/// Shows a hand over a link, so it reads as clickable.
	/// </summary>
	/// <param name="sender">The source of the event.</param>
	/// <param name="e">The data for the event.</param>
	private void OnPointerMoved(object? sender, PointerEventArgs e)
	{
		this.view.TextArea.TextView.Cursor = this.LinkAt(e) is null ? ArrowCursor : HandCursor;
	}

	/// <summary>
	/// Finds the link under the pointer.
	/// </summary>
	/// <param name="e">The pointer event.</param>
	/// <returns>
	/// The link, or <see langword="null" /> when the pointer isn't over one.
	/// </returns>
	private AnsiLink? LinkAt(PointerEventArgs e)
	{
		if (this.links.Count == 0 || this.view.GetPositionFromPoint(e.GetPosition(this.view)) is not { } position)
		{
			return null;
		}

		var offset = this.view.Document.GetOffset(position.Location);

		foreach (var link in this.links)
		{
			if (offset >= link.Start && offset < link.Start + link.Length)
			{
				return link;
			}
		}

		return null;
	}
}
