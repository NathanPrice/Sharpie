using System.Collections.Immutable;
using FluentAssertions;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Jobs;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="TrapCommand" />.
/// </summary>
/// <remarks>
/// The trap table belongs to the whole process, so each test clears what it set. Only <c>EXIT</c>, KILL, which does
/// nothing, and USR1, which the test host doesn't use, are set.
/// </remarks>
[Collection(GlobalState.Name)]
public sealed class TrapCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly List<(string Line, int Status)> lines = [];
	private readonly DeclareCommand declare = new(new ShellSettings());
	private readonly TrapCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="TrapCommandTests" /> class with a line runner that records each
	/// line with the <c>$?</c> it saw, and gives 9.
	/// </summary>
	public TrapCommandTests() => this.sut = new TrapCommand(
		line =>
		{
			this.lines.Add((line, this.declare.Status));

			return this.declare.Status = 9;
		},
		this.declare);

	/// <summary>
	/// Gets the signals the tests set, which Windows doesn't all have.
	/// </summary>
	/// <value>
	/// <c>EXIT</c> and KILL, and USR1 off Windows.
	/// </value>
	/// <summary>
	/// Gets the words that print every trap.
	/// </summary>
	/// <value>
	/// None, <c>-p</c>, and <c>--</c>.
	/// </value>
	public static TheoryData<string[]> PrintForms => [[], ["-p"], ["--"]];

	/// <summary>
	/// Gets the words that remove the traps on <c>EXIT</c> and KILL.
	/// </summary>
	/// <value>
	/// <c>-</c> as the action, signal numbers only, and <c>-</c> after <c>--</c>.
	/// </value>
	public static TheoryData<string[]> ResetForms => [["-", "EXIT", "KILL"], ["0", "9"], ["--", "-", "0", "KILL"]];

	private static string[] UsedSignals => OperatingSystem.IsWindows() ? ["EXIT", "KILL"] : ["EXIT", "KILL", "USR1"];

	/// <summary>
	/// Checks that <c>trap</c> with no words, and <c>trap -p</c>, print each trap as the command that sets it, in
	/// single quotes with <c>'</c> written as <c>'\''</c>, <c>EXIT</c> first and the rest by signal number, as bash
	/// does.
	/// </summary>
	/// <param name="print">The words that print the traps.</param>
	[Theory]
	[MemberData(nameof(PrintForms))]
	public void Run_WithNoSignals_PrintsEveryTrapInOrder(string[] print)
	{
		// Arrange
		_ = this.sut.Run(["it's", "KILL"]);
		_ = this.sut.Run(["", "exit"]);

		// Act
		var status = this.sut.Run(print);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be("trap -- '' EXIT\ntrap -- 'it'\\''s' SIGKILL\n");
	}

	/// <summary>
	/// Checks that <c>trap -p SIGNAL</c> prints only that signal's trap, and nothing for a signal without one.
	/// </summary>
	[Fact]
	public void Run_WithPrintAndSignals_PrintsOnlyThose()
	{
		// Arrange
		_ = this.sut.Run(["echo bye", "EXIT", "SIGKILL"]);

		// Act
		var status = this.sut.Run(["-p", "9", "INT"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be("trap -- 'echo bye' SIGKILL\n");
	}

	/// <summary>
	/// Checks the ways to remove a trap, as bash reads them: <c>-</c> as the action, a lone signal, and a first word
	/// that is a signal number, which makes every word a signal.
	/// </summary>
	/// <param name="reset">The words that remove the traps on <c>EXIT</c> and KILL.</param>
	[Theory]
	[MemberData(nameof(ResetForms))]
	public void Run_WithReset_RemovesTraps(string[] reset)
	{
		// Arrange
		_ = this.sut.Run(["echo bye", "EXIT", "KILL"]);

		// Act
		var status = this.sut.Run(reset);

		// Assert
		_ = status.Should().Be(0);
		_ = this.sut.Run([]);
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a lone signal removes only its own trap.
	/// </summary>
	[Fact]
	public void Run_WithLoneSignal_RemovesItsTrap()
	{
		// Arrange
		_ = this.sut.Run(["echo bye", "EXIT", "KILL"]);

		// Act
		var status = this.sut.Run(["EXIT"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.sut.Run([]);
		_ = this.environment.Output.Should().Be("trap -- 'echo bye' SIGKILL\n");
	}

	/// <summary>
	/// Checks that an unknown signal gives 1 after an error, and the known ones around it are still set.
	/// </summary>
	[Fact]
	public void Run_WithUnknownSignal_GivesOneAndSetsTheOthers()
	{
		// Act
		var status = this.sut.Run(["echo bye", "NOPE", "EXIT"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Error.Should().Contain("`NOPE` isn't a signal");
		_ = this.sut.Run([]);
		_ = this.environment.Output.Should().Be("trap -- 'echo bye' EXIT\n");
	}

	/// <summary>
	/// Checks that <c>trap -p</c> with an unknown signal gives 1 and still prints the known ones.
	/// </summary>
	[Fact]
	public void Run_WithPrintAndUnknownSignal_GivesOne()
	{
		// Arrange
		_ = this.sut.Run(["echo bye", "EXIT"]);

		// Act
		var status = this.sut.Run(["-p", "NOPE", "EXIT"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Output.Should().Be("trap -- 'echo bye' EXIT\n");
	}

	/// <summary>
	/// Checks that an unknown option, or an action with no signal after it, gives 2 after an error and sets nothing.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="error">Part of the error expected.</param>
	[Theory]
	[InlineData(new[] { "-x", "echo", "EXIT" }, "Unknown option `-x`")]
	[InlineData(new[] { "-px" }, "Unknown option `-x`")]
	[InlineData(new[] { "echo" }, "`echo` needs a signal after it")]
	public void Run_WithBadUsage_GivesTwo(string[] args, string error)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(2);
		_ = this.environment.Error.Should().Contain(error);
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>trap -l</c> lists the signals as <c>kill -l</c> does.
	/// </summary>
	[Fact]
	public void Run_WithList_ListsSignals()
	{
		// Act
		var status = this.sut.Run(["-l"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().StartWith(OperatingSystem.IsWindows() ? " 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT" : " 1) SIGHUP\t 2) SIGINT");
	}

	/// <summary>
	/// Checks that an <c>EXIT</c> trap a subshell set is taken out of the table, and runs with <c>$?</c> set to the
	/// subshell's status, which comes back whatever the action gives.
	/// </summary>
	[Fact]
	public void TakeSubshellExitTrap_WithSubshellsOwnTrap_RunsItWithStatus()
	{
		// Arrange
		var saved = TrapCommand.Save();

		_ = this.sut.Run(["echo bye", "EXIT"]);

		// Act
		var status = TrapCommand.TakeSubshellExitTrap(saved)!(4);

		// Assert
		_ = status.Should().Be(4);
		_ = this.lines.Should().Equal(("echo bye", 4));
		_ = this.declare.Status.Should().Be(4);
		_ = TrapCommand.Save().Should().BeEquivalentTo(ImmutableSortedDictionary<int, TrapCommand.Trap>.Empty);
	}

	/// <summary>
	/// Checks that a subshell's own <c>EXIT</c> trap is taken out to run at its end, and the shell's own one isn't.
	/// </summary>
	[Fact]
	public void TakeSubshellExitTrap_WithShellsOwnTrap_GivesNull()
	{
		// Arrange
		_ = this.sut.Run(["echo bye", "EXIT"]);
		var saved = TrapCommand.Save();

		// Act
		var exitTrap = TrapCommand.TakeSubshellExitTrap(saved);

		// Assert
		_ = exitTrap.Should().BeNull();
		_ = this.sut.Run([]);
		_ = this.environment.Output.Should().Be("trap -- 'echo bye' EXIT\n");
	}

	/// <summary>
	/// Checks that <see cref="TrapCommand.Restore" /> puts back the table from before a subshell.
	/// </summary>
	[Fact]
	public void Restore_AfterSubshellChanges_PutsBackTraps()
	{
		// Arrange
		_ = this.sut.Run(["echo bye", "EXIT"]);
		var saved = TrapCommand.Save();

		_ = this.sut.Run(["-", "EXIT"]);
		_ = this.sut.Run(["x", "KILL"]);

		// Act
		TrapCommand.Restore(saved);

		// Assert
		_ = this.sut.Run([]);
		_ = this.environment.Output.Should().Be("trap -- 'echo bye' EXIT\n");
	}

	/// <summary>
	/// Checks that a trapped signal waits for <see cref="TrapCommand.RunPending" />, which runs its action with
	/// <c>$?</c> kept, and that an ignored one is dropped and counted as ignored.
	/// </summary>
	[Fact]
	public void RunPending_WithTrappedSignal_RunsActionKeepingStatus()
	{
		// Arrange
		// Windows has no USR1.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var usr1 = KillCommand.ParseSignal("USR1")!.Value;

		_ = this.sut.Run(["echo got", "USR1"]);
		TrapCommand.Arrived(usr1, endsShell: false);

		// Act
		TrapCommand.RunPending(interactive: false, 4);

		// Assert
		_ = this.lines.Should().Equal(("echo got", 4));
		_ = this.declare.Status.Should().Be(4);
		_ = TrapCommand.LastSignal.Should().Be(usr1);
		_ = TrapCommand.IsIgnored(usr1).Should().BeFalse();

		_ = this.sut.Run(["", "USR1"]);
		TrapCommand.Arrived(usr1, endsShell: false);
		TrapCommand.RunPending(interactive: false, 4);

		_ = this.lines.Should().HaveCount(1);
		_ = TrapCommand.IsIgnored(usr1).Should().BeTrue();
	}

	/// <summary>
	/// Clears every trap a test set and restores the console.
	/// </summary>
	public void Dispose()
	{
		_ = this.sut.Run(["-", .. UsedSignals]);
		this.environment.Dispose();
	}
}
