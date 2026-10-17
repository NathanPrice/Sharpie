using System.Text.RegularExpressions;
using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="UlimitCommand" />. Setting a limit would change the test process's own limits, so only
/// reading runs here, and <see cref="UlimitProcessTests" /> sets them in a shell of its own.
/// </summary>
[Collection(GlobalState.Name)]
public sealed partial class UlimitCommandTests : IDisposable
{
	private const ulong Unlimited = ulong.MaxValue;
	private readonly ShellEnvironment environment = new();
	private readonly UlimitCommand sut = new();

	/// <summary>
	/// Checks that each OS gets bash's limits, in bash's order, with that OS's resource numbers.
	/// </summary>
	/// <param name="macOS">Whether to ask for macOS.</param>
	/// <param name="letters">The option letters, in order.</param>
	/// <param name="resources">The resource numbers, in the same order.</param>
	[Theory]
	[InlineData(false, "Rcdefilmnpqrstuvx", new[] { 15, 4, 2, 13, 1, 11, 8, 5, 7, -1, 12, 14, 3, 0, 6, 9, 10 })]
	[InlineData(true, "cdflmnpstuv", new[] { 4, 2, 1, 6, 5, 8, -1, 3, 0, 7, 5 })]
	public void Limits_ForOs_GivesBashOrderAndNumbers(bool macOS, string letters, int[] resources)
	{
		// Act
		var limits = UlimitCommand.Limits(macOS);

		// Assert
		_ = string.Concat(limits.Select(limit => limit.Letter)).Should().Be(letters);
		_ = limits.Select(limit => limit.Resource).Should().Equal(resources);
	}

	/// <summary>
	/// Checks that a value prints in the limit's unit, with bash's <c>%-20s %20s</c> layout when described.
	/// </summary>
	/// <param name="letter">The limit's letter.</param>
	/// <param name="value">The raw value.</param>
	/// <param name="describe">Whether to describe it.</param>
	/// <param name="expected">The text.</param>
	[Theory]
	[InlineData('n', 1024UL, false, "1024")]
	[InlineData('c', 1048576UL, false, "2048")]
	[InlineData('d', 8388608UL, false, "8192")]
	[InlineData('c', 1000UL, false, "1")]
	[InlineData('t', Unlimited, false, "unlimited")]
	[InlineData('R', Unlimited, true, "real-time non-blocking time  (microseconds, -R) unlimited")]
	[InlineData('c', Unlimited, true, "core file size              (blocks, -c) unlimited")]
	[InlineData('n', 1048576UL, true, "open files                          (-n) 1048576")]
	[InlineData('p', 4096UL, true, "pipe size                (512 bytes, -p) 8")]
	[InlineData('q', 819200UL, true, "POSIX message queues         (bytes, -q) 819200")]
	public void Format_WithValue_MatchesBash(char letter, ulong value, bool describe, string expected)
	{
		// Arrange
		var limit = Find(letter);

		// Act
		var text = UlimitCommand.Format(limit, value, Unlimited, describe);

		// Assert
		_ = text.Should().Be(expected);
	}

	/// <summary>
	/// Checks every Linux limit's name, unit and letter against bash's <c>ulimit -a</c>, line for line.
	/// </summary>
	[Fact]
	public void Format_WithEveryLinuxLimit_MatchesBashLabels()
	{
		// Act
		var lines = UlimitCommand.Limits(macOS: false).Select(limit => UlimitCommand.Format(limit, Unlimited, Unlimited, describe: true));

		// Assert
		_ = lines.Should().Equal(
			"real-time non-blocking time  (microseconds, -R) unlimited",
			"core file size              (blocks, -c) unlimited",
			"data seg size               (kbytes, -d) unlimited",
			"scheduling priority                 (-e) unlimited",
			"file size                   (blocks, -f) unlimited",
			"pending signals                     (-i) unlimited",
			"max locked memory           (kbytes, -l) unlimited",
			"max memory size             (kbytes, -m) unlimited",
			"open files                          (-n) unlimited",
			"pipe size                (512 bytes, -p) unlimited",
			"POSIX message queues         (bytes, -q) unlimited",
			"real-time priority                  (-r) unlimited",
			"stack size                  (kbytes, -s) unlimited",
			"cpu time                   (seconds, -t) unlimited",
			"max user processes                  (-u) unlimited",
			"virtual memory              (kbytes, -v) unlimited",
			"file locks                          (-x) unlimited");
	}

	/// <summary>
	/// Checks that a value is read in the limit's unit, or as one of the words bash takes.
	/// </summary>
	/// <param name="text">The value given.</param>
	/// <param name="factor">The unit's size.</param>
	/// <param name="expected">The raw limit.</param>
	[Theory]
	[InlineData("100", 512UL, 51200UL)]
	[InlineData("0", 1024UL, 0UL)]
	[InlineData("256", 1UL, 256UL)]
	[InlineData("unlimited", 512UL, Unlimited)]
	[InlineData("hard", 512UL, 9000UL)]
	[InlineData("soft", 512UL, 3000UL)]
	public void TryParseValue_WithValidValue_GivesRawLimit(string text, ulong factor, ulong expected)
	{
		// Act
		var valid = UlimitCommand.TryParseValue(text, factor, 3000, 9000, Unlimited, out var value, out var error);

		// Assert
		_ = valid.Should().BeTrue();
		_ = value.Should().Be(expected);
		_ = error.Should().BeNull();
	}

	/// <summary>
	/// Checks that a value bash rejects gives an error.
	/// </summary>
	/// <param name="text">The value given.</param>
	/// <param name="code">The error code.</param>
	/// <param name="message">The message.</param>
	/// <param name="help">The hint.</param>
	[Theory]
	[InlineData("abc", "sharpie::ulimit::not_a_number", "`abc` isn't a number", "give a whole number, unlimited, hard or soft")]
	[InlineData("", "sharpie::ulimit::not_a_number", "`` isn't a number", "give a whole number, unlimited, hard or soft")]
	[InlineData("+5", "sharpie::ulimit::not_a_number", "`+5` isn't a number", "give a whole number, unlimited, hard or soft")]
	[InlineData(" 5", "sharpie::ulimit::not_a_number", "` 5` isn't a number", "give a whole number, unlimited, hard or soft")]
	[InlineData("0x10", "sharpie::ulimit::not_a_number", "`0x10` isn't a number", "give a whole number, unlimited, hard or soft")]
	[InlineData("36028797018963968", "sharpie::ulimit::out_of_range", "`36028797018963968` is too big for a limit", "use unlimited for no limit")]
	[InlineData("99999999999999999999999", "sharpie::ulimit::out_of_range", "`99999999999999999999999` is too big for a limit", "use unlimited for no limit")]
	public void TryParseValue_WithBadValue_ReportsIt(string text, string code, string message, string help)
	{
		// Act
		var valid = UlimitCommand.TryParseValue(text, 512, 3000, 9000, Unlimited, out _, out var error);

		// Assert
		_ = valid.Should().BeFalse();
		_ = error.Should().Be(new ShellError(code, message) { Help = help });
	}

	/// <summary>
	/// Checks that the largest value that fits after scaling is taken.
	/// </summary>
	[Fact]
	public void TryParseValue_WithLargestScalableValue_GivesIt()
	{
		// Act
		var valid = UlimitCommand.TryParseValue("36028797018963967", 512, 0, 0, Unlimited, out var value, out _);

		// Assert
		_ = valid.Should().BeTrue();
		_ = value.Should().Be(36028797018963967UL * 512);
	}

	/// <summary>
	/// Checks that options and values pair up the way bash pairs them.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The flags, then each limit letter with its value.</param>
	[Theory]
	[InlineData(new string[0], ": f")]
	[InlineData(new[] { "100" }, ": f=100")]
	[InlineData(new[] { "--", "5" }, ": f=5")]
	[InlineData(new[] { "-n" }, ": n")]
	[InlineData(new[] { "-n256" }, ": n=256")]
	[InlineData(new[] { "-na" }, ": n=a")]
	[InlineData(new[] { "-n", "256", "-c" }, ": n=256 c")]
	[InlineData(new[] { "-f", "1", "-c", "2" }, ": f=1 c=2")]
	[InlineData(new[] { "-n", "-c" }, ": n c")]
	[InlineData(new[] { "-n", "--", "300" }, ": n=300")]
	[InlineData(new[] { "-n", "1", "2" }, ": n=1")]
	[InlineData(new[] { "-Hn" }, "H: n")]
	[InlineData(new[] { "-S", "-n", "5" }, "S: n=5")]
	[InlineData(new[] { "-HS", "-c" }, "HS: c")]
	[InlineData(new[] { "-HH", "-SS", "-aa" }, "HSa: f")]
	[InlineData(new[] { "-a" }, "a: f")]
	[InlineData(new[] { "-H", "100" }, "H: f=100")]
	public void TryParse_WithArguments_PairsLettersAndValues(string[] args, string expected)
	{
		// Act
		var valid = UlimitCommand.TryParse(args, UlimitCommand.Limits(macOS: false), out var request, out var error);

		// Assert
		_ = valid.Should().BeTrue();
		_ = error.Should().BeNull();
		_ = Describe(request).Should().Be(expected);
	}

	/// <summary>
	/// Checks that a letter with no limit on the OS is an error naming the letters there are.
	/// </summary>
	/// <param name="macOS">Whether to ask for macOS.</param>
	/// <param name="args">The arguments.</param>
	/// <param name="option">The option named in the message.</param>
	/// <param name="letters">The letters the hint lists.</param>
	[Theory]
	[InlineData(false, new[] { "-z" }, "-z", "Rcdefilmnpqrstuvx")]
	[InlineData(false, new[] { "-n", "-5" }, "-5", "Rcdefilmnpqrstuvx")]
	[InlineData(true, new[] { "-x" }, "-x", "cdflmnpstuv")]
	public void TryParse_WithUnknownLetter_ReportsIt(bool macOS, string[] args, string option, string letters)
	{
		// Act
		var valid = UlimitCommand.TryParse(args, UlimitCommand.Limits(macOS), out _, out var error);

		// Assert
		_ = valid.Should().BeFalse();
		_ = error.Should().Be(new ShellError("sharpie::ulimit::invalid_option", $"`{option}` isn't an option of ulimit") { Help = $"ulimit takes -a, -H, -S and -{letters}" });
	}

	/// <summary>
	/// Checks which limit <c>-H</c> and <c>-S</c> pick for printing and for setting, as bash picks them.
	/// </summary>
	/// <param name="hard">Whether <c>-H</c> was given.</param>
	/// <param name="soft">Whether <c>-S</c> was given.</param>
	/// <param name="showHard">Whether printing shows the hard limit.</param>
	/// <param name="setHard">Whether setting changes the hard limit.</param>
	/// <param name="setSoft">Whether setting changes the soft limit.</param>
	[Theory]
	[InlineData(false, false, false, true, true)]
	[InlineData(true, false, true, true, false)]
	[InlineData(false, true, false, false, true)]
	[InlineData(true, true, false, true, true)]
	public void Request_WithHardAndSoft_PicksBashLimits(bool hard, bool soft, bool showHard, bool setHard, bool setSoft)
	{
		// Arrange
		var request = new UlimitCommand.Request { Hard = hard, Soft = soft };

		// Act
		var picked = (request.ShowHard, request.SetHard, request.SetSoft);

		// Assert
		_ = picked.Should().Be((showHard, setHard, setSoft));
	}

	/// <summary>
	/// Checks that the pipe size prints as bash prints it, in 512-byte units.
	/// </summary>
	[Fact]
	public void Run_WithPipeSize_PrintsBlocks()
	{
		// Act
		var status = this.sut.Run(["-p"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be(OperatingSystem.IsMacOS() ? "1\n" : "8\n");
	}

	/// <summary>
	/// Checks that <c>-a</c> prints every limit of the OS with its name, in bash's order.
	/// </summary>
	/// <param name="args">The arguments, separated by spaces.</param>
	[Theory]
	[InlineData("-a")]
	[InlineData("-Ha")]
	[InlineData("-a -n 5")]
	public void Run_WithAll_PrintsEveryLimitDescribed(string args)
	{
		// Act
		var status = this.sut.Run(args.Split(' '));

		// Assert
		_ = status.Should().Be(0);

		var lines = this.environment.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		_ = string.Concat(lines.Select(line => LetterPattern().Match(line).Groups[1].Value)).Should().Be(OperatingSystem.IsMacOS() ? "cdflmnpstuv" : "Rcdefilmnpqrstuvx");
		_ = lines.Should().Contain(OperatingSystem.IsMacOS() ? "pipe size                (512 bytes, -p) 1" : "pipe size                (512 bytes, -p) 8");
	}

	/// <summary>
	/// Checks that printing more than one limit describes each, and one limit prints its value alone.
	/// </summary>
	[Fact]
	public void Run_WithTwoLimits_DescribesBoth()
	{
		// Act
		var status = this.sut.Run(["-p", "-p"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be(OperatingSystem.IsMacOS()
			? "pipe size                (512 bytes, -p) 1\npipe size                (512 bytes, -p) 1\n"
			: "pipe size                (512 bytes, -p) 8\npipe size                (512 bytes, -p) 8\n");
	}

	/// <summary>
	/// Checks that the soft and hard limits print through <c>-S</c> and <c>-H</c>, in kbytes for the stack, and that
	/// the soft one is the default, for one limit and for <c>-a</c>. The stack is used because its soft and hard
	/// limits usually differ.
	/// </summary>
	[Fact]
	public void Run_WithHardOrSoft_PrintsThatLimit()
	{
		// Arrange
		_ = NativeMethods.GetResourceLimit(Find('s', OperatingSystem.IsMacOS()).Resource, out var limit);
		var soft = Show(limit.Current);
		var hard = Show(limit.Maximum);

		// Act
		var statuses = new[] { this.sut.Run(["-s"]), this.sut.Run(["-Ss"]), this.sut.Run(["-Hs"]), this.sut.Run(["-a"]), this.sut.Run(["-Ha"]) };

		// Assert
		_ = statuses.Should().Equal(0, 0, 0, 0, 0);

		var lines = this.environment.Output.Split('\n');

		_ = lines[..3].Should().Equal(soft, soft, hard);
		_ = lines.Where(line => line.StartsWith("stack size", StringComparison.Ordinal)).Should().Equal(
			$"stack size                  (kbytes, -s) {soft}",
			$"stack size                  (kbytes, -s) {hard}");

		static string Show(nuint value) => value == NativeMethods.ResourceUnlimited ? "unlimited" : $"{value / 1024}";
	}

	/// <summary>
	/// Checks that a bad option gives 2 and a bad value or a fixed limit gives 1, as in bash, before anything is
	/// changed.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">The status.</param>
	/// <param name="code">The error code.</param>
	/// <param name="message">The message.</param>
	/// <param name="help">The hint.</param>
	[Theory]
	[InlineData(new[] { "-n", "abc" }, 1, "sharpie::ulimit::not_a_number", "`abc` isn't a number", "give a whole number, unlimited, hard or soft")]
	[InlineData(new[] { "-p", "8" }, 1, "sharpie::ulimit::cannot_change", "Can't change the pipe size limit", "Invalid argument")]
	public void Run_WithBadValue_GivesOne(string[] args, int expected, string code, string message, string help)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(expected);
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Be(new ShellError(code, message) { Help = help }.Render(color: false));
	}

	/// <summary>
	/// Checks that an option ulimit doesn't have gives 2 and prints nothing else.
	/// </summary>
	[Fact]
	public void Run_WithBadOption_GivesTwo()
	{
		// Act
		var status = this.sut.Run(["-p", "-z"]);

		// Assert
		_ = status.Should().Be(2);
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::ulimit::invalid_option", "`-z` isn't an option of ulimit")
		{
			Help = OperatingSystem.IsMacOS() ? "ulimit takes -a, -H, -S and -cdflmnpstuv" : "ulimit takes -a, -H, -S and -Rcdefilmnpqrstuvx",
		}.Render(color: false));
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Finds a limit by its letter.
	/// </summary>
	/// <param name="letter">The letter.</param>
	/// <param name="macOS">Whether to look among macOS's limits instead of Linux's.</param>
	/// <returns>
	/// The limit.
	/// </returns>
	private static UlimitCommand.Limit Find(char letter, bool macOS = false) => UlimitCommand.Limits(macOS).Single(limit => limit.Letter == letter);

	/// <summary>
	/// Writes a parsed request as its flags, then each limit letter with its value.
	/// </summary>
	/// <param name="request">The request.</param>
	/// <returns>
	/// Text like <c>H: n=256 c</c>.
	/// </returns>
	private static string Describe(UlimitCommand.Request request) =>
		$"{(request.Hard ? "H" : string.Empty)}{(request.Soft ? "S" : string.Empty)}{(request.All ? "a" : string.Empty)}: "
		+ string.Join(' ', request.Items.Select(item => item.Value is null ? $"{item.Limit.Letter}" : $"{item.Limit.Letter}={item.Value}"));

	/// <summary>
	/// Matches the option letter in a described limit, like the <c>n</c> in <c>(-n)</c>.
	/// </summary>
	/// <returns>
	/// The pattern.
	/// </returns>
	[GeneratedRegex(@"-(\w)\) ")]
	private static partial Regex LetterPattern();
}
