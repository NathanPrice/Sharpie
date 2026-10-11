using System.Globalization;
using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Properties;

namespace Sharpie.Shell.Features.DotNetDocs;

/// <summary>
/// Shows man pages for .NET APIs and C# or Visual Basic keywords, built from Microsoft's documentation.
/// </summary>
/// <remarks>
/// <c>dman NAME</c> takes a keyword, namespace, type or member, like <c>dman foreach</c>, <c>dman System.IO</c>,
/// <c>dman List</c> or <c>dman String.Split</c>, or a learn.microsoft.com address. <c>-vb</c> shows Visual Basic
/// keywords, signatures and examples instead of C#. <c>dman --download</c> saves every page for offline use. In the window the page opens in the window's reader, the same as <c>man</c>; in a terminal it goes
/// through <c>less</c>; redirected or piped it's plain text.
/// </remarks>
internal sealed class DmanCommand : ICommand
{
	/// <summary>
	/// The width a page is laid out to when it isn't going to a terminal, the same default <c>man</c> uses.
	/// </summary>
	private const int FileWidth = 80;

	private static readonly CompositeFormat NotFoundFormat = CompositeFormat.Parse(Resources.DmanNotFound);

	// The console's own writer, captured while the shell starts and nothing is redirected yet. The shell runs a
	// redirected or piped builtin by swapping Console.Out, so a different writer at run time means the page is going
	// to a file or another program.
	private readonly TextWriter terminal = Console.Out;

	/// <inheritdoc />
	public string Name => "dman";
	/// <inheritdoc />
	public string Description => "Shows .NET API and C# or VB keyword docs as man pages";

	/// <inheritdoc />
	/// <remarks>
	/// The status is 2 for a usage mistake, as bash gives its builtins, and 1 when the page or download fails.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args is ["--download"])
		{
			// Stryker disable once Conditional : the download needs the network, which the unit tests stay off.
			return OfflineDocs.Download(DocsSource.Shared, Console.Error) ? 0 : 1;
		}

		if (Parse(args) is null)
		{
			Console.Error.WriteLine(Resources.DmanUsage);

			return 2;
		}

		_ = OfflineDocs.RefreshIfDue(DocsSource.Shared, DateTimeOffset.UtcNow);

		var toTerminal = ReferenceEquals(Console.Out, this.terminal) && !Console.IsOutputRedirected;
		var page = Render(args, toTerminal ? Console.WindowWidth : FileWidth, styled: toTerminal);

		if (page is null)
		{
			Console.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, NotFoundFormat, Parse(args)!.Value.Name));

			return 1;
		}

		if (toTerminal)
		{
			using var input = new MemoryStream(Encoding.UTF8.GetBytes(page));

			// -R passes the color codes in examples through; less draws bold and underline overstrike either way.
			if (ExternalProgram.Run("less", ["-R"], input: input) is { } status)
			{
				return status;
			}
		}

		Console.Write(page);

		return 0;
	}

	/// <summary>
	/// Checks whether <c>dman</c> has a page for the arguments, downloading what the page needs so the window can then
	/// build it from the saved copies.
	/// </summary>
	/// <param name="args">The words after <c>dman</c>.</param>
	/// <returns>
	/// <see langword="true" /> when there's a page.
	/// </returns>
	/// <remarks>
	/// The shell calls this for every <c>dman</c> line in the window, so it's also where an old offline copy starts
	/// refreshing in the background.
	/// </remarks>
	public static bool Find(string[] args)
	{
		_ = OfflineDocs.RefreshIfDue(DocsSource.Shared, DateTimeOffset.UtcNow);

		return Render(args, FileWidth, styled: false) is not null;
	}

	/// <summary>
	/// Builds the page for the arguments.
	/// </summary>
	/// <param name="args">The words after <c>dman</c>, like <c>-vb Select</c>.</param>
	/// <param name="columns">The number of columns to fill.</param>
	/// <param name="styled"><see langword="true" /> for bold and underline; <see langword="false" /> for plain text.</param>
	/// <returns>
	/// The page, or <see langword="null" /> when the arguments are wrong or nothing matches.
	/// </returns>
	/// <remarks>
	/// A keyword is tried before an API, so <c>dman string</c> explains the C# keyword and <c>dman String</c> the
	/// <c>System.String</c> type. A learn.microsoft.com address, which is what links in pages point to, opens the API
	/// or article it names.
	/// </remarks>
	public static string? Render(string[] args, int columns, bool styled = true) => Render(args, columns, styled, DocsSource.Shared);

	/// <summary>
	/// Builds the page for the arguments from the docs in <paramref name="source" />.
	/// </summary>
	/// <param name="args">The words after <c>dman</c>.</param>
	/// <param name="columns">The number of columns to fill.</param>
	/// <param name="styled"><see langword="true" /> for bold and underline; <see langword="false" /> for plain text.</param>
	/// <param name="source">Where the docs are downloaded from.</param>
	/// <returns>
	/// The page, or <see langword="null" /> when the arguments are wrong or nothing matches.
	/// </returns>
	internal static string? Render(string[] args, int columns, bool styled, DocsSource source)
	{
		return Parse(args) is not var (name, language) ? null
			: !Links.TryParse(name, out var api, out var article) ? LanguageDocs.Render(name, language, source, columns, styled) ?? ApiDocs.Render(name, language, source, columns, styled)
			: api is not null ? ApiDocs.Render(api, language, source, columns, styled)
			: LanguageDocs.RenderArticle(article!, null, source, columns, styled);
	}

	/// <summary>
	/// Reads the language flag and the name from the arguments.
	/// </summary>
	/// <param name="args">The words after <c>dman</c>.</param>
	/// <returns>
	/// The name and language, or <see langword="null" /> unless there's exactly one name.
	/// </returns>
	internal static (string Name, DocLanguage Language)? Parse(string[] args)
	{
		var language = DocLanguage.CSharp;
		var names = new List<string>();

		foreach (var arg in args.ArgumentNotNull())
		{
			if (arg is "-vb" or "--vb")
			{
				language = DocLanguage.VisualBasic;
			}
			else if (arg is "-cs" or "--cs")
			{
				language = DocLanguage.CSharp;
			}
			else if (arg.StartsWith('-'))
			{
				return null;
			}
			else
			{
				names.Add(arg);
			}
		}

		return names.Count == 1 ? (names[0], language) : null;
	}
}
