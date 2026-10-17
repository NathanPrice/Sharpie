using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Runs <c>ulimit</c> through the built shell as a real process, so setting a limit changes only that shell.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class UlimitProcessTests
{
	/// <summary>
	/// Checks that setting a limit with neither <c>-H</c> nor <c>-S</c> lowers both, and that programs the shell
	/// starts afterwards inherit it.
	/// </summary>
	[Fact]
	public async Task Shell_WithLimitSet_ChangesBothAndChildrenInherit()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "ulimit -n 256; ulimit -n; ulimit -Hn; sh -c 'ulimit -n'"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("256\n256\n256\n");
		_ = run.Error.Should().BeEmpty();
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that each limit prints, in its unit and as <c>unlimited</c> when there is none, what <c>sh</c> prints
	/// for the same limit.
	/// </summary>
	[Fact]
	public async Task Shell_WithEachLimit_PrintsWhatShPrints()
	{
		// Arrange
		var script = string.Join("; ", "cdflmnstv".SelectMany(letter => new[] { $"ulimit -{letter}", $"sh -c 'ulimit -{letter}'", $"ulimit -H{letter}", $"sh -c 'ulimit -H{letter}'" }));

		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", script]).ConfigureAwait(true);

		// Assert
		var lines = run.Output.Split('\n');

		_ = lines.Should().HaveCount(37);
		_ = lines.Where((_, index) => index % 2 == 0).SkipLast(1).Should().Equal(lines.Where((_, index) => index % 2 == 1));
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>-S</c> sets only the soft limit and leaves the hard one alone.
	/// </summary>
	[Fact]
	public async Task Shell_WithSoftLimitSet_KeepsHardLimit()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "ulimit -Hn; ulimit -Sn 200; ulimit -Hn; ulimit -n"]).ConfigureAwait(true);

		// Assert
		var lines = run.Output.Split('\n');

		_ = lines.Should().HaveCount(4);
		_ = lines[1].Should().Be(lines[0]);
		_ = lines[2].Should().Be("200");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>-H</c> sets only the hard limit and leaves the soft one alone.
	/// </summary>
	[Fact]
	public async Task Shell_WithHardLimitSet_KeepsSoftLimit()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "ulimit -n 500; ulimit -Sn 300; ulimit -Hn 400; ulimit -Sn; ulimit -Hn"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("300\n400\n");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that a value is taken in the limit's unit, so <c>-c 2</c> is two 512-byte blocks, and that
	/// <c>soft</c> stands for the current soft limit.
	/// </summary>
	[Fact]
	public async Task Shell_WithBlockValue_ScalesAndReadsBack()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "ulimit -f 1 -c 2; ulimit -f -c; ulimit -Hc soft; ulimit -Hc"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("file size                   (blocks, -f) 1\ncore file size              (blocks, -c) 2\n2\n");
		_ = run.ExitCode.Should().Be(0);
	}

	/// <summary>
	/// Checks that a soft limit above the hard one is refused with status 1 and the limit stays as it was.
	/// </summary>
	[Fact]
	public async Task Shell_WithSoftAboveHard_GivesOne()
	{
		// Act
		var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", "ulimit -n 300; ulimit -Sn 400; echo $?; ulimit -n"]).ConfigureAwait(true);

		// Assert
		_ = run.Output.Should().Be("1\n300\n");
		_ = run.Error.Should().Contain("sharpie::ulimit::cannot_change").And.Contain("Can't change the open files limit");
	}
}
