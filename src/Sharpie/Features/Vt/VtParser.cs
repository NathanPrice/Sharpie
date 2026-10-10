using System.Text;

namespace Sharpie.Features.Vt;

/// <summary>
/// Receives what <see cref="VtParser" /> finds in program output.
/// </summary>
internal interface IVtHandler
{
	/// <summary>
	/// Shows one printable character at the cursor.
	/// </summary>
	/// <param name="character">The character, which may be half of a surrogate pair.</param>
	public void Print(char character);

	/// <summary>
	/// Acts on a C0 control character such as a line feed or backspace.
	/// </summary>
	/// <param name="control">The control character.</param>
	public void Execute(char control);

	/// <summary>
	/// Acts on a CSI sequence, <c>ESC [</c> parameters, intermediates and a final character.
	/// </summary>
	/// <param name="marker">The private marker, such as <c>?</c> or <c>&gt;</c>, or <c>\0</c> for none.</param>
	/// <param name="parameters">The numeric parameters, with -1 for one left empty.</param>
	/// <param name="intermediate">The intermediate character, such as a space, or <c>\0</c> for none.</param>
	/// <param name="final">The final character, which names the sequence.</param>
	public void Csi(char marker, ReadOnlySpan<int> parameters, char intermediate, char final);

	/// <summary>
	/// Acts on an escape sequence that isn't CSI or OSC, such as <c>ESC 7</c> or <c>ESC ( 0</c>.
	/// </summary>
	/// <param name="intermediate">The intermediate character, such as <c>(</c>, or <c>\0</c> for none.</param>
	/// <param name="final">The final character.</param>
	public void Escape(char intermediate, char final);

	/// <summary>
	/// Acts on an OSC string, the text between <c>ESC ]</c> and BEL or <c>ESC \</c>.
	/// </summary>
	/// <param name="text">The string, such as <c>0;title</c>.</param>
	public void Osc(string text);
}

/// <summary>
/// Splits program output into printable text, control characters and escape sequences, following the state machine
/// DEC's VT500 series uses, as described at vt100.net.
/// </summary>
/// <remarks>
/// It keeps its state between calls, because output arrives in chunks and a sequence can straddle two of them. DCS,
/// SOS, PM and APC strings are read and dropped, since nothing here uses them.
/// </remarks>
/// <param name="handler">What the parser reports to.</param>
internal sealed class VtParser(IVtHandler handler)
{
	// Most sequences have fewer than six parameters. Extra ones are dropped instead of growing the list.
	private const int MaxParameters = 32;

	// Caps an OSC string, so a program that never ends one can't use up memory.
	private const int MaxOscLength = 64 * 1024;

	private readonly int[] parameters = new int[MaxParameters];
	private readonly StringBuilder osc = new();

	private State state;
	private int count;
	private char marker;
	private char intermediate;

	// Set when ESC arrives inside a string, which may be the start of the ESC \ that ends it.
	private bool stringEscape;

	/// <summary>
	/// The parser's states.
	/// </summary>
	private enum State
	{
		/// <summary>
		/// Plain text.
		/// </summary>
		Ground,

		/// <summary>
		/// After ESC.
		/// </summary>
		Escape,

		/// <summary>
		/// After ESC and an intermediate character.
		/// </summary>
		EscapeIntermediate,

		/// <summary>
		/// Reading a CSI sequence's parameters.
		/// </summary>
		Csi,

		/// <summary>
		/// Reading a CSI sequence that is malformed, until its final character.
		/// </summary>
		CsiIgnore,

		/// <summary>
		/// Reading an OSC string.
		/// </summary>
		Osc,

		/// <summary>
		/// Reading a DCS, SOS, PM or APC string, which is dropped.
		/// </summary>
		IgnoredString,
	}

	/// <summary>
	/// Reads the next chunk of output.
	/// </summary>
	/// <param name="text">The chunk.</param>
	public void Feed(ReadOnlySpan<char> text)
	{
		foreach (var character in text)
		{
			this.Next(character);
		}
	}

	/// <summary>
	/// Moves the state machine on by one character.
	/// </summary>
	/// <param name="character">The character.</param>
	private void Next(char character)
	{
		// CAN and SUB cancel any sequence, and ESC starts a new one, except inside a string, where ESC may begin the
		// ESC \ terminator.
		if (character is '\x18' or '\x1A' && this.state != State.Ground)
		{
			this.state = State.Ground;

			return;
		}

		switch (this.state)
		{
			case State.Ground:
				this.Ground(character);
				break;
			case State.Escape:
				this.AfterEscape(character);
				break;
			case State.EscapeIntermediate:
				this.AfterEscapeIntermediate(character);
				break;
			case State.Csi:
				this.InCsi(character);
				break;
			case State.CsiIgnore:
				this.InIgnoredCsi(character);
				break;
			case State.Osc:
				this.InOsc(character);
				break;
			case State.IgnoredString:
				this.InIgnoredString(character);
				break;
			default:
				this.state = State.Ground;
				break;
		}
	}

	/// <summary>
	/// Handles a character in plain text.
	/// </summary>
	/// <param name="character">The character.</param>
	private void Ground(char character)
	{
		if (character == '\e')
		{
			this.state = State.Escape;
		}
		else if (character is < ' ' or '\x7F')
		{
			handler.Execute(character);
		}
		else
		{
			handler.Print(character);
		}
	}

	/// <summary>
	/// Handles the character after ESC.
	/// </summary>
	/// <param name="character">The character.</param>
	private void AfterEscape(char character)
	{
		switch (character)
		{
			case '[':
				this.StartCsi();
				break;
			case ']':
				_ = this.osc.Clear();
				this.stringEscape = false;
				this.state = State.Osc;
				break;
			case 'P' or 'X' or '^' or '_':
				this.stringEscape = false;
				this.state = State.IgnoredString;
				break;
			case '\e':
				break;
			case >= ' ' and <= '/':
				this.intermediate = character;
				this.state = State.EscapeIntermediate;
				break;
			case < ' ':
				handler.Execute(character);
				break;
			default:
				handler.Escape('\0', character);
				this.state = State.Ground;
				break;
		}
	}

	/// <summary>
	/// Handles the character after ESC and an intermediate, such as the <c>0</c> in <c>ESC ( 0</c>.
	/// </summary>
	/// <param name="character">The character.</param>
	private void AfterEscapeIntermediate(char character)
	{
		if (character is >= ' ' and <= '/')
		{
			this.intermediate = character;
		}
		else if (character < ' ')
		{
			handler.Execute(character);
		}
		else
		{
			handler.Escape(this.intermediate, character);
			this.state = State.Ground;
		}
	}

	/// <summary>
	/// Clears the parameters and starts reading a CSI sequence.
	/// </summary>
	private void StartCsi()
	{
		this.count = 0;
		this.parameters[0] = -1;
		this.marker = '\0';
		this.intermediate = '\0';
		this.state = State.Csi;
	}

	/// <summary>
	/// Handles a character inside a CSI sequence.
	/// </summary>
	/// <param name="character">The character.</param>
	private void InCsi(char character)
	{
		if (char.IsAsciiDigit(character))
		{
			if (this.intermediate != '\0')
			{
				this.state = State.CsiIgnore;

				return;
			}

			var value = this.parameters[this.count];

			// Capped so a long run of digits can't overflow. Every real parameter is far below this.
			this.parameters[this.count] = Math.Min((Math.Max(value, 0) * 10) + (character - '0'), 100_000);
		}
		else if (character is ';' or ':')
		{
			if (this.count < MaxParameters - 1)
			{
				this.count++;
				this.parameters[this.count] = -1;
			}
		}
		else if (character is >= '<' and <= '?')
		{
			// A private marker only counts before any parameter.
			if (this.count == 0 && this.parameters[0] == -1 && this.marker == '\0')
			{
				this.marker = character;
			}
			else
			{
				this.state = State.CsiIgnore;
			}
		}
		else if (character is >= ' ' and <= '/')
		{
			this.intermediate = character;
		}
		else if (character is >= '@' and <= '~')
		{
			handler.Csi(this.marker, this.parameters.AsSpan(0, this.count + 1), this.intermediate, character);
			this.state = State.Ground;
		}
		else if (character < ' ')
		{
			handler.Execute(character);
		}
	}

	/// <summary>
	/// Skips a malformed CSI sequence up to its final character.
	/// </summary>
	/// <param name="character">The character.</param>
	private void InIgnoredCsi(char character)
	{
		if (character is >= '@' and <= '~')
		{
			this.state = State.Ground;
		}
		else if (character < ' ')
		{
			handler.Execute(character);
		}
	}

	/// <summary>
	/// Collects an OSC string until BEL or <c>ESC \</c>.
	/// </summary>
	/// <param name="character">The character.</param>
	private void InOsc(char character)
	{
		if (this.EndsString(character))
		{
			handler.Osc(this.osc.ToString());
			this.state = State.Ground;

			return;
		}

		// An ESC that didn't make ESC \ ended the string without it, and the parser has moved on.
		if (this.state != State.Osc)
		{
			return;
		}

		if (!this.stringEscape && this.osc.Length < MaxOscLength)
		{
			_ = this.osc.Append(character);
		}
	}

	/// <summary>
	/// Skips a DCS, SOS, PM or APC string until <c>ESC \</c>.
	/// </summary>
	/// <param name="character">The character.</param>
	private void InIgnoredString(char character)
	{
		if (this.EndsString(character))
		{
			this.state = State.Ground;
		}
	}

	/// <summary>
	/// Tells whether <paramref name="character" /> ends the string being read, as BEL or the backslash of <c>ESC \</c>.
	/// </summary>
	/// <param name="character">The character.</param>
	/// <returns>
	/// <see langword="true" /> when the string is over.
	/// </returns>
	/// <remarks>
	/// An ESC followed by anything else ends the string too, and that character is read again as the start of a new
	/// escape sequence, as a terminal would.
	/// </remarks>
	private bool EndsString(char character)
	{
		if (this.stringEscape)
		{
			this.stringEscape = false;

			if (character == '\\')
			{
				return true;
			}

			this.state = State.Escape;
			this.AfterEscape(character);

			return false;
		}

		if (character == '\e')
		{
			this.stringEscape = true;

			return false;
		}

		return character == '\a';
	}
}
