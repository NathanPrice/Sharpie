using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FluentAssertions;
using Sharpie.Features.Terminal;

namespace Sharpie.Tests.Features.Terminal;

/// <summary>
/// Drives a <see cref="TerminalView" /> in a headless window, checking what Ctrl+V and dropped files send to the
/// program.
/// </summary>
public sealed class TerminalViewInputTests : IDisposable
{
	// Everything the view sent to the program, as one run of bytes.
	private readonly List<byte> sent = [];
	private readonly TerminalView view;
	private readonly Window window;

	/// <summary>
	/// Initializes a new instance of the <see cref="TerminalViewInputTests" /> class.
	/// </summary>
	public TerminalViewInputTests()
	{
		this.view = new TerminalView(this.sent.AddRange);
		this.window = new Window { Content = this.view, Width = 400, Height = 300 };
		this.window.Show();
		_ = this.view.Focus();
		Dispatcher.UIThread.RunJobs();
	}

	/// <summary>
	/// Checks that Ctrl+V with an image on the clipboard sends the Ctrl+V key, so the program can read the image
	/// itself, instead of the image's bytes.
	/// </summary>
	/// <returns>
	/// A task that completes when the test is done.
	/// </returns>
	[AvaloniaFact]
	public async Task CtrlV_WithImageOnClipboard_SendsCtrlVKey()
	{
		// Arrange
		using var image = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

		await this.window.Clipboard!.SetValueAsync(DataFormat.Bitmap, image).ConfigureAwait(true);

		// Act
		this.Press(RawInputModifiers.Control);

		// Assert
		_ = this.sent.Should().Equal(0x16);
	}

	/// <summary>
	/// Checks that the menu's Paste with an image on the clipboard sends the Ctrl+V key, as Ctrl+V does.
	/// </summary>
	/// <returns>
	/// A task that completes when the test is done.
	/// </returns>
	[AvaloniaFact]
	public async Task MenuPaste_WithImageOnClipboard_SendsCtrlVKey()
	{
		// Arrange
		using var image = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

		await this.window.Clipboard!.SetValueAsync(DataFormat.Bitmap, image).ConfigureAwait(true);

		var paste = this.view.ContextMenu!.Items.OfType<MenuItem>().Single(item => (string?)item.Header == "Paste");

		// Act
		paste.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		Dispatcher.UIThread.RunJobs();

		// Assert
		_ = this.sent.Should().Equal(0x16);
	}

	/// <summary>
	/// Checks that Ctrl+V with text on the clipboard pastes the text, both kinds of line break sent as Enter.
	/// </summary>
	/// <returns>
	/// A task that completes when the test is done.
	/// </returns>
	[AvaloniaFact]
	public async Task CtrlV_WithTextOnClipboard_PastesText()
	{
		// Arrange
		await this.window.Clipboard!.SetTextAsync("a\r\nb\nc").ConfigureAwait(true);

		// Act
		this.Press(RawInputModifiers.Control);

		// Assert
		_ = this.sent.Should().Equal("a\rb\rc"u8.ToArray());
	}

	/// <summary>
	/// Checks that Ctrl+Shift+V, which only pastes text, sends nothing when the clipboard holds only an image.
	/// </summary>
	/// <returns>
	/// A task that completes when the test is done.
	/// </returns>
	[AvaloniaFact]
	public async Task CtrlShiftV_WithImageOnly_SendsNothing()
	{
		// Arrange
		using var image = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

		await this.window.Clipboard!.SetValueAsync(DataFormat.Bitmap, image).ConfigureAwait(true);

		// Act
		this.Press(RawInputModifiers.Control | RawInputModifiers.Shift);

		// Assert
		_ = this.sent.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a paste is bracketed when the program asked for that, with the end marker taken out of the text so
	/// the paste can't end the bracket early.
	/// </summary>
	/// <returns>
	/// A task that completes when the test is done.
	/// </returns>
	[AvaloniaFact]
	public async Task CtrlV_WithBracketedPaste_BracketsTextWithoutEndMarker()
	{
		// Arrange
		this.view.Feed("\e[?2004h");
		await this.window.Clipboard!.SetTextAsync("x\e[201~y").ConfigureAwait(true);

		// Act
		this.Press(RawInputModifiers.Control);

		// Assert
		_ = this.sent.Should().Equal("\e[200~xy\e[201~"u8.ToArray());
	}

	/// <summary>
	/// Checks that Ctrl+Shift+V pastes the text when the clipboard holds an image and text, instead of sending the
	/// Ctrl+V key as plain Ctrl+V does.
	/// </summary>
	/// <returns>
	/// A task that completes when the test is done.
	/// </returns>
	[AvaloniaFact]
	public async Task CtrlShiftV_WithImageAndText_PastesText()
	{
		// Arrange
		using var image = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
		using var data = new DataTransfer();
		var item = new DataTransferItem();

		item.SetBitmap(image);
		item.SetText("caption");
		data.Add(item);
		await this.window.Clipboard!.SetDataAsync(data).ConfigureAwait(true);

		// Act
		this.Press(RawInputModifiers.Control | RawInputModifiers.Shift);

		// Assert
		_ = this.sent.Should().Equal("caption"u8.ToArray());
	}

	/// <summary>
	/// Checks that V without Ctrl types a v rather than pasting.
	/// </summary>
	/// <returns>
	/// A task that completes when the test is done.
	/// </returns>
	[AvaloniaFact]
	public async Task V_WithoutCtrl_TypesV()
	{
		// Arrange
		await this.window.Clipboard!.SetTextAsync("pasted").ConfigureAwait(true);

		// Act
		this.window.KeyPress(Key.V, RawInputModifiers.None, PhysicalKey.V, "v");
		this.window.KeyTextInput("v");
		Dispatcher.UIThread.RunJobs();

		// Assert
		_ = this.sent.Should().Equal("v"u8.ToArray());
	}

	/// <summary>
	/// Checks that dragging files over the view shows they can be dropped, and dragging only text shows it can't.
	/// </summary>
	/// <param name="withFile">Whether the dragged data holds a file.</param>
	/// <param name="expected">The effect the view should show.</param>
	/// <returns>
	/// A task that completes when the test is done.
	/// </returns>
	[AvaloniaTheory]
	[InlineData(true, DragDropEffects.Copy)]
	[InlineData(false, DragDropEffects.None)]
	public async Task DragOver_ShowsWhetherDataCanBeDropped(bool withFile, DragDropEffects expected)
	{
		// Arrange
		using var data = new DataTransfer();

		data.Add(withFile
			? DataTransferItem.CreateFile((await this.window.StorageProvider.TryGetFileFromPathAsync(new Uri(Environment.ProcessPath!)).ConfigureAwait(true))!)
			: DataTransferItem.CreateText("text"));

		var args = new DragEventArgs(DragDrop.DragOverEvent, data, this.view, default, KeyModifiers.None) { DragEffects = DragDropEffects.Copy | DragDropEffects.Move };

		// Act
		this.view.RaiseEvent(args);

		// Assert
		_ = args.DragEffects.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a drop holding no files sends nothing.
	/// </summary>
	/// <remarks>
	/// The drop is raised on the view directly, because a real drag of text only ends with no drop once
	/// <see cref="DragDrop.DragOverEvent" /> turns it down.
	/// </remarks>
	[AvaloniaFact]
	public void Drop_TextOnly_SendsNothing()
	{
		// Arrange
		using var data = new DataTransfer();

		data.Add(DataTransferItem.CreateText("text"));

		// Act
		this.view.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, this.view, default, KeyModifiers.None));

		// Assert
		_ = this.sent.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that dropping files pastes their paths, quoted only where a shell needs it, with a space after the last.
	/// </summary>
	/// <returns>
	/// A task that completes when the test is done.
	/// </returns>
	[AvaloniaFact]
	public async Task Drop_Files_PastesQuotedPathsWithTrailingSpace()
	{
		// Arrange
		Assert.SkipWhen(OperatingSystem.IsWindows(), "the expected paths are quoted the POSIX way");

		var folder = Directory.CreateTempSubdirectory("sharpie-drop-");

		try
		{
			var spaced = Path.Combine(folder.FullName, "sample shot.png");
			var plain = Path.Combine(folder.FullName, "notes.txt");
			using var data = new DataTransfer();

			foreach (var path in new[] { spaced, plain })
			{
				await File.WriteAllTextAsync(path, string.Empty).ConfigureAwait(true);
				data.Add(DataTransferItem.CreateFile((await this.window.StorageProvider.TryGetFileFromPathAsync(new Uri(path)).ConfigureAwait(true))!));
			}

			// Act
			var point = new Point(100, 100);

			this.window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
			this.window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
			this.window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
			Dispatcher.UIThread.RunJobs();

			// Assert
			_ = this.sent.Should().Equal(Encoding.UTF8.GetBytes($"'{spaced}' {plain} "));
		}
		finally
		{
			folder.Delete(recursive: true);
		}
	}

	/// <inheritdoc />
	public void Dispose() => this.window.Close();

	/// <summary>
	/// Presses V with the given modifiers and lets the paste that follows finish.
	/// </summary>
	/// <param name="modifiers">The modifier keys held.</param>
	private void Press(RawInputModifiers modifiers)
	{
		this.window.KeyPress(Key.V, modifiers, PhysicalKey.V, null);
		Dispatcher.UIThread.RunJobs();
	}
}
