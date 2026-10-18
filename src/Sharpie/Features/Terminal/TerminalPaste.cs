using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Sharpie.Shell.Common;

namespace Sharpie.Features.Terminal;

/// <summary>
/// Pastes the clipboard and dropped files into a <see cref="TerminalView" />.
/// </summary>
internal static class TerminalPaste
{
	/// <summary>
	/// Sends the clipboard's text to a view's program, or the Ctrl+V key when the clipboard holds an image.
	/// </summary>
	/// <param name="view">The view to paste into.</param>
	/// <param name="key">
	/// The encoded Ctrl+V key when Ctrl+V or the menu asked for the paste; <see langword="null" /> for Ctrl+Shift+V,
	/// which only ever pastes text.
	/// </param>
	/// <returns>
	/// A task that completes once the text or the key is sent, or nothing is.
	/// </returns>
	/// <remarks>
	/// A program that takes images reads the clipboard itself when it gets Ctrl+V, as it does in Konsole and kitty.
	/// Text is read only when the clipboard offers it, because Avalonia's X11 clipboard asks for text anyway and KDE
	/// answers with the image's bytes, which would be typed into the program.
	/// </remarks>
	public static async Task FromClipboardAsync(TerminalView view, string? key)
	{
		if (TopLevel.GetTopLevel(view)?.Clipboard is not { } clipboard)
		{
			return;
		}

		var formats = await clipboard.GetDataFormatsAsync().ConfigureAwait(true);

		if (key is not null && formats.Contains(DataFormat.Bitmap))
		{
			view.SendText(key);
		}
		else if (formats.Contains(DataFormat.Text) && await clipboard.TryGetTextAsync().ConfigureAwait(true) is { Length: > 0 } text)
		{
			view.PasteText(text);
		}
	}

	/// <summary>
	/// Makes a view take dropped files and paste their paths, the way other terminals do.
	/// </summary>
	/// <param name="view">The view the paths are pasted into.</param>
	public static void AttachDrop(TerminalView view)
	{
		DragDrop.SetAllowDrop(view, true);

		// Only files are taken, so dragging anything else shows it can't be dropped.
		view.AddHandler(DragDrop.DragOverEvent, (_, e) =>
			e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);

		view.AddHandler(DragDrop.DropEvent, (_, e) =>
		{
			if (PathsText(e.DataTransfer) is { } text)
			{
				view.PasteText(text);
				_ = view.Focus();
			}
		});
	}

	/// <summary>
	/// Gets the text a drop pastes.
	/// </summary>
	/// <param name="data">The dropped data.</param>
	/// <returns>
	/// The local paths of the dropped files, each quoted where a shell needs it, joined with spaces and ending in one
	/// so the next argument can be typed straight after; <see langword="null" /> when no file has a local path.
	/// </returns>
	private static string? PathsText(IDataTransfer data)
	{
		var paths = data.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>().Select(CommandLine.QuoteIfNeeded).ToList();

		return paths is { Count: > 0 } ? string.Join(' ', paths) + ' ' : null;
	}
}
