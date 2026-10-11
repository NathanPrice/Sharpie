using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using FluentAssertions;
using Sharpie.Features.Ansi;
using Sharpie.Features.ManPages;

namespace Sharpie.Tests.Features.ManPages;

/// <summary>
/// Checks which text <see cref="ManPager" /> paints in which style, by giving it a paint function that marks each style
/// with a color of its own and reading the colors back from the laid out lines.
/// </summary>
public sealed class ManPagerDrawingTests
{
	// Red 1 marks a painted element. Green is the style's index in this list, so it can be read back.
	private readonly List<AnsiStyle> styles = [];
	private readonly Window window;
	private readonly ManPager pager;

	/// <summary>
	/// Initializes a new instance of the <see cref="ManPagerDrawingTests" /> class with the pager shown in a window.
	/// </summary>
	public ManPagerDrawingTests()
	{
		this.pager = new ManPager(FontFamily.Default, 15, Brushes.White, Brushes.Gray, Brushes.Blue, this.Paint);
		this.window = new Window { Width = 800, Height = 400, Content = this.pager };
		this.window.Show();
	}

	/// <summary>
	/// Checks that each styled run is painted over exactly its own text, including a run that crosses a line break, and
	/// that a link is painted in cyan and underlined.
	/// </summary>
	[AvaloniaFact]
	public void Open_StyledRunsAndLink_PaintsExactlyTheirText()
	{
		// Act
		var painted = this.PaintedAfterOpen("plain \e[1mbold\e[0m mid\n\e[4mtwo\nlines\e[0m end\nsee \e]8;;https://a.test\e\\link\e]8;;\e\\ tail\n");

		// Assert
		var bold = new AnsiStyle(null, null, Bold: true);
		var underline = new AnsiStyle(null, null, Bold: false, Underline: true);
		var link = new AnsiStyle(new AnsiColor(6, 0), null, Bold: false);

		_ = painted.Should().Equal(("bold", bold, false), ("two", underline, false), ("lines", underline, false), ("link", link, true));
	}

	/// <summary>
	/// Checks that every one of many runs on many lines is painted, so none is skipped when looking up the first run a
	/// line reaches.
	/// </summary>
	[AvaloniaFact]
	public void Open_ManyRuns_PaintsEveryOne()
	{
		// Act
		var painted = this.PaintedAfterOpen(string.Concat(Enumerable.Range(0, 9).Select(index => $"x \e[1mw{index}\e[0m y \e[1mv{index}\e[0m\n")));

		// Assert
		_ = painted.Select(each => each.Text).Should().Equal(Enumerable.Range(0, 9).SelectMany(index => (string[])[$"w{index}", $"v{index}"]));
	}

	/// <summary>
	/// Checks that opening a plain page after a styled one paints nothing left over from the first.
	/// </summary>
	[AvaloniaFact]
	public void Open_PlainAfterStyled_PaintsNothing()
	{
		// Arrange
		_ = this.PaintedAfterOpen("\e[1mbold\e[0m \e]8;;https://a.test\e\\link\e]8;;\e\\\n");

		// Act
		var painted = this.PaintedAfterOpen("bold link\n");

		// Assert
		_ = painted.Should().BeEmpty();
	}

	/// <summary>
	/// Marks an element with the color that stands for <paramref name="style" />.
	/// </summary>
	/// <param name="style">The style the pager paints.</param>
	/// <returns>
	/// The action that colors one element.
	/// </returns>
	private Action<VisualLineElement> Paint(AnsiStyle style)
	{
		if (!this.styles.Contains(style))
		{
			this.styles.Add(style);
		}

		var index = (byte)this.styles.IndexOf(style);

		return element => element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(Color.FromRgb(1, index, 0)));
	}

	/// <summary>
	/// Opens a page, lays it out, and reads back what was painted.
	/// </summary>
	/// <param name="output">The page, with its escape sequences.</param>
	/// <returns>
	/// Each painted piece of text in order, with its style and whether it's underlined.
	/// </returns>
	private List<(string Text, AnsiStyle Style, bool Underlined)> PaintedAfterOpen(string output)
	{
		this.pager.Open("page", output);
		Dispatcher.UIThread.RunJobs();

		var editor = this.pager.GetVisualDescendants().OfType<TextEditor>().Single();
		var textView = editor.TextArea.TextView;
		var painted = new List<(string, AnsiStyle, bool)>();

		textView.EnsureVisualLines();

		foreach (var line in textView.VisualLines)
		{
			foreach (var element in line.Elements)
			{
				if (element.TextRunProperties.ForegroundBrush is ISolidColorBrush { Color.R: 1 } brush)
				{
					var offset = line.FirstDocumentLine.Offset + element.RelativeTextOffset;

					painted.Add((editor.Document.GetText(offset, element.DocumentLength), this.styles[brush.Color.G], element.TextRunProperties.TextDecorations is { Count: > 0 }));
				}
			}
		}

		return painted;
	}
}
