using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="Program.CreateBuiltins" />.
/// </summary>
public sealed class CreateBuiltinsTests
{
	/// <summary>
	/// Checks that every builtin has a name and a description, and that no two share a name, since the shell adds them
	/// to one dictionary by name.
	/// </summary>
	[Fact]
	public void CreateBuiltins_EveryBuiltin_HasUniqueNameAndDescription()
	{
		// Act
		var builtins = Program.CreateBuiltins([]);

		// Assert
		_ = builtins.Should().OnlyContain(command => command.Name.Length > 0 && command.Description.Length > 0);
		_ = builtins.Select(command => command.Name).Should().OnlyHaveUniqueItems();
	}
}
