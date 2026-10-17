using FluentAssertions;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="EvalCommand" />.
/// </summary>
public sealed class EvalCommandTests
{
	private readonly List<string> lines = [];
	private readonly EvalCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="EvalCommandTests" /> class with a line runner that records each
	/// line and gives 3.
	/// </summary>
	public EvalCommandTests() => this.sut = new EvalCommand(line =>
	{
		this.lines.Add(line);

		return 3;
	});

	/// <summary>
	/// Checks that the arguments are joined with single spaces into one line, a leading <c>--</c> is dropped, and the
	/// line's status is given back.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The line that should be run.</param>
	[Theory]
	[InlineData(new[] { "echo", "$1" }, "echo $1")]
	[InlineData(new[] { "a=1; echo $a" }, "a=1; echo $a")]
	[InlineData(new[] { "echo  a", "  b" }, "echo  a   b")]
	[InlineData(new[] { "--", "echo", "hi" }, "echo hi")]
	[InlineData(new[] { "echo", "--" }, "echo --")]
	public void Run_WithArguments_RunsThemAsOneLine(string[] args, string expected)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(3);
		_ = this.lines.Should().Equal(expected);
	}

	/// <summary>
	/// Gets argument lists that leave nothing to run.
	/// </summary>
	/// <value>
	/// No arguments, an empty one, only blanks, and a lone <c>--</c>.
	/// </value>
	public static TheoryData<string[]> NothingToRun => [[], [""], [" ", "\t"], ["--"]];

	/// <summary>
	/// Checks that with nothing to run the status is 0 and no line is run, as bash's <c>eval</c> gives 0 even after a
	/// failed command.
	/// </summary>
	/// <param name="args">The arguments.</param>
	[Theory]
	[MemberData(nameof(NothingToRun))]
	public void Run_WithNothingToRun_GivesZero(string[] args)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.lines.Should().BeEmpty();
	}
}
