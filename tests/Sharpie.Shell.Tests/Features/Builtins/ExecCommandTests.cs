using System.Text;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="ExecCommand" /> that leave the test process in place. The ones that replace it or move its
/// output are in <see cref="ExecProcessTests" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ExecCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly string folder;
	private readonly ExecCommand sut = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="ExecCommandTests" /> class with an empty folder as the only
	/// <c>PATH</c> entry.
	/// </summary>
	public ExecCommandTests()
	{
		this.folder = this.environment.CreateFolder();
		ShellEnvironment.SetPath(this.folder);
	}

	/// <summary>
	/// Checks that <c>exec</c> in a pipeline refuses rather than replace the whole shell.
	/// </summary>
	[Fact]
	public void Run_FromPipeline_ReportsErrorAndReturnsOne()
	{
		// Act
		var status = this.sut.Run(["cat"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Error.Should().Contain("sharpie::exec::in_pipeline").And.Contain("`exec` can't run in a pipeline").And.Contain("run the program without exec");
	}

	/// <summary>
	/// Checks that <c>exec</c> with no program and no redirects does nothing.
	/// </summary>
	[Fact]
	public void Run_WithNothing_ReturnsZero()
	{
		// Act
		var status = this.sut.Run([], [], null, null, interactive: true);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a program that isn't found gives 127 in an interactive shell, which keeps going, without saving
	/// the history first.
	/// </summary>
	[Fact]
	public void Run_WithMissingProgram_ReportsNotFoundAndReturns127()
	{
		// Arrange
		var saved = false;

		this.sut.BeforeReplace = () => saved = true;

		// Act
		var status = this.sut.Run(["sampletool_missing"], [], null, null, interactive: true);

		// Assert
		_ = status.Should().Be(127);
		_ = saved.Should().BeFalse();
		_ = this.environment.Error.Should().Contain("sharpie::exec::not_found").And.Contain("`sampletool_missing`").And.Contain("not a program on PATH");
	}

	/// <summary>
	/// Checks that an assigned <c>PATH</c> is where the program is looked for, as in bash.
	/// </summary>
	[Fact]
	public void Run_WithAssignedPathMissingProgram_SearchesAssignedPath()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "sampletool", executable: true, "echo hi\n");

		// Act
		var status = this.sut.Run(["sampletool"], ["PATH=/sampletool_missing_folder"], null, null, interactive: true);

		// Assert
		_ = status.Should().Be(127);
	}

	/// <summary>
	/// Checks that a program the system can't start gives 126 in an interactive shell, after saving the history, and
	/// that the shell's standard output and signals are put back as they were.
	/// </summary>
	/// <remarks>
	/// A file marked executable with no <c>#!</c> line is refused by the system with "Exec format error".
	/// </remarks>
	[Fact]
	public void Run_WithProgramThatCantStart_RestoresShellAndReturns126()
	{
		// Arrange
		var program = ShellEnvironment.CreateFile(this.folder, "sampletool", executable: true, "echo hi\n");
		var redirect = Path.Combine(this.folder, "out.txt");
		var signalsBefore = IgnoredSignals();
		var saved = false;

		this.sut.BeforeReplace = () => saved = true;

		int status;

		using (var output = File.Create(redirect))
		{
			// Act
			status = this.sut.Run([program], [], output, null, interactive: true);

			// Written straight to descriptor 1, which would still be the file if it wasn't put back.
			using var handle = new SafeFileHandle(1, ownsHandle: false);
			using var standardOutput = new FileStream(handle, FileAccess.Write);

			standardOutput.Write("\n"u8);
		}

		// Assert
		_ = status.Should().Be(126);
		_ = saved.Should().BeTrue();
		_ = this.environment.Error.Should().Contain("sharpie::exec::cannot_start");
		_ = File.ReadAllText(redirect).Should().BeEmpty();
		_ = IgnoredSignals().Should().Be(signalsBefore);
	}

	/// <summary>
	/// Checks that the program <c>exec</c> starts gets the shell's <c>SHLVL</c> minus one, so a shell started that way
	/// lands on the same level, and that a typed <c>SHLVL=</c> wins.
	/// </summary>
	/// <param name="assignments">The words typed in front of <c>exec</c>.</param>
	/// <param name="expected">The <c>SHLVL</c> the program should see.</param>
	[Theory]
	[InlineData(new string[0], "4\n")]
	[InlineData(new[] { "SHLVL=9" }, "9\n")]
	public void RunInSubstitution_WithProgram_HandsDownLevelBelow(string[] assignments, string expected)
	{
		// Arrange
		var saved = Environment.GetEnvironmentVariable("SHLVL");

		using var output = new MemoryStream();

		try
		{
			Environment.SetEnvironmentVariable("SHLVL", "5");

			// Act
			var run = () => ExecCommand.RunInSubstitution(["/usr/bin/printenv", "SHLVL"], assignments, output, null);

			// Assert
			_ = run.Should().Throw<ExitCommand.SubshellExit>();
			_ = Encoding.UTF8.GetString(output.ToArray()).Should().Be(expected);
		}
		finally
		{
			Environment.SetEnvironmentVariable("SHLVL", saved);
		}
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Reads which signals this process ignores, on Linux.
	/// </summary>
	/// <returns>
	/// The <c>SigIgn</c> line of <c>/proc/self/status</c>, or <see langword="null" /> where there's no such file.
	/// </returns>
	private static string? IgnoredSignals() =>
		File.Exists("/proc/self/status") ? File.ReadLines("/proc/self/status").First(line => line.StartsWith("SigIgn:", StringComparison.Ordinal)) : null;
}
