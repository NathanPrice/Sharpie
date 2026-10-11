using FluentAssertions;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Shell.Tests.Features.DotNetDocs;

/// <summary>
/// Tests for <see cref="DmanCommand" />, against a small framework index, keyword list and article shaped like
/// Microsoft's.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class DmanCommandTests : IDisposable
{
	private const string ApiRepository = "https://raw.githubusercontent.com/dotnet/dotnet-api-docs/main/xml/";
	private const string Docs = "https://raw.githubusercontent.com/dotnet/docs/main/docs/";

	private readonly ShellEnvironment environment = new();
	private readonly FakeDocs server = new();
	private readonly DocsSource source;

	/// <summary>
	/// Initializes a new instance of the <see cref="DmanCommandTests" /> class with the <c>string</c> keyword and the
	/// <c>System.String</c> type.
	/// </summary>
	public DmanCommandTests()
	{
		this.server.Add($"{ApiRepository}FrameworksIndex/{ApiDocs.Moniker}.xml", $"""<Framework><Namespace Name="System"><Type Name="System.String" /></Namespace></Framework>""");
		this.server.Add($"{ApiRepository}System/String.xml", """<Type Name="String" FullName="System.String"><Docs><summary>Text.</summary></Docs></Type>""");
		this.server.Add($"{Docs}csharp/language-reference/keywords/index.md", "[`string`](../builtin-types/reference-types.md#the-string-type)");
		this.server.Add($"{Docs}csharp/language-reference/builtin-types/reference-types.md", "## The string type\n\nText.\n");
		this.source = this.server.CreateSource(TimeProvider.System);
	}

	/// <summary>
	/// Checks that arguments without exactly one name, or with an unknown option, give no page before any docs are
	/// looked up.
	/// </summary>
	/// <param name="args">The words after <c>dman</c>.</param>
	[Theory]
	[InlineData]
	[InlineData("List", "String")]
	[InlineData("-x", "List")]
	[InlineData("-vb")]
	public void Render_BadArguments_ReturnsNull(params string[] args)
	{
		// Act
		var result = DmanCommand.Render(args, 80);

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks how the arguments are read: one name, with <c>-vb</c> or <c>-cs</c> in either spelling picking the
	/// language and the last one winning.
	/// </summary>
	/// <param name="args">The words after <c>dman</c>, separated by spaces.</param>
	/// <param name="visualBasic">Whether Visual Basic is expected rather than C#.</param>
	[Theory]
	[InlineData("List", false)]
	[InlineData("-vb List", true)]
	[InlineData("List --vb", true)]
	[InlineData("-vb -cs List", false)]
	[InlineData("--vb --cs List", false)]
	public void Parse_OneName_ReturnsNameAndLanguage(string args, bool visualBasic)
	{
		// Act
		var result = DmanCommand.Parse(args.Split(' '));

		// Assert
		_ = result.Should().Be(("List", visualBasic ? DocLanguage.VisualBasic : DocLanguage.CSharp));
	}

	/// <summary>
	/// Checks that a keyword is looked up before an API, so <c>string</c> is the C# keyword and <c>String</c>, which no
	/// keyword matches in C#'s case, is the type.
	/// </summary>
	/// <param name="name">The name typed.</param>
	/// <param name="header">The start of the page's first line.</param>
	[Theory]
	[InlineData("string", "string(7cs)")]
	[InlineData("String", "String(3net)")]
	[InlineData("https://learn.microsoft.com/dotnet/api/system.string", "String(3net)")]
	[InlineData("https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/reference-types#the-string-type", "reference-types(7cs)")]
	public void Render_Name_FindsKeywordBeforeApi(string name, string header)
	{
		// Act
		var result = DmanCommand.Render([name], 80, styled: false, this.source);

		// Assert
		_ = result.Should().StartWith(header);
	}

	/// <summary>
	/// Checks that wrong arguments print the usage and look nothing up.
	/// </summary>
	[Fact]
	public void Run_NoName_PrintsUsage()
	{
		// Act
		new DmanCommand().Run([]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Contain("usage: dman [-vb] NAME");
	}

	/// <inheritdoc />
	public void Dispose()
	{
		this.server.Dispose();
		this.environment.Dispose();
	}
}
