using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Completion;
using Sharpie.Shell.Features.Jobs;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Completion;

/// <summary>
/// Tests for <see cref="CompletionEngine" />, with a stand-in for the shell that records each line it's asked to run
/// and answers as a completion function would.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class CompletionEngineTests : IDisposable
{
	private static readonly string[] CompVariables = ["COMP_WORDS", "COMP_CWORD", "COMP_LINE", "COMP_POINT", "COMP_TYPE", "COMP_KEY"];

	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new(new ShellSettings());
	private readonly List<string> lines = [];
	private readonly List<Dictionary<string, string>> seen = [];
	private readonly CompletionEngine sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="CompletionEngineTests" /> class, whose stand-in shell replies
	/// <c>alpha</c> and <c>beta</c> to any function, unless <see cref="Answer" /> is changed.
	/// </summary>
	public CompletionEngineTests()
	{
		this.sut = new CompletionEngine(this.declare, new Dictionary<string, ICommand>(), new Dictionary<string, string>(), this.RunLine);
		this.Answer = call =>
		{
			_ = this.declare.Run(["COMPREPLY=(alpha beta)"]);

			return 0;
		};
	}

	/// <summary>
	/// Gets or sets what the stand-in shell does for each line, given the line.
	/// </summary>
	private Func<string, int> Answer { get; set; }

	/// <summary>
	/// Checks the variables and arguments a completion function gets, as bash sets them, that its <c>COMPREPLY</c>
	/// comes back as is, and that the variables are gone and <c>$?</c> is back after.
	/// </summary>
	/// <param name="line">The line, with the cursor at <c>^</c>.</param>
	/// <param name="listing">Whether this is the Tab that lists.</param>
	/// <param name="words">The expected <c>COMP_WORDS</c>, joined by <c>,</c>.</param>
	/// <param name="current">The expected <c>COMP_CWORD</c>.</param>
	/// <param name="commandLine">The expected <c>COMP_LINE</c>.</param>
	/// <param name="point">The expected <c>COMP_POINT</c>, in bytes.</param>
	/// <param name="call">The line the function is run with.</param>
	[Theory]
	[InlineData("sampletool al^", false, "sampletool,al", "1", "sampletool al", "13", "'_sampletool' 'sampletool' 'al' 'sampletool'")]
	[InlineData("sampletool ^", true, "sampletool,", "1", "sampletool ", "11", "'_sampletool' 'sampletool' '' 'sampletool'")]
	[InlineData("ls | sampletool é x^", false, "sampletool,é,x", "2", "sampletool é x", "15", "'_sampletool' 'sampletool' 'x' 'é'")]
	[InlineData("sampletool alp^ha next; ls", false, "sampletool,alpha,next", "1", "sampletool alpha next", "14", "'_sampletool' 'sampletool' 'alp' 'sampletool'")]
	[InlineData("sampletool --opt=^", false, "sampletool,--opt,=,", "3", "sampletool --opt=", "17", "'_sampletool' 'sampletool' '' '='")]
	[InlineData("sampletool \"a b^", false, "sampletool,\"a b", "1", "sampletool \"a b", "15", "'_sampletool' 'sampletool' 'a b' 'sampletool'")]
	[InlineData("sampletool a ^b", false, "sampletool,a,,b", "2", "sampletool a b", "13", "'_sampletool' 'sampletool' '' 'a'")]
	public void Complete_WithFunction_GivesItBashVariables(string line, bool listing, string words, string current, string commandLine, string point, string call)
	{
		// Arrange
		this.sut.Set("sampletool", new CompletionSpec { Function = "_sampletool" });
		this.declare.Status = 7;

		// Act
		var result = this.sut.Complete(line.Replace("^", string.Empty, StringComparison.Ordinal), line.IndexOf('^', StringComparison.Ordinal), listing);

		// Assert
		_ = result!.Matches.Should().Equal("alpha", "beta");
		_ = result.Failed.Should().BeFalse();
		_ = this.lines.Should().Equal($"{call} </dev/null >/dev/null 2>/dev/null");
		_ = this.seen[0]["COMP_WORDS"].Should().Be(words);
		_ = this.seen[0]["COMP_CWORD"].Should().Be(current);
		_ = this.seen[0]["COMP_LINE"].Should().Be(commandLine);
		_ = this.seen[0]["COMP_POINT"].Should().Be(point);
		_ = this.seen[0]["COMP_TYPE"].Should().Be(listing ? "63" : "9");
		_ = this.seen[0]["COMP_KEY"].Should().Be("9");
		_ = CompVariables.Append("COMPREPLY").Where(this.declare.Variables.ContainsKey).Should().BeEmpty();
		_ = this.declare.Status.Should().Be(7);
	}

	/// <summary>
	/// Checks that there's nothing to run when the cursor is on the command's name or the command has no spec.
	/// </summary>
	/// <param name="line">The line, with the cursor at its end.</param>
	[Theory]
	[InlineData("samplet")]
	[InlineData("ls | samplet")]
	[InlineData("othertool x")]
	[InlineData("ls >|sampletool x")]
	[InlineData("")]
	public void Complete_WithoutSpecForWord_GivesNothing(string line)
	{
		// Arrange
		this.sut.Set("sampletool", new CompletionSpec { Function = "_sampletool" });

		// Act
		var result = this.sut.Complete(line, line.Length, listing: false);

		// Assert
		_ = result.Should().BeNull();
		_ = this.lines.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that the spec's actions and word list come before the function's words, that the filter, prefix and
	/// suffix apply to all of them, and that the options come back.
	/// </summary>
	[Fact]
	public void Complete_WithWordsAndFunction_JoinsThemInOrder()
	{
		// Arrange
		this.sut.Set("sampletool", new CompletionSpec
		{
			WordList = "al1 al2 be",
			Function = "_sampletool",
			Filter = "*2",
			Prefix = "<",
			Suffix = ">",
			Options = new HashSet<string> { "nospace" },
		});

		// Act
		var result = this.sut.Complete("sampletool al", 13, listing: false);

		// Assert
		_ = result!.Matches.Should().Equal("<al1>", "<alpha>", "<beta>");
		_ = result.Options.Should().BeEquivalentTo(["nospace"]);
	}

	/// <summary>
	/// Checks that a function that fails and gives no words is marked so Tab falls back to the usual completion.
	/// </summary>
	[Fact]
	public void Complete_WhenFunctionFailsEmpty_MarksFailed()
	{
		// Arrange
		this.sut.Set("sampletool", new CompletionSpec { Function = "_sampletool" });
		this.Answer = _ => 127;

		// Act
		var result = this.sut.Complete("sampletool x", 12, listing: false);

		// Assert
		_ = result!.Matches.Should().BeEmpty();
		_ = result.Failed.Should().BeTrue();
	}

	/// <summary>
	/// Checks that a function that gives 124 after setting a new spec is followed by the new spec, once.
	/// </summary>
	[Fact]
	public void Complete_WhenFunctionLoadsSpec_RunsTheNewOne()
	{
		// Arrange
		this.sut.Set("sampletool", new CompletionSpec { Function = "_loader" });
		this.Answer = line =>
		{
			if (line.StartsWith("'_loader'", StringComparison.Ordinal))
			{
				this.sut.Set("sampletool", new CompletionSpec { Function = "_loader2" });

				return 124;
			}

			_ = this.declare.Run(["COMPREPLY=(loaded)"]);

			return 124;
		};

		// Act
		var result = this.sut.Complete("sampletool x", 12, listing: false);

		// Assert
		_ = result!.Matches.Should().Equal("loaded");
		_ = this.lines.Select(line => line.Split(' ')[0]).Should().Equal("'_loader'", "'_loader2'");
	}

	/// <summary>
	/// Checks that only 124 with a new spec runs again, and only once: 124 alone, or a new spec with another status,
	/// runs the function once, and a function that keeps loading new specs runs twice.
	/// </summary>
	/// <param name="status">What the function gives.</param>
	/// <param name="loads">Whether it sets a new spec each time.</param>
	/// <param name="runs">How many times it should run.</param>
	[Theory]
	[InlineData(124, false, 1)]
	[InlineData(0, true, 1)]
	[InlineData(124, true, 2)]
	public void Complete_WithRetryStatus_RunsAgainOnlyForNewSpec(int status, bool loads, int runs)
	{
		// Arrange
		this.sut.Set("sampletool", new CompletionSpec { Function = "_sampletool" });
		this.Answer = _ =>
		{
			if (loads)
			{
				this.sut.Set("sampletool", new CompletionSpec { Function = "_sampletool" });
			}

			// Bounds a loop that never stops, so a wrong count fails instead of hanging.
			return this.lines.Count > 5 ? 0 : status;
		};

		// Act
		_ = this.sut.Complete("sampletool x", 12, listing: false);

		// Assert
		_ = this.lines.Should().HaveCount(runs);
	}

	/// <summary>
	/// Checks that a <c>COMPREPLY</c> left from before doesn't show up when the function sets none.
	/// </summary>
	[Fact]
	public void Complete_WithOldReply_StartsEmpty()
	{
		// Arrange
		this.sut.Set("sampletool", new CompletionSpec { Function = "_sampletool" });
		_ = this.declare.Run(["COMPREPLY=(stale)"]);
		this.Answer = _ => 0;

		// Act
		var result = this.sut.Complete("sampletool x", 12, listing: false);

		// Assert
		_ = result!.Matches.Should().BeEmpty();
	}

	/// <summary>
	/// Checks the order specs are found in: the name as typed, then its last part, then a script, then <c>-D</c>.
	/// </summary>
	[Fact]
	public void Find_WithSeveralSpecs_TakesTheClosest()
	{
		// Arrange
		var path = new CompletionSpec { Function = "_path" };
		var name = new CompletionSpec { Function = "_name" };
		var fallback = new CompletionSpec { Function = "_default" };

		this.sut.Set("/opt/bin/sampletool", path);
		this.sut.Set("sampletool", name);
		this.sut.Default = fallback;

		// Act
		var byPath = this.sut.Find("/opt/bin/sampletool");
		var byName = this.sut.Find("/usr/bin/sampletool");
		var other = this.sut.Find("othertool");

		// Assert
		_ = byPath.Should().BeSameAs(path);
		_ = byName.Should().BeSameAs(name);
		_ = other.Should().BeSameAs(fallback);
	}

	/// <summary>
	/// Checks that a command's script wins over the <c>-D</c> spec.
	/// </summary>
	[Fact]
	public void Find_WithScriptAndDefault_TakesScript()
	{
		// Arrange
		var folder = this.environment.CreateFolder();

		_ = ShellEnvironment.CreateFile(Directory.CreateDirectory(Path.Combine(folder, "completions")).FullName, "sampletool", executable: false, string.Empty);
		this.declare.Set("BASH_COMPLETION_USER_DIR", folder);
		this.sut.Default = new CompletionSpec { Function = "_default" };
		this.Answer = _ =>
		{
			this.sut.Set("sampletool", new CompletionSpec { Function = "_sampletool" });

			return 0;
		};

		// Act
		var found = this.sut.Find("sampletool");

		// Assert
		_ = found!.Function.Should().Be("_sampletool");
	}

	/// <summary>
	/// Checks that a completion function runs with job control off, so the programs it starts never take the
	/// terminal, and that job control is back on after.
	/// </summary>
	[Fact]
	public void Complete_WithJobControl_TurnsItOffWhileRunning()
	{
		// Arrange
		var enabled = JobControl.Enabled;
		var during = true;

		this.sut.Set("sampletool", new CompletionSpec { Function = "_sampletool" });
		this.Answer = _ =>
		{
			during = JobControl.Enabled;

			return 0;
		};
		JobControl.Enabled = true;

		try
		{
			// Act
			_ = this.sut.Complete("sampletool x", 12, listing: false);

			// Assert
			_ = during.Should().BeFalse();
			_ = JobControl.Enabled.Should().BeTrue();
		}
		finally
		{
			JobControl.Enabled = enabled;
		}
	}

	/// <summary>
	/// Checks that <c>compopt</c> during a completion changes the options that come back, and that outside one it
	/// has nothing to change.
	/// </summary>
	[Fact]
	public void Complete_WithCompopt_ChangesRunningOptions()
	{
		// Arrange
		var compopt = new CompoptCommand(this.sut);
		var outside = compopt.Run(["-o", "filenames"]);

		this.sut.Set("sampletool", new CompletionSpec { Function = "_sampletool", Options = new HashSet<string> { "nospace" } });
		this.Answer = _ => compopt.Run(["-o", "filenames", "+o", "nospace"]);

		// Act
		var result = this.sut.Complete("sampletool x", 12, listing: false);

		// Assert
		_ = outside.Should().Be(1);
		_ = result!.Options.Should().BeEquivalentTo(["filenames"]);
		_ = this.sut.Running.Should().BeNull();
		_ = this.sut.Find("sampletool")!.Options.Should().BeEquivalentTo(["nospace"]);
	}

	/// <summary>
	/// Checks that a command with no spec gets one from its bash-completion script, sourced once, from
	/// <c>BASH_COMPLETION_USER_DIR</c>, and that a command with no script is looked for only once.
	/// </summary>
	[Fact]
	public void Find_WithScriptInUserFolder_SourcesItOnce()
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		var completions = Directory.CreateDirectory(Path.Combine(folder, "completions")).FullName;
		var script = ShellEnvironment.CreateFile(completions, "sampletool.bash", executable: false, "complete -F _sampletool sampletool\n");

		this.declare.Set("BASH_COMPLETION_USER_DIR", folder);
		this.declare.Set("XDG_DATA_DIRS", folder);
		this.declare.Set("PATH", string.Empty);
		this.Answer = _ =>
		{
			this.sut.Set("sampletool", new CompletionSpec { Function = "_sampletool" });

			return 0;
		};

		// Act
		var found = this.sut.Find("/usr/bin/sampletool");
		var again = this.sut.Find("sampletool");
		var missing = this.sut.Find("othertool");
		var missingAgain = this.sut.Find("othertool");

		// Assert
		_ = found!.Function.Should().Be("_sampletool");
		_ = again.Should().BeSameAs(found);
		_ = (missing, missingAgain).Should().Be((null, null));
		_ = this.lines.Should().Equal($"source {CommandLine.Quote(script)} </dev/null >/dev/null 2>/dev/null");
	}

	/// <summary>
	/// Checks that a command that has a spec under its last part isn't looked for again by its full path, and that a
	/// script that sets no spec is still sourced only once.
	/// </summary>
	[Fact]
	public void Find_WithSpecOrEmptyScript_SourcesNothingMore()
	{
		// Arrange
		var folder = this.environment.CreateFolder();
		var completions = Directory.CreateDirectory(Path.Combine(folder, "completions")).FullName;

		_ = ShellEnvironment.CreateFile(completions, "othertool.bash", executable: false, string.Empty);
		var empty = ShellEnvironment.CreateFile(completions, "emptytool.bash", executable: false, string.Empty);

		this.declare.Set("BASH_COMPLETION_USER_DIR", folder);
		this.sut.Set("othertool", new CompletionSpec { Function = "_othertool" });
		this.Answer = _ => 0;

		// Act
		var other = this.sut.Find("/opt/bin/othertool");
		var first = this.sut.Find("emptytool");
		var second = this.sut.Find("emptytool");

		// Assert
		_ = other!.Function.Should().Be("_othertool");
		_ = (first, second).Should().Be((null, null));
		_ = this.lines.Should().Equal($"source {CommandLine.Quote(empty)} </dev/null >/dev/null 2>/dev/null");
	}

	/// <summary>
	/// Checks the folders scripts are loaded from, in bash-completion's order, each once.
	/// </summary>
	[Fact]
	public void ScriptFolders_WithVariables_FollowsBashCompletion()
	{
		// Arrange
		var variables = new Dictionary<string, string>
		{
			["HOME"] = "/home/sample",
			["XDG_DATA_HOME"] = "/data",
			["PATH"] = "/opt/tool/bin:/usr/bin",
			["XDG_DATA_DIRS"] = "/usr/share:/extra",
		};

		// Act
		var folders = CompletionEngine.ScriptFolders(variables).ToList();

		// Assert
		_ = folders.Should().Equal(
			"/data/bash-completion/completions",
			"/opt/tool/share/bash-completion/completions",
			"/usr/share/bash-completion/completions",
			"/extra/bash-completion/completions");
	}

	/// <summary>
	/// Checks the folders scripts are loaded from when only <c>HOME</c> is set, and that
	/// <c>BASH_COMPLETION_USER_DIR</c> wins over <c>XDG_DATA_HOME</c>.
	/// </summary>
	[Fact]
	public void ScriptFolders_WithDefaults_UsesHomeAndUsrShare()
	{
		// Arrange
		var plain = new Dictionary<string, string> { ["HOME"] = "/home/sample", ["PATH"] = string.Empty, ["XDG_DATA_DIRS"] = string.Empty };
		var user = new Dictionary<string, string>(plain) { ["BASH_COMPLETION_USER_DIR"] = "/mine", ["XDG_DATA_HOME"] = "/data" };

		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// Act
		var folders = CompletionEngine.ScriptFolders(plain).ToList();
		var first = CompletionEngine.ScriptFolders(user).First();

		// Assert
		_ = folders.Should().Equal(
			"/home/sample/.local/share/bash-completion/completions",
			"/usr/local/share/bash-completion/completions",
			"/usr/share/bash-completion/completions");
		_ = first.Should().Be("/mine/completions");
	}

	/// <summary>
	/// Puts the console back after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Stands in for the shell: records the line and the completion variables, then answers.
	/// </summary>
	/// <param name="line">The line to run.</param>
	/// <returns>
	/// What <see cref="Answer" /> gives.
	/// </returns>
	private int RunLine(string line)
	{
		this.lines.Add(line);
		this.seen.Add(CompVariables.ToDictionary(name => name, name => name == "COMP_WORDS"
			? string.Join(',', DeclareCommand.Elements(this.declare.Variables, name).Select(element => element.Value))
			: this.declare.Variables.GetValueOrDefault(name, "(unset)")));

		return this.Answer(line);
	}
}
