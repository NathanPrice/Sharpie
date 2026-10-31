using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Completion;

/// <summary>
/// Tests for <see cref="CompgenCommand" /> and <see cref="CompoptCommand" />. Each output was checked against bash
/// 5.3.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class CompgenCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new(new ShellSettings());
	private readonly Dictionary<string, ICommand> commands = new() { ["echo"] = new EchoCommand() };
	private readonly CompletionEngine engine;
	private readonly CompgenCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="CompgenCommandTests" /> class, with the functions
	/// <c>f_beta</c> and <c>f_alpha</c>, the alias <c>al_x</c>, the variables <c>xyz_one</c> and <c>xyz_two</c> and
	/// the array <c>arr_q</c>.
	/// </summary>
	public CompgenCommandTests()
	{
		this.engine = new CompletionEngine(this.declare, this.commands, new Dictionary<string, string> { ["al_x"] = "ls" }, _ => 0);
		this.sut = new CompgenCommand(this.engine);
		this.commands["f_beta"] = new FunctionCommand("f_beta", "{ :; }", null, this.declare, _ => 0);
		this.commands["f_alpha"] = new FunctionCommand("f_alpha", "{ :; }", null, this.declare, _ => 0);
		_ = this.declare.Run(["xyz_two=2", "xyz_one=1", "arr_q=(1)", "sample=val"]);
	}

	/// <summary>
	/// Checks what each option prints, one word per line.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The words bash prints.</param>
	[Theory]
	[InlineData(new[] { "-W", "a1 a2 b", "--", "a" }, new[] { "a1", "a2" })]
	[InlineData(new[] { "-W", "b \"c d\" a 'e f'" }, new[] { "b", "c d", "a", "e f" })]
	[InlineData(new[] { "-W", @"x\ y $sample ""$sample""" }, new[] { "x y", "val", "val" })]
	[InlineData(new[] { "-P", "<", "-S", ">", "-W", "one two", "--", "t" }, new[] { "<two>" })]
	[InlineData(new[] { "-X", "!*1", "-W", "a1 a2 b1" }, new[] { "a1", "b1" })]
	[InlineData(new[] { "-X", "&b", "-W", "a1 ab a", "a" }, new[] { "a1", "a" })]
	[InlineData(new[] { "-X", @"\&", "-W", "& b" }, new[] { "b" })]
	[InlineData(new[] { "-v", "xyz_" }, new[] { "xyz_one", "xyz_two" })]
	[InlineData(new[] { "-A", "arrayvar", "arr_" }, new[] { "arr_q" })]
	[InlineData(new[] { "-v", "arr_" }, new[] { "arr_q" })]
	[InlineData(new[] { "-A", "function", "f_" }, new[] { "f_alpha", "f_beta" })]
	[InlineData(new[] { "-a", "al_" }, new[] { "al_x" })]
	[InlineData(new[] { "-k", "fi" }, new[] { "fi" })]
	[InlineData(new[] { "-b" }, new[] { "echo" })]
	[InlineData(new[] { "-A", "setopt", "errex" }, new[] { "errexit" })]
	[InlineData(new[] { "-c", "f_al" }, new[] { "f_alpha" })]
	[InlineData(new[] { "-c", "al_" }, new[] { "al_x" })]
	[InlineData(new[] { "-c", "esa" }, new[] { "esac" })]
	[InlineData(new[] { "-W", @"x\" }, new[] { "x" })]
	[InlineData(new[] { "-W", "a  b" }, new[] { "a", "b" })]
	public void Run_WithSpec_PrintsWords(string[] args, string[] expected)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be(string.Concat(expected.Select(word => word + Environment.NewLine)));
	}

	/// <summary>
	/// Checks that <c>-W</c> splits on <c>IFS</c> when it's set.
	/// </summary>
	[Fact]
	public void Run_WithIfs_SplitsOnIt()
	{
		// Arrange
		_ = this.declare.Run(["IFS=:"]);

		// Act
		_ = this.sut.Run(["-W", "p:q r"]);

		// Assert
		_ = this.environment.Output.Should().Be($"p{Environment.NewLine}q r{Environment.NewLine}");
	}

	/// <summary>
	/// Checks the file actions: <c>-f</c> gives files and folders, <c>-d</c> only folders, neither with a
	/// <c>/</c>, a folder part as typed is kept, and <c>-G</c> gives the files a pattern matches.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The words bash prints, sorted.</param>
	[Theory]
	[InlineData(new[] { "-f" }, new[] { "file1", "sub", "sub2" })]
	[InlineData(new[] { "-d" }, new[] { "sub", "sub2" })]
	[InlineData(new[] { "-d", "-S", "/", "su" }, new[] { "sub/", "sub2/" })]
	[InlineData(new[] { "-f", "sub/" }, new[] { "sub/inner" })]
	[InlineData(new[] { "-G", "*1" }, new[] { "file1" })]
	[InlineData(new[] { "-G", "*.none" }, new string[0])]
	[InlineData(new[] { "-G", "su*" }, new[] { "sub", "sub2" })]
	public void Run_WithFileActions_PrintsNames(string[] args, string[] expected)
	{
		// Arrange
		var folder = this.environment.CreateFolder();

		_ = Directory.CreateDirectory(Path.Combine(folder, "sub"));
		_ = Directory.CreateDirectory(Path.Combine(folder, "sub2"));
		_ = ShellEnvironment.CreateFile(folder, "file1", executable: false);
		_ = ShellEnvironment.CreateFile(Path.Combine(folder, "sub"), "inner", executable: false);
		Directory.SetCurrentDirectory(folder);

		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(expected.Length > 0 ? 0 : 1);
		_ = this.environment.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <c>-e</c> and <c>-v</c> give the environment's names.
	/// </summary>
	[Fact]
	public void Run_WithExport_PrintsEnvironmentNames()
	{
		// Arrange
		Environment.SetEnvironmentVariable("SHARPIE_SAMPLE_EXPORTED", "1");

		try
		{
			// Act
			_ = this.sut.Run(["-e", "SHARPIE_SAMPLE_EXP"]);
			_ = this.sut.Run(["-v", "SHARPIE_SAMPLE_EXP"]);
		}
		finally
		{
			Environment.SetEnvironmentVariable("SHARPIE_SAMPLE_EXPORTED", null);
		}

		// Assert
		_ = this.environment.Output.Should().Be($"SHARPIE_SAMPLE_EXPORTED{Environment.NewLine}SHARPIE_SAMPLE_EXPORTED{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <c>-u</c> gives user names, root among them.
	/// </summary>
	[Fact]
	public void Run_WithUser_PrintsUserNames()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		_ = this.sut.Run(["-u", "roo"]);

		// Assert
		_ = this.environment.Output.Should().Be("root" + Environment.NewLine);
	}

	/// <summary>
	/// Checks that no match gives 1, an action with no words here gives 1, no arguments at all give 0, and a bad option
	/// gives 2.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The status.</param>
	[Theory]
	[InlineData(new[] { "-W", "x", "--", "z" }, 1)]
	[InlineData(new[] { "-A", "signal", "x" }, 1)]
	[InlineData(new string[0], 0)]
	[InlineData(new[] { "-o", "nospace" }, 1)]
	[InlineData(new[] { "-A", "bogus" }, 2)]
	[InlineData(new[] { "-p" }, 2)]
	public void Run_WithNothingToPrint_GivesStatus(string[] args, int expected)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(expected);
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>compopt</c> with names changes their specs' options, reports a name with none, and refuses a
	/// bad option.
	/// </summary>
	[Fact]
	public void Compopt_WithNames_ChangesTheirSpecs()
	{
		// Arrange
		var compopt = new CompoptCommand(this.engine);

		this.engine.Set("sampletool", new CompletionSpec { Function = "_sampletool", Options = new HashSet<string> { "nospace" } });

		// Act
		var changed = compopt.Run(["+o", "nospace", "-ofilenames", "-D", "-E", "-I", "--", "sampletool"]);
		var missing = compopt.Run(["-o", "default", "nope"]);
		var bad = compopt.Run(["-o", "bogus", "sampletool"]);
		var badFlag = compopt.Run(["-x"]);
		var noValue = compopt.Run(["-o"]);
		var notRunning = compopt.Run(["-o", "default"]);

		// Assert
		_ = (changed, missing, bad, badFlag, noValue, notRunning).Should().Be((0, 1, 2, 2, 2, 1));
		_ = this.engine.Specs["sampletool"].Options.Should().BeEquivalentTo(["filenames"]);
		_ = this.engine.Specs["sampletool"].Function.Should().Be("_sampletool");

		var help = "compopt [-o|+o option] [name...], where option is one of bashdefault, default, dirnames, filenames, noquote, nosort, nospace, plusdirs";

		_ = this.environment.Error.Should().Be(
			new ShellError("sharpie::compopt::not_found", "No completion is set for `nope`").Render(color: false)
			+ new ShellError("sharpie::compopt::invalid_option", "`-o` isn't an option of `compopt` here") { Help = help }.Render(color: false)
			+ new ShellError("sharpie::compopt::invalid_option", "`-x` isn't an option of `compopt` here") { Help = help }.Render(color: false)
			+ new ShellError("sharpie::compopt::invalid_option", "`-o` isn't an option of `compopt` here") { Help = help }.Render(color: false)
			+ new ShellError("sharpie::compopt::not_completing", "No completion function is running") { Help = "name the commands to change instead" }.Render(color: false));
	}

	/// <summary>
	/// Puts the console and folder back after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
