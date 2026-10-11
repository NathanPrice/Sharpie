using System.Globalization;
using FluentAssertions;
using Sharpie.Features.Vt;

namespace Sharpie.Tests.Features.Vt;

/// <summary>
/// Tests for <see cref="VtParser" />, through a handler that writes down every call it gets.
/// </summary>
public sealed class VtParserTests
{
	/// <summary>
	/// Checks the calls each input makes, one per <c>|</c>: <c>P</c> prints, <c>X</c> executes a control code (in
	/// hex), <c>E</c> is an escape with its intermediate and final, <c>C</c> a CSI with marker, parameters,
	/// intermediate and final, and <c>O</c> an OSC. A <c>0</c> stands for no marker or intermediate.
	/// </summary>
	/// <param name="input">What the program writes.</param>
	/// <param name="calls">The calls expected.</param>
	[Theory]
	[InlineData("\x18", "X18")]
	[InlineData("\e[1\x18x", "Px")]
	[InlineData("\e]0;t\x1Ax", "Px")]
	[InlineData("\e[1 2mx", "Px")]
	[InlineData("\e[1 2@x", "Px")]
	[InlineData("\e[1 2~x", "Px")]
	[InlineData("\e[1 2\nmx", "X0A|Px")]
	[InlineData("\e[1<mx", "Px")]
	[InlineData("\e[??1hx", "Px")]
	[InlineData("\e[?1h", "C?[1]0h")]
	[InlineData("\e[1\n2H", "X0A|C0[12]0H")]
	[InlineData("\e[ q", "C0[-1] q")]
	[InlineData("\e(0", "E(0")]
	[InlineData("\e( /0", "E/0")]
	[InlineData("\e(\n0", "X0A|E(0")]
	[InlineData("\e\nD", "X0A|E0D")]
	[InlineData("\e\e[1m", "C0[1]0m")]
	[InlineData("\eP1;2|x\e\\y", "Py")]
	[InlineData("\e_hidden\ay", "Py")]
	[InlineData("\e]0;t\a", "O0;t")]
	[InlineData("\e]0;t\e\\", "O0;t")]
	[InlineData("\e]0;t\e[1m", "C0[1]0m")]
	[InlineData("\e]x\eP\\y\e\\z", "Pz")]
	public void Feed_Input_MakesCalls(string input, string calls)
	{
		// Arrange
		var handler = new RecordingHandler();

		// Act
		new VtParser(handler).Feed(input);

		// Assert
		_ = string.Join('|', handler.Calls).Should().Be(calls);
	}

	/// <summary>
	/// Checks that a CSI with more parameters than are kept still parses, with the first ones intact.
	/// </summary>
	[Fact]
	public void Feed_CsiWithTooManyParameters_KeepsFirstOnes()
	{
		// Arrange
		var handler = new RecordingHandler();

		// Act
		new VtParser(handler).Feed($"\e[{string.Join(';', Enumerable.Range(1, 40))}H");

		// Assert
		_ = handler.Calls.Should().ContainSingle().Which.Should().StartWith($"C0[{string.Join(',', Enumerable.Range(1, 31))},").And.EndWith("]0H");
	}

	/// <summary>
	/// Writes down every call, in the short form <see cref="Feed_Input_MakesCalls" /> describes.
	/// </summary>
	private sealed class RecordingHandler : IVtHandler
	{
		/// <summary>
		/// Gets the calls so far, in order.
		/// </summary>
		/// <value>
		/// One short string per call.
		/// </value>
		public List<string> Calls { get; } = [];

		/// <inheritdoc />
		public void Print(char character) => this.Calls.Add($"P{character}");

		/// <inheritdoc />
		public void Execute(char control) => this.Calls.Add(string.Create(CultureInfo.InvariantCulture, $"X{(int)control:X2}"));

		/// <inheritdoc />
		public void Csi(char marker, ReadOnlySpan<int> parameters, char intermediate, char final) =>
			this.Calls.Add($"C{Shown(marker)}[{string.Join(',', parameters.ToArray())}]{Shown(intermediate)}{final}");

		/// <inheritdoc />
		public void Escape(char intermediate, char final) => this.Calls.Add($"E{Shown(intermediate)}{final}");

		/// <inheritdoc />
		public void Osc(string text) => this.Calls.Add($"O{text}");

		/// <summary>
		/// Shows a marker or intermediate, with <c>0</c> for none.
		/// </summary>
		/// <param name="character">The character, or <c>\0</c> for none.</param>
		/// <returns>
		/// The character, or <c>0</c>.
		/// </returns>
		private static char Shown(char character) => character == '\0' ? '0' : character;
	}
}
