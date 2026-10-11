using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Sharpie.Shell.Properties;

namespace Sharpie.Shell.Features.DotNetDocs;

/// <summary>
/// A type the name index matched, with the ids of its members.
/// </summary>
/// <param name="Namespace">The type's namespace.</param>
/// <param name="Name">
/// The type's full name as the index writes it, like <c>System.Collections.Generic.List`1</c>, or
/// <c>System.Environment/SpecialFolder</c> for a nested type.
/// </param>
/// <param name="MemberIds">The doc ids of the members the index lists for this .NET version.</param>
internal sealed record IndexedType(string Namespace, string Name, HashSet<string> MemberIds);

/// <summary>
/// What a lookup found in the name index.
/// </summary>
/// <param name="Namespace">The namespace the page is about, or the found type's namespace.</param>
/// <param name="Type">The type, or <see langword="null" /> for a namespace page.</param>
/// <param name="Member">The member name, or <see langword="null" /> for a type or namespace page.</param>
/// <param name="Others">Other index names that matched as well, for SEE ALSO, like <c>Other.Library.List`1</c>.</param>
/// <param name="NamespaceTypes">The types in the namespace, for a namespace page.</param>
/// <param name="ChildNamespaces">The namespaces directly under it, for a namespace page.</param>
internal sealed record ApiTarget(string Namespace, IndexedType? Type, string? Member, IReadOnlyList<string> Others, IReadOnlyList<string> NamespaceTypes, IReadOnlyList<string> ChildNamespaces);

/// <summary>
/// Builds man pages for .NET namespaces, types and members from Microsoft's <c>dotnet-api-docs</c> repository, the
/// source of the API reference on learn.microsoft.com.
/// </summary>
/// <remarks>
/// Names are looked up in the repository's framework index for the running .NET version, which lists every type and
/// member that version has. learn.microsoft.com's search was tried first, but it ranks Java and Xamarin types above
/// <c>List&lt;T&gt;</c> for <c>List</c> and finds nothing for <c>Dictionary.TryGetValue</c>.
/// </remarks>
internal static partial class ApiDocs
{
	// Member kinds in the order a type page lists them.
	private static readonly string[] Categories = ["CONSTRUCTORS", "FIELDS", "PROPERTIES", "METHODS", "OPERATORS", "EVENTS"];

	private static readonly CompositeFormat AssemblyFormat = CompositeFormat.Parse(Resources.DmanAssembly);
	private static readonly CompositeFormat DownloadingIndexFormat = CompositeFormat.Parse(Resources.DmanDownloadingIndex);

	/// <summary>
	/// Gets the framework the pages describe, which matches the .NET the shell runs on.
	/// </summary>
	/// <value>
	/// A moniker like <c>net-10.0</c>, as the repository names its framework index files.
	/// </value>
	public static string Moniker { get; } = $"net-{Environment.Version.Major}.0";

	/// <summary>
	/// Builds the page for a namespace, type or member.
	/// </summary>
	/// <param name="query">The name the user typed, like <c>List</c>, <c>String.Split</c> or <c>System.IO</c>.</param>
	/// <param name="language">The language to show signatures and examples in.</param>
	/// <param name="source">Where the docs are downloaded from.</param>
	/// <param name="width">The number of columns to fill.</param>
	/// <param name="styled"><see langword="true" /> for bold and underline; <see langword="false" /> for plain text.</param>
	/// <returns>
	/// The page, or <see langword="null" /> when nothing matches or the docs can't be downloaded.
	/// </returns>
	public static string? Render(string query, DocLanguage language, DocsSource source, int width, bool styled)
	{
		if (Resolve(query, source) is not { } target)
		{
			return null;
		}

		var writer = new ManPageWriter(width, styled);

		return target.Type is null
			? NamespacePage(target, source, writer)
			: TypePage(target, target.Type, language, source, writer);
	}

	/// <summary>
	/// Finds what a name refers to in the framework index.
	/// </summary>
	/// <param name="query">The name the user typed.</param>
	/// <param name="source">Where the index is downloaded from.</param>
	/// <returns>
	/// A namespace whose name matches exactly, else the best type, else the best member; or <see langword="null" />.
	/// </returns>
	/// <remarks>
	/// Case doesn't matter, and the name can be cut at any dot from the left, so <c>List</c>, <c>Generic.List</c> and
	/// <c>System.Collections.Generic.List&lt;T&gt;</c> all find <c>List&lt;T&gt;</c>. When several match, an exact-case
	/// match wins, then a <c>System</c> namespace, then the shortest name.
	/// </remarks>
	public static ApiTarget? Resolve(string query, DocsSource source)
	{
		var name = GenericPattern().Replace(query, string.Empty).Trim().TrimEnd('(', ')');

		if (name.Length == 0)
		{
			return null;
		}

		var dot = name.LastIndexOf('.');
		var typePart = dot > 0 ? name[..dot] : null;
		var memberPart = dot > 0 ? name[(dot + 1)..] : null;
		var namespaces = new List<string>();
		var namespaceTypes = new List<string>();
		var types = new List<IndexedType>();
		var members = new List<IndexedType>();

		var indexes = ((string[])[string.Empty, "-pp"]).Select(suffix => new Uri(Links.ApiRepository, $"xml/FrameworksIndex/{Moniker}{suffix}.xml")).ToList();

		if (!indexes.All(source.IsSaved))
		{
			Console.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, DownloadingIndexFormat, Moniker));
		}

		foreach (var url in indexes)
		{
			if (source.Get(url) is not { } index)
			{
				continue;
			}

			ReadIndex(index.Text, name, typePart, memberPart, namespaces, namespaceTypes, types, members);
		}

		if (namespaces.FirstOrDefault(each => each.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } ns)
		{
			var children = namespaces
				.Where(each => each.StartsWith(ns + ".", StringComparison.Ordinal) && !each[(ns.Length + 1)..].Contains('.', StringComparison.Ordinal))
				.Distinct(StringComparer.Ordinal)
				.Order(StringComparer.Ordinal);

			return new ApiTarget(ns, null, null, [], [.. namespaceTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], [.. children]);
		}

		var hits = types.Count > 0 ? Ranked(types, name) : Ranked(members, typePart ?? name);

		if (hits.Count == 0)
		{
			return null;
		}

		var best = hits[0];
		var member = types.Count > 0 ? null : memberPart;
		var others = hits.Skip(1).Take(10).Select(hit => hit.Name.Replace('/', '.') + (member is null ? string.Empty : "." + member));

		return new ApiTarget(best.Namespace, best, member, [.. others], [], []);
	}

	/// <summary>
	/// Reads one framework index file and collects what matches.
	/// </summary>
	/// <param name="xml">The index file.</param>
	/// <param name="name">The whole name being looked up.</param>
	/// <param name="typePart">The name up to its last dot, in case the last part is a member; otherwise, <see langword="null" />.</param>
	/// <param name="memberPart">The name after its last dot; otherwise, <see langword="null" />.</param>
	/// <param name="namespaces">Collects every namespace.</param>
	/// <param name="namespaceTypes">Collects the type names in a namespace called <paramref name="name" />.</param>
	/// <param name="types">Collects types matching <paramref name="name" />.</param>
	/// <param name="members">Collects types matching <paramref name="typePart" /> that have a <paramref name="memberPart" /> member.</param>
	/// <remarks>
	/// The file is streamed and only matching types keep their member lists, since the two index files together list
	/// several hundred thousand members.
	/// </remarks>
	private static void ReadIndex(string xml, string name, string? typePart, string? memberPart, List<string> namespaces, List<string> namespaceTypes, List<IndexedType> types, List<IndexedType> members)
	{
		var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true, IgnoreComments = true };
		using var reader = XmlReader.Create(new StringReader(xml), settings);
		var ns = string.Empty;

		while (reader.Read())
		{
			if (reader.NodeType != XmlNodeType.Element)
			{
				continue;
			}

			if (reader.LocalName == "Namespace")
			{
				ns = reader.GetAttribute("Name") ?? string.Empty;
				namespaces.Add(ns);

				continue;
			}

			if (reader.LocalName != "Type" || reader.GetAttribute("Name") is not { } full)
			{
				continue;
			}

			var display = Display(full);

			if (ns.Equals(name, StringComparison.OrdinalIgnoreCase) && full.Length > ns.Length)
			{
				namespaceTypes.Add(full[(ns.Length + 1)..]);
			}

			var isType = Matches(display, name);
			var isMemberType = typePart is not null && Matches(display, typePart);

			if (!isType && !isMemberType)
			{
				continue;
			}

			var type = new IndexedType(ns, full, ReadMemberIds(reader));

			if (isType)
			{
				types.Add(type);
			}

			if (isMemberType && type.MemberIds.Any(id => MemberName(id, full.Replace('/', '.')).Equals(memberPart, StringComparison.OrdinalIgnoreCase)))
			{
				members.Add(type);
			}
		}
	}

	/// <summary>
	/// Reads the member ids listed under the type element the reader is on.
	/// </summary>
	/// <param name="reader">A reader on a <c>Type</c> element.</param>
	/// <returns>
	/// The ids, like <c>M:System.String.Split(System.Char[])</c>.
	/// </returns>
	private static HashSet<string> ReadMemberIds(XmlReader reader)
	{
		var ids = new HashSet<string>(StringComparer.Ordinal);
		using var members = reader.ReadSubtree();

		while (members.Read())
		{
			if (members.NodeType == XmlNodeType.Element && members.LocalName == "Member" && members.GetAttribute("Id") is { } id)
			{
				_ = ids.Add(id);
			}
		}

		return ids;
	}

	/// <summary>
	/// Sorts matching types best first.
	/// </summary>
	/// <param name="hits">The matches.</param>
	/// <param name="name">The name they matched, to prefer an exact-case match.</param>
	/// <returns>
	/// The matches, without the duplicates the two index files share.
	/// </returns>
	private static List<IndexedType> Ranked(List<IndexedType> hits, string name) =>
		[.. hits
			.DistinctBy(hit => hit.Name, StringComparer.Ordinal)
			.OrderBy(hit => Matches(Display(hit.Name), name, StringComparison.Ordinal) ? 0 : 1)
			.ThenBy(hit => hit.Namespace == "System" || hit.Namespace.StartsWith("System.", StringComparison.Ordinal) ? 0 : 1)
			.ThenBy(hit => hit.Name.Count(character => character == '.'))
			.ThenBy(hit => hit.Name.Length)
			.ThenBy(hit => hit.Name, StringComparer.Ordinal)];

	/// <summary>
	/// Checks whether a type's name ends in the name being looked up, at a dot.
	/// </summary>
	/// <param name="display">The type's full name without generic arity.</param>
	/// <param name="name">The name being looked up.</param>
	/// <param name="comparison">How to compare.</param>
	/// <returns>
	/// <see langword="true" /> for <c>List</c>, <c>Generic.List</c> or the whole name.
	/// </returns>
	private static bool Matches(string display, string name, StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
		display.Equals(name, comparison) || display.EndsWith("." + name, comparison);

	/// <summary>
	/// Turns an index name into the dotted name people type.
	/// </summary>
	/// <param name="name">A name like <c>System.Collections.Generic.Dictionary`2</c> or <c>System.Environment/SpecialFolder</c>.</param>
	/// <returns>
	/// The name without arity and with nested types after a dot, like <c>System.Collections.Generic.Dictionary</c>
	/// or <c>System.Environment.SpecialFolder</c>.
	/// </returns>
	private static string Display(string name) => ArityPattern().Replace(name, string.Empty).Replace('/', '.');

	/// <summary>
	/// Gets the member name out of a doc id.
	/// </summary>
	/// <param name="id">The id, like <c>M:System.String.Split(System.Char[])</c>.</param>
	/// <param name="type">The full name of the type the member belongs to, with nested types after a dot as ids write them.</param>
	/// <returns>
	/// The name, like <c>Split</c>, or empty when the id isn't one of the type's.
	/// </returns>
	private static string MemberName(string id, string type)
	{
		if (id.Length < 2 || !id.AsSpan(2).StartsWith(type + ".", StringComparison.Ordinal))
		{
			return string.Empty;
		}

		var rest = id[(type.Length + 3)..];
		var end = rest.IndexOfAny(['(', '~']);

		return ArityPattern().Replace(end < 0 ? rest : rest[..end], string.Empty);
	}

	/// <summary>
	/// Writes the page for a namespace: its summary and the types and namespaces in it.
	/// </summary>
	/// <param name="target">The namespace.</param>
	/// <param name="source">Where its summary is downloaded from.</param>
	/// <param name="writer">The page.</param>
	/// <returns>
	/// The page.
	/// </returns>
	private static string NamespacePage(ApiTarget target, DocsSource source, ManPageWriter writer)
	{
		var title = $"{target.Namespace}(3net)";
		var file = source.Get(new Uri(Links.ApiRepository, $"xml/ns-{target.Namespace}.xml"));
		var summary = file is null ? null : XDocument.Parse(file.Text).Root?.Element("Docs")?.Element("summary");

		writer.Header(title, ".NET API Reference");
		writer.Section("NAME");
		writer.Paragraph([new Span(target.Namespace, SpanStyle.Bold), .. Dash(summary)]);
		Columns(writer, "TYPES", [.. target.NamespaceTypes.Select(type => (GenericName(type).Replace('/', '.'), Links.ApiUrl($"{target.Namespace}.{type.Replace('/', '.')}")))]);
		Columns(writer, "NAMESPACES", [.. target.ChildNamespaces.Select(child => (child, Links.ApiUrl(child)))]);
		writer.Section("SEE ALSO");
		writer.Paragraph([new Span($"dman {target.Namespace}.TYPE", SpanStyle.Bold), new Span(Resources.DmanTypePageHint, SpanStyle.Plain)]);
		writer.Paragraph([LinkSpan(Links.ApiUrl(target.Namespace))]);
		writer.Footer($".NET {Environment.Version.Major}", Checked(file), title);

		return writer.ToString();
	}

	/// <summary>
	/// Writes the page for a type, or for one of its members when the target names one.
	/// </summary>
	/// <param name="target">What was looked up.</param>
	/// <param name="type">The type.</param>
	/// <param name="language">The language for signatures and examples.</param>
	/// <param name="source">Where the type's docs are downloaded from.</param>
	/// <param name="writer">The page.</param>
	/// <returns>
	/// The page, or <see langword="null" /> when the repository has no file for the type, which happens for the few
	/// types the index lists but the docs don't cover yet.
	/// </returns>
	private static string? TypePage(ApiTarget target, IndexedType type, DocLanguage language, DocsSource source, ManPageWriter writer)
	{
		var url = new Uri(Links.ApiRepository, $"xml/{type.Namespace}/{type.Name[(type.Namespace.Length + 1)..].Replace('/', '+')}.xml");

		if (source.Get(url) is not { } file || XDocument.Parse(file.Text).Root is not { } root)
		{
			return null;
		}

		var renderer = new MarkdownRenderer(writer, source, language);
		var shortName = Attribute(root, "Name").Replace('+', '.');
		var fullName = Attribute(root, "FullName").Replace('+', '.');
		var members = (root.Element("Members")?.Elements("Member") ?? [])
			.Where(member => type.MemberIds.Count == 0 || DocIds(member).Any(type.MemberIds.Contains))
			.ToList();
		var groups = root.Element("Members")?.Elements("MemberGroup") ?? [];

		if (target.Member is { } memberName)
		{
			return MemberPage(target, type, memberName, root, members, language, renderer, url, file, writer);
		}

		var typeTitle = $"{shortName}(3net)";
		// A type lists every assembly it ever lived in, like mscorlib for .NET Framework, each with its versions. The one
		// with this .NET's major version is where it lives now.
		var assembly = (root.Elements("AssemblyInfo").FirstOrDefault(info => info.Elements("AssemblyVersion").Any(version => version.Value.StartsWith($"{Environment.Version.Major}.", StringComparison.Ordinal)))
			?? root.Element("AssemblyInfo"))?.Element("AssemblyName")?.Value;

		writer.Header(typeTitle, ".NET API Reference");
		writer.Section("NAME");
		writer.Paragraph([new Span(fullName, SpanStyle.Bold), .. Dash(root.Element("Docs")?.Element("summary"))]);
		writer.Section("SYNOPSIS");
		writer.Hanging(Lines(Signature(root, "TypeSignature", language)).Select(line => (IReadOnlyList<Span>)[new Span(line, SpanStyle.Plain)]));
		writer.Verbatim([[new Span(Resources.DmanNamespace, SpanStyle.Plain), new Span(type.Namespace, SpanStyle.Bold), new Span(string.Format(CultureInfo.CurrentCulture, AssemblyFormat, assembly), SpanStyle.Plain)]], ManPageWriter.BodyIndent);
		renderer.Render(DocText(root.Element("Docs")?.Element("remarks")), url, Links.ApiRepository, 2, "DESCRIPTION");
		renderer.Render(DocText(root.Element("Docs")?.Element("example")), url, Links.ApiRepository, 2, "EXAMPLES");

		foreach (var category in Categories)
		{
			var inCategory = members.Where(member => Category(member) == category).GroupBy(member => Attribute(member, "MemberName")).ToList();

			if (inCategory.Count == 0)
			{
				continue;
			}

			writer.Section(category);

			foreach (var overloads in inCategory)
			{
				var first = overloads.First();
				var link = overloads.Key is ".ctor" or ".cctor" ? null : Links.ApiUrl($"{type.Name.Replace('/', '.')}.{overloads.Key}");
				var tag = StyledSignature(first, language, shortName).First().Select(span => span.Style == SpanStyle.Bold ? span with { Link = link } : span).ToList();
				var more = overloads.Count() - 1;
				var summary = groups.FirstOrDefault(group => Attribute(group, "MemberName") == overloads.Key)?.Element("Docs")?.Element("summary")
					?? first.Element("Docs")?.Element("summary");

				writer.Tagged(
					[more == 0 ? tag : [.. tag, new Span($"  (+{more} more)", SpanStyle.Plain)]],
					MarkdownRenderer.Inline(Collapse(DocText(summary))));
			}
		}

		SeeAlso(writer, target.Others, Links.ApiUrl(type.Name.Replace('/', '.')), $"dman {shortName}.MEMBER");
		writer.Footer($".NET {Environment.Version.Major}", Checked(file), typeTitle);

		return writer.ToString();
	}

	/// <summary>
	/// Writes the page for one member of a type, with every form it has.
	/// </summary>
	/// <param name="target">What was looked up.</param>
	/// <param name="type">The type.</param>
	/// <param name="memberName">The member's name as the user typed it.</param>
	/// <param name="root">The type's docs.</param>
	/// <param name="members">The type's members for this .NET version.</param>
	/// <param name="language">The language for signatures and examples.</param>
	/// <param name="renderer">The markdown renderer for remarks and examples.</param>
	/// <param name="url">The type file's address.</param>
	/// <param name="file">The type file, for the footer date.</param>
	/// <param name="writer">The page.</param>
	/// <returns>
	/// The page.
	/// </returns>
	private static string MemberPage(ApiTarget target, IndexedType type, string memberName, XElement root, List<XElement> members, DocLanguage language, MarkdownRenderer renderer, Uri url, CachedText file, ManPageWriter writer)
	{
		var shortName = Attribute(root, "Name").Replace('+', '.');
		var fullName = Attribute(root, "FullName").Replace('+', '.');
		var groups = root.Element("Members")?.Elements("MemberGroup") ?? [];
		var overloads = members.Where(member => Attribute(member, "MemberName").Equals(memberName, StringComparison.OrdinalIgnoreCase)).ToList();
		var name = overloads.Count > 0 ? Attribute(overloads[0], "MemberName") : memberName;
		var title = $"{shortName}.{name}(3net)";
		var groupSummary = groups.FirstOrDefault(group => Attribute(group, "MemberName") == name)?.Element("Docs")?.Element("summary");

		writer.Header(title, ".NET API Reference");
		writer.Section("NAME");
		writer.Paragraph([new Span($"{fullName}.{name}", SpanStyle.Bold), .. Dash(groupSummary ?? overloads.FirstOrDefault()?.Element("Docs")?.Element("summary"))]);
		writer.Section("SYNOPSIS");
		writer.Hanging(overloads.SelectMany(overload => StyledSignature(overload, language, shortName)));
		writer.Section("DESCRIPTION");

		foreach (var overload in overloads)
		{
			Overload(writer, renderer, overload, language, url, overloads.Count > 1);
		}

		SeeAlso(writer, [type.Name.Replace('/', '.'), .. target.Others], Links.ApiUrl($"{type.Name.Replace('/', '.')}.{name}"), null);
		writer.Footer($".NET {Environment.Version.Major}", Checked(file), title);

		return writer.ToString();
	}

	/// <summary>
	/// Writes one form of a member: its summary, parameters, return value, exceptions and remarks.
	/// </summary>
	/// <param name="writer">The page.</param>
	/// <param name="renderer">The markdown renderer for remarks and examples.</param>
	/// <param name="overload">The member.</param>
	/// <param name="language">The language for signatures and examples.</param>
	/// <param name="url">The type file's address, for example code paths.</param>
	/// <param name="heading"><see langword="true" /> to start with the signature as a heading, when there's more than one form.</param>
	private static void Overload(ManPageWriter writer, MarkdownRenderer renderer, XElement overload, DocLanguage language, Uri url, bool heading)
	{
		var docs = overload.Element("Docs");

		if (heading)
		{
			writer.Subsection(Signature(overload, "MemberSignature", language).Replace('\n', ' '));
		}

		writer.Paragraph(MarkdownRenderer.Inline(Collapse(DocText(docs?.Element("summary")))));

		foreach (var parameter in docs?.Elements("typeparam").Concat(docs.Elements("param")) ?? [])
		{
			writer.Tagged([[new Span(Attribute(parameter, "name"), SpanStyle.Underline)]], MarkdownRenderer.Inline(Collapse(DocText(parameter))));
		}

		foreach (var (element, label) in (ReadOnlySpan<(string, string)>)[("returns", "Returns"), ("value", "Value")])
		{
			if (docs?.Element(element) is { } text && Collapse(DocText(text)).Length > 0)
			{
				writer.Tagged([[new Span(label, SpanStyle.Bold)]], MarkdownRenderer.Inline(Collapse(DocText(text))));
			}
		}

		foreach (var exception in docs?.Elements("exception") ?? [])
		{
			var cref = WithoutKind(Attribute(exception, "cref"));

			writer.Tagged([[new Span(Resources.DmanThrows, SpanStyle.Plain), new Span(MarkdownRenderer.XrefName(cref), SpanStyle.Bold, Links.ApiUrl(cref))]], MarkdownRenderer.Inline(Collapse(DocText(exception))));
		}

		renderer.Render(DocText(docs?.Element("remarks")), url, Links.ApiRepository, 0, null);
		renderer.Render(DocText(docs?.Element("example")), url, Links.ApiRepository, 0, null);
	}

	/// <summary>
	/// Writes the SEE ALSO section, each related page linked to its own.
	/// </summary>
	/// <param name="writer">The page.</param>
	/// <param name="names">Index names of related APIs and of other matches for the name, like <c>System.Timers.Timer</c>.</param>
	/// <param name="learnUrl">The same page on learn.microsoft.com.</param>
	/// <param name="hint">A command to suggest before the names, like <c>dman String.MEMBER</c>, or <see langword="null" />.</param>
	private static void SeeAlso(ManPageWriter writer, IEnumerable<string> names, string learnUrl, string? hint)
	{
		var spans = new List<Span>();

		foreach (var (text, link) in names.Select(name => (Display(name) + "(3net)", (string?)Links.ApiUrl(name))).Prepend((hint ?? string.Empty, null)))
		{
			if (text.Length == 0)
			{
				continue;
			}

			if (spans.Count > 0)
			{
				spans.Add(new Span(Resources.DmanListSeparator, SpanStyle.Plain));
			}

			spans.Add(new Span(text, SpanStyle.Bold, link));
		}

		writer.Section("SEE ALSO");
		writer.Paragraph(spans);
		writer.Paragraph([LinkSpan(learnUrl)]);
	}

	/// <summary>
	/// Makes an address that links to itself.
	/// </summary>
	/// <param name="url">The address.</param>
	/// <returns>
	/// The address as plain text, linked.
	/// </returns>
	private static Span LinkSpan(string url) => new(url, SpanStyle.Plain, url);

	/// <summary>
	/// Writes names in columns across the page, the way <c>ls</c> lays out files, each linked to its page.
	/// </summary>
	/// <param name="writer">The page.</param>
	/// <param name="section">The section to put them in.</param>
	/// <param name="names">The names, already sorted, with the addresses they link to. Nothing is written when empty.</param>
	private static void Columns(ManPageWriter writer, string section, IReadOnlyList<(string Name, string Link)> names)
	{
		if (names.Count == 0)
		{
			return;
		}

		var cell = names.Max(name => name.Name.Length) + 2;
		var perLine = Math.Max(1, (writer.Width - ManPageWriter.BodyIndent) / cell);
		var lines = (names.Count + perLine - 1) / perLine;

		writer.Section(section);
		writer.Verbatim(
			Enumerable.Range(0, lines).Select(line => (IReadOnlyList<Span>)[.. Enumerable.Range(0, perLine)
				.Select(column => (column * lines) + line)
				.Where(index => index < names.Count)
				.SelectMany(index => (Span[])[
					new Span(names[index].Name, SpanStyle.Bold, names[index].Link),
					new Span(new string(' ', cell - names[index].Name.Length), SpanStyle.Plain)])]),
			ManPageWriter.BodyIndent);
	}

	/// <summary>
	/// Gets a member's signature lines, with its name in bold and its parameters underlined, as man pages print a
	/// function's synopsis.
	/// </summary>
	/// <param name="member">The member.</param>
	/// <param name="language">The language to show.</param>
	/// <param name="typeName">The type's short name, which a constructor's signature uses as its name.</param>
	/// <returns>
	/// One styled line per signature line.
	/// </returns>
	private static IEnumerable<IReadOnlyList<Span>> StyledSignature(XElement member, DocLanguage language, string typeName)
	{
		var name = Attribute(member, "MemberName") is ".ctor" or ".cctor" ? GenericPattern().Replace(typeName, string.Empty) : Attribute(member, "MemberName");
		var parameters = (member.Element("Parameters")?.Elements("Parameter") ?? []).Select(parameter => Attribute(parameter, "Name")).ToHashSet(StringComparer.Ordinal);

		foreach (var line in Lines(Signature(member, "MemberSignature", language)))
		{
			var spans = new List<Span>();
			var position = 0;

			foreach (Match word in WordPattern().Matches(line))
			{
				var style = word.Value == name ? SpanStyle.Bold : parameters.Contains(word.Value) ? SpanStyle.Underline : SpanStyle.Plain;

				if (style == SpanStyle.Plain)
				{
					continue;
				}

				spans.Add(new Span(line[position..word.Index], SpanStyle.Plain));
				spans.Add(new Span(word.Value, style));
				position = word.Index + word.Length;
			}

			spans.Add(new Span(line[position..], SpanStyle.Plain));

			yield return spans;
		}
	}

	/// <summary>
	/// Gets the signature in a language for the running .NET version.
	/// </summary>
	/// <param name="element">The type or member element.</param>
	/// <param name="signatureName"><c>TypeSignature</c> or <c>MemberSignature</c>.</param>
	/// <param name="language">The language wanted.</param>
	/// <returns>
	/// The signature without the trailing semicolon or the space the docs put before <c>(</c>. Falls back to C# when
	/// there's no Visual Basic form.
	/// </returns>
	/// <remarks>
	/// A signature that changed between versions has one element per version, each with the versions it applies to
	/// in <c>FrameworkAlternate</c>. The one for <see cref="Moniker" /> is picked, else the one that applies to all.
	/// </remarks>
	private static string Signature(XElement element, string signatureName, DocLanguage language)
	{
		var all = element.Elements(signatureName).ToList();
		var wanted = language == DocLanguage.VisualBasic && all.Any(each => Attribute(each, "Language") == "VB.NET") ? "VB.NET" : "C#";
		var forms = all.Where(each => Attribute(each, "Language") == wanted).ToList();
		var form = forms.FirstOrDefault(each => Attribute(each, "FrameworkAlternate").Split(';').Contains(Moniker, StringComparer.Ordinal))
			?? forms.FirstOrDefault(each => each.Attribute("FrameworkAlternate") is null)
			?? forms.FirstOrDefault();

		return SpaceBeforeParenthesisPattern().Replace(form is null ? string.Empty : Attribute(form, "Value"), "$1(").TrimEnd(';');
	}

	/// <summary>
	/// Gets a member's doc ids, one per version its signature changed in.
	/// </summary>
	/// <param name="member">The member.</param>
	/// <returns>
	/// The ids.
	/// </returns>
	private static IEnumerable<string> DocIds(XElement member) =>
		member.Elements("MemberSignature").Where(each => Attribute(each, "Language") == "DocId").Select(each => Attribute(each, "Value"));

	/// <summary>
	/// Decides which section of a type page a member is listed in.
	/// </summary>
	/// <param name="member">The member.</param>
	/// <returns>
	/// One of <see cref="Categories" />, or empty for an explicit interface implementation, which isn't listed.
	/// </returns>
	private static string Category(XElement member)
	{
		var name = Attribute(member, "MemberName");

		return name.Contains('.', StringComparison.Ordinal) && name is not ".ctor" and not ".cctor"
			? string.Empty
			: member.Element("MemberType")?.Value switch
			{
				"Constructor" => "CONSTRUCTORS",
				"Field" => "FIELDS",
				"Property" => "PROPERTIES",
				"Method" when name.StartsWith("op_", StringComparison.Ordinal) => "OPERATORS",
				"Method" => "METHODS",
				"Event" => "EVENTS",
				_ => string.Empty,
			};
	}

	/// <summary>
	/// Turns doc comment XML into markdown, so summaries and remarks go through the same renderer as the language
	/// pages.
	/// </summary>
	/// <param name="element">The doc element, like <c>summary</c> or <c>remarks</c>.</param>
	/// <returns>
	/// The markdown, or empty for <see langword="null" /> and for the repository's "To be added." placeholder.
	/// </returns>
	/// <remarks>
	/// Remarks are mostly markdown already, inside <c>&lt;format type="text/markdown"&gt;</c>, and pass through as
	/// written. Plain text has <c>&lt;</c> escaped, or a generic like <c>List&lt;T&gt;</c> would read as an HTML tag
	/// and vanish.
	/// </remarks>
	internal static string DocText(XElement? element)
	{
		var text = new StringBuilder();

		foreach (var node in element?.Nodes() ?? [])
		{
			AppendDoc(text, node);
		}

		return text.ToString().Trim() == "To be added." ? string.Empty : text.ToString();
	}

	/// <summary>
	/// Adds one doc comment node to the markdown.
	/// </summary>
	/// <param name="text">The markdown so far.</param>
	/// <param name="node">The node.</param>
	private static void AppendDoc(StringBuilder text, XNode node)
	{
		if (node is XText plain)
		{
			_ = plain.Parent?.Name.LocalName == "format"
				? text.Append(plain.Value)
				: text.Append(WhitespacePattern().Replace(plain.Value, " ").Replace("<", "&lt;", StringComparison.Ordinal));

			return;
		}

		if (node is not XElement element)
		{
			return;
		}

		var inner = DocText(element);

		_ = element.Name.LocalName switch
		{
			"see" when element.Attribute("langword") is { } word => text.Append('`').Append(word.Value).Append('`'),
			"see" when element.Attribute("cref") is { } cref => text.Append("<xref:").Append(WithoutKind(cref.Value)).Append('>'),
			"see" => text.Append(inner.Length > 0 ? inner : Attribute(element, "href")),
			"seealso" => text,
			"paramref" or "typeparamref" => text.Append('*').Append(Attribute(element, "name")).Append('*'),
			"c" => text.Append('`').Append(element.Value.Replace("<", "&lt;", StringComparison.Ordinal)).Append('`'),
			"code" => text.Append("\n\n```").Append(Attribute(element, "language")).Append('\n').Append(element.Value).Append("\n```\n\n"),
			"para" or "block" => text.Append("\n\n").Append(inner).Append("\n\n"),
			"list" => text.Append(ListText(element)).Append("\n\n"),
			"b" or "strong" => text.Append("**").Append(inner).Append("**"),
			"i" or "em" => text.Append('*').Append(inner).Append('*'),
			_ => text.Append(inner),
		};
	}

	/// <summary>
	/// Turns a doc comment list into a markdown list.
	/// </summary>
	/// <param name="list">The <c>list</c> element.</param>
	/// <returns>
	/// One <c>- </c> line per item, with the item's term in bold before its description.
	/// </returns>
	private static string ListText(XElement list)
	{
		var text = new StringBuilder("\n\n");

		foreach (var item in list.Elements("item"))
		{
			var term = Collapse(DocText(item.Element("term")));
			var description = Collapse(DocText(item.Element("description") ?? item));

			_ = text.Append("\n- ").Append(term.Length > 0 ? $"**{term}**: " : string.Empty).Append(description);
		}

		return text.ToString();
	}

	/// <summary>
	/// Gets the text after a page's name on its NAME line.
	/// </summary>
	/// <param name="summary">The summary element, or <see langword="null" /> when there's none.</param>
	/// <returns>
	/// <c> - </c> and the summary, or nothing without one.
	/// </returns>
	private static List<Span> Dash(XElement? summary)
	{
		var text = Collapse(DocText(summary));

		return text.Length == 0 ? [] : [new Span(Resources.DmanNameSeparator, SpanStyle.Plain), .. MarkdownRenderer.Inline(text)];
	}

	/// <summary>
	/// Joins markdown onto one line.
	/// </summary>
	/// <param name="markdown">The markdown.</param>
	/// <returns>
	/// The text with every run of whitespace, line breaks included, made one space.
	/// </returns>
	private static string Collapse(string markdown) => WhitespacePattern().Replace(markdown, " ").Trim();

	/// <summary>
	/// Splits a signature into lines; Visual Basic signatures put <c>Implements</c> and <c>Inherits</c> on their own.
	/// </summary>
	/// <param name="signature">The signature.</param>
	/// <returns>
	/// The lines.
	/// </returns>
	private static string[] Lines(string signature) => signature.Split('\n');

	/// <summary>
	/// Gets an attribute's value.
	/// </summary>
	/// <param name="element">The element.</param>
	/// <param name="name">The attribute name.</param>
	/// <returns>
	/// The value, or empty when the attribute is missing.
	/// </returns>
	private static string Attribute(XElement element, string name) => element.Attribute(name)?.Value ?? string.Empty;

	/// <summary>
	/// Removes the kind from a doc id.
	/// </summary>
	/// <param name="id">A doc id like <c>T:System.String</c>, or a bare name like <c>ArgumentException</c>.</param>
	/// <returns>
	/// The id without its <c>T:</c>, <c>M:</c> or other kind; a bare name unchanged.
	/// </returns>
	private static string WithoutKind(string id) => id.Length > 2 && id[1] == ':' ? id[2..] : id;

	/// <summary>
	/// Writes an index type name the way C# reads, like <c>List&lt;&gt;</c> for <c>List`1</c>.
	/// </summary>
	/// <param name="name">The name.</param>
	/// <returns>
	/// The name with each arity shown as empty angle brackets, one comma per extra parameter.
	/// </returns>
	private static string GenericName(string name) =>
		ArityPattern().Replace(name, match => $"<{new string(',', int.Parse(match.Value[1..], CultureInfo.InvariantCulture) - 1)}>");

	/// <summary>
	/// Gets the footer's date for when a file was last confirmed current.
	/// </summary>
	/// <param name="file">The page's main file, or <see langword="null" />.</param>
	/// <returns>
	/// Text like <c>checked 2026-10-10</c>, or empty without a file.
	/// </returns>
	private static string Checked(CachedText? file) =>
		file is null ? string.Empty : $"checked {file.Checked.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

	[GeneratedRegex(@"`+\d+")]
	private static partial Regex ArityPattern();

	[GeneratedRegex(@"<[^>]*>|\(Of [^)]*\)|`+\d+")]
	private static partial Regex GenericPattern();

	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespacePattern();

	[GeneratedRegex(@"\w+")]
	private static partial Regex WordPattern();

	[GeneratedRegex(@"([\w>\]?]) +\(")]
	private static partial Regex SpaceBeforeParenthesisPattern();
}
