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
		      <Member Id="M:System.String.Split(System.Char[])" />
		      <Member Id="P:System.String.Length" />
		    </Type>
		    <Type Name="System.Environment/SpecialFolder" Id="T:System.Environment.SpecialFolder" />
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

	private static readonly string StringType = $"""
		<Type Name="String" FullName="System.String">
		  <TypeSignature Language="C#" Value="public sealed class String" />
		  <TypeSignature Language="VB.NET" Value="Public NotInheritable Class String" />
		  <AssemblyInfo><AssemblyName>mscorlib</AssemblyName><AssemblyVersion>4.0.0.0</AssemblyVersion></AssemblyInfo>
		  <AssemblyInfo><AssemblyName>System.Runtime</AssemblyName><AssemblyVersion>{Environment.Version.Major}.0.0.0</AssemblyVersion></AssemblyInfo>
		  <Docs><summary>Represents text.</summary><remarks>To be added.</remarks></Docs>
		  <Members>
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
		      </Docs>
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

	private readonly FakeDocs server = new();
	private readonly DocsSource source;

	/// <summary>
	/// Initializes a new instance of the <see cref="ApiDocsTests" /> class.
	/// </summary>
	public ApiDocsTests()
	{
		this.server.Add($"{Repository}FrameworksIndex/{ApiDocs.Moniker}.xml", Index);
		this.server.Add($"{Repository}System/String.xml", StringType);
		this.source = this.server.CreateSource(TimeProvider.System);
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
	/// Checks that a type page lists only the members this .NET version has, names the current assembly, and leaves
	/// out the docs' "To be added." placeholder.
	/// </summary>
	[Fact]
	public void Render_TypePage_ListsCurrentMembersOnly()
	{
		// Act
		var result = ApiDocs.Render("String", DocLanguage.CSharp, this.source, 80, styled: false);

		// Assert
		_ = result.Should().Contain("NAME\n       System.String - Represents text.")
			.And.Contain("METHODS\n       public string[] Split(params char[] separator)\n              Splits a String.")
			.And.Contain("Assembly: System.Runtime")
			.And.NotContain("Copy")
			.And.NotContain("To be added");
	}

	/// <summary>
	/// Checks that a member page with <c>-vb</c> shows the Visual Basic signature with its parameters, return value
	/// and exceptions.
	/// </summary>
	[Fact]
	public void Render_MemberPageInVisualBasic_ShowsVisualBasicSignature()
	{
		// Act
		var result = ApiDocs.Render("String.Split", DocLanguage.VisualBasic, this.source, 100, styled: false);

		// Assert
		_ = result.Should().Contain("SYNOPSIS\n       Public Function Split(ParamArray separator As Char()) As String()\n")
			.And.Contain("       separator\n              The delimiters, or null.")
			.And.Contain("       Returns\n              The parts.")
			.And.Contain("       Throws ArgumentException\n              separator is bad.");
	}

	/// <inheritdoc />
	public void Dispose() => this.server.Dispose();
}
