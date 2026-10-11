using System.Xml.Linq;
using FluentAssertions;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Shell.Tests.Features.DotNetDocs;

/// <summary>
/// Tests for <see cref="ApiDocs" />, against a small framework index and type file shaped like Microsoft's.
/// </summary>
public sealed class ApiDocsTests : IDisposable
{
	private const string Repository = "https://raw.githubusercontent.com/dotnet/dotnet-api-docs/main/xml/";

	private static readonly string Index = $"""
		<Framework Name="{ApiDocs.Moniker}">
		  <Namespace Name="System">
		    <Type Name="System.String" Id="T:System.String">
		      <Member Id="M:System.String.#ctor(System.Char[])" />
		      <Member Id="M:System.String.#ctor(System.Char,System.Int32)" />
		      <Member Id="F:System.String.Empty" />
		      <Member Id="P:System.String.Length" />
		      <Member Id="M:System.String.Split(System.Char[])" />
		      <Member Id="M:System.String.Split(System.Char,System.Int32)" />
		      <Member Id="M:System.String.op_Equality(System.String,System.String)" />
		      <Member Id="E:System.String.Changed" />
		      <Member Id="M:System.String.System#IComparable#CompareTo(System.Object)" />
		      <Member Id="M:System.Object.Equals(System.Object)" />
		    </Type>
		    <Type Name="System.Environment/SpecialFolder" Id="T:System.Environment.SpecialFolder" />
		    <Type Name="System.Int32" Id="T:System.Int32">
		      <Member Id="F:System.Int32.MaxValue" />
		    </Type>
		    <Type Name="System.Tuple`2" Id="T:System.Tuple`2">
		      <Member Id="P:System.Tuple`2.Item1" />
		    </Type>
		  </Namespace>
		  <Namespace Name="System.Collections.Generic">
		    <Type Name="System.Collections.Generic.List`1" Id="T:System.Collections.Generic.List`1" />
		  </Namespace>
		  <Namespace Name="System.IO">
		    <Type Name="System.IO.File" Id="T:System.IO.File" />
		  </Namespace>
		  <Namespace Name="Other.Library">
		    <Type Name="Other.Library.List`1" Id="T:Other.Library.List`1" />
		  </Namespace>
		</Framework>
		""";

	// Every Timer and Clock would rank first if one of the ranking rules were skipped.
	private static readonly string RankingIndex = $"""
		<Framework Name="{ApiDocs.Moniker}">
		  <Namespace Name="System.X"><Type Name="System.X.timer" /></Namespace>
		  <Namespace Name="Beta"><Type Name="Beta.Timer" /></Namespace>
		  <Namespace Name="System.A.B"><Type Name="System.A.B.Timer" /></Namespace>
		  <Namespace Name="System.Configuration"><Type Name="System.Configuration.Timer" /></Namespace>
		  <Namespace Name="System.Z"><Type Name="System.Z.Timer" /></Namespace>
		  <Namespace Name="System.Y"><Type Name="System.Y.Timer" /><Type Name="System.Y.MyTimer" /></Namespace>
		  <Namespace Name="Alpha"><Type Name="Alpha.Clock" /></Namespace>
		  <Namespace Name="System"><Type Name="System.Clock" /></Namespace>
		</Framework>
		""";

	private static readonly string StringType = $$"""
		<Type Name="String" FullName="System.String">
		  <TypeSignature Language="C#" Value="public sealed class String : IComparable" />
		  <TypeSignature Language="VB.NET" Value="Public NotInheritable Class String&#xA;Implements IComparable" />
		  <AssemblyInfo><AssemblyName>mscorlib</AssemblyName><AssemblyVersion>4.0.0.0</AssemblyVersion></AssemblyInfo>
		  <AssemblyInfo><AssemblyName>System.Runtime</AssemblyName><AssemblyVersion>1.0.0.0</AssemblyVersion><AssemblyVersion>{{Environment.Version.Major}}.0.0.0</AssemblyVersion></AssemblyInfo>
		  <Docs>
		    <summary>Represents text.</summary>
		    <remarks>Strings never change.</remarks>
		    <example>Join two strings with <c>+</c>.</example>
		  </Docs>
		  <Members>
		    <MemberGroup MemberName="Split">
		      <Docs><summary>Splits a string into parts.</summary></Docs>
		    </MemberGroup>
		    <Member MemberName=".ctor">
		      <MemberSignature Language="C#" Value="public String (char[] value);" />
		      <MemberSignature Language="DocId" Value="M:System.String.#ctor(System.Char[])" />
		      <MemberType>Constructor</MemberType>
		      <Parameters><Parameter Name="value" Type="System.Char[]" /></Parameters>
		      <Docs><summary>Makes a string from characters.</summary></Docs>
		    </Member>
		    <Member MemberName=".ctor">
		      <MemberSignature Language="C#" Value="public String (char c, int count);" />
		      <MemberSignature Language="DocId" Value="M:System.String.#ctor(System.Char,System.Int32)" />
		      <MemberType>Constructor</MemberType>
		      <Docs><summary>Repeats a character.</summary></Docs>
		    </Member>
		    <Member MemberName="Empty">
		      <MemberSignature Language="C#" Value="public static readonly string Empty;" />
		      <MemberSignature Language="DocId" Value="F:System.String.Empty" />
		      <MemberType>Field</MemberType>
		      <Docs><summary>The empty string.</summary></Docs>
		    </Member>
		    <Member MemberName="Length">
		      <MemberSignature Language="C#" Value="public int Count { get; }" FrameworkAlternate="net-5.0" />
		      <MemberSignature Language="C#" Value="public int Length { get; }" FrameworkAlternate="net-6.0;{{ApiDocs.Moniker}}" />
		      <MemberSignature Language="C#" Value="public int Size { get; }" />
		      <MemberSignature Language="DocId" Value="P:System.String.Length" />
		      <MemberType>Property</MemberType>
		      <Docs><summary>Gets the length.</summary><value>The number of characters.</value><returns> </returns></Docs>
		    </Member>
		    <Member MemberName="Split">
		      <MemberSignature Language="C#" Value="public string[] Split (params char[] separator);" />
		      <MemberSignature Language="VB.NET" Value="Public Function Split (ParamArray separator As Char()) As String()" />
		      <MemberSignature Language="DocId" Value="M:System.String.Split(System.Char[])" />
		      <MemberType>Method</MemberType>
		      <Parameters><Parameter Name="separator" Type="System.Char[]" /></Parameters>
		      <Docs>
		        <param name="separator">The delimiters, or <see langword="null" />.</param>
		        <summary>Splits a <see cref="T:System.String" />.</summary>
		        <returns>The parts.</returns>
		        <exception cref="T:System.ArgumentException"><paramref name="separator" /> is bad.</exception>
		        <remarks>Empty parts are kept.</remarks>
		      </Docs>
		    </Member>
		    <Member MemberName="Split">
		      <MemberSignature Language="C#" Value="public string[] Split (char old, int count);" FrameworkAlternate="net-5.0" />
		      <MemberSignature Language="C#" Value="public string[] Split (char separator, int count);" />
		      <MemberSignature Language="DocId" Value="M:System.String.Split(System.Char,System.Int32)" />
		      <MemberType>Method</MemberType>
		      <Parameters><Parameter Name="separator" Type="System.Char" /><Parameter Name="count" Type="System.Int32" /></Parameters>
		      <Docs>
		        <typeparam name="T">Unused.</typeparam>
		        <summary>Splits into at most <paramref name="count" /> parts.</summary>
		        <example>Split <c>a,b</c> in two.</example>
		      </Docs>
		    </Member>
		    <Member MemberName="op_Equality">
		      <MemberSignature Language="C#" Value="public static bool operator == (string a, string b);" />
		      <MemberSignature Language="DocId" Value="M:System.String.op_Equality(System.String,System.String)" />
		      <MemberType>Method</MemberType>
		      <Docs><summary>Compares two strings.</summary></Docs>
		    </Member>
		    <Member MemberName="Changed">
		      <MemberSignature Language="C#" Value="public event EventHandler Changed;" />
		      <MemberSignature Language="DocId" Value="E:System.String.Changed" />
		      <MemberType>Event</MemberType>
		      <Docs><summary>Never raised.</summary></Docs>
		    </Member>
		    <Member MemberName="System.IComparable.CompareTo">
		      <MemberSignature Language="C#" Value="int IComparable.CompareTo (object value);" />
		      <MemberSignature Language="DocId" Value="M:System.String.System#IComparable#CompareTo(System.Object)" />
		      <MemberType>Method</MemberType>
		      <Docs><summary>Explicit interface member.</summary></Docs>
		    </Member>
		    <Member MemberName="Copy">
		      <MemberSignature Language="C#" Value="public static string Copy (string str);" />
		      <MemberSignature Language="DocId" Value="M:System.String.Copy(System.String)" />
		      <MemberType>Method</MemberType>
		      <Docs><summary>Only in .NET Framework.</summary></Docs>
		    </Member>
		  </Members>
		</Type>
		""";

	// No members element, and the index lists a member the file doesn't have.
	private static readonly string TupleType = """
		<Type Name="Tuple&lt;T1,T2&gt;" FullName="System.Tuple&lt;T1,T2&gt;">
		  <TypeSignature Language="C#" Value="public class Tuple&lt;T1,T2&gt;" />
		  <Docs><summary>A pair.</summary></Docs>
		</Type>
		""";

	// One assembly only, for no .NET version in particular. MaxValue has no docs, its signature is only for an older
	// .NET, and it has a second doc id the index doesn't list.
	private static readonly string Int32Type = """
		<Type Name="Int32" FullName="System.Int32">
		  <TypeSignature Language="C#" Value="public struct Int32" />
		  <AssemblyInfo><AssemblyName>mscorlib</AssemblyName><AssemblyVersion>4.0.0.0</AssemblyVersion></AssemblyInfo>
		  <Docs><summary>A whole number.</summary></Docs>
		  <Members>
		    <Member MemberName="MaxValue">
		      <MemberSignature Language="C#" Value="public const int MaxValue = 2147483647;" FrameworkAlternate="net-5.0" />
		      <MemberSignature Language="DocId" Value="F:System.Int32.Max" />
		      <MemberSignature Language="DocId" Value="F:System.Int32.MaxValue" />
		      <MemberType>Field</MemberType>
		    </Member>
		  </Members>
		</Type>
		""";

	private static readonly string ListType = """
		<Type Name="List&lt;T&gt;" FullName="System.Collections.Generic.List&lt;T&gt;">
		  <TypeSignature Language="C#" Value="public class List&lt;T&gt;" />
		  <Docs><summary>A list.</summary></Docs>
		</Type>
		""";

	private static readonly string SystemNamespace = """
		<Namespace Name="System">
		  <Docs><summary>Fundamental classes.</summary></Docs>
		</Namespace>
		""";

	// Noon UTC, so the footer's local date is the same day in every time zone the tests run in.
	private static readonly DateTimeOffset Downloaded = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

	private readonly FakeDocs server = new();
	private readonly DocsSource source;

	/// <summary>
	/// Initializes a new instance of the <see cref="ApiDocsTests" /> class.
	/// </summary>
	public ApiDocsTests()
	{
		this.server.Add($"{Repository}FrameworksIndex/{ApiDocs.Moniker}.xml", Index);
		this.server.Add($"{Repository}System/String.xml", StringType);
		this.server.Add($"{Repository}ns-System.xml", SystemNamespace);
		this.server.Add($"{Repository}System/Int32.xml", Int32Type);
		this.server.Add(new Uri($"{Repository}System/Tuple`2.xml").AbsoluteUri, TupleType);
		this.server.Add(new Uri($"{Repository}System.Collections.Generic/List`1.xml").AbsoluteUri, ListType);
		this.source = this.server.CreateSource(new ManualTime(Downloaded));
	}

	/// <summary>
	/// Checks that a short, partial, generic or full type name finds the type.
	/// </summary>
	/// <param name="query">The name typed.</param>
	[Theory]
	[InlineData("List")]
	[InlineData("Generic.List")]
	[InlineData("List<T>")]
	[InlineData("System.Collections.Generic.List`1")]
	public void Resolve_TypeName_FindsSystemTypeFirst(string query)
	{
		// Act
		var result = ApiDocs.Resolve(query, this.source);

		// Assert
		_ = result!.Type!.Name.Should().Be("System.Collections.Generic.List`1");
	}

	/// <summary>
	/// Checks that when two types share a name the <c>System</c> one wins and the other is listed for SEE ALSO.
	/// </summary>
	[Fact]
	public void Resolve_AmbiguousName_PrefersSystemAndListsOther()
	{
		// Act
		var result = ApiDocs.Resolve("List", this.source);

		// Assert
		_ = result!.Type!.Namespace.Should().Be("System.Collections.Generic");
		_ = result.Others.Should().Equal("Other.Library.List`1");
	}

	/// <summary>
	/// Checks that a type and member name finds the member, ignoring case.
	/// </summary>
	[Fact]
	public void Resolve_MemberName_FindsTypeAndMember()
	{
		// Act
		var result = ApiDocs.Resolve("string.split", this.source);

		// Assert
		_ = result!.Type!.Name.Should().Be("System.String");
		_ = result.Member.Should().Be("split");
	}

	/// <summary>
	/// Checks that a nested type is found by its dotted name, though the index separates it with a slash.
	/// </summary>
	[Fact]
	public void Resolve_NestedType_FindsIt()
	{
		// Act
		var result = ApiDocs.Resolve("Environment.SpecialFolder", this.source);

		// Assert
		_ = result!.Type!.Name.Should().Be("System.Environment/SpecialFolder");
		_ = result.Member.Should().BeNull();
	}

	/// <summary>
	/// Checks that a namespace name finds the namespace with its types.
	/// </summary>
	[Fact]
	public void Resolve_Namespace_ListsItsTypes()
	{
		// Act
		var result = ApiDocs.Resolve("System.IO", this.source);

		// Assert
		_ = result!.Type.Should().BeNull();
		_ = result.NamespaceTypes.Should().Equal("File");
	}

	/// <summary>
	/// Checks that names the index doesn't have find nothing.
	/// </summary>
	/// <param name="query">The name typed.</param>
	[Theory]
	[InlineData("Nothing")]
	[InlineData("String.Nothing")]
	[InlineData("")]
	public void Resolve_UnknownName_ReturnsNull(string query)
	{
		// Act
		var result = ApiDocs.Resolve(query, this.source);

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks the whole type page: one line per member name in the order man pages use, the count of other overloads,
	/// the member group's summary over the first overload's, the signature for this .NET version, and the current
	/// assembly. Members this version doesn't have, explicit interface members and "To be added." are left out.
	/// </summary>
	[Fact]
	public void Render_TypePage_WritesWholePage()
	{
		// Act
		var result = ApiDocs.Render("String", DocLanguage.CSharp, this.source, 80, styled: false);

		// Assert
		_ = result.Should().Be($$"""
			String(3net)                   .NET API Reference                   String(3net)

			NAME
			       System.String - Represents text.

			SYNOPSIS
			       public sealed class String : IComparable

			       Namespace: System    Assembly: System.Runtime

			DESCRIPTION
			       Strings never change.

			EXAMPLES
			       Join two strings with +.

			CONSTRUCTORS
			       public String(char[] value) (+1 more)
			              Makes a string from characters.

			FIELDS
			       public static readonly string Empty
			              The empty string.

			PROPERTIES
			       public int Length { get; }
			              Gets the length.

			METHODS
			       public string[] Split(params char[] separator) (+1 more)
			              Splits a string into parts.

			OPERATORS
			       public static bool operator == (string a, string b)
			              Compares two strings.

			EVENTS
			       public event EventHandler Changed
			              Never raised.

			SEE ALSO
			       dman String.MEMBER

			       https://learn.microsoft.com/dotnet/api/system.string

			.NET {{Environment.Version.Major}}                     checked 2026-01-15                      String(3net)

			""");
	}

	/// <summary>
	/// Checks that a type page with <c>-vb</c> puts each line of a Visual Basic type signature on its own line.
	/// </summary>
	[Fact]
	public void Render_TypePageInVisualBasic_KeepsSignatureLines()
	{
		// Act
		var result = ApiDocs.Render("String", DocLanguage.VisualBasic, this.source, 80, styled: false);

		// Assert
		_ = result.Should().Contain("SYNOPSIS\n       Public NotInheritable Class String\n       Implements IComparable\n\n");
	}

	/// <summary>
	/// Checks the whole member page: every overload in SYNOPSIS, then each one under its own heading with its
	/// parameters, return value, exceptions, remarks and examples.
	/// </summary>
	[Fact]
	public void Render_MemberPage_WritesEveryOverload()
	{
		// Act
		var result = ApiDocs.Render("String.Split", DocLanguage.CSharp, this.source, 80, styled: false);

		// Assert
		_ = result.Should().Be($"""
			String.Split(3net)             .NET API Reference             String.Split(3net)

			NAME
			       System.String.Split - Splits a string into parts.

			SYNOPSIS
			       public string[] Split(params char[] separator)
			       public string[] Split(char separator, int count)

			DESCRIPTION
			   public string[] Split(params char[] separator)
			       Splits a String.

			       separator
			              The delimiters, or null.

			       Returns
			              The parts.

			       Throws ArgumentException
			              separator is bad.

			       Empty parts are kept.

			   public string[] Split(char separator, int count)
			       Splits into at most count parts.

			       T
			              Unused.

			       Split a,b in two.

			SEE ALSO
			       System.String(3net)

			       https://learn.microsoft.com/dotnet/api/system.string.split

			.NET {Environment.Version.Major}                  checked 2026-01-15                   String.Split(3net)

			""");
	}

	/// <summary>
	/// Checks that a member with one form has no heading per overload, shows its value, and is found in any case.
	/// </summary>
	[Fact]
	public void Render_PropertyPage_ShowsValueWithoutOverloadHeading()
	{
		// Act
		var result = ApiDocs.Render("string.length", DocLanguage.CSharp, this.source, 80, styled: false);

		// Assert
		_ = result.Should().Be($$"""
			String.Length(3net)            .NET API Reference            String.Length(3net)

			NAME
			       System.String.Length - Gets the length.

			SYNOPSIS
			       public int Length { get; }

			DESCRIPTION
			       Gets the length.

			       Value
			              The number of characters.

			SEE ALSO
			       System.String(3net)

			       https://learn.microsoft.com/dotnet/api/system.string.length

			.NET {{Environment.Version.Major}}                  checked 2026-01-15                  String.Length(3net)

			""");
	}

	/// <summary>
	/// Checks that a member page with <c>-vb</c> shows the Visual Basic signature where there is one and the C# one
	/// where there isn't.
	/// </summary>
	[Fact]
	public void Render_MemberPageInVisualBasic_ShowsVisualBasicSignature()
	{
		// Act
		var result = ApiDocs.Render("String.Split", DocLanguage.VisualBasic, this.source, 100, styled: false);

		// Assert
		_ = result.Should().Contain("SYNOPSIS\n       Public Function Split(ParamArray separator As Char()) As String()\n       public string[] Split(char separator, int count)\n");
	}

	/// <summary>
	/// Checks the whole namespace page, with its types laid out down then across in columns like <c>ls</c>, generic
	/// types written the way C# reads them, and only the namespaces directly under it.
	/// </summary>
	[Fact]
	public void Render_NamespacePage_ListsTypesInColumns()
	{
		// Act
		var result = ApiDocs.Render("System", DocLanguage.CSharp, this.source, 70, styled: false);

		// Assert
		_ = string.Join('\n', result!.Split('\n').Select(line => line.TrimEnd())).Should().Be($"""
			System(3net)              .NET API Reference              System(3net)

			NAME
			       System - Fundamental classes.

			TYPES
			       Environment.SpecialFolder  String
			       Int32                      Tuple<,>

			NAMESPACES
			       System.IO

			SEE ALSO
			       dman System.TYPE for a type's page.

			       https://learn.microsoft.com/dotnet/api/system

			.NET {Environment.Version.Major}                checked 2026-01-15                 System(3net)

			""");
	}

	/// <summary>
	/// Checks that a styled type page links each member's name to its page, but not a constructor's, which has no
	/// page of its own, and underlines parameter names.
	/// </summary>
	[Fact]
	public void Render_StyledTypePage_LinksMemberNamesButNotConstructors()
	{
		// Act
		var result = ApiDocs.Render("String", DocLanguage.CSharp, this.source, 80, styled: true);

		// Assert
		_ = result.Should().Contain($"public {Bold("String")}(char[] {Underline("value")}) (+1 more)")
			.And.Contain($"public string[] {Link("https://learn.microsoft.com/dotnet/api/system.string.split", Bold("Split"))}(params char[] {Underline("separator")})")
			.And.Contain(Link("https://learn.microsoft.com/dotnet/api/system.string", "https://learn.microsoft.com/dotnet/api/system.string"));
	}

	/// <summary>
	/// Checks that a styled member page links each exception to its page.
	/// </summary>
	[Fact]
	public void Render_StyledMemberPage_LinksExceptions()
	{
		// Act
		var result = ApiDocs.Render("String.Split", DocLanguage.CSharp, this.source, 80, styled: true);

		// Assert
		_ = result.Should().Contain($"Throws {Link("https://learn.microsoft.com/dotnet/api/system.argumentexception", Bold("ArgumentException"))}")
			.And.Contain(Link("https://learn.microsoft.com/dotnet/api/system.string", Bold("System.String(3net)")));
	}

	/// <summary>
	/// Checks the order matches are ranked in: an exact-case match first, then <c>System</c> namespaces, then fewer
	/// dots, then shorter names, then by name.
	/// </summary>
	[Fact]
	public void Resolve_SeveralTypesMatch_RanksThem()
	{
		// Act
		var result = ResolveIn(RankingIndex, "Timer");

		// Assert
		_ = result!.Type!.Name.Should().Be("System.Y.Timer");
		_ = result.Others.Should().Equal("System.Z.Timer", "System.Configuration.Timer", "System.A.B.Timer", "Beta.Timer", "System.X.timer");
	}

	/// <summary>
	/// Checks that a type in the <c>System</c> namespace itself counts as a <c>System</c> type, over a shorter name.
	/// </summary>
	[Fact]
	public void Resolve_TypeInSystemNamespace_RanksFirst()
	{
		// Act
		var result = ResolveIn(RankingIndex, "Clock");

		// Assert
		_ = result!.Type!.Name.Should().Be("System.Clock");
		_ = result.Others.Should().Equal("Alpha.Clock");
	}

	/// <summary>
	/// Checks that a member the index lists under a type but that belongs to another type, like an inherited one,
	/// isn't found as the type's member.
	/// </summary>
	[Fact]
	public void Resolve_MemberOfAnotherType_ReturnsNull()
	{
		// Act
		var result = ApiDocs.Resolve("String.Equals", this.source);

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks how each kind of doc comment XML turns into markdown.
	/// </summary>
	/// <param name="xml">The doc element.</param>
	/// <param name="expected">The markdown.</param>
	[Theory]
	[InlineData("<summary>To be added.</summary>", "")]
	[InlineData("<summary> To be added. </summary>", "")]
	[InlineData("<summary>a  \n  b &lt; c</summary>", "a b &lt; c")]
	[InlineData("<remarks><format type=\"text/markdown\"><![CDATA[# Head\n\n<b>x</b>]]></format></remarks>", "# Head\n\n<b>x</b>")]
	[InlineData("<summary><see langword=\"null\" /></summary>", "`null`")]
	[InlineData("<summary><see cref=\"T:System.String\" /></summary>", "<xref:System.String>")]
	[InlineData("<summary><see cref=\"String\" /></summary>", "<xref:String>")]
	[InlineData("<summary><see cref=\"T:\" /></summary>", "<xref:T:>")]
	[InlineData("<summary><see href=\"https://x.test\">site</see></summary>", "site")]
	[InlineData("<summary><see href=\"https://x.test\" /></summary>", "https://x.test")]
	[InlineData("<summary>a<seealso cref=\"T:X\" />b</summary>", "ab")]
	[InlineData("<summary>a<seealso cref=\"T:X\">see</seealso>b</summary>", "ab")]
	[InlineData("<summary><paramref name=\"p\" /> and <typeparamref name=\"T\" /></summary>", "*p* and *T*")]
	[InlineData("<summary><c>List&lt;T&gt;</c></summary>", "`List&lt;T>`")]
	[InlineData("<summary><code language=\"csharp\">var x = 1;</code></summary>", "\n\n```csharp\nvar x = 1;\n```\n\n")]
	[InlineData("<summary><para>One</para><block>Two</block></summary>", "\n\nOne\n\n\n\nTwo\n\n")]
	[InlineData("<summary><list><item><term>A</term><description>first</description></item><item>plain</item></list></summary>", "\n\n\n- **A**: first\n- plain\n\n")]
	[InlineData("<summary><b>w</b><strong>x</strong><i>y</i><em>z</em></summary>", "**w****x***y**z*")]
	[InlineData("<summary><span>q</span></summary>", "q")]
	public void DocText_DocElement_TurnsIntoMarkdown(string xml, string expected)
	{
		// Act
		var result = ApiDocs.DocText(XElement.Parse(xml));

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a missing doc element gives no markdown.
	/// </summary>
	[Fact]
	public void DocText_NoElement_ReturnsEmpty()
	{
		// Act
		var result = ApiDocs.DocText(null);

		// Assert
		_ = result.Should().BeEmpty();
	}

	/// <summary>
	/// Writes text in bold the way a styled page does, by overstriking each character.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <returns>
	/// Each character, a backspace, and the character again.
	/// </returns>
	private static string Bold(string text) => string.Concat(text.Select(character => $"{character}\b{character}"));

	/// <summary>
	/// Writes text underlined the way a styled page does, by overstriking each character on an underscore.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <returns>
	/// An underscore, a backspace, and the character, for each character.
	/// </returns>
	private static string Underline(string text) => string.Concat(text.Select(character => $"_\b{character}"));

	/// <summary>
	/// Wraps text in an OSC 8 hyperlink the way a styled page does.
	/// </summary>
	/// <param name="url">The address.</param>
	/// <param name="text">The text shown.</param>
	/// <returns>
	/// The text between the link's start and end sequences.
	/// </returns>
	private static string Link(string url, string text) => $"\e]8;;{url}\e\\{text}\e]8;;\e\\";

	/// <summary>
	/// Looks a name up in an index of its own.
	/// </summary>
	/// <param name="index">The framework index file.</param>
	/// <param name="query">The name typed.</param>
	/// <returns>
	/// What <see cref="ApiDocs.Resolve" /> found.
	/// </returns>
	private static ApiTarget? ResolveIn(string index, string query)
	{
		using var server = new FakeDocs();

		server.Add($"{Repository}FrameworksIndex/{ApiDocs.Moniker}.xml", index);

		return ApiDocs.Resolve(query, server.CreateSource(TimeProvider.System));
	}

	/// <summary>
	/// Checks the type page fallbacks: the only assembly when none is for this .NET version, the only signature when
	/// none is for this version or for all, a member listed by its second doc id, and no summary line for a member
	/// without docs. Sections with no members are left out.
	/// </summary>
	[Fact]
	public void Render_TypePageWithFallbacks_WritesWholePage()
	{
		// Act
		var result = ApiDocs.Render("Int32", DocLanguage.CSharp, this.source, 80, styled: false);

		// Assert
		_ = result.Should().Be($"""
			Int32(3net)                    .NET API Reference                    Int32(3net)

			NAME
			       System.Int32 - A whole number.

			SYNOPSIS
			       public struct Int32

			       Namespace: System    Assembly: mscorlib

			FIELDS
			       public const int MaxValue = 2147483647

			SEE ALSO
			       dman Int32.MEMBER

			       https://learn.microsoft.com/dotnet/api/system.int32

			.NET {Environment.Version.Major}                      checked 2026-01-15                      Int32(3net)

			""");
	}

	/// <summary>
	/// Checks that a type file with no members still gives a page, and so does a member the index lists but the file
	/// doesn't have, named as the index does.
	/// </summary>
	[Fact]
	public void Render_TypeFileWithoutMembers_StillGivesPages()
	{
		// Act
		var type = ApiDocs.Render("Tuple", DocLanguage.CSharp, this.source, 80, styled: false);
		var member = ApiDocs.Render("Tuple.Item1", DocLanguage.CSharp, this.source, 80, styled: false);

		// Assert
		_ = type.Should().Contain("SYNOPSIS\n       public class Tuple<T1,T2>\n");
		_ = member.Should().Contain("NAME\n       System.Tuple<T1,T2>.Item1\n");
	}

	/// <summary>
	/// Checks a namespace with no file of its own: no summary on the NAME line and no date in the footer.
	/// </summary>
	[Fact]
	public void Render_NamespaceWithoutFile_WritesWholePage()
	{
		// Act
		var result = ApiDocs.Render("System.IO", DocLanguage.CSharp, this.source, 80, styled: false);

		// Assert
		_ = string.Join('\n', result!.Split('\n').Select(line => line.TrimEnd())).Should().Be($"""
			System.IO(3net)                .NET API Reference                System.IO(3net)

			NAME
			       System.IO

			TYPES
			       File

			SEE ALSO
			       dman System.IO.TYPE for a type's page.

			       https://learn.microsoft.com/dotnet/api/system.io

			.NET {Environment.Version.Major}                                                          System.IO(3net)

			""");
	}

	/// <summary>
	/// Checks that SEE ALSO suggests the member command first, then the other types of the same name.
	/// </summary>
	[Fact]
	public void Render_TypeWithOtherMatches_SuggestsCommandFirst()
	{
		// Act
		var result = ApiDocs.Render("List", DocLanguage.CSharp, this.source, 80, styled: false);

		// Assert
		_ = result.Should().Contain("SEE ALSO\n       dman List<T>.MEMBER, Other.Library.List(3net)\n");
	}

	/// <summary>
	/// Checks that a page too narrow for two columns lists one type per line, and that each type links to its page.
	/// </summary>
	[Fact]
	public void Render_NarrowStyledNamespacePage_ListsOnePerLineWithLinks()
	{
		// Act
		var narrow = ApiDocs.Render("System", DocLanguage.CSharp, this.source, 50, styled: false);
		var styled = ApiDocs.Render("System", DocLanguage.CSharp, this.source, 80, styled: true);

		// Assert
		_ = string.Join('\n', narrow!.Split('\n').Select(line => line.TrimEnd())).Should().Contain("TYPES\n       Environment.SpecialFolder\n       Int32\n");
		_ = styled.Should().Contain(Link("https://learn.microsoft.com/dotnet/api/system.int32", Bold("Int32")));
	}

	/// <inheritdoc />
	public void Dispose() => this.server.Dispose();
}
