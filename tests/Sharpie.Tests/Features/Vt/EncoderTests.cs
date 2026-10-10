using Avalonia.Input;
using FluentAssertions;
using Sharpie.Features.Vt;

namespace Sharpie.Tests.Features.Vt;

/// <summary>
/// Tests for <see cref="KeyEncoder" /> and <see cref="MouseEncoder" />.
/// </summary>
public sealed class EncoderTests
{
	/// <summary>
	/// Checks the bytes sent for special keys.
	/// </summary>
	/// <param name="key">The key.</param>
	/// <param name="modifiers">The modifiers held.</param>
	/// <param name="application">Whether application cursor keys are on.</param>
	/// <param name="expected">The bytes sent.</param>
	[Theory]
	[InlineData(Key.Up, KeyModifiers.None, false, "\e[A")]
	[InlineData(Key.Up, KeyModifiers.None, true, "\eOA")]
	[InlineData(Key.Right, KeyModifiers.Control, true, "\e[1;5C")]
	[InlineData(Key.Home, KeyModifiers.Shift, false, "\e[1;2H")]
	[InlineData(Key.Delete, KeyModifiers.None, false, "\e[3~")]
	[InlineData(Key.PageUp, KeyModifiers.Alt, false, "\e[5;3~")]
	[InlineData(Key.F1, KeyModifiers.None, false, "\eOP")]
	[InlineData(Key.F5, KeyModifiers.None, false, "\e[15~")]
	[InlineData(Key.F12, KeyModifiers.Shift, false, "\e[24;2~")]
	[InlineData(Key.Enter, KeyModifiers.None, false, "\r")]
	[InlineData(Key.Back, KeyModifiers.None, false, "\x7F")]
	[InlineData(Key.Tab, KeyModifiers.Shift, false, "\e[Z")]
	[InlineData(Key.C, KeyModifiers.Control, false, "\x03")]
	[InlineData(Key.Z, KeyModifiers.Control, false, "\x1A")]
	[InlineData(Key.OemOpenBrackets, KeyModifiers.Control, false, "\e")]
	[InlineData(Key.Escape, KeyModifiers.None, false, "\e")]
	public void Encode_SpecialKey_SendsXtermBytes(Key key, KeyModifiers modifiers, bool application, string expected)
	{
		// Act
		var sent = KeyEncoder.Encode(key, modifiers, null, application);

		// Assert
		_ = sent.Should().Be(expected);
	}

	/// <summary>
	/// Checks that Alt with a letter sends ESC and the letter, and a plain letter is left to text input.
	/// </summary>
	[Fact]
	public void Encode_Letter_OnlyAltSendsEscape()
	{
		// Act
		var alt = KeyEncoder.Encode(Key.B, KeyModifiers.Alt, "b", applicationCursorKeys: false);
		var plain = KeyEncoder.Encode(Key.B, KeyModifiers.None, "b", applicationCursorKeys: false);

		// Assert
		_ = alt.Should().Be("\eb");
		_ = plain.Should().BeNull();
	}

	/// <summary>
	/// Checks mouse reports in each encoding.
	/// </summary>
	/// <param name="encoding">The <see cref="MouseEncoding" />.</param>
	/// <param name="action">The <see cref="MouseAction" />.</param>
	/// <param name="expected">The report.</param>
	[Theory]
	[InlineData((int)MouseEncoding.Sgr, (int)MouseAction.Press, "\e[<0;5;3M")]
	[InlineData((int)MouseEncoding.Sgr, (int)MouseAction.Release, "\e[<0;5;3m")]
	[InlineData((int)MouseEncoding.Sgr, (int)MouseAction.WheelDown, "\e[<65;5;3M")]
	[InlineData((int)MouseEncoding.Urxvt, (int)MouseAction.Press, "\e[32;5;3M")]
	[InlineData((int)MouseEncoding.Default, (int)MouseAction.Press, "\e[M %#")]
	[InlineData((int)MouseEncoding.Default, (int)MouseAction.Release, "\e[M#%#")]
	public void Encode_LeftButton_WritesReport(int encoding, int action, string expected)
	{
		// Act
		var report = MouseEncoder.Encode(MouseTracking.PressRelease, (MouseEncoding)encoding, (MouseAction)action, 0, KeyModifiers.None, 5, 3);

		// Assert
		_ = report.Should().Be(expected);
	}

	/// <summary>
	/// Checks that modifiers add to the button code, except in mode 9.
	/// </summary>
	[Fact]
	public void Encode_WithModifiers_AddsToCodeExceptInPressMode()
	{
		// Act
		var tracked = MouseEncoder.Encode(MouseTracking.PressRelease, MouseEncoding.Sgr, MouseAction.Press, 2, KeyModifiers.Control | KeyModifiers.Shift, 1, 1);
		var pressOnly = MouseEncoder.Encode(MouseTracking.Press, MouseEncoding.Sgr, MouseAction.Press, 2, KeyModifiers.Control, 1, 1);

		// Assert
		_ = tracked.Should().Be("\e[<22;1;1M");
		_ = pressOnly.Should().Be("\e[<2;1;1M");
	}

	/// <summary>
	/// Checks which modes report a move, with and without a button held.
	/// </summary>
	/// <param name="tracking">The <see cref="MouseTracking" /> mode.</param>
	/// <param name="button">The button held, or -1.</param>
	/// <param name="expected">The report, or <see langword="null" /> when none is sent.</param>
	[Theory]
	[InlineData((int)MouseTracking.Press, 0, null)]
	[InlineData((int)MouseTracking.PressRelease, 0, null)]
	[InlineData((int)MouseTracking.Drag, 0, "\e[<32;2;2M")]
	[InlineData((int)MouseTracking.Drag, -1, null)]
	[InlineData((int)MouseTracking.AnyMove, -1, "\e[<35;2;2M")]
	[InlineData((int)MouseTracking.None, 0, null)]
	public void Encode_Move_FollowsTrackingMode(int tracking, int button, string? expected)
	{
		// Act
		var report = MouseEncoder.Encode((MouseTracking)tracking, MouseEncoding.Sgr, MouseAction.Move, button, KeyModifiers.None, 2, 2);

		// Assert
		_ = report.Should().Be(expected);
	}

	/// <summary>
	/// Checks that the original encoding holds positions past 223 at 223, the most one byte can carry.
	/// </summary>
	[Fact]
	public void Encode_DefaultPastLimit_CapsPosition()
	{
		// Act
		var report = MouseEncoder.Encode(MouseTracking.PressRelease, MouseEncoding.Default, MouseAction.Press, 0, KeyModifiers.None, 300, 1);

		// Assert
		_ = report.Should().Be("\e[M \xFF!");
	}
}
