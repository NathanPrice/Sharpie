using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Aliases;

namespace Sharpie.Shell.Tests.Features.Aliases;

/// <summary>
/// Tests for <see cref="UnaliasCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class UnaliasCommandTests : IDisposable
{
	private const string Usage = "use unalias NAME... or unalias -a";

	private readonly ShellEnvironment environment = new();
	private readonly AliasCommand alias = new();
	private readonly UnaliasCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="UnaliasCommandTests" /> class with the aliases <c>greet</c>,
	/// <c>list</c> and <c>-a</c>.
	/// </summary>
	public UnaliasCommandTests()
	{
		this.sut = new UnaliasCommand(this.alias);
		this.alias.Define("greet", "echo hello");
		this.alias.Define("list", "ls");
		this.alias.Define("-a", "sampletool");
	}

	/// <summary>
	/// Checks that each named alias is removed and the others stay.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="left">The aliases left, sorted.</param>
	[Theory]
	[InlineData(new[] { "greet" }, new[] { "-a", "list" })]
	[InlineData(new[] { "greet", "list" }, new[] { "-a" })]
	[InlineData(new[] { "--", "-a" }, new[] { "greet", "list" })]
	public void Run_WithNames_RemovesThem(string[] args, string[] left)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.alias.Aliases.Keys.Order(StringComparer.Ordinal).Should().Equal(left);
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>-a</c> removes every alias and ignores names after it, as bash does.
	/// </summary>
	[Fact]
	public void Run_WithAll_RemovesEveryAlias()
	{
		// Act
		var status = this.sut.Run(["-a", "missing"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.alias.Aliases.Should().BeEmpty();
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a name that isn't an alias is reported with status 1 while the other names are still removed.
	/// </summary>
	[Fact]
	public void Run_WithUnknownName_ReportsItAndRemovesTheRest()
	{
		// Act
		var status = this.sut.Run(["missing", "greet"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.alias.Aliases.Keys.Order(StringComparer.Ordinal).Should().Equal("-a", "list");
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::unalias::not_found", "No alias named `missing`").Render(color: false));
	}

	/// <summary>
	/// Checks that an option other than <c>-a</c> is refused with status 2 and nothing is removed.
	/// </summary>
	[Fact]
	public void Run_WithUnknownOption_GivesTwoAndRemovesNothing()
	{
		// Act
		var status = this.sut.Run(["-x", "greet"]);

		// Assert
		_ = status.Should().Be(2);
		_ = this.alias.Aliases.Should().HaveCount(3);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::unalias::invalid_option", "`-x` isn't an option of unalias") { Help = Usage }.Render(color: false));
	}

	/// <summary>
	/// Gets argument lists with no alias names in them.
	/// </summary>
	/// <value>
	/// No arguments, and a lone <c>--</c>.
	/// </value>
	public static TheoryData<string[]> NoNames => [[], ["--"]];

	/// <summary>
	/// Checks that no names at all, even after <c>--</c>, is refused with status 2.
	/// </summary>
	/// <param name="args">The arguments.</param>
	[Theory]
	[MemberData(nameof(NoNames))]
	public void Run_WithNoNames_GivesTwo(string[] args)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(2);
		_ = this.alias.Aliases.Should().HaveCount(3);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::unalias::missing_name", "`unalias` needs an alias name") { Help = Usage }.Render(color: false));
	}

	/// <summary>
	/// Checks that a lone <c>-</c> is a name, not an option.
	/// </summary>
	[Fact]
	public void Run_WithLoneDash_TreatsItAsName()
	{
		// Act
		var status = this.sut.Run(["-"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::unalias::not_found", "No alias named `-`").Render(color: false));
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
