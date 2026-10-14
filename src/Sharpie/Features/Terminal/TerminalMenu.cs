using Avalonia.Controls;
using Avalonia.Input;

namespace Sharpie.Features.Terminal;

/// <summary>
/// Builds the menu a right click opens over a <see cref="TerminalView" />.
/// </summary>
internal static class TerminalMenu
{
	/// <summary>
	/// Creates the Copy and Paste menu for a view.
	/// </summary>
	/// <param name="view">The view the menu copies from and pastes into.</param>
	/// <returns>
	/// The menu, with Copy greyed out each time it opens with nothing selected.
	/// </returns>
	public static ContextMenu Create(TerminalView view)
	{
		var copy = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift) };
		var paste = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift) };
		var menu = new ContextMenu { ItemsSource = new[] { copy, paste } };

		copy.Click += (_, _) => _ = view.CopySelection();
		paste.Click += (_, _) => _ = view.PasteAsync();
		menu.Opening += (_, _) => copy.IsEnabled = view.SelectedText() is not null;

		return menu;
	}
}
