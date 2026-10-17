using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="Glob" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class GlobTests : IDisposable
{
	private readonly string folder = Directory.CreateTempSubdirectory("glob-tests-").FullName;

	/// <summary>
	/// Initializes a new instance of the <see cref="GlobTests" /> class with a folder holding <c>a.tmp</c>,
	/// <c>b.tmp</c>, <c>c.txt</c>, <c>.hidden.tmp</c> and <c>sub/x.cs</c>.
	/// </summary>
	public GlobTests()
	{
		foreach (var name in new[] { "b.tmp", "a.tmp", "c.txt", ".hidden.tmp" })
		{
			File.WriteAllText(Path.Combine(this.folder, name), string.Empty);
		}

		_ = Directory.CreateDirectory(Path.Combine(this.folder, "sub"));
		File.WriteAllText(Path.Combine(this.folder, "sub", "x.cs"), string.Empty);
	}

	/// <summary>
	/// Deletes the test folder.
	/// </summary>
	public void Dispose() => Directory.Delete(this.folder, recursive: true);

	/// <summary>
	/// Checks that <see cref="Glob.IsMatch" /> handles <c>*</c>, <c>?</c>, <c>[...]</c> sets and backslash escapes
	/// the way bash does.
	/// </summary>
	/// <param name="name">The file name.</param>
	/// <param name="pattern">The pattern.</param>
	/// <param name="expected">Whether the name should match.</param>
	[Theory]
	[InlineData("a.tmp", "*.tmp", true)]
	[InlineData("a.tm", "*.tmp", false)]
	[InlineData("a.tmpx", "*.tmp", false)]
	[InlineData("ab", "a?", true)]
	[InlineData("a", "a?", false)]
	[InlineData("abc", "a?", false)]
	[InlineData("aXbXc", "a*c", true)]
	[InlineData("aXbXcd", "a*c", false)]
	[InlineData("abcbxd", "a*b?d", true)]
	[InlineData("abcbd", "a*b?d", false)]
	[InlineData("abc", "a**c", true)]
	[InlineData("", "*", true)]
	[InlineData("b", "[abc]", true)]
	[InlineData("d", "[abc]", false)]
	[InlineData("d", "[!abc]", true)]
	[InlineData("a", "[!abc]", false)]
	[InlineData("!", "[!abc]", true)]
	[InlineData("a", "[aa]", true)]
	[InlineData("d", "[^abc]", true)]
	[InlineData("m", "[a-z]", true)]
	[InlineData("M", "[a-z]", false)]
	[InlineData("z", "[a-y]", false)]
	[InlineData("]", "[]a]", true)]
	[InlineData("]", "[!]a]", false)]
	[InlineData("-", "[a-]", true)]
	[InlineData("]", @"[\]]", true)]
	[InlineData("-", @"[a\-z]", true)]
	[InlineData("b", @"[a\-z]", false)]
	[InlineData("*", @"\*", true)]
	[InlineData("x", @"\*", false)]
	[InlineData("[", "[", true)]
	[InlineData("a[", "a[", true)]
	[InlineData("a[b", "a[b", true)]
	[InlineData("[a-", "[a-", true)]
	[InlineData(@"[\", @"[\", true)]
	[InlineData(@"a\", @"a\", true)]
	[InlineData("5", "[[:digit:]]", true)]
	[InlineData("x", "[[:digit:]]", false)]
	[InlineData("x", "[[:alpha:][:digit:]]", true)]
	[InlineData("x", "[[:nosuchclass:]]", false)]
	[InlineData("file1.cs", "file[0-9].cs", true)]
	[InlineData("filea.cs", "file[0-9].cs", false)]
	public void IsMatch_WithPattern_MatchesLikeBash(string name, string pattern, bool expected)
	{
		// Act
		var matched = Glob.IsMatch(name, pattern);

		// Assert
		_ = matched.Should().Be(expected);
	}

	/// <summary>
	/// Checks that each POSIX class in a set matches a character in it and refuses one outside it.
	/// </summary>
	/// <param name="className">The class name.</param>
	/// <param name="inside">A character in the class.</param>
	/// <param name="outside">A character not in the class.</param>
	[Theory]
	[InlineData("alpha", 'q', '1')]
	[InlineData("digit", '7', 'a')]
	[InlineData("alnum", '7', '-')]
	[InlineData("upper", 'Q', 'q')]
	[InlineData("lower", 'q', 'Q')]
	[InlineData("space", '\t', 'x')]
	[InlineData("blank", ' ', '\n')]
	[InlineData("punct", '!', 'a')]
	[InlineData("xdigit", 'f', 'g')]
	[InlineData("cntrl", '\u0001', 'a')]
	[InlineData("graph", 'a', ' ')]
	[InlineData("print", ' ', '\u0001')]
	public void IsMatch_WithClass_MatchesOnlyItsCharacters(string className, char inside, char outside)
	{
		// Arrange
		var pattern = $"[[:{className}:]]";

		// Act
		var matches = (Glob.IsMatch(inside.ToString(), pattern), Glob.IsMatch(outside.ToString(), pattern));

		// Assert
		_ = matches.Should().Be((true, false));
	}

	/// <summary>
	/// Checks that <see cref="Glob.Expand" /> lists the matching paths sorted, skips dotfiles unless the pattern starts
	/// with a dot, only lists folders for a trailing slash, and gives nothing when nothing matches.
	/// </summary>
	/// <param name="pattern">The pattern, relative to the test folder.</param>
	/// <param name="expected">The matches, relative to the test folder.</param>
	[Theory]
	[InlineData("*.tmp", new[] { "a.tmp", "b.tmp" })]
	[InlineData("?.t[a-z]t", new[] { "c.txt" })]
	[InlineData(".*", new[] { ".hidden.tmp" })]
	[InlineData(@"\.*", new[] { ".hidden.tmp" })]
	[InlineData("*/x.cs", new[] { "sub/x.cs" })]
	[InlineData("s*/*", new[] { "sub/x.cs" })]
	[InlineData("*/", new[] { "sub/" })]
	[InlineData("*.none", new string[0])]
	[InlineData("*/missing", new string[0])]
	[InlineData("c.txt/*", new string[0])]
	[InlineData("nosuchfolder/*", new string[0])]
	[InlineData(@"\*.tmp", new string[0])]
	[InlineData(@"nosuchfolder\/*", new string[0])]
	[InlineData("a.tmp", new string[0])]
	public void Expand_WithPattern_ListsMatchingPathsSorted(string pattern, string[] expected)
	{
		// Act
		var matches = Glob.Expand($"{this.folder}/{pattern}");

		// Assert
		_ = matches.Should().Equal(expected.Select(match => $"{this.folder}/{match}"));
	}

	/// <summary>
	/// Checks that a pattern whose only wildcard is escaped gives nothing, even when a file has that literal name.
	/// </summary>
	[Fact]
	public void Expand_WithOnlyEscapedWildcard_GivesNothing()
	{
		// Arrange
		File.WriteAllText(Path.Combine(this.folder, "[x"), string.Empty);

		// Act
		var matches = Glob.Expand($@"{this.folder}/\[x");

		// Assert
		_ = matches.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a symbolic link whose target is gone still matches, as bash lists what the folder holds.
	/// </summary>
	[Fact]
	public void Expand_WithBrokenLink_ListsIt()
	{
		// Arrange
		_ = File.CreateSymbolicLink(Path.Combine(this.folder, "gone.tmp"), Path.Combine(this.folder, "missing"));

		// Act
		var matches = Glob.Expand($"{this.folder}/g*.tmp");

		// Assert
		_ = matches.Should().Equal($"{this.folder}/gone.tmp");
	}

	/// <summary>
	/// Checks that a wildcard right after the leading <c>/</c> is matched in the root folder.
	/// </summary>
	[Fact]
	public void Expand_WithWildcardAtRoot_ReadsRootFolder()
	{
		// Act
		var matches = Glob.Expand("/tm?");

		// Assert
		_ = matches.Should().Equal("/tmp");
	}

	/// <summary>
	/// Checks that a relative pattern is matched in the current folder and gives relative paths.
	/// </summary>
	[Fact]
	public void Expand_WithRelativePattern_ReadsCurrentFolder()
	{
		// Arrange
		var original = Directory.GetCurrentDirectory();

		Directory.SetCurrentDirectory(this.folder);

		try
		{
			// Act
			var matches = Glob.Expand("*.tmp");

			// Assert
			_ = matches.Should().Equal("a.tmp", "b.tmp");
		}
		finally
		{
			Directory.SetCurrentDirectory(original);
		}
	}

	/// <summary>
	/// Checks that a part with no wildcard keeps its escaped characters as plain ones.
	/// </summary>
	[Fact]
	public void Expand_WithEscapedLiteralPart_UnescapesIt()
	{
		// Arrange
		_ = Directory.CreateDirectory(Path.Combine(this.folder, "a b"));
		File.WriteAllText(Path.Combine(this.folder, "a b", "y.cs"), string.Empty);

		// Act
		var matches = Glob.Expand($@"{this.folder}/a\ b/*.cs");

		// Assert
		_ = matches.Should().Equal($"{this.folder}/a b/y.cs");
	}
}
