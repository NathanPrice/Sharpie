using System.Globalization;
using FluentAssertions;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for <see cref="ProcessSubstitution" />, with <c>/bin/sh</c> standing in for the copy of the shell, since the
/// test host isn't one.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class ProcessSubstitutionTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly Func<string, string[]>? original = ProcessSubstitution.CopyCommandLine;

	/// <summary>
	/// Initializes a new instance of the <see cref="ProcessSubstitutionTests" /> class, running each list with
	/// <c>/bin/sh -c</c>.
	/// </summary>
	public ProcessSubstitutionTests() => ProcessSubstitution.CopyCommandLine = list => ["/bin/sh", "-c", list];

	/// <summary>
	/// Checks that a <c>&lt;(...)</c> gives a <c>/dev/fd</c> path that reads what the list writes, and that the pipe is
	/// closed once the pipeline ends.
	/// </summary>
	[Fact]
	public void Start_WithReadingList_GivesPathToItsOutput()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		string? path = null;
		var held = 0;

		// Act
		var read = string.Empty;
		var status = ProcessSubstitution.Scoped(() =>
		{
			path = ProcessSubstitution.Start("echo hi", writes: false);
			held = ProcessSubstitution.HighestDescriptor;
			read = File.ReadAllText(path);

			return 3;
		});

		// Assert
		_ = status.Should().Be(3);
		_ = read.Should().Be("hi\n");
		_ = path.Should().Be($"/dev/fd/{held}");
		_ = ProcessSubstitution.HighestDescriptor.Should().Be(-1);
	}

	/// <summary>
	/// Checks that a <c>&gt;(...)</c> hands the list what the command writes, and closes the pipe when the pipeline
	/// ends, which <c>sort</c> waits for before it writes anything.
	/// </summary>
	[Fact]
	public void Start_WithWritingList_GivesItWhatCommandWrites()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var file = Path.Combine(this.environment.CreateFolder(), "out.txt");

		// Act
		_ = ProcessSubstitution.Scoped(() =>
		{
			File.WriteAllText(ProcessSubstitution.Start($"sort > '{file}'", writes: true), "written");

			return 0;
		});

		// Assert
		_ = WaitForText(file).Should().Be("written\n");
	}

	/// <summary>
	/// Checks that an inner pipeline closes only its own pipes, and that a copy started while another substitution is
	/// open doesn't hold that one's pipe, which would keep its reader from ever seeing the end.
	/// </summary>
	[Fact]
	public void Scoped_Nested_ClosesOnlyItsOwnPipes()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var inner = string.Empty;
		var outerOpen = false;
		var highest = 0;
		var highestOfBoth = -1;

		// Act
		_ = ProcessSubstitution.Scoped(() =>
		{
			var outer = ProcessSubstitution.Start("echo outer", writes: false);
			var number = outer["/dev/fd/".Length..];

			_ = ProcessSubstitution.Scoped(() =>
			{
				var path = ProcessSubstitution.Start($"if [ -e /dev/fd/{number} ]; then echo held; else echo closed; fi", writes: false);

				inner = File.ReadAllText(path);
				highest = ProcessSubstitution.HighestDescriptor;
				highestOfBoth = Math.Max(int.Parse(number, CultureInfo.InvariantCulture), int.Parse(path["/dev/fd/".Length..], CultureInfo.InvariantCulture));

				return 0;
			});

			outerOpen = ProcessSubstitution.HighestDescriptor == int.Parse(number, CultureInfo.InvariantCulture) && File.ReadAllText(outer) == "outer\n";

			return 0;
		});

		// Assert
		_ = inner.Should().Be("closed\n");
		_ = highest.Should().Be(highestOfBoth);
		_ = outerOpen.Should().BeTrue();
		_ = ProcessSubstitution.HighestDescriptor.Should().Be(-1);
	}

	/// <summary>
	/// Checks that the copy gets <c>SHLVL</c> one below the shell's, since it raises it again on starting, as bash's
	/// subshell stays on the same level. <c>printenv</c> stands in for it, since a shell would raise it too.
	/// </summary>
	[Fact]
	public void Start_Always_LowersShellLevelForCopy()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var level = Environment.GetEnvironmentVariable("SHLVL");

		ProcessSubstitution.CopyCommandLine = _ => ["/usr/bin/printenv", "SHLVL"];
		Environment.SetEnvironmentVariable("SHLVL", "5");

		try
		{
			// Act
			var read = string.Empty;

			_ = ProcessSubstitution.Scoped(() =>
			{
				read = File.ReadAllText(ProcessSubstitution.Start("unused", writes: false));

				return 0;
			});

			// Assert
			_ = read.Should().Be("4\n");
		}
		finally
		{
			Environment.SetEnvironmentVariable("SHLVL", level);
		}
	}

	/// <summary>
	/// Checks that a copy that can't start ends the command with an error and leaves no pipe open.
	/// </summary>
	[Fact]
	public void Start_WhenCopyCantStart_ThrowsExpansionError()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		ProcessSubstitution.CopyCommandLine = _ => ["/nonexistent/sampletool"];

		// Act
		var act = () => ProcessSubstitution.Scoped(() => ProcessSubstitution.Start("x", writes: false).Length);

		// Assert
		var error = act.Should().Throw<CommandLine.ExpansionError>().Which;

		_ = error.Fatal.Should().BeFalse();
		_ = error.Error.Code.Should().Be("sharpie::shell::cannot_start");
		_ = error.Error.Summary.Should().StartWith("Can't start the shell for a process substitution: ");
		_ = ProcessSubstitution.HighestDescriptor.Should().Be(-1);
	}

	/// <summary>
	/// Checks that the shell has to set what runs the copy first.
	/// </summary>
	[Fact]
	public void Start_WithoutCommandLine_Throws()
	{
		// Arrange
		ProcessSubstitution.CopyCommandLine = null;

		// Act
		var act = () => ProcessSubstitution.Start("x", writes: false);

		// Assert
		_ = act.Should().Throw<InvalidOperationException>();
	}

	/// <summary>
	/// Checks that splitting a line with nothing to run keeps a process substitution as typed, as one word with the
	/// text around it, and that one inside quotes is plain text.
	/// </summary>
	[Fact]
	public void Split_WithoutSubstitute_KeepsProcessSubstitutionAsTyped()
	{
		// Act
		var words = CommandLine.Split("cat <(a b) x>(y) '<(z)'");

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("cat", "<(a b)", "x>(y)", "<(z)");
	}

	/// <summary>
	/// Checks that splitting a line that runs substitutions gives the pipe's path in place of the text, as one word.
	/// </summary>
	[Fact]
	public void Split_WithSubstitute_GivesPipePath()
	{
		// Arrange
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var read = string.Empty;
		var words = Array.Empty<Word>();

		_ = ProcessSubstitution.Scoped(() =>
		{
			words = CommandLine.Split("cat --file=<(echo split) >(cat)", substitute: _ => string.Empty);
			read = File.ReadAllText(words[1].Text["--file=".Length..]);

			return 0;
		});

		// Assert
		_ = words.Should().HaveCount(3);
		_ = words[1].Text.Should().StartWith("--file=/dev/fd/");
		_ = words[2].Text.Should().StartWith("/dev/fd/");
		_ = read.Should().Be("split\n");
	}

	/// <summary>
	/// Puts back the shell's command line for copies.
	/// </summary>
	public void Dispose()
	{
		ProcessSubstitution.CopyCommandLine = this.original;
		this.environment.Dispose();
	}

	/// <summary>
	/// Waits up to ten seconds for a file to get any text, since a copy writes it after the pipeline ends.
	/// </summary>
	/// <param name="file">The file.</param>
	/// <returns>
	/// The text, or an empty string when none came.
	/// </returns>
	private static string WaitForText(string file)
	{
		for (var tries = 0; tries < 200; tries++)
		{
			if (File.Exists(file) && File.ReadAllText(file) is { Length: > 0 } text)
			{
				return text;
			}

			Thread.Sleep(50);
		}

		return string.Empty;
	}
}
