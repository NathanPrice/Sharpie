using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Sharpie.Shell.Features.DotNetDocs;

/// <summary>
/// The language a page shows signatures, keywords and examples in.
/// </summary>
internal enum DocLanguage
{
	/// <summary>
	/// C#, the default.
	/// </summary>
	CSharp,

	/// <summary>
	/// Visual Basic, chosen with <c>-vb</c>.
	/// </summary>
	VisualBasic,
}

/// <summary>
/// Writes the markdown Microsoft's docs are written in as man page sections, paragraphs, lists, tables and code.
/// </summary>
/// <remarks>
/// Only what the .NET docs use is handled. Their own extensions (<c>:::code</c>, <c>[!code-csharp[]]</c>,
/// <c>[!NOTE]</c>, <c>&lt;xref:&gt;</c>) are turned into text, and layout-only ones (<c>:::row:::</c>,
/// <c>[!INCLUDE]</c>, images) are dropped.
/// </remarks>
/// <param name="writer">The page being written.</param>
/// <param name="source">Where example code files are downloaded from.</param>
/// <param name="language">The language whose code blocks and examples are kept.</param>
internal sealed partial class MarkdownRenderer(ManPageWriter writer, DocsSource source, DocLanguage language)
{
	/// <summary>
	/// The column code blocks start at, one step in from the body like the examples in <c>man</c> pages.
	/// </summary>
	private const int CodeIndent = ManPageWriter.BodyIndent + 4;

	// Code block and snippet language names. A block in another .NET language is dropped, so a C# page doesn't repeat
	// every example in F# and C++. Unknown names, like json or console, are always kept.
	private static readonly string[] CSharpNames = ["csharp", "cs", "c#"];
	private static readonly string[] VisualBasicNames = ["vb", "vbnet", "visualbasic"];
	private static readonly string[] OtherNames = ["fsharp", "f#", "cpp", "c++", "cppcli"];

	private bool lastWasListItem;

	// The page being written, which relative links are resolved against.
	private Uri? page;

	/// <summary>
	/// Writes markdown into the page.
	/// </summary>
	/// <param name="markdown">The markdown text.</param>
	/// <param name="pageUrl">The markdown file's address, which relative example paths are resolved against.</param>
	/// <param name="repositoryRoot">The repository's root address, which <c>~/</c> example paths are resolved against.</param>
	/// <param name="sectionLevel">
	/// The heading level that starts a man page section; deeper headings become subsections and shallower ones, like
	/// the page title, are dropped. Zero makes every heading a subsection.
	/// </param>
	/// <param name="defaultSection">The section to start when the text doesn't open with a section heading, or <see langword="null" /> for none.</param>
	public void Render(string markdown, Uri pageUrl, Uri repositoryRoot, int sectionLevel, string? defaultSection)
	{
		var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		var paragraph = new List<string>();
		var started = defaultSection is null;

		this.lastWasListItem = false;
		this.page = pageUrl;

		for (var index = 0; index < lines.Length; index++)
		{
			var trimmed = lines[index].Trim();
			var heading = HeadingPattern().Match(trimmed);

			// Blank lines end a paragraph, and headings above the section level, like the page title, are dropped
			// without starting anything.
			if (trimmed.Length == 0 || (heading.Success && heading.Groups[1].Length < sectionLevel))
			{
				this.Flush(paragraph);

				continue;
			}

			if (!started && !(heading.Success && heading.Groups[1].Length == sectionLevel))
			{
				writer.Section(defaultSection!);
			}

			started = true;

			if (!heading.Success && !IsBlockStart(trimmed) && !ListItemPattern().IsMatch(lines[index]))
			{
				paragraph.Add(trimmed);

				continue;
			}

			this.Flush(paragraph);
			index = this.Block(lines, index, heading, sectionLevel, pageUrl, repositoryRoot);
		}

		this.Flush(paragraph);
	}

	/// <summary>
	/// Writes the block that starts on a line: a heading, list item, code, table, quote, or one of the docs' own
	/// extensions.
	/// </summary>
	/// <param name="lines">All lines.</param>
	/// <param name="index">The block's first line.</param>
	/// <param name="heading">The heading match for that line, which may have failed.</param>
	/// <param name="sectionLevel">The heading level that starts a section.</param>
	/// <param name="pageUrl">The page's address, for relative example paths.</param>
	/// <param name="repositoryRoot">The repository root, for <c>~/</c> example paths.</param>
	/// <returns>
	/// The block's last line.
	/// </returns>
	private int Block(string[] lines, int index, Match heading, int sectionLevel, Uri pageUrl, Uri repositoryRoot)
	{
		var trimmed = lines[index].Trim();

		if (ListItemPattern().Match(lines[index]) is { Success: true } item && !heading.Success)
		{
			return this.ListItem(lines, index, item);
		}

		this.lastWasListItem = false;

		if (heading.Success)
		{
			var title = Plain(Inline(heading.Groups[2].Value));

			if (heading.Groups[1].Length == sectionLevel)
			{
				writer.Section(title);
			}
			else
			{
				writer.Subsection(title);
			}

			return index;
		}

		if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
		{
			return this.Fence(lines, index, trimmed);
		}

		if (trimmed.StartsWith(":::code", StringComparison.Ordinal))
		{
			this.CodeReference(Attributes(trimmed), pageUrl, repositoryRoot);
		}
		else if (LegacyCodePattern().Match(trimmed) is { Success: true } legacy)
		{
			var path = legacy.Groups["path"].Value;
			var hash = path.IndexOf('#', StringComparison.Ordinal);

			this.CodeReference(
				new Dictionary<string, string>(StringComparer.Ordinal)
				{
					["language"] = legacy.Groups["language"].Value,
					["source"] = hash < 0 ? path : path[..hash],
					["id"] = hash < 0 ? string.Empty : path[(hash + 1)..],
				},
				pageUrl,
				repositoryRoot);
		}
		else if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
		{
			while (index < lines.Length - 1 && !lines[index].Contains("-->", StringComparison.Ordinal))
			{
				index++;
			}
		}
		else if (trimmed.StartsWith('|'))
		{
			return this.Table(lines, index);
		}
		else if (trimmed.StartsWith('>'))
		{
			return this.Quote(lines, index);
		}

		// Anything else, like :::row::: or [!INCLUDE], only arranges the web page.
		return index;
	}

	/// <summary>
	/// Checks whether a line starts a block rather than continuing a paragraph.
	/// </summary>
	/// <param name="trimmed">The line without surrounding whitespace.</param>
	/// <returns>
	/// <see langword="true" /> for code fences, tables, quotes, comments and the docs' <c>:::</c> and <c>[!</c>
	/// extensions.
	/// </returns>
	private static bool IsBlockStart(string trimmed) =>
		trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)
		|| trimmed.StartsWith(":::", StringComparison.Ordinal) || trimmed.StartsWith("[!", StringComparison.Ordinal)
		|| trimmed.StartsWith('|') || trimmed.StartsWith('>') || trimmed.StartsWith("<!--", StringComparison.Ordinal);

	/// <summary>
	/// Turns inline markdown into styled text.
	/// </summary>
	/// <param name="text">One line or paragraph of markdown.</param>
	/// <param name="page">The raw address of the page the text is from, for relative links; or <see langword="null" /> when unknown.</param>
	/// <returns>
	/// The text, with code and bold in <see cref="SpanStyle.Bold" />, emphasis in <see cref="SpanStyle.Underline" />,
	/// links shown as their text with <see cref="Span.Link" /> set, and API references as the API's name linked to its
	/// page.
	/// </returns>
	public static List<Span> Inline(string text, Uri? page = null)
	{
		text = LineBreakPattern().Replace(text, " ");

		var spans = new List<Span>();
		var position = 0;

		foreach (Match match in InlinePattern().Matches(text))
		{
			AddPlain(spans, text[position..match.Index]);
			position = match.Index + match.Length;

			if (match.Groups["code"].Success)
			{
				spans.Add(new Span(WebUtility.HtmlDecode(match.Groups["codetext"].Value.Trim()), SpanStyle.Bold));
			}
			else if (match.Groups["bold"].Success)
			{
				spans.AddRange(Restyle(Inline(match.Groups["bold"].Value, page), SpanStyle.Bold));
			}
			else if (match.Groups["em"].Success)
			{
				spans.AddRange(Restyle(Inline(match.Groups["em"].Value, page), SpanStyle.Underline));
			}
			else if (match.Groups["link"].Success)
			{
				var label = match.Groups["link"].Value;
				var href = match.Groups["href"].Value;

				if (match.Value.StartsWith('!'))
				{
					continue;
				}

				var link = Links.Resolve(href, page);
				var shown = label.Length > 0 || !href.StartsWith("xref:", StringComparison.Ordinal)
					? Inline(label)
					: [new Span(XrefName(href[5..]), SpanStyle.Bold)];

				spans.AddRange(shown.Select(span => span with { Link = link }));
			}
			else if (match.Groups["xref"].Success)
			{
				spans.Add(new Span(XrefName(match.Groups["xref"].Value), SpanStyle.Bold, Links.ApiUrl(match.Groups["xref"].Value)));
			}
		}

		AddPlain(spans, text[position..]);

		return spans;
	}

	/// <summary>
	/// Joins styled text back into a plain string.
	/// </summary>
	/// <param name="spans">The text.</param>
	/// <returns>
	/// The text without styles.
	/// </returns>
	public static string Plain(IEnumerable<Span> spans) => string.Concat(spans.Select(span => span.Text));

	/// <summary>
	/// Gets the name to show for a cross-reference id like <c>System.String.Split*</c>.
	/// </summary>
	/// <param name="xref">The id, optionally followed by <c>?displayProperty=nameWithType</c> or <c>fullName</c>.</param>
	/// <returns>
	/// The last part of the name, or the whole name for <c>nameWithType</c> and <c>fullName</c>, without parameter
	/// lists, overload stars or generic arity.
	/// </returns>
	/// <remarks>
	/// learn.microsoft.com shows <c>nameWithType</c> as <c>Console.WriteLine</c> for a member but <c>IEnumerable</c>
	/// for a type, and the id alone doesn't say which it is: <c>System.Collections.IEnumerable</c> and
	/// <c>System.Console.WriteLine</c> look alike. The whole name is never wrong, so it's used for both.
	/// </remarks>
	public static string XrefName(string xref)
	{
		var query = xref.IndexOf('?', StringComparison.Ordinal);
		var display = query < 0 ? string.Empty : xref[query..];
		var id = Uri.UnescapeDataString(query < 0 ? xref : xref[..query]);
		var parenthesis = id.IndexOf('(', StringComparison.Ordinal);

		id = ArityPattern().Replace(parenthesis < 0 ? id : id[..parenthesis], string.Empty).TrimEnd('*');

		return display.Length > 0 ? id : id[(id.LastIndexOf('.') + 1)..];
	}

	/// <summary>
	/// Cuts the lines between <c>&lt;id&gt;</c> and <c>&lt;/id&gt;</c> comment markers out of a code file, the way the
	/// docs pick an example out of a larger sample.
	/// </summary>
	/// <param name="code">The whole code file.</param>
	/// <param name="id">The region name, or empty for the whole file.</param>
	/// <returns>
	/// The region with marker lines removed and its common indent taken off, or <see langword="null" /> when the file
	/// has no such region.
	/// </returns>
	public static List<string>? CutRegion(string code, string id)
	{
		var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\t", "    ", StringComparison.Ordinal).Split('\n');
		var start = 0;
		var end = lines.Length;

		if (id.Length > 0)
		{
			var open = $"<{id}>";
			var close = $"</{id}>";

			start = Array.FindIndex(lines, line => line.Contains(open, StringComparison.OrdinalIgnoreCase)) + 1;

			if (start == 0)
			{
				return null;
			}

			end = Array.FindIndex(lines, start, line => line.Contains(close, StringComparison.OrdinalIgnoreCase));
			end = end < 0 ? lines.Length : end;
		}

		var region = lines[start..end].Where(line => !MarkerLinePattern().IsMatch(line)).ToList();

		return Dedent(region);
	}

	/// <summary>
	/// Finds the heading whose web anchor is <paramref name="anchor" /> and returns it with the text under it.
	/// </summary>
	/// <param name="markdown">The whole page.</param>
	/// <param name="anchor">The anchor from a link, like <c>the-foreach-statement</c>.</param>
	/// <returns>
	/// The heading's text, its level, and the markdown up to the next heading of the same or a higher level; or
	/// <see langword="null" /> when no heading has that anchor.
	/// </returns>
	public static (string Heading, int Level, string Body)? CutSection(string markdown, string anchor)
	{
		var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		var inFence = false;
		var start = -1;
		var level = 0;
		var heading = string.Empty;

		for (var index = 0; index < lines.Length; index++)
		{
			var trimmed = lines[index].Trim();

			if (trimmed.StartsWith("```", StringComparison.Ordinal))
			{
				inFence = !inFence;
			}

			if (inFence || HeadingPattern().Match(trimmed) is not { Success: true } match)
			{
				continue;
			}

			if (start >= 0 && match.Groups[1].Length <= level)
			{
				return (heading, level, string.Join('\n', lines[start..index]));
			}

			if (start < 0 && Slug(match.Groups[2].Value) == anchor)
			{
				start = index + 1;
				level = match.Groups[1].Length;
				heading = Plain(Inline(match.Groups[2].Value));
			}
		}

		return start < 0 ? null : (heading, level, string.Join('\n', lines[start..]));
	}

	/// <summary>
	/// Makes the web anchor for a heading the way learn.microsoft.com does.
	/// </summary>
	/// <param name="heading">The heading's markdown.</param>
	/// <returns>
	/// The heading in lowercase with punctuation removed and spaces turned into hyphens, like
	/// <c>the-foreach-statement</c>.
	/// </returns>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Web anchors are lowercase.")]
	public static string Slug(string heading)
	{
		var text = Plain(Inline(heading)).ToLowerInvariant();

		return SlugSpacePattern().Replace(SlugDropPattern().Replace(text, string.Empty).Trim(), "-");
	}

	/// <summary>
	/// Adds text with markdown escapes and HTML entities decoded.
	/// </summary>
	/// <param name="spans">The text so far.</param>
	/// <param name="text">The text to add.</param>
	private static void AddPlain(List<Span> spans, string text)
	{
		if (text.Length > 0)
		{
			spans.Add(new Span(WebUtility.HtmlDecode(EscapePattern().Replace(text, "$1")), SpanStyle.Plain));
		}
	}

	/// <summary>
	/// Gives plain parts of styled text a new style, leaving parts already bold or underlined alone.
	/// </summary>
	/// <param name="spans">The text.</param>
	/// <param name="style">The style for its plain parts.</param>
	/// <returns>
	/// The restyled text.
	/// </returns>
	private static IEnumerable<Span> Restyle(IEnumerable<Span> spans, SpanStyle style) =>
		spans.Select(span => span.Style == SpanStyle.Plain ? span with { Style = style } : span);

	/// <summary>
	/// Reads the <c>name="value"</c> pairs of a <c>:::code</c> line.
	/// </summary>
	/// <param name="line">The line.</param>
	/// <returns>
	/// The values by name.
	/// </returns>
	private static Dictionary<string, string> Attributes(string line) =>
		AttributePattern().Matches(line).ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);

	/// <summary>
	/// Takes the indent every non-blank line shares off all of them.
	/// </summary>
	/// <param name="lines">The lines.</param>
	/// <returns>
	/// The lines without the shared indent or blank lines at either end.
	/// </returns>
	private static List<string> Dedent(List<string> lines)
	{
		while (lines.Count > 0 && lines[0].Trim().Length == 0)
		{
			lines.RemoveAt(0);
		}

		while (lines.Count > 0 && lines[^1].Trim().Length == 0)
		{
			lines.RemoveAt(lines.Count - 1);
		}

		var indent = lines.Where(line => line.Trim().Length > 0).Select(line => line.Length - line.TrimStart().Length).DefaultIfEmpty(0).Min();

		return [.. lines.Select(line => line.Length >= indent ? line[indent..].TrimEnd() : line.Trim())];
	}

	/// <summary>
	/// Gets the language a code block is in, for coloring it.
	/// </summary>
	/// <param name="name">The language name from a code fence or <c>:::code</c> line.</param>
	/// <returns>
	/// C# or Visual Basic, or <see langword="null" /> for anything else, like json or console output.
	/// </returns>
	private static DocLanguage? LanguageOf(string name) =>
		CSharpNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? DocLanguage.CSharp
		: VisualBasicNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? DocLanguage.VisualBasic
		: null;

	/// <summary>
	/// Checks whether code in a language belongs on this page.
	/// </summary>
	/// <param name="name">The language name from a code fence or <c>:::code</c> line.</param>
	/// <returns>
	/// <see langword="false" /> for another .NET language than the page's; otherwise, <see langword="true" />.
	/// </returns>
	private bool Keeps(string name)
	{
		var own = language == DocLanguage.CSharp ? CSharpNames : VisualBasicNames;
		var other = language == DocLanguage.CSharp ? VisualBasicNames : CSharpNames;

		return own.Contains(name, StringComparer.OrdinalIgnoreCase)
			|| !(other.Contains(name, StringComparer.OrdinalIgnoreCase) || OtherNames.Contains(name, StringComparer.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Writes the paragraph collected so far and starts a new one.
	/// </summary>
	/// <param name="paragraph">The paragraph's lines, which are cleared.</param>
	private void Flush(List<string> paragraph)
	{
		if (paragraph.Count == 0)
		{
			return;
		}

		writer.Paragraph(Inline(string.Join(' ', paragraph), this.page));
		paragraph.Clear();
		this.lastWasListItem = false;
	}

	/// <summary>
	/// Writes one list item with a bullet or number hanging out to the left of its text.
	/// </summary>
	/// <param name="lines">All lines.</param>
	/// <param name="index">The item's first line.</param>
	/// <param name="item">The list item match for that line.</param>
	/// <returns>
	/// The item's last line.
	/// </returns>
	private int ListItem(string[] lines, int index, Match item)
	{
		var depth = Math.Min(3, item.Groups["space"].Length / 2);
		var marker = char.IsDigit(item.Groups["marker"].Value[0]) ? item.Groups["marker"].Value : "•";
		var text = new List<string> { item.Groups["text"].Value };

		// Lines indented under the item that don't start another item or block continue its text.
		while (index + 1 < lines.Length && lines[index + 1].Trim().Length > 0 && lines[index + 1].StartsWith(' ')
			&& !ListItemPattern().IsMatch(lines[index + 1]) && !lines[index + 1].TrimStart().StartsWith("```", StringComparison.Ordinal))
		{
			text.Add(lines[++index].Trim());
		}

		var first = ManPageWriter.BodyIndent + (depth * 2);
		var spans = new List<Span> { new($"{marker} ", SpanStyle.Plain) };

		spans.AddRange(Inline(string.Join(' ', text), this.page));

		if (this.lastWasListItem)
		{
			writer.Line(spans, first + marker.Length + 1, first);
		}
		else
		{
			writer.Paragraph(spans, first + marker.Length + 1, first);
		}

		this.lastWasListItem = true;

		return index;
	}

	/// <summary>
	/// Writes a fenced code block, unless it's in another .NET language.
	/// </summary>
	/// <param name="lines">All lines.</param>
	/// <param name="index">The opening fence.</param>
	/// <param name="fence">The opening fence line, trimmed.</param>
	/// <returns>
	/// The closing fence's line.
	/// </returns>
	private int Fence(string[] lines, int index, string fence)
	{
		var marker = fence[..3];
		var name = fence[3..].Trim();
		var end = index + 1;

		while (end < lines.Length && !lines[end].Trim().StartsWith(marker, StringComparison.Ordinal))
		{
			end++;
		}

		if (this.Keeps(name))
		{
			var code = Dedent([.. lines[(index + 1)..Math.Min(end, lines.Length)].Select(line => line.Replace("\t", "    ", StringComparison.Ordinal))]);

			writer.Verbatim(CodeHighlighter.Highlight(code, writer.Styled ? LanguageOf(name) : null), CodeIndent);
		}

		return end;
	}

	/// <summary>
	/// Downloads and writes the example a <c>:::code</c> or <c>[!code-...]</c> line points to.
	/// </summary>
	/// <param name="attributes">The line's <c>language</c>, <c>source</c>, and optional <c>id</c> or <c>range</c>.</param>
	/// <param name="pageUrl">The page's address, for relative sources.</param>
	/// <param name="repositoryRoot">The repository root, for <c>~/</c> sources.</param>
	/// <remarks>
	/// An example that can't be downloaded is left out rather than failing the page.
	/// </remarks>
	private void CodeReference(Dictionary<string, string> attributes, Uri pageUrl, Uri repositoryRoot)
	{
		if (!attributes.TryGetValue("source", out var path) || !this.Keeps(attributes.GetValueOrDefault("language", string.Empty)))
		{
			return;
		}

		var url = path.StartsWith("~/", StringComparison.Ordinal) ? new Uri(repositoryRoot, path[2..]) : new Uri(pageUrl, path);

		if (source.Get(url) is not { } file)
		{
			return;
		}

		var id = attributes.GetValueOrDefault("id", string.Empty);

		// Older pages write the region as #1 for a region named Snippet1.
		var code = CutRegion(file.Text, id) ?? (id.All(char.IsDigit) ? CutRegion(file.Text, $"Snippet{id}") : null);

		if (code is not null && attributes.TryGetValue("range", out var range) && RangePattern().Match(range) is { Success: true } lineRange)
		{
			var from = int.Parse(lineRange.Groups[1].Value, CultureInfo.InvariantCulture);
			var to = lineRange.Groups[2].Success ? int.Parse(lineRange.Groups[2].Value, CultureInfo.InvariantCulture) : from;

			code = Dedent([.. code.Skip(from - 1).Take(to - from + 1)]);
		}

		if (code is { Count: > 0 })
		{
			writer.Verbatim(CodeHighlighter.Highlight(code, writer.Styled ? LanguageOf(attributes.GetValueOrDefault("language", string.Empty)) : null), CodeIndent);
		}
	}

	/// <summary>
	/// Writes a table with its columns lined up.
	/// </summary>
	/// <param name="lines">All lines.</param>
	/// <param name="index">The table's first row.</param>
	/// <returns>
	/// The table's last row.
	/// </returns>
	/// <remarks>
	/// Rows aren't wrapped, the same as code, so a wide table scrolls sideways instead of breaking its columns.
	/// </remarks>
	private int Table(string[] lines, int index)
	{
		var rows = new List<List<List<Span>>>();

		for (; index < lines.Length && lines[index].Trim().StartsWith('|'); index++)
		{
			var cells = lines[index].Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToList();

			if (cells.All(cell => SeparatorCellPattern().IsMatch(cell)))
			{
				continue;
			}

			rows.Add([.. cells.Select(cell => rows.Count == 0 ? [.. Restyle(Inline(cell, this.page), SpanStyle.Bold)] : Inline(cell, this.page))]);
		}

		var columns = rows.Max(row => row.Count);
		var widths = Enumerable.Range(0, columns).Select(column => rows.Max(row => column < row.Count ? ManPageWriter.VisibleLength(row[column]) : 0)).ToList();

		// A table with sentences in its cells reads better as a list: the first cell as a tag and the rest wrapped under
		// it, each labeled with its column heading when there are several. A table of short cells stays a table even
		// when it's a little wider than the page, since lined-up columns of code compare better side by side.
		if (widths.Skip(1).Any(width => width > (writer.Width - ManPageWriter.BodyIndent) / 2) && rows.Count > 1)
		{
			foreach (var row in rows.Skip(1))
			{
				var body = new List<Span>();

				for (var column = 1; column < row.Count; column++)
				{
					if (columns > 2)
					{
						body.Add(new Span($"{Plain(rows[0][Math.Min(column, rows[0].Count - 1)])}: ", SpanStyle.Underline));
					}

					body.AddRange(row[column].Append(new Span(new string(' ', 1), SpanStyle.Plain)));
				}

				writer.Tagged([[.. Restyle(row[0], SpanStyle.Bold)]], body);
			}

			return index - 1;
		}

		writer.Verbatim(
			rows.Select(row => (IReadOnlyList<Span>)[.. row.SelectMany((cell, column) =>
				cell.Append(new Span(new string(' ', column == row.Count - 1 ? 0 : widths[column] - ManPageWriter.VisibleLength(cell) + 2), SpanStyle.Plain)))]),
			ManPageWriter.BodyIndent);

		return index - 1;
	}

	/// <summary>
	/// Writes a quoted block, such as a note or warning.
	/// </summary>
	/// <param name="lines">All lines.</param>
	/// <param name="index">The block's first line.</param>
	/// <returns>
	/// The block's last line.
	/// </returns>
	private int Quote(string[] lines, int index)
	{
		var text = new List<string>();

		for (; index < lines.Length && lines[index].TrimStart().StartsWith('>'); index++)
		{
			text.Add(lines[index].TrimStart()[1..].Trim());
		}

		var spans = new List<Span>();
		var joined = string.Join(' ', text).Trim();

		if (AlertPattern().Match(joined) is { Success: true } alert)
		{
			// [!div class="..."] only styles the web page; [!NOTE] and the like become a label.
			if (alert.Groups[1].Value.StartsWith("div", StringComparison.OrdinalIgnoreCase))
			{
				return index - 1;
			}

			var label = alert.Groups[1].Value;

			spans.Add(new Span($"{label.ToUpperInvariant()}:", SpanStyle.Bold));
			joined = joined[alert.Length..];
		}

		spans.AddRange(Inline(joined, this.page));
		writer.Paragraph(spans);

		return index - 1;
	}

	[GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*$")]
	private static partial Regex HeadingPattern();

	[GeneratedRegex(@"^(?<space> *)(?<marker>[-*+]|\d+\.)\s+(?<text>.*)$")]
	private static partial Regex ListItemPattern();

	[GeneratedRegex(@"^\[!code-(?<language>[\w#+]+)\[[^\]]*\]\((?<path>[^)\s]+)[^)]*\)\]")]
	private static partial Regex LegacyCodePattern();

	[GeneratedRegex("""
		(?<code>`+)(?<codetext>.+?)\k<code>
		| \*\*(?<bold>.+?)\*\*
		| (?<![\w*])\*(?<em>[^*\s][^*]*?)\*(?!\w)
		| (?<!\w)_(?<em>[^_\s][^_]*?)_(?!\w)
		| !?\[(?<link>[^\]]*)\]\((?<href>[^)\s]*)(?:\s+"[^"]*")?\)
		| <xref:(?<xref>[^>]+)>
		| <[/!]?[a-zA-Z][^>]*>
		""", RegexOptions.IgnorePatternWhitespace)]
	private static partial Regex InlinePattern();

	[GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
	private static partial Regex LineBreakPattern();

	[GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!<>|])")]
	private static partial Regex EscapePattern();

	[GeneratedRegex(@"`+\d+")]
	private static partial Regex ArityPattern();

	[GeneratedRegex(@"^\s*(//|'|#|<!--|\(\*|REM\b)\s*</?[\w.-]+>\s*(-->|\*\))?\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex MarkerLinePattern();

	[GeneratedRegex(@"(\w+)=""([^""]*)""")]
	private static partial Regex AttributePattern();

	[GeneratedRegex(@"^(\d+)(?:-(\d+))?")]
	private static partial Regex RangePattern();

	[GeneratedRegex(@"^:?-+:?$")]
	private static partial Regex SeparatorCellPattern();

	[GeneratedRegex(@"^\[!(\w+)[^\]]*\]")]
	private static partial Regex AlertPattern();

	[GeneratedRegex(@"[^\w\s-]")]
	private static partial Regex SlugDropPattern();

	[GeneratedRegex(@"\s")]
	private static partial Regex SlugSpacePattern();
}
