using FluentAssertions;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Functions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="SourceCommand" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class SourceCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly List<string> ran = [];
	private readonly DeclareCommand declare = new(new ShellSettings());
	private readonly string folder;
	private readonly SourceCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="SourceCommandTests" /> class with a command that records each line
	/// it runs, and gives the line's length as its status.
	/// </summary>
	public SourceCommandTests()
	{
		this.folder = this.environment.CreateFolder();
		this.sut = new SourceCommand(".", this.declare, line =>
		{
			this.ran.Add(line);

			return line.Length;
		});
	}

	/// <summary>
	/// Checks that <c>return</c> in a file ends it with its status, and that the builtin says it's running only while
	/// the file's lines run.
	/// </summary>
	[Fact]
	public void Run_WithReturnInFile_EndsFileWithItsStatus()
	{
		// Arrange
		var path = ShellEnvironment.CreateFile(this.folder, "sample.rc", executable: false, "a\nreturn\nb\n");
		var running = new List<bool>();
		SourceCommand? sut = null;

		sut = new SourceCommand(".", this.declare, line =>
		{
			this.ran.Add(line);
			running.Add(sut!.Running);

			return line == "return" ? throw new ReturnCommand.Signal(4) : 0;
		});

		// Act
		var status = sut.Run([path]);

		// Assert
		_ = status.Should().Be(4);
		_ = this.ran.Should().Equal("a", "return");
		_ = running.Should().Equal(true, true);
		_ = sut.Running.Should().BeFalse();
	}

	/// <summary>
	/// Checks that the command reports the name it was created with.
	/// </summary>
	[Fact]
	public void Name_WithSourceName_ReturnsIt()
	{
		// Act
		var command = new SourceCommand("source", this.declare, _ => 0);

		// Assert
		_ = command.Name.Should().Be("source");
	}

	/// <summary>
	/// Checks that every line of the file is run in order, Windows line endings included.
	/// </summary>
	/// <param name="newline">The line ending the file uses.</param>
	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void Run_WithFile_RunsEachLineInOrder(string newline)
	{
		// Arrange
		var path = ShellEnvironment.CreateFile(this.folder, "sample.rc", executable: false, $"alias a=b{newline}# note{newline}{newline}export X=1{newline}");

		// Act
		_ = this.sut.Run([path]);

		// Assert
		_ = this.ran.Should().Equal("alias a=b", "# note", string.Empty, "export X=1");
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a name with no folder is found in a <c>PATH</c> folder even when the file isn't executable.
	/// </summary>
	[Fact]
	public void Run_WithBareNameOnPath_RunsThatFile()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "sample.rc", executable: false, "echo from-path\n");
		ShellEnvironment.SetPath(this.environment.CreateFolder(), this.folder);

		// Act
		_ = this.sut.Run(["sample.rc"]);

		// Assert
		_ = this.ran.Should().Equal("echo from-path");
	}

	/// <summary>
	/// Checks that a name with no folder that isn't on <c>PATH</c> is read from the current folder.
	/// </summary>
	[Fact]
	public void Run_WithBareNameInCurrentFolder_RunsThatFile()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "sample.rc", executable: false, "echo from-here\n");
		ShellEnvironment.SetPath(this.environment.CreateFolder());
		Directory.SetCurrentDirectory(this.folder);

		// Act
		_ = this.sut.Run(["sample.rc"]);

		// Assert
		_ = this.ran.Should().Equal("echo from-here");
	}

	/// <summary>
	/// Checks that a name with no folder is read from the current folder when <c>PATH</c> is unset.
	/// </summary>
	[Fact]
	public void Run_WithBareNameAndNoPath_RunsFileInCurrentFolder()
	{
		// Arrange
		_ = ShellEnvironment.CreateFile(this.folder, "sample.rc", executable: false, "echo from-here\n");
		Environment.SetEnvironmentVariable("PATH", null);
		Directory.SetCurrentDirectory(this.folder);

		// Act
		_ = this.sut.Run(["sample.rc"]);

		// Assert
		_ = this.ran.Should().Equal("echo from-here");
	}

	/// <summary>
	/// Checks that a name with a folder in it is used as typed, even when <c>PATH</c> has a file of the same name.
	/// </summary>
	[Fact]
	public void Run_WithRelativePath_SkipsPath()
	{
		// Arrange
		var other = this.environment.CreateFolder();

		_ = ShellEnvironment.CreateFile(other, "sample.rc", executable: false, "echo from-path\n");
		_ = ShellEnvironment.CreateFile(this.folder, "sample.rc", executable: false, "echo from-here\n");
		ShellEnvironment.SetPath(other);
		Directory.SetCurrentDirectory(this.folder);

		// Act
		_ = this.sut.Run(["./sample.rc"]);

		// Assert
		_ = this.ran.Should().Equal("echo from-here");
	}

	/// <summary>
	/// Checks that a missing file is reported with the command's name and nothing runs.
	/// </summary>
	[Fact]
	public void Run_WithMissingFile_ReportsIt()
	{
		// Arrange
		var path = Path.Combine(this.folder, "missing.rc");

		// Act
		_ = this.sut.Run([path]);

		// Assert
		_ = this.ran.Should().BeEmpty();
		_ = this.environment.Error.Should().Contain($"× Can't read `{path}`: ");
	}

	/// <summary>
	/// Checks that a folder or an empty name is reported rather than ending the shell.
	/// </summary>
	[Fact]
	public void Run_WithFolderOrEmptyName_ReportsBoth()
	{
		// Act
		_ = this.sut.Run([this.folder]);
		_ = this.sut.Run([string.Empty]);

		// Assert
		_ = this.ran.Should().BeEmpty();
		_ = this.environment.Error.Split("sharpie::source::unreadable").Should().HaveCount(3);
	}

	/// <summary>
	/// Checks that no file name is reported.
	/// </summary>
	[Fact]
	public void Run_WithNoArguments_ReportsMissingName()
	{
		// Act
		_ = this.sut.Run([]);

		// Assert
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::source::missing_file", "`.` needs a file to run").Render(color: false));
	}

	/// <summary>
	/// Checks that a file that runs itself stops at <see cref="SourceCommand.MaxDepth" /> with one error, and that a
	/// later file still runs once the nesting has unwound.
	/// </summary>
	[Fact]
	public void Run_WithFileThatRunsItself_StopsAtMaxDepth()
	{
		// Arrange
		var path = ShellEnvironment.CreateFile(this.folder, "loop.rc", executable: false, "again\n");
		var runs = 0;
		SourceCommand? command = null;

		command = new SourceCommand(".", this.declare, _ =>
		{
			runs++;

			return command!.Run([path]);
		});

		// Act
		_ = command.Run([path]);
		var afterLoop = runs;

		_ = command.Run([path]);

		// Assert
		_ = afterLoop.Should().Be(SourceCommand.MaxDepth);
		_ = runs.Should().Be(SourceCommand.MaxDepth * 2);
		var error = new ShellError("sharpie::source::too_deep", $"`{path}` is nested more than {SourceCommand.MaxDepth} files deep")
		{
			Help = "a file that runs itself, directly or through another file, never ends",
		}.Render(color: false);

		_ = this.environment.Error.Should().Be(error + error);
	}

	/// <summary>
	/// Checks that words after the file are the positional parameters while it runs, and the caller's come back after.
	/// </summary>
	[Fact]
	public void Run_WithArguments_SetsPositionalParametersWhileFileRuns()
	{
		// Arrange
		var path = ShellEnvironment.CreateFile(this.folder, "args.rc", executable: false, "one\n");
		IReadOnlyList<string>? during = null;
		var command = new SourceCommand(".", this.declare, _ =>
		{
			during = this.declare.Positional;

			return 0;
		});

		this.declare.SetPositional(["caller"]);

		// Act
		_ = command.Run([path, "a", "b"]);

		// Assert
		_ = during.Should().Equal("a", "b");
		_ = this.declare.Positional.Should().Equal("caller");
	}

	/// <summary>
	/// Checks that with no words after the file, it sees and can change the caller's positional parameters.
	/// </summary>
	[Fact]
	public void Run_WithoutArguments_SharesCallersPositionalParameters()
	{
		// Arrange
		var path = ShellEnvironment.CreateFile(this.folder, "noargs.rc", executable: false, "one\n");
		IReadOnlyList<string>? during = null;
		var command = new SourceCommand(".", this.declare, _ =>
		{
			during = this.declare.Positional;
			this.declare.SetPositional(["changed"]);

			return 0;
		});

		this.declare.SetPositional(["caller"]);

		// Act
		_ = command.Run([path]);

		// Assert
		_ = during.Should().Equal("caller");
		_ = this.declare.Positional.Should().Equal("changed");
	}

	/// <summary>
	/// Checks that a quote still open at the end of the file is reported with status 2 after the lines before it ran,
	/// and the open command doesn't run.
	/// </summary>
	[Fact]
	public void Run_WithQuoteOpenAtEnd_ReportsItAndGivesTwo()
	{
		// Arrange
		var path = ShellEnvironment.CreateFile(this.folder, "open.rc", executable: false, "echo before\necho 'open\nstill\n");

		// Act
		var status = this.sut.Run([path]);

		// Assert
		_ = status.Should().Be(CommandList.UnsupportedStatus);
		_ = this.ran.Should().Equal("echo before");
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::source::unclosed_quote", $"`'` is never closed in `{path}`") { Help = "add the closing '" }.Render(color: false));
	}

	/// <summary>
	/// Checks that a line ending in an odd number of backslashes joins the next one, and an even number doesn't, and
	/// that a line leaving a quote, substitution or here-document open joins the lines that close it.
	/// </summary>
	/// <param name="lines">The lines of the file.</param>
	/// <param name="expected">The command lines that should come out.</param>
	[Theory]
	[InlineData(new[] { @"echo a \", "b" }, new[] { "echo a b" })]
	[InlineData(new[] { @"echo a \", @"b \", "c" }, new[] { "echo a b c" })]
	[InlineData(new[] { @"echo a\\", "b" }, new[] { @"echo a\\", "b" })]
	[InlineData(new[] { @"echo a\\\", "b" }, new[] { @"echo a\\b" })]
	[InlineData(new[] { @"echo a\" }, new[] { "echo a" })]
	[InlineData(new[] { "one", "two" }, new[] { "one", "two" })]
	[InlineData(new[] { @"\" }, new string[0])]
	[InlineData(new[] { "X=$(echo a", "echo b)", "echo c" }, new[] { "X=$(echo a\necho b)", "echo c" })]
	[InlineData(new[] { "echo `a", "b`" }, new[] { "echo `a\nb`" })]
	[InlineData(new[] { "echo 'a", "", "b'" }, new[] { "echo 'a\n\nb'" })]
	[InlineData(new[] { @"echo 'a\", "b'" }, new[] { "echo 'a\\\nb'" })]
	[InlineData(new[] { "echo \"a\\", "b\"" }, new[] { "echo \"ab\"" })]
	[InlineData(new[] { @"X=$(echo a \", "b)" }, new[] { "X=$(echo a b)" })]
	[InlineData(new[] { "# don't", "echo hi" }, new[] { "# don't", "echo hi" })]
	[InlineData(new[] { "echo 'open", "" }, new[] { "echo 'open" })]
	[InlineData(new[] { "cat <<EOF", @"body \", "EOF", "next" }, new[] { "cat <<EOF\nbody \\\nEOF", "next" })]
	[InlineData(new[] { @"cat \", "<<EOF", "x", "EOF" }, new[] { "cat <<EOF\nx\nEOF" })]
	[InlineData(new[] { "cat <<A <<B", "1", "A", "2", "B", @"a\", "b" }, new[] { "cat <<A <<B\n1\nA\n2\nB", "ab" })]
	[InlineData(new[] { "cat <<EOF", "never closed" }, new[] { "cat <<EOF\nnever closed" })]
	[InlineData(new[] { "cat <<EOF", "don't $(", "EOF", "echo c" }, new[] { "cat <<EOF\ndon't $(\nEOF", "echo c" })]
	[InlineData(new[] { "if true; then", "  echo a", "fi", "echo b" }, new[] { "if true; then\n  echo a\nfi", "echo b" })]
	[InlineData(new[] { "f() {", @"  echo a \", "b", "}" }, new[] { "f() {\n  echo a b\n}" })]
	[InlineData(new[] { "while read l; do", "  cat <<EOF", "fi", "EOF", "done" }, new[] { "while read l; do\n  cat <<EOF\nfi\nEOF\ndone" })]
	[InlineData(new[] { "if true; then" }, new[] { "if true; then" })]
	public void JoinContinuedLines_WithLines_JoinsOnOddBackslashes(string[] lines, string[] expected)
	{
		// Act
		var joined = SourceCommand.JoinContinuedLines(lines);

		// Assert
		_ = joined.Select(command => command.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that each command line comes with the file line it starts on, past lines joined by a backslash, a quote
	/// or a here-document's body.
	/// </summary>
	/// <param name="lines">The lines of the file.</param>
	/// <param name="expected">The line number each command line should start on.</param>
	[Theory]
	[InlineData(new[] { "one", "two" }, new[] { 1, 2 })]
	[InlineData(new[] { "", "one" }, new[] { 1, 2 })]
	[InlineData(new[] { @"echo a \", "b", "c" }, new[] { 1, 3 })]
	[InlineData(new[] { "echo 'a", "b'", "c" }, new[] { 1, 3 })]
	[InlineData(new[] { "cat <<EOF", "x", "EOF", "next" }, new[] { 1, 4 })]
	[InlineData(new[] { "one", "echo 'open" }, new[] { 1, 2 })]
	[InlineData(new[] { "one", @"two \" }, new[] { 1, 2 })]
	public void JoinContinuedLines_WithLines_GivesStartLines(string[] lines, int[] expected)
	{
		// Act
		var joined = SourceCommand.JoinContinuedLines(lines);

		// Assert
		_ = joined.Select(command => command.Line).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that <c>$LINENO</c> holds the file line each command line starts on while it runs.
	/// </summary>
	[Fact]
	public void Run_WithFile_SetsLineNumberForEachLine()
	{
		// Arrange
		var path = Path.Combine(this.folder, "lines.sh");
		var seen = new List<int>();
		var sut = new SourceCommand(".", this.declare, _ =>
		{
			seen.Add(this.declare.LineNumber);

			return 0;
		});

		File.WriteAllLines(path, ["one", "echo a \\", "b", "", "two"]);

		// Act
		_ = sut.Run([path]);

		// Assert
		_ = seen.Should().Equal(1, 2, 4, 5);
	}

	/// <summary>
	/// Checks that a function's body is joined to its <c>}</c>, a trailing backslash in it still joins, and one in a
	/// here-document's body inside it stays for the here-document's rules.
	/// </summary>
	/// <param name="lines">The lines of the file.</param>
	/// <param name="expected">The command lines.</param>
	[Theory]
	[InlineData(new[] { "samplefn() {", "echo a \\", "b", "}", "c" }, new[] { "samplefn() {\necho a b\n}", "c" })]
	[InlineData(new[] { "samplefn() {", "cat <<'EOF'", "a\\", "b", "EOF", "}" }, new[] { "samplefn() {\ncat <<'EOF'\na\\\nb\nEOF\n}" })]
	[InlineData(new[] { "samplefn() {", "cat <<EOF \\", "x", "a", "EOF", "}" }, new[] { "samplefn() {\ncat <<EOF x\na\nEOF\n}" })]
	public void JoinContinuedLines_WithFunctionBody_JoinsToItsEnd(string[] lines, string[] expected)
	{
		// Act
		var joined = SourceCommand.JoinContinuedLines(lines);

		// Assert
		_ = joined.Select(command => command.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Restores the console, <c>PATH</c> and the working directory after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
