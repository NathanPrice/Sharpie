using FluentAssertions;
using Sharpie.Shell.Features.DotNetDocs;

namespace Sharpie.Shell.Tests.Features.DotNetDocs;

/// <summary>
/// Tests for <see cref="DmanCommand" />.
/// </summary>
public sealed class DmanCommandTests
{
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
}
