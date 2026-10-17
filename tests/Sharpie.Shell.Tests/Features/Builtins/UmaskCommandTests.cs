using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="UmaskCommand" />, with a stand-in for the C <c>umask</c> function so the test process keeps
/// its own mask.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class UmaskCommandTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly UmaskCommand sut;
	private int mask = Convert.ToInt32("022", 8);

	/// <summary>
	/// Initializes a new instance of the <see cref="UmaskCommandTests" /> class with the mask 0022.
	/// </summary>
	public UmaskCommandTests()
	{
		this.sut = new UmaskCommand(next =>
		{
			var old = this.mask;

			this.mask = next;

			return old;
		});
	}

	/// <summary>
	/// Checks that <c>umask</c> without a mode prints the mask the way each option asks, and leaves it as it was.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="expected">What it should print.</param>
	[Theory]
	[InlineData(new string[0], "0022\n")]
	[InlineData(new[] { "--" }, "0022\n")]
	[InlineData(new[] { "-S" }, "u=rwx,g=rx,o=rx\n")]
	[InlineData(new[] { "-p" }, "umask 0022\n")]
	[InlineData(new[] { "-pp" }, "umask 0022\n")]
	[InlineData(new[] { "-pS" }, "umask -S u=rwx,g=rx,o=rx\n")]
	[InlineData(new[] { "-S", "-p" }, "umask -S u=rwx,g=rx,o=rx\n")]
	public void Run_WithoutMode_PrintsMask(string[] args, string expected)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be(expected);
		_ = this.mask.Should().Be(Convert.ToInt32("022", 8));
	}

	/// <summary>
	/// Checks that a mode sets the mask bash sets for it, starting from the given mask. Each row was checked against
	/// bash 5.3.
	/// </summary>
	/// <param name="start">The mask before, in octal.</param>
	/// <param name="mode">The mode.</param>
	/// <param name="expected">The mask after, in octal.</param>
	[Theory]
	[InlineData("022", "027", "027")]
	[InlineData("022", "0", "000")]
	[InlineData("022", "7777", "777")]
	[InlineData("022", "u=rwx,g=rx,o=", "027")]
	[InlineData("022", "g+w", "002")]
	[InlineData("022", "a-w", "222")]
	[InlineData("022", "+w", "000")]
	[InlineData("022", "=rx", "222")]
	[InlineData("022", "=", "777")]
	[InlineData("022", "-", "022")]
	[InlineData("022", "u+r-w", "222")]
	[InlineData("022", "uu=r", "322")]
	[InlineData("022", "u=s", "722")]
	[InlineData("022", "o=Xw", "024")]
	[InlineData("022", "g+w,o-r", "006")]
	[InlineData("027", "u=rw=x", "627")]
	[InlineData("027", "g=u", "007")]
	[InlineData("277", "o=u", "272")]
	[InlineData("027", "g=o", "077")]
	[InlineData("022", "u=o", "222")]
	[InlineData("027", "go=u", "000")]
	[InlineData("027", "o+u", "020")]
	[InlineData("027", "o=gw", "020")]
	[InlineData("027", "o=wg", "022")]
	[InlineData("027", "o=gu", "020")]
	[InlineData("027", "o=u-r", "024")]
	[InlineData("027", "o=rst", "023")]
	[InlineData("027", "o=g,g=", "072")]
	[InlineData("027", "g=,o=g", "072")]
	[InlineData("066", "g=X", "066")]
	[InlineData("777", "g=X", "777")]
	[InlineData("677", "u=,g=X", "767")]
	[InlineData("677", "u=-r+X", "677")]
	public void Run_WithMode_SetsMask(string start, string mode, string expected)
	{
		// Arrange
		this.mask = Convert.ToInt32(start, 8);

		// Act
		var status = this.sut.Run([mode]);

		// Assert
		_ = status.Should().Be(0);
		_ = this.mask.Should().Be(Convert.ToInt32(expected, 8));
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that <c>-S</c> with a mode prints the new mask as permissions, and that words after the mode are
	/// ignored, as in bash.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="output">What it should print.</param>
	/// <param name="expected">The mask after, in octal.</param>
	[Theory]
	[InlineData(new[] { "-S", "027" }, "u=rwx,g=rx,o=\n", "027")]
	[InlineData(new[] { "-p", "027" }, "", "027")]
	[InlineData(new[] { "--", "-" }, "", "022")]
	[InlineData(new[] { "1", "2" }, "", "001")]
	public void Run_WithOptionsAndMode_SetsAndPrints(string[] args, string output, string expected)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(0);
		_ = this.environment.Output.Should().Be(output);
		_ = this.mask.Should().Be(Convert.ToInt32(expected, 8));
	}

	/// <summary>
	/// Checks that a mode bash rejects is reported with status 1 and leaves the mask alone.
	/// </summary>
	/// <param name="mode">The mode.</param>
	/// <param name="code">The error code.</param>
	/// <param name="message">The message.</param>
	/// <param name="help">The hint.</param>
	[Theory]
	[InlineData("999", "sharpie::umask::out_of_range", "`999` isn't an octal number from 0 to 7777", "use octal digits like 022, or a symbolic mode like u=rwx,g=rx,o=rx")]
	[InlineData("08", "sharpie::umask::out_of_range", "`08` isn't an octal number from 0 to 7777", "use octal digits like 022, or a symbolic mode like u=rwx,g=rx,o=rx")]
	[InlineData("0x1", "sharpie::umask::out_of_range", "`0x1` isn't an octal number from 0 to 7777", "use octal digits like 022, or a symbolic mode like u=rwx,g=rx,o=rx")]
	[InlineData("10000", "sharpie::umask::out_of_range", "`10000` isn't an octal number from 0 to 7777", "use octal digits like 022, or a symbolic mode like u=rwx,g=rx,o=rx")]
	[InlineData("x=r", "sharpie::umask::invalid_mode", "`x=r` isn't a valid mode", "expected +, - or = where `x` is")]
	[InlineData("u", "sharpie::umask::invalid_mode", "`u` isn't a valid mode", "expected +, - or = at the end")]
	[InlineData("", "sharpie::umask::invalid_mode", "`` isn't a valid mode", "expected +, - or = at the end")]
	[InlineData("u=r,", "sharpie::umask::invalid_mode", "`u=r,` isn't a valid mode", "expected +, - or = at the end")]
	[InlineData("u=r,,g=r", "sharpie::umask::invalid_mode", "`u=r,,g=r` isn't a valid mode", "expected +, - or = where `,` is")]
	[InlineData("u=rq", "sharpie::umask::invalid_mode", "`u=rq` isn't a valid mode", "`q` isn't a permission: use r, w, x, X, s, t, u, g or o")]
	[InlineData("g=r,u=rq", "sharpie::umask::invalid_mode", "`g=r,u=rq` isn't a valid mode", "`q` isn't a permission: use r, w, x, X, s, t, u, g or o")]
	public void Run_WithBadMode_ReportsIt(string mode, string code, string message, string help)
	{
		// Act
		var status = this.sut.Run([mode]);

		// Assert
		_ = status.Should().Be(1);
		_ = this.mask.Should().Be(Convert.ToInt32("022", 8));
		_ = this.environment.Error.Should().Be(new ShellError(code, message) { Help = help }.Render(color: false));
	}

	/// <summary>
	/// Checks that an option umask doesn't have gives status 2, as in bash, and changes nothing.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="option">The option named in the message.</param>
	[Theory]
	[InlineData(new[] { "-x" }, "-x")]
	[InlineData(new[] { "-Sw", "027" }, "-w")]
	public void Run_WithBadOption_GivesTwo(string[] args, string option)
	{
		// Act
		var status = this.sut.Run(args);

		// Assert
		_ = status.Should().Be(2);
		_ = this.mask.Should().Be(Convert.ToInt32("022", 8));
		_ = this.environment.Output.Should().BeEmpty();
		_ = this.environment.Error.Should().Be(new ShellError("sharpie::umask::invalid_option", $"`{option}` isn't an option of umask") { Help = "umask takes -p and -S" }.Render(color: false));
	}

	/// <summary>
	/// Checks the two ways a mask prints.
	/// </summary>
	/// <param name="mask">The mask, in octal.</param>
	/// <param name="octal">The octal form.</param>
	/// <param name="symbolic">The symbolic form.</param>
	[Theory]
	[InlineData("0", "0000", "u=rwx,g=rwx,o=rwx")]
	[InlineData("777", "0777", "u=,g=,o=")]
	[InlineData("135", "0135", "u=rw,g=r,o=w")]
	[InlineData("642", "0642", "u=x,g=wx,o=rx")]
	public void Format_WithMask_GivesBothForms(string mask, string octal, string symbolic)
	{
		// Arrange
		var value = Convert.ToInt32(mask, 8);

		// Act
		var plain = UmaskCommand.Format(value, symbolic: false);
		var permissions = UmaskCommand.Format(value, symbolic: true);

		// Assert
		_ = plain.Should().Be(octal);
		_ = permissions.Should().Be(symbolic);
	}

	/// <summary>
	/// Restores the console after each test.
	/// </summary>
	public void Dispose() => this.environment.Dispose();
}
