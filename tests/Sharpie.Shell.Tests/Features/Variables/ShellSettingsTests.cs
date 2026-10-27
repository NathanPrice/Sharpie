using FluentAssertions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for <see cref="ShellSettings" />.
/// </summary>
public sealed class ShellSettingsTests
{
	/// <summary>
	/// Checks that every option starts off.
	/// </summary>
	[Fact]
	public void New_EveryOption_IsOff()
	{
		// Act
		var settings = new ShellSettings();

		// Assert
		_ = ShellSettings.Options.Should().OnlyContain(option => !settings.IsOn(option.Name));
		_ = settings.Interactive.Should().BeFalse();
	}

	/// <summary>
	/// Checks that <see cref="ShellSettings.Flags" /> lists the letter of each option that's on, in order, then
	/// <c>i</c> for an interactive shell.
	/// </summary>
	/// <param name="options">The options to turn on.</param>
	/// <param name="interactive">Whether the shell is interactive.</param>
	/// <param name="expected">The letters.</param>
	[Theory]
	[InlineData(new string[0], false, "")]
	[InlineData(new[] { "errexit" }, false, "e")]
	[InlineData(new[] { "pipefail" }, false, "")]
	[InlineData(new[] { "xtrace", "nounset", "noclobber", "errexit" }, false, "eCux")]
	[InlineData(new[] { "errexit" }, true, "ei")]
	[InlineData(new[] { "noclobber", "histexpand" }, true, "HCi")]
	[InlineData(new string[0], true, "i")]
	public void Flags_WithOptions_ListsLetters(string[] options, bool interactive, string expected)
	{
		// Arrange
		var settings = new ShellSettings { Interactive = interactive };

		foreach (var option in options)
		{
			_ = settings.Set(option, true);
		}

		// Act
		var flags = settings.Flags;

		// Assert
		_ = flags.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a name that isn't an option is refused and turns nothing on.
	/// </summary>
	[Fact]
	public void Set_WithUnknownName_ReturnsFalse()
	{
		// Arrange
		var settings = new ShellSettings();

		// Act
		var known = settings.Set("nosuch", true);

		// Assert
		_ = known.Should().BeFalse();
		_ = settings.IsOn("nosuch").Should().BeFalse();
	}

	/// <summary>
	/// Checks that each letter <c>set</c> takes maps to its option, and other letters to none.
	/// </summary>
	/// <param name="letter">The letter.</param>
	/// <param name="expected">The option name.</param>
	[Theory]
	[InlineData('e', "errexit")]
	[InlineData('H', "histexpand")]
	[InlineData('u', "nounset")]
	[InlineData('x', "xtrace")]
	[InlineData('q', null)]
	[InlineData('o', null)]
	public void NameOf_WithLetter_GivesOption(char letter, string? expected)
	{
		// Act
		var name = ShellSettings.NameOf(letter);

		// Assert
		_ = name.Should().Be(expected);
	}
}
