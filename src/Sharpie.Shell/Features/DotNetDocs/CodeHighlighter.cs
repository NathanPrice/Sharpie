using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using CS = Microsoft.CodeAnalysis.CSharp;
using CSSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using VB = Microsoft.CodeAnalysis.VisualBasic;
using VBSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Sharpie.Shell.Features.DotNetDocs;

/// <summary>
/// Colors C# and Visual Basic code in a page's examples the way an editor does: keywords, strings, comments, numbers,
/// and names by what they are, such as types, methods, parameters and variables.
/// </summary>
/// <remarks>
/// The compiler decides what each name is, compiling the snippet against the running .NET's own assemblies. A token
/// rule can't, since <c>Console.WriteLine</c> and <c>person.Speak</c> look the same and only one starts with a type.
/// Snippets often don't compile on their own; names the compiler can't bind fall back to where they stand, so a name
/// before <c>(</c> is still a method and one after <c>new</c> still a type.
/// </remarks>
internal static class CodeHighlighter
{
	// Most doc snippets leave these out, the same set .NET projects import implicitly.
	private static readonly string[] ImplicitNamespaces =
		["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks"];

	// Loaded once per process: reading the metadata of every framework assembly takes a few hundred milliseconds.
	private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(LoadReferences);

	/// <summary>
	/// Colors code in a language, line by line.
	/// </summary>
	/// <param name="lines">The code.</param>
	/// <param name="language">The code's language, or <see langword="null" /> for anything else, which stays plain.</param>
	/// <returns>
	/// One styled line per line of code, with the same text.
	/// </returns>
	public static List<IReadOnlyList<Span>> Highlight(IReadOnlyList<string> lines, DocLanguage? language)
	{
		if (language is not { } known)
		{
			return [.. lines.Select(line => (IReadOnlyList<Span>)[new Span(line, SpanStyle.Plain)])];
		}

		var code = string.Join('\n', lines);
		var styles = known == DocLanguage.CSharp ? ClassifyCSharp(code) : ClassifyVisualBasic(code);

		return ToLines(code, styles);
	}

	/// <summary>
	/// Classifies each character of C# code.
	/// </summary>
	/// <param name="code">The code.</param>
	/// <returns>
	/// One style per character.
	/// </returns>
	/// <remarks>
	/// Statements compile as top-level statements. A snippet of members, like a property on its own, doesn't, so it's
	/// also tried inside a class, and whichever reads with fewer syntax errors is used.
	/// </remarks>
	private static SpanStyle[] ClassifyCSharp(string code)
	{
		var options = new CS.CSharpParseOptions(CS.LanguageVersion.Preview);
		var compilationOptions = new CS.CSharpCompilationOptions(OutputKind.ConsoleApplication);

		// A file of global usings, the way the .NET SDK adds implicit usings. The compilation options' own usings list
		// only applies to scripts, and a script can't hold the namespaces and classes full examples declare.
		var usings = CS.CSharpSyntaxTree.ParseText(string.Concat(ImplicitNamespaces.Select(name => $"global using {name};\n")), options);

		return Best(code, ["", "class Snippet\n{\n"], ["", "\n}"], text =>
		{
			var tree = CS.CSharpSyntaxTree.ParseText(text, options);

			return (tree, CS.CSharpCompilation.Create("Snippet", [usings, tree], References.Value, compilationOptions));
		}, ClassifyCSharpToken);
	}

	/// <summary>
	/// Classifies each character of Visual Basic code.
	/// </summary>
	/// <param name="code">The code.</param>
	/// <returns>
	/// One style per character.
	/// </returns>
	/// <remarks>
	/// Visual Basic has no top-level statements, so the snippet is also tried inside a <c>Sub</c> and inside a
	/// <c>Class</c>, and whichever reads with the fewest syntax errors is used.
	/// </remarks>
	private static SpanStyle[] ClassifyVisualBasic(string code)
	{
		var options = new VB.VisualBasicParseOptions(VB.LanguageVersion.Latest);
		var compilationOptions = new VB.VisualBasicCompilationOptions(OutputKind.ConsoleApplication, globalImports: VB.GlobalImport.Parse(ImplicitNamespaces));

		return Best(code, ["", "Module Snippet\nSub Main()\n", "Class Snippet\n"], ["", "\nEnd Sub\nEnd Module", "\nEnd Class"], text =>
		{
			var tree = VB.VisualBasicSyntaxTree.ParseText(text, options);

			return (tree, VB.VisualBasicCompilation.Create("Snippet", [tree], References.Value, compilationOptions));
		}, ClassifyVisualBasicToken);
	}

	/// <summary>
	/// Compiles the code as written and inside each wrapper, then classifies the version with the fewest syntax errors.
	/// </summary>
	/// <param name="code">The code.</param>
	/// <param name="prefixes">Text put before the code, the first one empty.</param>
	/// <param name="suffixes">Text put after the code, matching <paramref name="prefixes" />.</param>
	/// <param name="compile">Parses and compiles one version.</param>
	/// <param name="classify">Classifies one token.</param>
	/// <returns>
	/// One style per character of <paramref name="code" />, with the wrapper's characters taken off.
	/// </returns>
	private static SpanStyle[] Best(string code, string[] prefixes, string[] suffixes, Func<string, (SyntaxTree Tree, Compilation Compilation)> compile, Func<SyntaxToken, SemanticModel, SpanStyle> classify)
	{
		var versions = prefixes
			.Select((prefix, index) => (Prefix: prefix, Parsed: compile(prefix + code + suffixes[index])))
			.ToList();
		var (best, (tree, compilation)) = versions.MinBy(version => version.Parsed.Tree.GetDiagnostics().Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
		var model = compilation.GetSemanticModel(tree);
		var text = tree.GetText().ToString();
		var styles = new SpanStyle[text.Length];
		var root = tree.GetRoot();

		foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
		{
			Array.Fill(styles, classify(token, model), token.Span.Start, token.Span.Length);
		}

		// After the tokens, so a documentation comment's own tokens end up colored as the comment.
		foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true).Where(IsComment))
		{
			Array.Fill(styles, SpanStyle.Comment, trivia.Span.Start, trivia.Span.Length);
		}

		return styles[best.Length..(best.Length + code.Length)];
	}

	/// <summary>
	/// Classifies a C# token.
	/// </summary>
	/// <param name="token">The token.</param>
	/// <param name="model">The snippet's semantic model.</param>
	/// <returns>
	/// The token's style.
	/// </returns>
	private static SpanStyle ClassifyCSharpToken(SyntaxToken token, SemanticModel model)
	{
		var kind = (CS.SyntaxKind)token.RawKind;

		if (CS.SyntaxFacts.IsKeywordKind(kind) || token.Parent is CSSyntax.IdentifierNameSyntax { IsVar: true } || kind == CS.SyntaxKind.HashToken)
		{
			return SpanStyle.Keyword;
		}

		if (kind == CS.SyntaxKind.NumericLiteralToken)
		{
			return SpanStyle.Number;
		}

		if (token.Parent is CSSyntax.InterpolatedStringExpressionSyntax or CSSyntax.InterpolatedStringTextSyntax
			|| kind is CS.SyntaxKind.StringLiteralToken or CS.SyntaxKind.CharacterLiteralToken or CS.SyntaxKind.Utf8StringLiteralToken
				or CS.SyntaxKind.SingleLineRawStringLiteralToken or CS.SyntaxKind.MultiLineRawStringLiteralToken
				or CS.SyntaxKind.Utf8SingleLineRawStringLiteralToken or CS.SyntaxKind.Utf8MultiLineRawStringLiteralToken)
		{
			return SpanStyle.String;
		}

		if (kind != CS.SyntaxKind.IdentifierToken || token.Parent is not { } node)
		{
			return SpanStyle.Plain;
		}

		if (StyleOf(SymbolOf(node, model)) is { } style)
		{
			return style;
		}

		// The compiler couldn't bind the name, so go by where it stands.
		return node switch
		{
			CSSyntax.IdentifierNameSyntax name when name.Parent is CSSyntax.InvocationExpressionSyntax call && call.Expression == name => SpanStyle.Method,
			CSSyntax.IdentifierNameSyntax name when name.Parent is CSSyntax.MemberAccessExpressionSyntax access && access.Name == name
				&& access.Parent is CSSyntax.InvocationExpressionSyntax => SpanStyle.Method,
			CSSyntax.SimpleNameSyntax name when CS.SyntaxFacts.IsInTypeOnlyContext(name) => SpanStyle.Type,
			_ => SpanStyle.Plain,
		};
	}

	/// <summary>
	/// Classifies a Visual Basic token.
	/// </summary>
	/// <param name="token">The token.</param>
	/// <param name="model">The snippet's semantic model.</param>
	/// <returns>
	/// The token's style.
	/// </returns>
	private static SpanStyle ClassifyVisualBasicToken(SyntaxToken token, SemanticModel model)
	{
		var kind = (VB.SyntaxKind)token.RawKind;

		if (VB.VisualBasicExtensions.IsKeyword(token) || kind == VB.SyntaxKind.HashToken)
		{
			return SpanStyle.Keyword;
		}

		if (kind is VB.SyntaxKind.IntegerLiteralToken or VB.SyntaxKind.FloatingLiteralToken or VB.SyntaxKind.DecimalLiteralToken)
		{
			return SpanStyle.Number;
		}

		if (kind is VB.SyntaxKind.StringLiteralToken or VB.SyntaxKind.CharacterLiteralToken or VB.SyntaxKind.DateLiteralToken
			|| token.Parent is VBSyntax.InterpolatedStringExpressionSyntax or VBSyntax.InterpolatedStringTextSyntax)
		{
			return SpanStyle.String;
		}

		if (kind != VB.SyntaxKind.IdentifierToken || token.Parent is not { } node)
		{
			return SpanStyle.Plain;
		}

		// A declared name's token sits on a ModifiedIdentifier, whose parent is the declaration the symbol belongs to.
		if (StyleOf(SymbolOf(node, model) ?? (node is VBSyntax.ModifiedIdentifierSyntax ? SymbolOf(node.Parent!, model) : null)) is { } style)
		{
			return style;
		}

		return node switch
		{
			VBSyntax.IdentifierNameSyntax name when name.Parent is VBSyntax.InvocationExpressionSyntax call && call.Expression == name => SpanStyle.Method,
			VBSyntax.IdentifierNameSyntax name when name.Parent is VBSyntax.MemberAccessExpressionSyntax access && access.Name == name
				&& access.Parent is VBSyntax.InvocationExpressionSyntax => SpanStyle.Method,
			VBSyntax.IdentifierNameSyntax name when name.Parent is VBSyntax.SimpleAsClauseSyntax or VBSyntax.ObjectCreationExpressionSyntax => SpanStyle.Type,
			_ => SpanStyle.Plain,
		};
	}

	/// <summary>
	/// Finds the symbol a name declares or refers to.
	/// </summary>
	/// <param name="node">The node the name's token belongs to.</param>
	/// <param name="model">The snippet's semantic model.</param>
	/// <returns>
	/// The declared symbol, else the bound one, else the compiler's first guess when binding was ambiguous or failed;
	/// <see langword="null" /> when there's none.
	/// </returns>
	private static ISymbol? SymbolOf(SyntaxNode node, SemanticModel model)
	{
		if (model.GetDeclaredSymbol(node) is { } declared)
		{
			return declared;
		}

		var info = model.GetSymbolInfo(node);

		return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
	}

	/// <summary>
	/// Gets the style for what a symbol is.
	/// </summary>
	/// <param name="symbol">The symbol, or <see langword="null" />.</param>
	/// <returns>
	/// The style; plain for fields, properties, events and namespaces; or <see langword="null" /> when there's no
	/// symbol, so the caller can fall back to the syntax.
	/// </returns>
	/// <remarks>
	/// A type the compiler couldn't find still binds to an error type, which counts as a type here, since it stands
	/// where a type goes. A constructor is drawn as its type, the way <c>new List()</c> reads.
	/// </remarks>
	private static SpanStyle? StyleOf(ISymbol? symbol) => symbol switch
	{
		null => null,
		ITypeSymbol => SpanStyle.Type,
		IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => SpanStyle.Type,
		IMethodSymbol => SpanStyle.Method,
		IParameterSymbol => SpanStyle.Parameter,
		ILocalSymbol or IRangeVariableSymbol => SpanStyle.Variable,
		_ => SpanStyle.Plain,
	};

	/// <summary>
	/// Checks whether trivia is a comment in either language.
	/// </summary>
	/// <param name="trivia">The trivia.</param>
	/// <returns>
	/// <see langword="true" /> for line, block and documentation comments.
	/// </returns>
	private static bool IsComment(SyntaxTrivia trivia) =>
		trivia.Language == LanguageNames.CSharp
			? (CS.SyntaxKind)trivia.RawKind is CS.SyntaxKind.SingleLineCommentTrivia or CS.SyntaxKind.MultiLineCommentTrivia
				or CS.SyntaxKind.SingleLineDocumentationCommentTrivia or CS.SyntaxKind.MultiLineDocumentationCommentTrivia
			: (VB.SyntaxKind)trivia.RawKind is VB.SyntaxKind.CommentTrivia or VB.SyntaxKind.DocumentationCommentTrivia;

	/// <summary>
	/// Joins classified characters back into styled lines.
	/// </summary>
	/// <param name="code">The code.</param>
	/// <param name="styles">One style per character.</param>
	/// <returns>
	/// One styled line per line of code, each a run per change of style.
	/// </returns>
	private static List<IReadOnlyList<Span>> ToLines(string code, SpanStyle[] styles)
	{
		var lines = new List<IReadOnlyList<Span>>();
		var current = new List<Span>();
		var start = 0;

		for (var index = 0; index <= code.Length; index++)
		{
			var lineEnd = index == code.Length || code[index] == '\n';

			if (index > start && (lineEnd || styles[index] != styles[start]))
			{
				current.Add(new Span(code[start..index], styles[start]));
				start = index;
			}

			if (lineEnd)
			{
				lines.Add(current);
				current = [];
				start = index + 1;
			}
		}

		return lines;
	}

	/// <summary>
	/// Loads references to the running .NET's own assemblies, which the snippets compile against.
	/// </summary>
	/// <returns>
	/// One reference per framework assembly.
	/// </returns>
	/// <remarks>
	/// The runtime lists every assembly it can load in <c>TRUSTED_PLATFORM_ASSEMBLIES</c>. Only the framework's are
	/// kept: the app's own, like Avalonia or the compiler itself, would add names no example uses.
	/// </remarks>
	private static ImmutableArray<MetadataReference> LoadReferences()
	{
		var paths = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
		var framework = Path.GetDirectoryName(typeof(object).Assembly.Location);

		return [.. paths
			.Where(path => Path.GetDirectoryName(path) == framework)
			.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
	}
}
