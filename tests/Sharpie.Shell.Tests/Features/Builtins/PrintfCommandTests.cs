using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="PrintfCommand" />. Each output was checked against bash 5.3.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class PrintfCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand declare = new(new ShellSettings());
	private readonly PrintfCommand sut;

	/// <summary>
	/// Initializes a new instance of the <see cref="PrintfCommandTests" /> class.
	/// </summary>
	public PrintfCommandTests() => this.sut = new PrintfCommand(this.declare);

	/// <summary>
	/// Checks what each conversion prints for arguments that are all valid.
	/// </summary>
	/// <param name="args">The format and its arguments.</param>
	/// <param name="expected">What bash prints.</param>
	[Theory]
	[InlineData(new[] { "[%5s][%-5s][%.2s][%*s][%-*d]", "ab", "cd", "efgh", "4", "x", "3", "7" }, "[   ab][cd   ][ef][   x][7  ]")]
	[InlineData(new[] { "[%d][%i][%u][%x][%X][%o][%#x][%#o][%05d][%+d][% d][%-4d|]", "42", "-3", "7", "255", "255", "8", "255", "8", "42", "5", "5", "1" }, "[42][-3][7][ff][FF][10][0xff][010][00042][+5][ 5][1   |]")]
	[InlineData(new[] { "[%d][%d][%d][%d][%d][%d][%d]", "0x1F", "010", "'A", "\"B", " 12", "'é", "\"" }, "[31][8][65][66][12][233][0]")]
	[InlineData(new[] { "[%c][%c][%c][%5c][%-3c]", "hello", "", "5", "a", "b" }, "[h][\0][5][    a][b  ]")]
	[InlineData(new[] { "[%e][%f][%g][%E][%G][%.2f][%10.3f][%F]", "1234.5", "3.14159", "0.0001", "1e10", "1e-10", "2.675", "3.14159", "1.5" }, "[1.234500e+03][3.141590][0.0001][1.000000E+10][1E-10][2.67][     3.142][1.500000]")]
	[InlineData(new[] { "%.0f %.0f %.0f %.0f %.1f %.2f %.3d", "2.5", "3.5", "-2.5", "0.5", "0.25", "1.005", "5" }, "2 4 -2 0 0.2 1.00 005")]
	[InlineData(new[] { "%f %e %g %G %F", "inf", "-inf", "nan", "inf", "nan" }, "inf -inf nan INF NAN")]
	[InlineData(new[] { "%#g %#.3g %g %g %.3g %g %g", "1.5", "2", "123456789", "0.0001234", "1234.5678", "1000000", "1e-5" }, "1.50000 2.00 1.23457e+08 0.0001234 1.23e+03 1e+06 1e-05")]
	[InlineData(new[] { "%.3e|%.0e|%#.0e|%E|%.1e|%e", "9.9996", "15", "15", "0.000001", "0", "-0.000123" }, "1.000e+01|2e+01|2.e+01|1.000000E-06|0.0e+00|-1.230000e-04")]
	[InlineData(new[] { "%g %g %g %e %f %f", "0", "-0", "1e-300", "1e300", "0x10", "'A" }, "0 -0 1e-300 1.000000e+300 16.000000 65.000000")]
	[InlineData(new[] { "[%05s][%-05d][%+s][%+x][% u][%+.0d][%.0d][% 05d][%+05.1f][%-+8.2e]", "ab", "3", "x", "10", "5", "0", "0", "7", "2.25", "12345" }, "[   ab][3    ][x][a][5][+][][ 0007][+02.2][+1.23e+04]")]
	[InlineData(new[] { "[%*d][%-*d][%.*f][%*.*s][%.*s]", "-5", "1", "3", "2", "2", "3.14159", "6", "2", "abcdef", "-1", "xy" }, "[1    ][2  ][3.14][    ab][xy]")]
	[InlineData(new[] { "%x %o %u %u %.3x|%#.0o|%#X|%x", "-1", "-1", "-1", "18446744073709551615", "10", "0", "255", "0" }, "ffffffffffffffff 1777777777777777777777 18446744073709551615 18446744073709551615 00a|0|0XFF|0")]
	[InlineData(new[] { "%hd %ld %lld %jd %zd %i %d", "1", "2", "3", "4", "5", "+5", "-0x10" }, "1 2 3 4 5 5 -16")]
	[InlineData(new[] { "%s-%s|", "a", "b", "c" }, "a-b|c-|")]
	[InlineData(new[] { "[%s][%d][%q][%b][%5q]" }, "[][0][''][][   '']")]
	[InlineData(new[] { "x", "extra" }, "x")]
	[InlineData(new[] { "%5.1s|%.s|%10.4s|%-10b|", "xyz", "abc", "abcdefg", @"x\ty" }, "    x||      abcd|x\ty       |")]
	[InlineData(new[] { @"\%d|\\%d|\045d|%%|100%%", "5", "6" }, @"\5|\6|%d|%|100%")]
	[InlineData(new[] { @"a\cb|\101\0101\x41\u00e9\e\?\q\""" }, "a\\cb|A\b1Aé\e?\\q\"")]
	[InlineData(new[] { "%b|", @"\101", @"\0101", @"\x41", @"\08", @"\8" }, "A|A|A|\08|\\8|")]
	[InlineData(new[] { "--", "-%s", "x" }, "-x")]
	[InlineData(new[] { "[%#d][%#.0f][%#.0e][%q][%f]|", "5", "2.5", "3", "a b" }, @"[5][2.][3.e+00][a\ b][0.000000]|")]
	[InlineData(new[] { @"a\" }, @"a\")]
	public void Run_WithValidArguments_PrintsAsBash(string[] args, string expected)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be(expected);
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a number whose logarithm rounds up to the next power of ten still gets its exponent right. The
	/// digits are the exact double's, where bash's long double gives 9.9999999999999990e+02.
	/// </summary>
	[Fact]
	public void Run_WithLogarithmRoundingUp_FindsExponent()
	{
		// Act
		_ = this.sut.Run(["%.16e", "999.9999999999999"]);

		// Assert
		_ = this.environment.Output.Should().Be("9.9999999999999989e+02");
	}

	/// <summary>
	/// Checks the reports for an argument that isn't a number and one out of range.
	/// </summary>
	[Fact]
	public void Run_WithBadNumbers_ReportsEach()
	{
		// Act
		_ = this.sut.Run(["%d|%d", "abc", "9223372036854775808"]);

		// Assert
		_ = this.environment.Error.Should().Be(
			new ShellError("sharpie::printf::invalid_number", "`abc` isn't a number").Render(color: false)
			+ new ShellError("sharpie::printf::out_of_range", "`9223372036854775808` is out of range") { Help = "a number here has to be between -9223372036854775808 and 9223372036854775807" }.Render(color: false));
	}

	/// <summary>
	/// Checks that a <c>\c</c> in a <c>%b</c> argument stops all output, the rest of the format and the arguments
	/// after it included.
	/// </summary>
	[Fact]
	public void Run_WithStopInArgument_StopsAllOutput()
	{
		// Act
		var status = this.sut.Run(["%s %b %s|", "1", @"x\cy", "3", "4", "5"]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be("1 x");
	}

	/// <summary>
	/// Checks that <c>%q</c> quotes each kind of text the way bash does.
	/// </summary>
	/// <param name="text">The argument.</param>
	/// <param name="expected">What <c>%q</c> gives.</param>
	[Theory]
	[InlineData("a b", @"a\ b")]
	[InlineData("it's", @"it\'s")]
	[InlineData("", "''")]
	[InlineData("a\nb", @"$'a\nb'")]
	[InlineData("\e", @"$'\E'")]
	[InlineData("a'b\\\n", @"$'a\'b\\\n'")]
	[InlineData("\u0001x\a\b\f\r\v\t", @"$'\001x\a\b\f\r\v\t'")]
	[InlineData("x*y?[z]", @"x\*y\?\[z\]")]
	[InlineData("~", @"\~")]
	[InlineData("a~b", "a~b")]
	[InlineData("=~", @"=\~")]
	[InlineData(":~x", @":\~x")]
	[InlineData("#x", @"\#x")]
	[InlineData("a#b=c", "a#b=c")]
	[InlineData("a,b^c{d}!e", @"a\,b\^c\{d\}\!e")]
	[InlineData("$`\"\\|&;()<>\t", @"$'$`""\\|&;()<>\t'")]
	[InlineData("$`\"\\|&;()<>", @"\$\`\""\\\|\&\;\(\)\<\>")]
	[InlineData("é b", @"é\ b")]
	public void Quote_WithText_QuotesAsBash(string text, string expected)
	{
		// Act
		var quoted = PrintfCommand.Quote(text);

		// Assert
		_ = quoted.Should().Be(expected);
	}

	/// <summary>
	/// Checks that an argument that isn't a whole number is reported and its valid start used, and one out of range is
	/// reported and the nearest number used, with status 1 and the rest still printed.
	/// </summary>
	/// <param name="args">The format and its arguments.</param>
	/// <param name="expected">What bash prints.</param>
	/// <param name="code">The error reported.</param>
	[Theory]
	[InlineData(new[] { "%d|", "abc" }, "0|", "sharpie::printf::invalid_number")]
	[InlineData(new[] { "%d|", "12abc" }, "12|", "sharpie::printf::invalid_number")]
	[InlineData(new[] { "%d|", "" }, "0|", "sharpie::printf::invalid_number")]
	[InlineData(new[] { "%i|", "3.5" }, "3|", "sharpie::printf::invalid_number")]
	[InlineData(new[] { "%x|", "3.0" }, "3|", "sharpie::printf::invalid_number")]
	[InlineData(new[] { "%f|", "abc" }, "0.000000|", "sharpie::printf::invalid_number")]
	[InlineData(new[] { "%f|", "1.5x" }, "1.500000|", "sharpie::printf::invalid_number")]
	[InlineData(new[] { "[%*d]", "x", "1" }, "[1]", "sharpie::printf::invalid_number")]
	[InlineData(new[] { "%d|", "9223372036854775808" }, "9223372036854775807|", "sharpie::printf::out_of_range")]
	[InlineData(new[] { "%d|", "-9223372036854775809" }, "-9223372036854775808|", "sharpie::printf::out_of_range")]
	[InlineData(new[] { "%u|", "18446744073709551616" }, "18446744073709551615|", "sharpie::printf::out_of_range")]
	public void Run_WithBadNumber_ReportsAndGoesOn(string[] args, string expected, string code)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Output.Should().Be(expected);
		_ = this.environment.Error.Should().Contain(code);
	}

	/// <summary>
	/// Checks that a conversion <c>printf</c> doesn't know stops the output there with status 1, after the text
	/// before it.
	/// </summary>
	/// <param name="format">The format.</param>
	/// <param name="expected">What bash prints before stopping.</param>
	/// <param name="code">The error code.</param>
	/// <param name="summary">The error summary.</param>
	[Theory]
	[InlineData("ab%z|%d", "ab", "sharpie::printf::invalid_format", "`%z|` isn't a format printf knows")]
	[InlineData("%5%|", "", "sharpie::printf::invalid_format", "`%5%` isn't a format printf knows")]
	[InlineData("x%3$s", "x", "sharpie::printf::invalid_format", "`%3$` isn't a format printf knows")]
	[InlineData("ab%", "ab", "sharpie::printf::missing_format", "The format ends with a `%` that has no letter after it")]
	public void Run_WithBadFormat_StopsThere(string format, string expected, string code, string summary)
	{
		// Act
		var status = this.sut.Run([format, "a", "b"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Output.Should().Be(expected);
		_ = this.environment.Error.Should().Be(new ShellError(code, summary)
		{
			Help = code == "sharpie::printf::missing_format" ? "write %% for a % sign" : "printf takes %d %i %o %u %x %X %e %E %f %F %g %G %c %s %b %q and %%",
		}.Render(color: false));
	}

	/// <summary>
	/// Checks that a bad option, a bad <c>-v</c> name or no format gives status 2 and prints nothing.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="code">The error code.</param>
	/// <param name="summary">The error summary.</param>
	/// <param name="help">The hint, or <see langword="null" /> for none.</param>
	[Theory]
	[InlineData(new string[0], "sharpie::printf::missing_format", "printf needs a format", "printf [-v var] format [arguments]")]
	[InlineData(new[] { "--" }, "sharpie::printf::missing_format", "printf needs a format", "printf [-v var] format [arguments]")]
	[InlineData(new[] { "-x" }, "sharpie::printf::invalid_option", "`-x` isn't an option of `printf`", "printf [-v var] format [arguments]")]
	[InlineData(new[] { "-v" }, "sharpie::printf::missing_variable", "`-v` needs a variable name after it", null)]
	[InlineData(new[] { "-v", "1x", "a" }, "sharpie::printf::invalid_identifier", "`1x` isn't a valid variable name", DeclareCommand.IdentifierHelp)]
	[InlineData(new[] { "-v", "1x[0]", "a" }, "sharpie::printf::invalid_identifier", "`1x[0]` isn't a valid variable name", DeclareCommand.IdentifierHelp)]
	public void Run_WithBadOptions_GivesTwo(string[] args, string code, string summary, string? help)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(2);
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Be(new ShellError(code, summary) { Help = help }.Render(color: false));
	}

	/// <summary>
	/// Checks that <c>-v</c> stores the text in a variable or an array element instead of printing it, with the name
	/// in its own word or joined to <c>-v</c>.
	/// </summary>
	[Fact]
	public void Run_WithVariable_StoresText()
	{
		// Act
		var plain = this.sut.Run(["-v", "v", "%s-%d", "a", "5"]);
		var joined = this.sut.Run(["-vw", "q"]);
		var element = this.sut.Run(["-v", "arr[2]", "%s", "z"]);

		// Assert
		_ = (plain, joined, element).Should().Be((0, 0, 0));
		_ = this.declare.Variables["v"].Should().Be("a-5");
		_ = this.declare.Variables["w"].Should().Be("q");
		_ = DeclareCommand.Elements(this.declare.Variables, "arr").Select(pair => $"{pair.Key}={pair.Value}").Should().Equal("2=z");
		_ = this.environment.Output.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>-v</c> into an element whose subscript names none reports it and gives 1.
	/// </summary>
	[Fact]
	public void Run_WithVariableAtBadSubscript_GivesOne()
	{
		// Act
		var status = this.sut.Run(["-v", "arr[-1]", "%s", "z"]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Error.Should().NotBeEmpty();
	}

	/// <summary>
	/// Puts the console back after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
