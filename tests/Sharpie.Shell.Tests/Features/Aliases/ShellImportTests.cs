using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Sharpie.Shell.Features.Aliases;

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
	private static readonly string ShellPath = Path.Combine(AppContext.BaseDirectory, "Sharpie.Shell");

	private static readonly string[] Builtins = ["echo", "cd"];

	private readonly ShellEnvironment environment = new();
	[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The process-wide writer, borrowed only to put it back.")]
	private readonly TextWriter originalError = Console.Error;
	private readonly StringWriter error = new();
	private readonly AliasCommand aliases = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="ShellImportTests" /> class.
	/// </summary>
	public ShellImportTests() => Console.SetError(this.error);

	/// <summary>
	/// Checks that the variables between the first two markers are read and the ones about the asking shell itself
	/// are left out.
	/// </summary>
	[Fact]
	public void Parse_WithEnvironment_KeepsUserVariables()
	{
		// Arrange
		var output = $"{Marker}SAMPLE=a=b\0PWD=/x\0SHLVL=2\0_=/bin/env\0EMPTY=\0{Marker}{Marker}";

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
			$"#!/bin/sh\necho banner\nexport {SampleVariable}=imported\nalias sample_alias='echo it'\\''s here'\nalias zsh_only='nocorrect-like echo'\neval \"$4\"\n");

		// Act
		ShellImport.Run(shell, ShellPath, this.aliases, Builtins);

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("imported");
		_ = this.aliases.Aliases.Should().Contain("sample_alias", "echo it's here");
		_ = this.aliases.Aliases.Should().NotContainKey("zsh_only");
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.error.ToString().Should().BeEmpty();
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
		_ = this.error.ToString().Should().Be($"sharpie: couldn't read settings from {shell}{Environment.NewLine}");
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
		_ = this.error.ToString().Should().BeEmpty();
	}

	/// <summary>
	/// Restores the console, <c>PATH</c> and the sample variable after each test.
	/// </summary>
	public void Dispose()
	{
		Environment.SetEnvironmentVariable(SampleVariable, null);
		Console.SetError(this.originalError);
		this.error.Dispose();
		this.environment.Dispose();
	}
}
