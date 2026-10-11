using System.Globalization;
using System.Text;

namespace Sharpie.Shell.Features.DotNetDocs;

/// <summary>
/// How a piece of man page text is drawn.
/// </summary>
internal enum SpanStyle
{
	/// <summary>
	/// Ordinary text.
	/// </summary>
	Plain,

	/// <summary>
	/// Bold, which man uses for headings, names and literal code.
	/// </summary>
	Bold,

	/// <summary>
	/// Underlined, which man uses for arguments and parameter names.
	/// </summary>
	Underline,

	/// <summary>
	/// A language keyword inside code.
	/// </summary>
	Keyword,

	/// <summary>
	/// A string or character literal inside code.
	/// </summary>
	String,

	/// <summary>
	/// A comment inside code.
	/// </summary>
	Comment,

	/// <summary>
	/// A number inside code.
	/// </summary>
	Number,

	/// <summary>
	/// The name of a type or type parameter inside code.
	/// </summary>
	Type,

	/// <summary>
	/// The name of a method inside code.
	/// </summary>
	Method,

	/// <summary>
	/// The name of a parameter inside code.
	/// </summary>
	Parameter,

	/// <summary>
	/// The name of a local variable inside code.
	/// </summary>
	Variable,
}

/// <summary>
/// A run of man page text in one style.
/// </summary>
/// <param name="Text">The text.</param>
/// <param name="Style">How the text is drawn.</param>
/// <param name="Link">The address the text links to, or <see langword="null" /> when it isn't a link.</param>
internal readonly record struct Span(string Text, SpanStyle Style, string? Link = null);

/// <summary>
/// Lays text out the way <c>man</c> prints a page: a header and footer line, section names at the left edge, body text
/// indented and wrapped to the width, bold and underline written as overstrike, and code colored with standard color
/// codes.
/// </summary>
/// <remarks>
/// Overstrike (<c>X\bX</c> for bold, <c>_\bX</c> for underline) is what groff hands to a pager, so <c>less</c> draws
/// these pages like any other, and the window's reader colors them the way it colors <c>man ls</c>.
/// </remarks>
/// <param name="width">The number of columns to fill.</param>
/// <param name="styled"><see langword="true" /> to write bold, underline and colors; <see langword="false" /> for plain text.</param>
internal sealed class ManPageWriter(int width, bool styled)
{
	/// <summary>
	/// The column body text starts at, as in groff's man macros.
	/// </summary>
	public const int BodyIndent = 7;

	/// <summary>
	/// The column the text under a tag starts at, one indent step past <see cref="BodyIndent" />.
	/// </summary>
	public const int TaggedIndent = 14;

	/// <summary>
	/// How much further than its first line a wrapped signature's later lines start.
	/// </summary>
	private const int HangingIndent = 4;

	private readonly StringBuilder text = new();
	private bool startOfSection = true;

	/// <summary>
	/// Gets the number of columns the page fills.
	/// </summary>
	/// <value>
	/// At least 40, so a very narrow window still gets readable lines.
	/// </value>
	public int Width { get; } = Math.Max(40, width);

	/// <summary>
	/// Gets a value indicating whether the page shows styles, so callers can skip work, like coloring code, that a
	/// plain page would throw away.
	/// </summary>
	/// <value>
	/// <see langword="true" /> when bold, underline, colors and links are written.
	/// </value>
	public bool Styled => styled;

	/// <summary>
	/// Writes the line at the top of a page, like <c>LS(1)   User Commands   LS(1)</c>.
	/// </summary>
	/// <param name="title">The page name with its section, like <c>String.Split(3net)</c>.</param>
	/// <param name="center">The name of the manual, like <c>.NET API Reference</c>.</param>
	public void Header(string title, string center)
	{
		_ = this.text.Append(this.ThreePart(title, center, title)).Append('\n');
	}

	/// <summary>
	/// Writes the line at the bottom of a page.
	/// </summary>
	/// <param name="left">The source, like <c>.NET 10</c>.</param>
	/// <param name="center">The date, like <c>checked 2026-10-10</c>.</param>
	/// <param name="title">The page name with its section, as in the header.</param>
	public void Footer(string left, string center, string title)
	{
		_ = this.text.Append('\n').Append(this.ThreePart(left, center, title)).Append('\n');
	}

	/// <summary>
	/// Starts a section, like <c>NAME</c> or <c>DESCRIPTION</c>.
	/// </summary>
	/// <param name="name">The section name, which is written in capitals.</param>
	public void Section(string name)
	{
		_ = this.text.Append('\n');
		this.Append(new Span(name.ToUpper(CultureInfo.InvariantCulture), SpanStyle.Bold));
		_ = this.text.Append('\n');
		this.startOfSection = true;
	}

	/// <summary>
	/// Starts a heading inside a section, indented three columns as groff's <c>.SS</c> does.
	/// </summary>
	/// <param name="name">The heading.</param>
	public void Subsection(string name)
	{
		this.BlankLineUnlessStart();
		_ = this.text.Append("   ");
		this.Append(new Span(name, SpanStyle.Bold));
		_ = this.text.Append('\n');
		this.startOfSection = true;
	}

	/// <summary>
	/// Writes a paragraph wrapped to the width, with a blank line before it unless it opens a section.
	/// </summary>
	/// <param name="spans">The paragraph text. A paragraph with no text writes nothing, not even its indent.</param>
	/// <param name="indent">The column every line starts at.</param>
	/// <param name="firstIndent">The column the first line starts at, or <see langword="null" /> for <paramref name="indent" />.</param>
	public void Paragraph(IReadOnlyList<Span> spans, int indent = BodyIndent, int? firstIndent = null)
	{
		if (VisibleLength(spans) == 0)
		{
			return;
		}

		this.BlankLineUnlessStart();
		this.Wrap(spans, indent, firstIndent ?? indent);
	}

	/// <summary>
	/// Writes a paragraph straight after the previous line, with no blank line between, such as the next item of a
	/// list.
	/// </summary>
	/// <param name="spans">The paragraph text.</param>
	/// <param name="indent">The column every line after the first starts at.</param>
	/// <param name="firstIndent">The column the first line starts at.</param>
	public void Line(IReadOnlyList<Span> spans, int indent, int firstIndent)
	{
		this.Wrap(spans, indent, firstIndent);
		this.startOfSection = false;
	}

	/// <summary>
	/// Writes tag lines with a description indented under them, as groff's <c>.TP</c> does for options.
	/// </summary>
	/// <param name="tags">The tag lines, such as the forms of a member or a parameter name. A long one wraps with its later lines indented further.</param>
	/// <param name="body">The description, or empty to write only the tags.</param>
	/// <param name="tagIndent">The column the tags start at.</param>
	/// <param name="bodyIndent">The column the description starts at.</param>
	public void Tagged(IEnumerable<IReadOnlyList<Span>> tags, IReadOnlyList<Span> body, int tagIndent = BodyIndent, int bodyIndent = TaggedIndent)
	{
		this.BlankLineUnlessStart();

		foreach (var tag in tags)
		{
			this.Wrap(tag, tagIndent + HangingIndent, tagIndent);
		}

		if (body.Count > 0)
		{
			this.Wrap(body, bodyIndent, bodyIndent);
		}
	}

	/// <summary>
	/// Writes lines such as signatures, each wrapped with its later lines indented further so a long one still reads
	/// as one line, as the SYNOPSIS of a man page does.
	/// </summary>
	/// <param name="lines">The lines.</param>
	public void Hanging(IEnumerable<IReadOnlyList<Span>> lines)
	{
		this.BlankLineUnlessStart();

		foreach (var line in lines)
		{
			this.Wrap(line, BodyIndent + HangingIndent, BodyIndent);
		}
	}

	/// <summary>
	/// Writes lines exactly as given, never wrapped, such as code or a table.
	/// </summary>
	/// <param name="lines">The lines.</param>
	/// <param name="indent">The column each line starts at.</param>
	public void Verbatim(IEnumerable<IReadOnlyList<Span>> lines, int indent)
	{
		this.BlankLineUnlessStart();

		foreach (var line in lines)
		{
			this.WriteUnwrapped(line, indent);
		}
	}

	/// <summary>
	/// Returns the page written so far.
	/// </summary>
	/// <returns>
	/// The page, each line ending in <c>\n</c>.
	/// </returns>
	public override string ToString() => this.text.ToString();

	/// <summary>
	/// Gets the length of text as it shows on screen.
	/// </summary>
	/// <param name="spans">The text.</param>
	/// <returns>
	/// The number of characters, not counting overstrike.
	/// </returns>
	public static int VisibleLength(IReadOnlyList<Span> spans) => spans.Sum(span => span.Text.Length);

	/// <summary>
	/// Writes a blank line before a block, except as the first block of a section.
	/// </summary>
	private void BlankLineUnlessStart()
	{
		if (!this.startOfSection)
		{
			_ = this.text.Append('\n');
		}

		this.startOfSection = false;
	}

	/// <summary>
	/// Writes one line at an indent without wrapping it.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <param name="indent">The column the line starts at.</param>
	private void WriteUnwrapped(IReadOnlyList<Span> line, int indent)
	{
		// A blank line inside code gets no indent, so the page has no trailing spaces.
		_ = this.text.Append(' ', VisibleLength(line) == 0 ? 0 : indent);

		foreach (var span in line)
		{
			this.Append(span);
		}

		_ = this.text.Append('\n');
	}

	/// <summary>
	/// Fills lines word by word up to the width.
	/// </summary>
	/// <param name="spans">The text.</param>
	/// <param name="indent">The column lines after the first start at.</param>
	/// <param name="firstIndent">The column the first line starts at.</param>
	/// <remarks>
	/// A word longer than a whole line, like a long URL, goes on a line of its own and runs past the width, as groff
	/// does, rather than being cut.
	/// </remarks>
	private void Wrap(IReadOnlyList<Span> spans, int indent, int firstIndent)
	{
		var column = firstIndent;
		var lineEmpty = true;

		_ = this.text.Append(' ', firstIndent);

		foreach (var word in Words(spans))
		{
			var length = VisibleLength(word);

			if (!lineEmpty && column + 1 + length > this.Width)
			{
				_ = this.text.Append('\n').Append(' ', indent);
				column = indent;
				lineEmpty = true;
			}

			if (!lineEmpty)
			{
				_ = this.text.Append(' ');
				column++;
			}

			foreach (var span in word)
			{
				this.Append(span);
			}

			column += length;
			lineEmpty = false;
		}

		_ = this.text.Append('\n');
	}

	/// <summary>
	/// Splits text into words at spaces and line breaks, keeping each piece's style.
	/// </summary>
	/// <param name="spans">The text.</param>
	/// <returns>
	/// The words in order. A word can hold more than one style, like a bold name followed by a plain period.
	/// </returns>
	private static List<List<Span>> Words(IReadOnlyList<Span> spans)
	{
		var words = new List<List<Span>>();
		var current = new List<Span>();

		foreach (var span in spans)
		{
			var pieces = span.Text.Split([' ', '\n', '\t']);

			for (var index = 0; index < pieces.Length; index++)
			{
				if (index > 0 && current.Count > 0)
				{
					words.Add(current);
					current = [];
				}

				if (pieces[index].Length > 0)
				{
					current.Add(span with { Text = pieces[index] });
				}
			}
		}

		if (current.Count > 0)
		{
			words.Add(current);
		}

		return words;
	}

	/// <summary>
	/// Writes styled text: overstrike for bold and underline, standard color codes for code, and an OSC 8 hyperlink
	/// around it when it's a link. A plain page gets the text alone.
	/// </summary>
	/// <param name="span">The text.</param>
	/// <remarks>
	/// OSC 8 is the terminal standard for links. <c>less -R</c> passes it through, terminals like Konsole open it on a
	/// click, and the window's reader opens it as another dman page.
	/// </remarks>
	private void Append(Span span)
	{
		if (!styled)
		{
			_ = this.text.Append(span.Text);

			return;
		}

		if (span.Link is not null)
		{
			_ = this.text.Append("\e]8;;").Append(span.Link).Append("\e\\");
		}

		// Code is colored with the 16 standard colors rather than exact ones, so the window's reader draws it in the
		// shell's own palette and a terminal in its theme. 90 (bright black) is used for comments instead of 30, which
		// is invisible on a black terminal. Strings get the yellow the input line gives quoted text.
		var color = span.Style switch
		{
			SpanStyle.Keyword => "35",
			SpanStyle.String => "33",
			SpanStyle.Comment => "90",
			SpanStyle.Number => "91",
			SpanStyle.Type => "32",
			SpanStyle.Method => "94",
			SpanStyle.Parameter => "96",
			SpanStyle.Variable => "36",
			SpanStyle.Plain or SpanStyle.Bold or SpanStyle.Underline or _ => null,
		};

		if (color is not null)
		{
			_ = this.text.Append("\e[").Append(color).Append('m').Append(span.Text).Append("\e[39m");
		}
		else if (span.Style == SpanStyle.Plain)
		{
			_ = this.text.Append(span.Text);
		}
		else
		{
			foreach (var character in span.Text)
			{
				_ = character == ' '
					? this.text.Append(' ')
					: this.text.Append(span.Style == SpanStyle.Bold ? character : '_').Append('\b').Append(character);
			}
		}

		if (span.Link is not null)
		{
			_ = this.text.Append("\e]8;;\e\\");
		}
	}

	/// <summary>
	/// Lays out a header or footer line with text at the left, center and right.
	/// </summary>
	/// <param name="left">The text at the left edge.</param>
	/// <param name="center">The text in the middle.</param>
	/// <param name="right">The text at the right edge.</param>
	/// <returns>
	/// The line. The center text is dropped when the three don't fit, as on a very narrow page.
	/// </returns>
	private string ThreePart(string left, string center, string right)
	{
		var gap = this.Width - left.Length - right.Length;

		if (gap < center.Length + 2)
		{
			return left + new string(' ', Math.Max(1, gap)) + right;
		}

		var before = (gap - center.Length) / 2;

		return left + new string(' ', before) + center + new string(' ', gap - center.Length - before) + right;
	}
}
