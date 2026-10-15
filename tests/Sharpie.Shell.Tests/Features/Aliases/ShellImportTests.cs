using FluentAssertions;
using Sharpie.Shell.Features.Aliases;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Features.Aliases;

/// <summary>
/// Tests for <see cref="ShellImport" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ShellImportTests : IDisposable
{
	private const string Marker = "MARK";
	private const string SampleVariable = "SHARPIE_SAMPLE_VALUE";

	/// <summary>
	/// The built shell, which the fake login shells run to print their environment.
	/// </summary>
	private static readonly string ShellPath = Path.Combine(AppContext.BaseDirectory, "Sharpie");

	private static readonly string[] Builtins = ["echo", "cd"];

	private readonly ShellEnvironment environment = new();
	private readonly AliasCommand aliases = new();

	/// <summary>
	/// Checks that the variables between the first two markers are read and the ones about the asking shell itself
	/// are left out.
	/// </summary>
	[Fact]
	public void Parse_WithEnvironment_KeepsUserVariables()
	{
		// Arrange
		var output = $"{Marker}SAMPLE=a=b\0PWD=/x\0OLDPWD=/y\0SHLVL=2\0_=/bin/env\0EMPTY=\0{Marker}{Marker}";

		// Act
		var result = ShellImport.Parse(output, Marker);

		// Assert
		_ = result!.Value.Environment.Should().Equal(("SAMPLE", "a=b"), ("EMPTY", string.Empty));
	}

	/// <summary>
	/// Checks that alias lines in the zsh form and the bash form are both read, with their quoting removed.
	/// </summary>
	/// <param name="line">One line of <c>alias</c> output.</param>
	/// <param name="name">The expected alias name.</param>
	/// <param name="value">The expected alias value.</param>
	[Theory]
	[InlineData("greet='echo hello'", "greet", "echo hello")]
	[InlineData("alias greet='echo hello'", "greet", "echo hello")]
	[InlineData("plain=ls", "plain", "ls")]
	[InlineData("say='echo it'\\''s'", "say", "echo it's")]
	public void Parse_WithAliasLine_ReadsNameAndValue(string line, string name, string value)
	{
		// Act
		var result = ShellImport.Parse($"{Marker}{Marker}{line}\n{Marker}", Marker);

		// Assert
		_ = result!.Value.Aliases.Should().Equal((name, value));
	}

	/// <summary>
	/// Checks that an alias line in a form that isn't one name and one value, such as fish's, is skipped.
	/// </summary>
	[Fact]
	public void Parse_WithFishAliasLine_SkipsIt()
	{
		// Act
		var result = ShellImport.Parse($"{Marker}{Marker}alias greet 'echo hello'\n{Marker}", Marker);

		// Assert
		_ = result!.Value.Aliases.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that whatever startup files print before and after the markers, like a banner, is ignored.
	/// </summary>
	[Fact]
	public void Parse_WithBannerAroundMarkers_IgnoresBanner()
	{
		// Arrange
		var output = $"OS: Example\nKernel: 1.0\n{Marker}SAMPLE=1\0{Marker}greet='echo hello'\n{Marker}bye\n";

		// Act
		var result = ShellImport.Parse(output, Marker);

		// Assert
		_ = result!.Value.Environment.Should().Equal(("SAMPLE", "1"));
		_ = result.Value.Aliases.Should().Equal(("greet", "echo hello"));
	}

	/// <summary>
	/// Checks that output missing a marker, as from a shell that stopped early, gives nothing.
	/// </summary>
	[Fact]
	public void Parse_WithMissingMarker_ReturnsNull()
	{
		// Act
		var result = ShellImport.Parse($"{Marker}SAMPLE=1\0{Marker}", Marker);

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Checks which imported alias values this shell keeps: ones that start with a builtin, a program on
	/// <c>PATH</c>, or another imported alias that does, and use no syntax it can't read.
	/// </summary>
	/// <param name="value">The alias value.</param>
	/// <param name="expected">Whether it's kept.</param>
	[Theory]
	[InlineData("echo hi", true)]
	[InlineData("sample-tool --flag", true)]
	[InlineData("sample-tool -x", true)]
	[InlineData("to-echo world", true)]
	[InlineData("zshonly sample-tool", false)]
	[InlineData("missing-tool", false)]
	[InlineData("loop-a", false)]
	[InlineData("echo a; echo b", false)]
	[InlineData("echo a && echo b", false)]
	[InlineData("echo $(pwd)", false)]
	[InlineData("", false)]
	public void IsUsable_WithValue_KeepsOnlyRunnableAliases(string value, bool expected)
	{
		// Arrange
		var folder = this.environment.CreateFolder();

		_ = ShellEnvironment.CreateFile(folder, "sample-tool", executable: true);
		ShellEnvironment.SetPath(folder);

		var imported = new Dictionary<string, string>
		{
			["sample-tool"] = "sample-tool -x",
			["to-echo"] = "echo",
			["loop-a"] = "loop-b",
			["loop-b"] = "loop-a",
		};

		// Act
		var usable = ShellImport.IsUsable(value, imported, Builtins);

		// Assert
		_ = usable.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a real run of a login shell sets the variables its startup exported and defines its aliases,
	/// while the banner it prints stays out of the shell's output.
	/// </summary>
	[Fact]
	public void Run_WithLoginShell_ImportsVariablesAndAliases()
	{
		// Arrange
		var shell = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "fake-shell", executable: true,
			$"#!/bin/sh\necho banner\nexport {SampleVariable}=\"imported $1 $2 $3\"\nalias sample_alias='echo it'\\''s here'\nalias zsh_only='nocorrect-like echo'\neval \"$4\"\n");

		// Act
		ShellImport.Run(shell, ShellPath, this.aliases, Builtins);

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("imported -l -i -c");
		_ = this.aliases.Aliases.Should().Contain("sample_alias", "echo it's here");
		_ = this.aliases.Aliases.Should().NotContainKey("zsh_only");
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a shell that never answers is stopped after the timeout, nothing is imported, and one line says so.
	/// </summary>
	[Fact]
	public void Run_WithHangingShell_GivesUpAndReports()
	{
		// Arrange
		var shell = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "slow-shell", executable: true,
			$"#!/bin/sh\nexport {SampleVariable}=imported\nalias sample_alias=ls\nsleep 30\neval \"$4\"\n");

		// Act
		ShellImport.Run(shell, ShellPath, this.aliases, Builtins, TimeSpan.FromMilliseconds(300));

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().BeNull();
		_ = this.aliases.Aliases.Should().BeEmpty();
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::import::failed", $"Couldn't read settings from `{shell}`").Render(color: false));
	}

	/// <summary>
	/// Checks that a shell given by name is found on <c>PATH</c>, and one given by full path is run even when
	/// <c>PATH</c> is empty.
	/// </summary>
	/// <param name="byName"><see langword="true" /> to pass the bare name; otherwise, the full path.</param>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Run_WithShellNameOrPath_FindsIt(bool byName)
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		var shell = ShellEnvironment.CreateFile(folder, "fake-shell", executable: true, $"#!/bin/sh\nexport {SampleVariable}=imported\neval \"$4\"\n");
		ShellEnvironment.SetPath(byName ? [folder] : []);

		// Act
		ShellImport.Run(byName ? "fake-shell" : shell, ShellPath, this.aliases, Builtins);

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("imported");
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that an unset or empty <c>$SHELL</c> imports nothing and says nothing.
	/// </summary>
	/// <param name="shell">The <c>$SHELL</c> value.</param>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void Run_WithNoShell_DoesNothing(string? shell)
	{
		// Act
		ShellImport.Run(shell, ShellPath, this.aliases, Builtins);

		// Assert
		_ = this.aliases.Aliases.Should().BeEmpty();
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that the answer is read as soon as the last marker arrives, even while a program the startup files left
	/// running still holds the output open.
	/// </summary>
	[Fact]
	public void Run_WithProgramHoldingOutput_StillImports()
	{
		// Arrange
		var shell = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "fake-shell", executable: true,
			$"#!/bin/sh\nexport {SampleVariable}=imported\neval \"$4\"\nsleep 30\n");

		// Act
		ShellImport.Run(shell, ShellPath, this.aliases, Builtins, TimeSpan.FromSeconds(3));

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("imported");
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that the import is skipped when the user's shell is this shell, which would only ask itself.
	/// </summary>
	[Fact]
	public void Run_WithSelfAsShell_DoesNothing()
	{
		// Act
		ShellImport.Run(ShellPath, ShellPath, this.aliases, Builtins);

		// Assert
		_ = this.aliases.Aliases.Should().BeEmpty();
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that the import still works when this shell's own path has an apostrophe in it, which the script the
	/// login shell runs has to quote.
	/// </summary>
	[Fact]
	public void Run_WithApostropheInOwnPath_StillImports()
	{
		// Arrange
		var folder = Directory.CreateDirectory(Path.Combine(this.environment.CreateFolder(), "it's")).FullName;
		var self = Path.Combine(folder, "Sharpie");
		_ = File.CreateSymbolicLink(self, ShellPath);
		var shell = ShellEnvironment.CreateFile(this.environment.CreateFolder(), "fake-shell", executable: true,
			$"#!/bin/sh\nexport {SampleVariable}=imported\neval \"$4\"\n");

		// Act
		ShellImport.Run(shell, self, this.aliases, Builtins);

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("imported");
	}

	/// <summary>
	/// Restores the console, <c>PATH</c> and the sample variable after each test.
	/// </summary>
	public void Dispose()
	{
		Environment.SetEnvironmentVariable(SampleVariable, null);
		this.environment.Dispose();
	}

	/// <summary>
	/// Checks that <see cref="ShellImport.RunProfile" /> sets what <c>~/.profile</c> exports, through
	/// <c>/bin/sh -l</c>.
	/// </summary>
	[Fact]
	public void RunProfile_WithProfileInHome_ImportsItsVariables()
	{
		// Arrange
		var home = this.environment.CreateFolder();
		_ = ShellEnvironment.CreateFile(home, ".profile", executable: false, $"export {SampleVariable}=from-profile\n");
		ShellEnvironment.SetHome(home);

		// Act
		ShellImport.RunProfile(ShellPath, this.aliases, Builtins);

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("from-profile");
		_ = this.environment.Error.Should().BeEmpty();
	}
}
