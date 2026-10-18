using System.Globalization;
using FluentAssertions;
using Sharpie.Shell.Features.Variables;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for <see cref="DeclareCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class DeclareCommandTests : IDisposable
{
	/// <summary>
	/// A made-up environment variable name the tests set and remove.
	/// </summary>
	private const string SampleVariable = "SHARPIE_SAMPLE_DECLARED";

	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand sut = new(new ShellSettings());

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Run" /> stores a variable whose name is a valid identifier.
	/// </summary>
	/// <param name="name">The variable name.</param>
	/// <param name="value">The value assigned to it.</param>
	[Theory]
	[InlineData("foo", "bar")]
	[InlineData("_FOO", "BAR")]
	[InlineData("a1_b", "x")]
	public void Run_WithValidName_StoresVariable(string name, string value)
	{
		// Arrange
		_ = this.sut.Run([$"{name}={value}"]);

		// Act
		_ = this.sut.Run(["-p", name]);

		// Assert
		_ = this.environment.Output.Should().Be($"declare -- {name}=\"{value}\"{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Run" /> reports an invalid name and does not store the variable.
	/// </summary>
	/// <param name="argument">The <c>NAME=VALUE</c> argument with an invalid name.</param>
	/// <param name="name">The name part of <paramref name="argument" />.</param>
	[Theory]
	[InlineData("23=x", "23")]
	[InlineData("1abc=x", "1abc")]
	[InlineData("a-b=x", "a-b")]
	[InlineData("=x", "")]
	public void Run_WithInvalidName_PrintsErrorAndStoresNothing(string argument, string name)
	{
		// Act
		_ = this.sut.Run([argument]);
		_ = this.sut.Run(["-p", name]);

		// Assert
		_ = this.environment.Error.Should().StartWith(new ShellError("sharpie::declare::invalid_identifier", $"`{argument}` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Render(color: false));
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>-p</c> with a name that isn't set reports it each time and doesn't create it.
	/// </summary>
	[Fact]
	public void Run_PrintUnknownName_ReportsNotFoundAndCreatesNothing()
	{
		// Act
		_ = this.sut.Run(["-p", "missing"]);
		_ = this.sut.Run(["-p", "missing"]);

		// Assert
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Contain(string.Concat(Enumerable.Repeat(new ShellError("sharpie::declare::not_found", "No variable named `missing`").Render(color: false), 2)));
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.IsAssignment" /> accepts a valid name followed by <c>=</c> and rejects
	/// everything else.
	/// </summary>
	/// <param name="word">The word to check.</param>
	/// <param name="expected">Whether it is an assignment.</param>
	[Theory]
	[InlineData("A=1", true)]
	[InlineData("_a1=", true)]
	[InlineData("A=b=c", true)]
	[InlineData("A", false)]
	[InlineData("=1", false)]
	[InlineData("1A=1", false)]
	[InlineData("a-b=1", false)]
	public void IsAssignment_WithWord_ReturnsExpected(string word, bool expected)
	{
		// Act
		var result = DeclareCommand.IsAssignment(word);

		// Assert
		_ = result.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Set" /> also updates the environment when the name is already exported.
	/// </summary>
	[Fact]
	public void Set_WithExportedName_UpdatesEnvironment()
	{
		// Arrange
		Environment.SetEnvironmentVariable(SampleVariable, "old");

		// Act
		this.sut.Set(SampleVariable, "new");

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("new");
		_ = this.sut.Variables.Should().Contain(SampleVariable, "new");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Set" /> keeps a name that isn't exported out of the environment.
	/// </summary>
	[Fact]
	public void Set_WithNameNotExported_LeavesEnvironmentAlone()
	{
		// Act
		this.sut.Set(SampleVariable, "value");

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().BeNull();
		_ = this.sut.Variables.Should().Contain(SampleVariable, "value");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Remove" /> takes the name out of both the shell and the environment.
	/// </summary>
	[Fact]
	public void Remove_WithExportedName_RemovesBoth()
	{
		// Arrange
		Environment.SetEnvironmentVariable(SampleVariable, "value");
		this.sut.Set(SampleVariable, "value");

		// Act
		this.sut.Remove(SampleVariable);

		// Assert
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().BeNull();
		_ = this.sut.Variables.Should().NotContainKey(SampleVariable);
	}

	/// <summary>
	/// Restores the console and <c>PATH</c> after each test, and removes the sample variable.
	/// </summary>
	public void Dispose()
	{
		Environment.SetEnvironmentVariable(SampleVariable, null);
		this.environment.Dispose();
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Status" /> starts at 0 and is kept as the variable <c>?</c>.
	/// </summary>
	[Fact]
	public void Status_WhenSet_IsKeptAsQuestionMarkVariable()
	{
		// Arrange
		var before = this.sut.Status;

		// Act
		this.sut.Status = 42;

		// Assert
		_ = before.Should().Be(0);
		_ = this.sut.Status.Should().Be(42);
		_ = this.sut.Variables.Should().Contain("?", "42");
	}

	/// <summary>
	/// Checks that <c>declare -p</c> gives 1 when a name isn't set and 0 when every name is.
	/// </summary>
	/// <param name="name">The name to print.</param>
	/// <param name="expected">The expected status.</param>
	[Theory]
	[InlineData("set_one", 0)]
	[InlineData("missing_one", 1)]
	public void Run_WithPrintOption_GivesStatus(string name, int expected)
	{
		// Arrange
		_ = this.sut.Run(["set_one=1"]);

		// Act
		var status = this.sut.Run(["-p", name]);

		// Assert
		_ = status.Should().Be(expected);
	}

	/// <summary>
	/// Checks that an assignment gives 0, and a bad name gives 1 while the good names around it are still set.
	/// </summary>
	[Fact]
	public void Run_WithBadName_GivesOneAndSetsTheRest()
	{
		// Act
		var good = this.sut.Run(["a=1"]);
		var bad = this.sut.Run(["1x=2", "b=3"]);

		// Assert
		_ = good.Should().Be(0);
		_ = bad.Should().Be(1);
		_ = this.sut.Variables.Should().Contain("b", "3");
	}

	/// <summary>
	/// Checks that there are no positional parameters at first, and <c>$#</c> is already 0.
	/// </summary>
	[Fact]
	public void Positional_WhenNew_IsEmptyWithCountZero()
	{
		// Assert
		_ = this.sut.Positional.Should().BeEmpty();
		_ = this.sut.Variables.Should().Contain("#", "0");
	}

	/// <summary>
	/// Checks that setting fewer parameters than before drops the old ones past the new count.
	/// </summary>
	[Fact]
	public void SetPositional_WithFewerValues_DropsTheOldOnes()
	{
		// Arrange
		this.sut.SetPositional(["a", "b", "c"]);

		// Act
		this.sut.SetPositional(["x"]);

		// Assert
		_ = this.sut.Positional.Should().Equal("x");
		_ = this.sut.Variables.Should().Contain("1", "x").And.Contain("#", "1").And.NotContainKeys("2", "3");
	}

	/// <summary>
	/// Checks that positional parameters stay out of the environment.
	/// </summary>
	[Fact]
	public void SetPositional_WithValues_LeavesEnvironmentAlone()
	{
		// Act
		this.sut.SetPositional(["a"]);

		// Assert
		_ = Environment.GetEnvironmentVariable("1").Should().BeNull();
		_ = Environment.GetEnvironmentVariable("#").Should().BeNull();
	}

	/// <summary>
	/// Checks that a missing count or a count past the stored parameters still reads back in order.
	/// </summary>
	[Fact]
	public void ReadPositional_WithMissingCountOrValues_FillsTheGaps()
	{
		// Act
		var none = DeclareCommand.ReadPositional(new Dictionary<string, string> { ["1"] = "a" });
		var gaps = DeclareCommand.ReadPositional(new Dictionary<string, string> { ["#"] = "2", ["2"] = "b" });

		// Assert
		_ = none.Should().BeEmpty();
		_ = gaps.Should().Equal("", "b");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.RunWith" /> sets the variables, an empty one included, only while the
	/// builtin runs, and leaves the environment alone.
	/// </summary>
	[Fact]
	public void RunWith_NewNames_SetsThemOnlyDuringRun()
	{
		// Arrange
		Dictionary<string, string>? during = null;

		// Act
		var status = this.sut.RunWith([$"{SampleVariable}=", "other=a=b"], () =>
		{
			during = new Dictionary<string, string>(this.sut.Variables);

			return 3;
		});

		// Assert
		_ = status.Should().Be(3);
		_ = during.Should().Contain(SampleVariable, string.Empty).And.Contain("other", "a=b");
		_ = this.sut.Variables.Should().NotContainKey(SampleVariable).And.NotContainKey("other");
		_ = Environment.GetEnvironmentVariable("other").Should().BeNull();
	}

	/// <summary>
	/// Checks that a name set before keeps its old value afterwards, even when it's assigned twice or the builtin
	/// throws.
	/// </summary>
	[Fact]
	public void RunWith_SetName_PutsOldValueBack()
	{
		// Arrange
		_ = this.sut.Run(["name=old"]);
		string? during = null;

		// Act
		var act = () => this.sut.RunWith(["name=one", "name=two"], () =>
		{
			during = this.sut.Variables["name"];

			throw new InvalidOperationException();
		});

		// Assert
		_ = act.Should().Throw<InvalidOperationException>();
		_ = during.Should().Be("two");
		_ = this.sut.Variables["name"].Should().Be("old");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Restore" /> leaves exactly the saved variables, dropping later ones and
	/// putting back changed ones.
	/// </summary>
	[Fact]
	public void Restore_WithSavedVariables_ReplacesAll()
	{
		// Arrange
		this.sut.Set("kept", "old");
		var saved = this.sut.Variables.ToDictionary();

		this.sut.Set("kept", "new");
		this.sut.Set("added", "x");

		// Act
		this.sut.Restore(saved);

		// Assert
		_ = this.sut.Variables.Keys.Should().BeEquivalentTo(saved.Keys);
		_ = this.sut.Variables["kept"].Should().Be("old");
	}

	/// <summary>
	/// Checks that <c>RANDOM=N</c> seeds <c>RANDOM</c>, so the same seed gives the same numbers, each from 0 to 32767,
	/// and that a seed that isn't a number counts as 0.
	/// </summary>
	[Fact]
	public void Set_WithRandomSeed_RepeatsNumbers()
	{
		// Arrange
		this.sut.Set("RANDOM", "5");
		var first = Enumerable.Range(0, 50).Select(_ => int.Parse(this.sut.Variables["RANDOM"], CultureInfo.InvariantCulture)).ToArray();

		this.sut.Set("RANDOM", "0");
		var zero = this.sut.Variables["RANDOM"];

		// Act
		this.sut.Set("RANDOM", "5");
		var second = Enumerable.Range(0, 50).Select(_ => int.Parse(this.sut.Variables["RANDOM"], CultureInfo.InvariantCulture)).ToArray();

		this.sut.Set("RANDOM", "x");

		// Assert
		_ = second.Should().Equal(first);
		_ = first.Should().OnlyContain(number => number >= 0 && number < 32768).And.Contain(number => number > 16383);
		_ = this.sut.Variables["RANDOM"].Should().Be(zero);
		_ = this.sut.Variables.Keys.Should().Contain("RANDOM");
	}

	/// <summary>
	/// Checks that <c>SECONDS=N</c> starts the count again from N.
	/// </summary>
	[Fact]
	public void Set_WithSeconds_CountsFromValue()
	{
		// Act
		this.sut.Set("SECONDS", "100");

		// Assert
		_ = this.sut.Variables["SECONDS"].Should().Be("100");
		_ = this.sut.Variables.Should().Contain("SECONDS", "100");
	}

	/// <summary>
	/// Checks that <c>SECONDS</c> counts whole seconds from when it was set.
	/// </summary>
	[Fact]
	public async Task Variables_AfterASecond_CountsSeconds()
	{
		// Arrange
		this.sut.Set("SECONDS", "7");

		// Act
		await Task.Delay(TimeSpan.FromMilliseconds(1100)).ConfigureAwait(true);

		// Assert
		_ = this.sut.Variables["SECONDS"].Should().Be("8");
	}

	/// <summary>
	/// Checks that an exported <c>SECONDS</c> hands the value it's given to the environment, while the shell still
	/// counts from it.
	/// </summary>
	[Fact]
	public void Set_WithExportedSeconds_UpdatesEnvironment()
	{
		// Arrange
		var saved = Environment.GetEnvironmentVariable("SECONDS");

		try
		{
			this.sut.Export("SECONDS", "1");

			// Act
			this.sut.Set("SECONDS", "50");

			// Assert
			_ = Environment.GetEnvironmentVariable("SECONDS").Should().Be("50");
			_ = this.sut.Variables["SECONDS"].Should().Be("50");
		}
		finally
		{
			Environment.SetEnvironmentVariable("SECONDS", saved);
		}
	}

	/// <summary>
	/// Checks that after <c>unset RANDOM</c>, <c>RANDOM</c> is an ordinary variable that keeps what it's given.
	/// </summary>
	[Fact]
	public void Remove_WithRandom_MakesItOrdinary()
	{
		// Arrange
		this.sut.Remove("RANDOM");

		// Act
		this.sut.Set("RANDOM", "x");

		// Assert
		_ = this.sut.Variables["RANDOM"].Should().Be("x");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.LineNumber" /> is what <c>LINENO</c> gives, and that <c>LINENO=N</c> sets
	/// it.
	/// </summary>
	[Fact]
	public void LineNumber_WhenSet_IsLineno()
	{
		// Arrange
		this.sut.LineNumber = 7;
		var seven = this.sut.Variables["LINENO"];

		// Act
		this.sut.Set("LINENO", "9");

		// Assert
		_ = seven.Should().Be("7");
		_ = this.sut.LineNumber.Should().Be(9);
		_ = this.sut.Variables.ContainsKey("LINENO").Should().BeTrue();
	}

	/// <summary>
	/// Checks that only a worked-out <c>LINENO</c> moves <see cref="DeclareCommand.LineNumber" />: neither another
	/// name nor <c>LINENO</c> after <c>unset</c> does.
	/// </summary>
	[Fact]
	public void Set_WithOtherNameOrUnsetLineno_KeepsLineNumber()
	{
		// Arrange
		this.sut.LineNumber = 2;

		// Act
		this.sut.Set("other", "8");
		this.sut.Remove("LINENO");
		this.sut.Set("LINENO", "9");

		// Assert
		_ = this.sut.LineNumber.Should().Be(2);
		_ = this.sut.Variables["LINENO"].Should().Be("9");
	}

	/// <summary>
	/// Checks that <c>declare -p</c> prints a worked-out variable too.
	/// </summary>
	[Fact]
	public void Run_WithPrintLineno_PrintsIt()
	{
		// Arrange
		this.sut.LineNumber = 3;

		// Act
		_ = this.sut.Run(["-p", "LINENO"]);

		// Assert
		_ = this.environment.Output.Should().Be($"declare -- LINENO=\"3\"{Environment.NewLine}");
	}

	/// <summary>
	/// Checks that <c>$</c> is the shell's process id and <c>-</c> follows the options as they change.
	/// </summary>
	[Fact]
	public void Variables_WithOptions_GivesProcessIdAndFlags()
	{
		// Arrange
		var settings = new ShellSettings();
		var sut = new DeclareCommand(settings);
		var before = sut.Variables["-"];

		// Act
		_ = settings.Set("errexit", true);

		// Assert
		_ = before.Should().BeEmpty();
		_ = sut.Variables["-"].Should().Be("e");
		_ = sut.Variables["$"].Should().Be(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
		_ = sut.Variables.Should().ContainKeys("$", "PPID", "-", "SECONDS", "LINENO");
	}

	/// <summary>
	/// Checks that <c>!</c> is unset until a program runs in the background, then gives its process id.
	/// </summary>
	[Fact]
	public void Variables_WithLastBackground_GivesItsId()
	{
		// Arrange
		var saved = ExternalProgram.LastBackground;

		try
		{
			ExternalProgram.LastBackground = null;
			var before = this.sut.Variables.ContainsKey("!");

			// Act
			ExternalProgram.LastBackground = 42;

			// Assert
			_ = before.Should().BeFalse();
			_ = this.sut.Variables.Should().Contain("!", "42");
		}
		finally
		{
			ExternalProgram.LastBackground = saved;
		}
	}

	/// <summary>
	/// Checks that the action <see cref="DeclareCommand.SaveDynamic" /> gives puts back what a command substitution
	/// changed: an unset <c>RANDOM</c>, a <c>SECONDS</c> count and <c>$!</c>.
	/// </summary>
	[Fact]
	public void SaveDynamic_WhenRestored_PutsBackComputedNames()
	{
		// Arrange
		var saved = ExternalProgram.LastBackground;

		try
		{
			ExternalProgram.LastBackground = 7;
			this.sut.Set("RANDOM", "3");
			var expected = this.sut.Variables["RANDOM"];

			this.sut.Set("RANDOM", "3");
			var restore = this.sut.SaveDynamic();

			this.sut.Set("SECONDS", "100");
			this.sut.Set("RANDOM", "4");
			this.sut.Remove("RANDOM");
			this.sut.Set("RANDOM", "x");
			ExternalProgram.LastBackground = 8;

			// Act
			restore();

			// Assert
			_ = this.sut.Variables["RANDOM"].Should().Be(expected);
			_ = int.Parse(this.sut.Variables["SECONDS"], CultureInfo.InvariantCulture).Should().BeLessThan(100);
			_ = this.sut.Variables["!"].Should().Be("7");
		}
		finally
		{
			ExternalProgram.LastBackground = saved;
		}
	}

	/// <summary>
	/// Checks that the view counts the stored variables and the worked-out ones that are set, and enumerates each
	/// name once.
	/// </summary>
	[Fact]
	public void Variables_WithStoredAndComputed_CountsEachOnce()
	{
		// Arrange
		this.sut.Set("PPID", "1");

		// Act
		var keys = this.sut.Variables.Keys.ToArray();

		// Assert
		_ = keys.Should().OnlyHaveUniqueItems();
		_ = this.sut.Variables.Count.Should().Be(keys.Length);
		_ = this.sut.Variables.Values.Should().HaveCount(keys.Length);
		_ = this.sut.Variables["PPID"].Should().NotBe("1");
	}

	/// <summary>
	/// Checks that reading a name that isn't set through the indexer throws.
	/// </summary>
	[Fact]
	public void Variables_WithMissingName_Throws()
	{
		// Act
		var read = () => this.sut.Variables["SHARPIE_SAMPLE_MISSING"];

		// Assert
		_ = read.Should().Throw<KeyNotFoundException>().WithMessage("*`SHARPIE_SAMPLE_MISSING`*");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.ShellLevel" /> reads <c>SHLVL</c>, counting one that's missing, not a
	/// number or below 0 as 0, and that <see cref="DeclareCommand.RaiseShellLevel" /> exports one more.
	/// </summary>
	/// <param name="level">The <c>SHLVL</c> in the environment, or <see langword="null" /> for none.</param>
	/// <param name="expected">The level it should read as.</param>
	[Theory]
	[InlineData("3", 3)]
	[InlineData("0", 0)]
	[InlineData("-2", 0)]
	[InlineData("x", 0)]
	[InlineData(null, 0)]
	public void ShellLevel_WithEnvironment_ReadsAndRaisesIt(string? level, int expected)
	{
		// Arrange
		var saved = Environment.GetEnvironmentVariable("SHLVL");

		try
		{
			Environment.SetEnvironmentVariable("SHLVL", level);

			// Act
			var read = DeclareCommand.ShellLevel;

			this.sut.RaiseShellLevel();

			// Assert
			var raised = (expected + 1).ToString(CultureInfo.InvariantCulture);

			_ = read.Should().Be(expected);
			_ = Environment.GetEnvironmentVariable("SHLVL").Should().Be(raised);
			_ = this.sut.Variables["SHLVL"].Should().Be(raised);
		}
		finally
		{
			Environment.SetEnvironmentVariable("SHLVL", saved);
		}
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.RunFunction" /> gives the arguments as the positional parameters while
	/// the body runs, counts the call, and puts the caller's back even when the body throws.
	/// </summary>
	[Fact]
	public void RunFunction_WithArguments_PutsCallersBack()
	{
		// Arrange
		this.sut.SetPositional(["a", "b", "c"]);
		this.sut.Set("0", "sampleshell");
		string? during = null;

		// Act
		var act = () => this.sut.RunFunction(["x"], () =>
		{
			during = $"{string.Join(',', this.sut.Positional)}|{this.sut.Variables["0"]}|{this.sut.FunctionDepth}";

			throw new InvalidOperationException();
		});

		// Assert
		_ = act.Should().Throw<InvalidOperationException>();
		_ = during.Should().Be("x|sampleshell|1");
		_ = this.sut.Positional.Should().Equal("a", "b", "c");
		_ = this.sut.FunctionDepth.Should().Be(0);
	}

	/// <summary>
	/// Checks that the variables <see cref="DeclareCommand.Localize" /> saved get their old shell and environment
	/// values back when the function returns, and one that wasn't set before is removed again.
	/// </summary>
	[Fact]
	public void RunFunction_WithLocals_PutsOldValuesBack()
	{
		// Arrange
		this.sut.Export(SampleVariable, "outer");
		this.sut.Set("plain", "outer");

		// Act
		var status = this.sut.RunFunction([], () =>
		{
			_ = this.sut.Localize(SampleVariable);
			_ = this.sut.Localize("plain");
			_ = this.sut.Localize("fresh");
			this.sut.Set(SampleVariable, "inner");
			this.sut.Remove("plain");
			this.sut.Export("fresh", "inner");

			return 3;
		});

		// Assert
		_ = status.Should().Be(3);
		_ = this.sut.Variables.Should().Contain(SampleVariable, "outer").And.Contain("plain", "outer").And.NotContainKey("fresh");
		_ = Environment.GetEnvironmentVariable(SampleVariable).Should().Be("outer");
		_ = Environment.GetEnvironmentVariable("plain").Should().BeNull();
		_ = Environment.GetEnvironmentVariable("fresh").Should().BeNull();
	}

	/// <summary>
	/// Checks that each call has its own scope, so an inner function's local comes back to the outer function's
	/// value, and a name made local twice in one call is saved once.
	/// </summary>
	[Fact]
	public void Localize_InNestedCalls_KeepsEachScope()
	{
		// Arrange
		this.sut.Set("samplevar", "top");
		var seen = new List<string>();

		// Act
		_ = this.sut.RunFunction([], () =>
		{
			seen.Add(this.sut.Localize("samplevar").ToString());
			this.sut.Set("samplevar", "outer");
			seen.Add(this.sut.Localize("samplevar").ToString());

			_ = this.sut.RunFunction([], () =>
			{
				seen.Add(this.sut.Localize("samplevar").ToString());
				this.sut.Set("samplevar", "inner");

				return 0;
			});

			seen.Add(this.sut.Variables["samplevar"]);

			return 0;
		});

		// Assert
		_ = seen.Should().Equal("True", "False", "True", "outer");
		_ = this.sut.Variables["samplevar"].Should().Be("top");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Restore" /> with a local count forgets the names made local since, so the
	/// function's later change stays when it returns, and that without one every local is kept.
	/// </summary>
	[Fact]
	public void Restore_WithLocalCount_ForgetsLocalsMadeSince()
	{
		// Arrange
		this.sut.Set("kept", "top");
		this.sut.Set("forgotten", "top");
		var counts = new List<int>();

		// Act
		_ = this.sut.RunFunction([], () =>
		{
			_ = this.sut.Localize("kept");
			counts.Add(this.sut.LocalCount);

			var saved = this.sut.Variables.ToDictionary();

			_ = this.sut.Localize("forgotten");
			this.sut.Restore(saved, counts[0]);
			counts.Add(this.sut.LocalCount);
			_ = this.sut.Localize("forgotten");
			this.sut.Restore(saved);
			counts.Add(this.sut.LocalCount);
			this.sut.Restore(saved, counts[0]);
			this.sut.Set("kept", "inner");
			this.sut.Set("forgotten", "inner");

			return 0;
		});

		// Assert
		_ = counts.Should().Equal(1, 1, 2);
		_ = this.sut.Variables["kept"].Should().Be("top");
		_ = this.sut.Variables["forgotten"].Should().Be("inner");
		_ = this.sut.LocalCount.Should().Be(0);
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Restore" /> with a local count outside any function only puts the
	/// variables back.
	/// </summary>
	[Fact]
	public void Restore_WithLocalCountOutsideFunction_PutsVariablesBack()
	{
		// Arrange
		this.sut.Set("kept", "old");
		var saved = this.sut.Variables.ToDictionary();

		this.sut.Set("kept", "new");
		this.sut.Set("added", "x");

		// Act
		this.sut.Restore(saved, 0);

		// Assert
		_ = this.sut.Variables.Should().Contain("kept", "old").And.NotContainKey("added");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Localize" /> outside a function throws, since there's no scope to save
	/// into.
	/// </summary>
	[Fact]
	public void Localize_OutsideFunction_Throws()
	{
		// Act
		var act = () => this.sut.Localize("samplevar");

		// Assert
		_ = act.Should().Throw<InvalidOperationException>();
	}
}
