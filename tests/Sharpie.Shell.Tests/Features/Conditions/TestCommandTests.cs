using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using FluentAssertions;
using Sharpie.Shell.Features.Conditions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Conditions;

/// <summary>
/// Tests for <see cref="TestCommand" />, run as <c>test</c> and as <c>[</c>, with file tests on a folder of its own.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class TestCommandTests : IDisposable
{
	private const string UnaryHelp = "use one like -f, -d, -n or -z";
	private const string BinaryHelp = "use one like =, !=, -eq or -lt";
	private const string TooManyHelp = "join the tests with -a or -o";
	private const string NumberHelp = "-eq, -ne, -lt, -le, -gt and -ge compare whole numbers; use = or != to compare text";

	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new(new ShellSettings());
	private readonly TestCommand test;
	private readonly TestCommand bracket;
	private readonly string folder;

	/// <summary>
	/// Initializes a new instance of the <see cref="TestCommandTests" /> class with a folder holding one file of each
	/// kind the file tests tell apart.
	/// </summary>
	public TestCommandTests()
	{
		this.test = new TestCommand("test", this.declare);
		this.bracket = new TestCommand("[", this.declare);
		this.folder = this.environment.CreateFolder();

		File.WriteAllText(this.Path("full"), "text");
		File.WriteAllText(this.Path("empty"), string.Empty);
		_ = Directory.CreateDirectory(this.Path("dir"));
		_ = File.CreateSymbolicLink(this.Path("link"), this.Path("full"));
		_ = File.CreateSymbolicLink(this.Path("dangling"), this.Path("missing"));
		Run("ln", this.Path("full"), this.Path("hard"));
		Run("mkfifo", this.Path("fifo"));

		File.WriteAllText(this.Path("locked"), "text");
		File.SetUnixFileMode(this.Path("locked"), UnixFileMode.None);
		File.WriteAllText(this.Path("tool"), "text");
		File.SetUnixFileMode(this.Path("tool"), UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.SetUser);
		File.SetUnixFileMode(this.Path("dir"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.StickyBit);

		File.SetLastWriteTimeUtc(this.Path("empty"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
	}

	/// <summary>
	/// Gets argument lists that <c>[</c> refuses, since none ends in <c>]</c>.
	/// </summary>
	/// <value>
	/// The arguments.
	/// </value>
	public static TheoryData<string[]> UnclosedBrackets => [[], ["a"], ["a", "]]"], ["]", "a"]];

	/// <inheritdoc />
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Checks that each number of arguments is read the way POSIX and bash read it, with the text and number
	/// operators. Each row was checked against bash 5.3.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The status.</param>
	[Theory]
	[InlineData(new string[0], 1)]
	[InlineData(new[] { "a" }, 0)]
	[InlineData(new[] { "" }, 1)]
	[InlineData(new[] { "-n" }, 0)]
	[InlineData(new[] { "!" }, 0)]
	[InlineData(new[] { "-t" }, 0)]
	[InlineData(new[] { "!", "" }, 0)]
	[InlineData(new[] { "!", "a" }, 1)]
	[InlineData(new[] { "-n", "a" }, 0)]
	[InlineData(new[] { "-n", "" }, 1)]
	[InlineData(new[] { "-z", "" }, 0)]
	[InlineData(new[] { "-z", "a" }, 1)]
	[InlineData(new[] { "a", "=", "a" }, 0)]
	[InlineData(new[] { "a", "==", "b" }, 1)]
	[InlineData(new[] { "a", "!=", "b" }, 0)]
	[InlineData(new[] { "a", "!=", "a" }, 1)]
	[InlineData(new[] { "a", "<", "b" }, 0)]
	[InlineData(new[] { "b", "<", "a" }, 1)]
	[InlineData(new[] { "b", ">", "a" }, 0)]
	[InlineData(new[] { "a", ">", "a" }, 1)]
	[InlineData(new[] { "B", "<", "a" }, 0)]
	[InlineData(new[] { "1", "-eq", "1" }, 0)]
	[InlineData(new[] { "1", "-eq", " +1 " }, 0)]
	[InlineData(new[] { "-1", "-eq", "1" }, 1)]
	[InlineData(new[] { "1", "-ne", "2" }, 0)]
	[InlineData(new[] { "1", "-ne", "1" }, 1)]
	[InlineData(new[] { "1", "-lt", "2" }, 0)]
	[InlineData(new[] { "2", "-lt", "2" }, 1)]
	[InlineData(new[] { "2", "-le", "2" }, 0)]
	[InlineData(new[] { "3", "-le", "2" }, 1)]
	[InlineData(new[] { "3", "-gt", "2" }, 0)]
	[InlineData(new[] { "2", "-gt", "2" }, 1)]
	[InlineData(new[] { "2", "-ge", "2" }, 0)]
	[InlineData(new[] { "1", "-ge", "2" }, 1)]
	[InlineData(new[] { "10", "-gt", "9" }, 0)]
	[InlineData(new[] { "a", "-a", "b" }, 0)]
	[InlineData(new[] { "a", "-a", "" }, 1)]
	[InlineData(new[] { "", "-o", "b" }, 0)]
	[InlineData(new[] { "", "-o", "" }, 1)]
	[InlineData(new[] { "!", "-n", "" }, 0)]
	[InlineData(new[] { "(", "", ")" }, 1)]
	[InlineData(new[] { "(", "a", ")" }, 0)]
	[InlineData(new[] { "!", "a", "=", "a" }, 1)]
	[InlineData(new[] { "!", "a", "=", "b" }, 0)]
	[InlineData(new[] { "(", "-z", "a", ")" }, 1)]
	[InlineData(new[] { "a", "=", "a", "-a", "b" }, 0)]
	[InlineData(new[] { "a", "=", "b", "-o", "b" }, 0)]
	[InlineData(new[] { "", "-o", "a", "-a", "" }, 1)]
	[InlineData(new[] { "a", "-o", "", "-a", "" }, 0)]
	[InlineData(new[] { "!", "a", "-o", "b" }, 1)]
	[InlineData(new[] { "!", "", "-a", "" }, 0)]
	[InlineData(new[] { "!", "!", "a", "-a", "a" }, 0)]
	[InlineData(new[] { "(", "a", "-o", "", ")", "-a", "" }, 1)]
	[InlineData(new[] { "-n", "a", "-a", "-z", "" }, 0)]
	[InlineData(new[] { "x", "-a", "-n" }, 0)]
	[InlineData(new[] { "a", "<", "a" }, 1)]
	[InlineData(new[] { "2", "-eq", "1" }, 1)]
	[InlineData(new[] { "", "-a", "b" }, 1)]
	[InlineData(new[] { "a", "-o", "" }, 0)]
	[InlineData(new[] { "!", "-z", "" }, 1)]
	[InlineData(new[] { "!", "a", "-a", "b", "-a", "c" }, 1)]
	[InlineData(new[] { "a", "-a", "b", "-a", "-z" }, 0)]
	public void Run_WithArguments_GivesBashStatus(string[] args, int expected)
	{
		// Act
		var status = this.test.Run(args);

		// Assert
		_ = status.Should().Be(expected);
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that arguments <c>test</c> can't read give status 2 and say what's wrong, as bash does.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="code">The last part of the error's code.</param>
	/// <param name="summary">What the error says is wrong.</param>
	/// <param name="help">Its hint.</param>
	[Theory]
	[InlineData(new[] { "foo", "bar" }, "unary_expected", "`foo` isn't a unary operator", UnaryHelp)]
	[InlineData(new[] { "-q", "bar" }, "unary_expected", "`-q` isn't a unary operator", UnaryHelp)]
	[InlineData(new[] { "(", "=", "=", ")" }, "unary_expected", "`=` isn't a unary operator", UnaryHelp)]
	[InlineData(new[] { "a", "-x", "b" }, "binary_expected", "`-x` isn't a binary operator", BinaryHelp)]
	[InlineData(new[] { "(", "a", "b" }, "binary_expected", "`a` isn't a binary operator", BinaryHelp)]
	[InlineData(new[] { "1", "-eq", "1", "x" }, "too_many_arguments", "`x` doesn't belong to any test", TooManyHelp)]
	[InlineData(new[] { "(", "a", ")", "-o", "b", "c" }, "too_many_arguments", "`c` doesn't belong to any test", TooManyHelp)]
	[InlineData(new[] { "a", "-a", "b", "-a", "c", "=" }, "too_many_arguments", "`=` doesn't belong to any test", TooManyHelp)]
	[InlineData(new[] { "a", "-a", "b", "-o" }, "argument_expected", "A test is missing at the end", "add the word or test the operator before it needs")]
	[InlineData(new[] { "(", "a", "-a", "b" }, "paren_expected", "`(` is never closed", "add a separate ) word")]
	[InlineData(new[] { "a", "-eq", "1" }, "not_a_number", "`a` isn't a whole number", NumberHelp)]
	[InlineData(new[] { "1", "-lt", "" }, "not_a_number", "`` isn't a whole number", NumberHelp)]
	[InlineData(new[] { "1", "-gt", "1.5" }, "not_a_number", "`1.5` isn't a whole number", NumberHelp)]
	[InlineData(new[] { "1", "-eq", "99999999999999999999" }, "not_a_number", "`99999999999999999999` isn't a whole number", NumberHelp)]
	[InlineData(new[] { "1", "-eq", "x", "-o", "a" }, "not_a_number", "`x` isn't a whole number", NumberHelp)]
	public void Run_WithBadArguments_ReportsAndGivesTwo(string[] args, string code, string summary, string help)
	{
		// Act
		var status = this.test.Run(args);

		// Assert
		_ = status.Should().Be(TestCommand.ErrorStatus);
		_ = this.environment.Error.Should().Be($"Error: sharpie::test::{code}\n\n  × {summary}\n  help: {help}\n");
	}

	/// <summary>
	/// Checks that <c>[</c> drops its closing <c>]</c> and reads the rest as <c>test</c> does.
	/// </summary>
	/// <param name="args">The arguments, <c>]</c> included.</param>
	/// <param name="expected">The status.</param>
	[Theory]
	[InlineData(new[] { "]" }, 1)]
	[InlineData(new[] { "a", "]" }, 0)]
	[InlineData(new[] { "]", "]" }, 0)]
	[InlineData(new[] { "-n", "", "]" }, 1)]
	[InlineData(new[] { "a", "=", "a", "]" }, 0)]
	public void Run_AsBracket_DropsClosingBracket(string[] args, int expected)
	{
		// Act
		var status = this.bracket.Run(args);

		// Assert
		_ = status.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <c>[</c> without <c>]</c> last gives status 2, while <c>test</c> reads a <c>]</c> as a word.
	/// </summary>
	/// <param name="args">The arguments.</param>
	[Theory]
	[MemberData(nameof(UnclosedBrackets))]
	public void Run_AsBracketWithoutClosingBracket_ReportsAndGivesTwo(string[] args)
	{
		// Act
		var status = this.bracket.Run(args);

		// Assert
		_ = status.Should().Be(TestCommand.ErrorStatus);
		_ = this.environment.Error.Should().Be("Error: sharpie::test::missing_bracket\n\n  × `[` is missing its closing `]`\n  help: end the condition with a separate ] word\n");
		_ = this.test.Run(["]"]).Should().Be(0);
	}

	/// <summary>
	/// Checks the names the command is typed as.
	/// </summary>
	[Fact]
	public void Name_ForEachCommand_IsWhatItWasBuiltWith()
	{
		// Assert
		_ = this.test.Name.Should().Be("test");
		_ = this.bracket.Name.Should().Be("[");
		_ = this.test.Description.Should().Be("Checks a condition about files, text or numbers");
	}

	/// <summary>
	/// Checks every file test against a file of each kind. A link counts as what it points to, except for <c>-h</c>
	/// and <c>-L</c>, and a link to nothing doesn't exist. The rows that need permissions assume the tests don't run
	/// as root.
	/// </summary>
	/// <param name="operator">The unary operator.</param>
	/// <param name="name">The file's name in the test folder, or a path that starts with <c>/</c>.</param>
	/// <param name="expected">The status.</param>
	[Theory]
	[InlineData("-e", "full", 0)]
	[InlineData("-e", "dir", 0)]
	[InlineData("-e", "link", 0)]
	[InlineData("-e", "dangling", 1)]
	[InlineData("-e", "missing", 1)]
	[InlineData("-a", "full", 0)]
	[InlineData("-a", "missing", 1)]
	[InlineData("-f", "full", 0)]
	[InlineData("-f", "link", 0)]
	[InlineData("-f", "dir", 1)]
	[InlineData("-f", "fifo", 1)]
	[InlineData("-f", "missing", 1)]
	[InlineData("-d", "dir", 0)]
	[InlineData("-d", "full", 1)]
	[InlineData("-d", "missing", 1)]
	[InlineData("-h", "link", 0)]
	[InlineData("-L", "dangling", 0)]
	[InlineData("-L", "full", 1)]
	[InlineData("-L", "missing", 1)]
	[InlineData("-s", "full", 0)]
	[InlineData("-s", "empty", 1)]
	[InlineData("-s", "missing", 1)]
	[InlineData("-p", "fifo", 0)]
	[InlineData("-p", "full", 1)]
	[InlineData("-c", "/dev/null", 0)]
	[InlineData("-c", "full", 1)]
	[InlineData("-b", "full", 1)]
	[InlineData("-b", "/dev/null", 1)]
	[InlineData("-S", "full", 1)]
	[InlineData("-r", "full", 0)]
	[InlineData("-r", "locked", 1)]
	[InlineData("-r", "missing", 1)]
	[InlineData("-w", "full", 0)]
	[InlineData("-w", "tool", 1)]
	[InlineData("-x", "tool", 0)]
	[InlineData("-x", "dir", 0)]
	[InlineData("-x", "full", 1)]
	[InlineData("-u", "tool", 0)]
	[InlineData("-u", "full", 1)]
	[InlineData("-g", "full", 1)]
	[InlineData("-k", "dir", 0)]
	[InlineData("-k", "full", 1)]
	[InlineData("-O", "full", 0)]
	[InlineData("-O", "missing", 1)]
	[InlineData("-G", "full", 0)]
	[InlineData("-G", "missing", 1)]
	[InlineData("-N", "missing", 1)]
	[InlineData("-e", "", 1)]
	[InlineData("-f", "", 1)]
	[InlineData("-r", "", 1)]
	public void Run_WithFileTest_ChecksFile(string @operator, string name, int expected)
	{
		// Act
		var status = this.test.Run([@operator, name.Length == 0 || name.StartsWith('/') ? name : this.Path(name)]);

		// Assert
		_ = status.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <c>-S</c> finds a Unix socket.
	/// </summary>
	[Fact]
	public void Run_WithSocket_FindsIt()
	{
		// Arrange
		using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		socket.Bind(new UnixDomainSocketEndPoint(this.Path("socket")));

		// Act
		var status = this.test.Run(["-S", this.Path("socket")]);

		// Assert
		_ = status.Should().Be(0);
	}

	/// <summary>
	/// Checks that <c>-N</c> holds for a file written after it was last read, or at the same time, as bash's
	/// <c>st_atime &lt;= st_mtime</c> does.
	/// </summary>
	[Fact]
	public void Run_WithFileWrittenAfterRead_FindsItModified()
	{
		// Arrange
		var written = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(this.Path("full"), written);
		File.SetLastAccessTimeUtc(this.Path("full"), written.AddHours(-1));

		// Act
		var modified = this.test.Run(["-N", this.Path("full")]);
		File.SetLastAccessTimeUtc(this.Path("full"), written);
		var same = this.test.Run(["-N", this.Path("full")]);
		File.SetLastAccessTimeUtc(this.Path("full"), written.AddHours(1));
		var read = this.test.Run(["-N", this.Path("full")]);

		// Assert
		_ = modified.Should().Be(0);
		_ = same.Should().Be(0);
		_ = read.Should().Be(1);
	}

	/// <summary>
	/// Checks the tests that compare two files: by write time, and whether two paths are the same file, where a hard
	/// link and a symbolic link count and a copy doesn't.
	/// </summary>
	/// <param name="left">The first file's name.</param>
	/// <param name="operator">The binary operator.</param>
	/// <param name="right">The second file's name.</param>
	/// <param name="expected">The status.</param>
	[Theory]
	[InlineData("full", "-nt", "empty", 0)]
	[InlineData("empty", "-nt", "full", 1)]
	[InlineData("full", "-nt", "missing", 0)]
	[InlineData("missing", "-nt", "full", 1)]
	[InlineData("missing", "-nt", "missing", 1)]
	[InlineData("full", "-nt", "hard", 1)]
	[InlineData("empty", "-ot", "full", 0)]
	[InlineData("full", "-ot", "empty", 1)]
	[InlineData("missing", "-ot", "full", 0)]
	[InlineData("full", "-ot", "missing", 1)]
	[InlineData("full", "-ef", "hard", 0)]
	[InlineData("full", "-ef", "link", 0)]
	[InlineData("full", "-ef", "full", 0)]
	[InlineData("full", "-ef", "empty", 1)]
	[InlineData("full", "-ef", "missing", 1)]
	[InlineData("missing", "-ef", "missing", 1)]
	public void Run_WithTwoFiles_ComparesThem(string left, string @operator, string right, int expected)
	{
		// Act
		var status = this.test.Run([this.Path(left), @operator, this.Path(right)]);

		// Assert
		_ = status.Should().Be(expected);
	}

	/// <summary>
	/// Checks that <c>-v</c> finds a shell variable, then an environment variable, and nothing for a name set in
	/// neither.
	/// </summary>
	[Fact]
	public void Run_WithVariableTest_FindsShellAndEnvironmentVariables()
	{
		// Arrange
		_ = this.declare.Run(["sample_set="]);

		// Act
		var shell = this.test.Run(["-v", "sample_set"]);
		var environment = this.test.Run(["-v", "PATH"]);
		var unset = this.test.Run(["-v", "sample_never_set"]);

		// Assert
		_ = shell.Should().Be(0);
		_ = environment.Should().Be(0);
		_ = unset.Should().Be(1);
	}

	/// <summary>
	/// Checks that <c>-t</c> finds no terminal behind a stream number that isn't 0, 1 or 2, or isn't a number. The
	/// test run's own streams are pipes, so they aren't terminals either.
	/// </summary>
	/// <param name="stream">The word after <c>-t</c>.</param>
	[Theory]
	[InlineData("0")]
	[InlineData("1")]
	[InlineData(" 2 ")]
	[InlineData("5")]
	[InlineData("x")]
	public void Run_WithTerminalTestAndNoTerminal_Fails(string stream)
	{
		// Act
		var status = this.test.Run(["-t", stream]);

		// Assert
		_ = status.Should().Be(1);
	}

	/// <summary>
	/// Checks that <c>&lt;</c> sorts in character code order whatever the culture, so an upper-case letter comes
	/// before every lower-case one.
	/// </summary>
	[Fact]
	public void Run_WithLessThanInCulture_SortsByCharacterCode()
	{
		// Arrange
		var culture = CultureInfo.CurrentCulture;
		CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

		try
		{
			// Act
			var status = this.test.Run(["a", "<", "B"]);

			// Assert
			_ = status.Should().Be(1);
		}
		finally
		{
			CultureInfo.CurrentCulture = culture;
		}
	}

	private static void Run(string program, params string[] arguments)
	{
		using var process = Process.Start(program, arguments);

		process.WaitForExit();
	}

	private string Path(string name) => System.IO.Path.Combine(this.folder, name);
}
