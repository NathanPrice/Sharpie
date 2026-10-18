using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for <see cref="SetCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class SetCommandTests : IDisposable
{
	/// <summary>
	/// A made-up environment variable name the tests set and remove.
	/// </summary>
	private const string SampleVariable = "SHARPIE_SAMPLE_SET";

	private readonly ShellEnvironment environment = new();
	private readonly ShellSettings settings = new();
	private readonly DeclareCommand declare;
	private readonly SetCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="SetCommandTests" /> class, whose <c>declare</c> reads the same
	/// options as <c>set</c>, as in the shell.
	/// </summary>
	public SetCommandTests()
	{
		this.declare = new DeclareCommand(this.settings);
		this.sut = new SetCommand(this.declare, this.settings);
	}

	/// <summary>
	/// Checks that words after <c>--</c> or <c>-</c>, or with neither, become the positional parameters, and that
	/// <c>set --</c> alone clears them.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The parameters after.</param>
	[Theory]
	[InlineData(new[] { "--", "a", "-x" }, new[] { "a", "-x" })]
	[InlineData(new[] { "-", "a" }, new[] { "a" })]
	[InlineData(new[] { "+", "a" }, new[] { "a" })]
	[InlineData(new[] { "-" }, new[] { "old1", "old2", "old3" })]
	[InlineData(new[] { "+" }, new[] { "old1", "old2", "old3" })]
	[InlineData(new[] { "a", "b c" }, new[] { "a", "b c" })]
	[InlineData(new[] { "ab" }, new[] { "ab" })]
	[InlineData(new[] { "", "a" }, new[] { "", "a" })]
	[InlineData(new[] { "--" }, new string[0])]
	public void Run_WithWords_SetsPositionalParameters(string[] args, string[] expected)
	{
		// Arrange
		this.declare.SetPositional(["old1", "old2", "old3"]);

		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.declare.Positional.Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a letter or name that isn't an option is refused with status 2, and the parameters are left alone.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="message">The message.</param>
	[Theory]
	[InlineData(new[] { "-q", "b" }, "`-q` isn't an option of set")]
	[InlineData(new[] { "+eq", "b" }, "`+q` isn't an option of set")]
	[InlineData(new[] { "-o", "nosuch", "b" }, "`nosuch` isn't an option name of set")]
	public void Run_WithUnknownOption_ReportsItAndGivesTwo(string[] args, string message)
	{
		// Arrange
		this.declare.SetPositional(["a"]);

		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(2);
		_ = this.declare.Positional.Should().Equal("a");
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::set::invalid_option", message)
		{
			Help = "set takes -e, -C, -u, -x and -o with errexit, noclobber, nounset, pipefail or xtrace",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that letters, joined letters and <c>-o NAME</c> turn options on, and the same with <c>+</c> turns them
	/// off, leaving the parameters alone.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The options on afterwards.</param>
	[Theory]
	[InlineData(new[] { "-e" }, new[] { "errexit" })]
	[InlineData(new[] { "-u", "-x" }, new[] { "nounset", "xtrace" })]
	[InlineData(new[] { "-euo", "pipefail" }, new[] { "errexit", "nounset", "pipefail" })]
	[InlineData(new[] { "-o", "errexit", "-o", "xtrace" }, new[] { "errexit", "xtrace" })]
	[InlineData(new[] { "-eux", "+u" }, new[] { "errexit", "xtrace" })]
	[InlineData(new[] { "-o", "pipefail", "+o", "pipefail" }, new string[0])]
	[InlineData(new[] { "-C" }, new[] { "noclobber" })]
	[InlineData(new[] { "-C", "+C" }, new string[0])]
	[InlineData(new[] { "-o", "noclobber" }, new[] { "noclobber" })]
	[InlineData(new[] { "-C", "+o", "noclobber" }, new string[0])]
	[InlineData(new[] { "-eC" }, new[] { "errexit", "noclobber" })]
	public void Run_WithOptions_TurnsThemOnAndOff(string[] args, string[] expected)
	{
		// Arrange
		this.declare.SetPositional(["a"]);

		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = ShellSettings.Options.Select(option => option.Name).Where(this.settings.IsOn).Should().Equal(expected);
		_ = this.declare.Positional.Should().Equal("a");
	}

	/// <summary>
	/// Checks that the properties the shell reads follow their options.
	/// </summary>
	[Fact]
	public void Run_WithEveryOption_SetsEachProperty()
	{
		// Act
		_ = this.sut.Run(["-euxC", "-o", "pipefail"]);

		// Assert
		_ = this.settings.ErrExit.Should().BeTrue();
		_ = this.settings.NoClobber.Should().BeTrue();
		_ = this.settings.NoUnset.Should().BeTrue();
		_ = this.settings.XTrace.Should().BeTrue();
		_ = this.settings.PipeFail.Should().BeTrue();
	}

	/// <summary>
	/// Checks that words after the options become the positional parameters, with or without <c>--</c>.
	/// </summary>
	/// <param name="option">The option that should be on.</param>
	/// <param name="args">The arguments.</param>
	[Theory]
	[InlineData("errexit", new[] { "-e", "a", "b" })]
	[InlineData("pipefail", new[] { "-o", "pipefail", "--", "a", "b" })]
	public void Run_WithOptionsThenWords_SetsBoth(string option, string[] args)
	{
		// Act
		_ = this.sut.Run(args);

		// Assert
		_ = this.declare.Positional.Should().Equal("a", "b");
		_ = this.settings.IsOn(option).Should().BeTrue();
	}

	/// <summary>
	/// Checks that a lone <c>-</c> turns tracing off and a lone <c>+</c> leaves it on.
	/// </summary>
	/// <param name="arg">The lone sign.</param>
	/// <param name="expected">Whether tracing is on afterwards.</param>
	[Theory]
	[InlineData("-", false)]
	[InlineData("+", true)]
	public void Run_WithLoneSign_TurnsTracingOffOnlyForDash(string arg, bool expected)
	{
		// Arrange
		_ = this.settings.Set("xtrace", true);

		// Act
		_ = this.sut.Run([arg]);

		// Assert
		_ = this.settings.XTrace.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <c>set -o</c> alone lists every option as bash does, and <c>set +o</c> prints the commands that set
	/// them again.
	/// </summary>
	/// <param name="arg">The argument.</param>
	/// <param name="expected">The lines it prints.</param>
	[Theory]
	[InlineData("-o", new[] { "errexit        \ton", "noclobber      \toff", "nounset        \toff", "pipefail       \ton", "xtrace         \toff" })]
	[InlineData("+o", new[] { "set -o errexit", "set +o noclobber", "set +o nounset", "set -o pipefail", "set +o xtrace" })]
	public void Run_WithBareO_ListsOptions(string arg, string[] expected)
	{
		// Arrange
		_ = this.settings.Set("errexit", true);
		_ = this.settings.Set("pipefail", true);

		// Act
		var status = this.sut.Run([arg]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be(string.Concat(expected.Select(line => line + Environment.NewLine)));
	}

	/// <summary>
	/// Checks that <c>set</c> alone lists shell and environment variables sorted by name, with values quoted only when
	/// needed and the shell variable winning over the environment, and leaves out names like <c>?</c> and <c>1</c>.
	/// </summary>
	[Fact]
	public void Run_WithNoArguments_ListsVariables()
	{
		// Arrange
		Environment.SetEnvironmentVariable(SampleVariable, "from environment");
		this.declare.Set(SampleVariable + "_A", "a b");
		this.declare.Set(SampleVariable + "_B", string.Empty);
		this.declare.Set(SampleVariable + "_C", "plain");
		this.declare.Set(SampleVariable, "shell");
		this.declare.Status = 3;
		this.declare.SetPositional(["one"]);

		// Act
		var status = this.sut.Run([]);

		// Assert
		_ = status.Should().Be(0);
		var lines = this.environment.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

		_ = lines.Where(line => line.StartsWith(SampleVariable, StringComparison.Ordinal)).Should().Equal(
			$"{SampleVariable}=shell",
			$"{SampleVariable}_A='a b'",
			$"{SampleVariable}_B=",
			$"{SampleVariable}_C=plain");
		_ = lines.Should().BeInAscendingOrder(StringComparer.Ordinal);
		_ = lines.Should().NotContain(line => line.StartsWith("?=", StringComparison.Ordinal) || line.StartsWith("1=", StringComparison.Ordinal) || line.StartsWith("#=", StringComparison.Ordinal));
		_ = lines.Should().Contain(line => line.StartsWith("PATH=", StringComparison.Ordinal));
	}

	/// <summary>
	/// Restores the console after each test and removes the sample variable.
	/// </summary>
	public void Dispose()
	{
		Environment.SetEnvironmentVariable(SampleVariable, null);
		this.environment.Dispose();
	}
}
