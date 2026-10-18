using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Conditions;

/// <summary>
/// The tests that <c>test</c>, <c>[</c> and <c>[[</c> share, as bash's <c>unary_test</c> and <c>binary_test</c> run
/// them.
/// </summary>
/// <remarks>
/// On Linux and macOS every file test reads <c>stat</c> or <c>lstat</c> and <c>access</c>, as bash does. Windows has
/// no file modes, so there <c>-r</c> is "exists", <c>-w</c> is "exists and isn't read-only", <c>-x</c> is a folder or
/// a file whose extension is in <c>PATHEXT</c>, <c>-O</c> and <c>-G</c> are "exists", and <c>-b</c>, <c>-c</c>,
/// <c>-p</c>, <c>-S</c>, <c>-u</c>, <c>-g</c> and <c>-k</c> are always false.
/// </remarks>
internal static class Condition
{
	// The file type bits of st_mode, and the set-user-id, set-group-id and sticky bits.
	private const int TypeMask = 0xF000;
	private const int Fifo = 0x1000;
	private const int CharacterDevice = 0x2000;
	private const int Folder = 0x4000;
	private const int BlockDevice = 0x6000;
	private const int Regular = 0x8000;
	private const int Link = 0xA000;
	private const int Socket = 0xC000;
	private const int SetUser = 0x800;
	private const int SetGroup = 0x400;
	private const int Sticky = 0x200;

	/// <summary>
	/// The letters of the unary operators, each typed after a <c>-</c>.
	/// </summary>
	private const string UnaryLetters = "abcdefghknprstuvwxzGLNOS";

	/// <summary>
	/// Checks whether <paramref name="text" /> is a unary operator, like <c>-f</c>.
	/// </summary>
	/// <param name="text">The word.</param>
	/// <returns>
	/// <see langword="true" /> for <c>-a -b -c -d -e -f -g -h -k -n -p -r -s -t -u -v -w -x -z -G -L -N -O -S</c>.
	/// </returns>
	internal static bool IsUnary(string text) => text is ['-', var letter] && UnaryLetters.Contains(letter, StringComparison.Ordinal);

	/// <summary>
	/// Checks whether <paramref name="text" /> is a binary operator, like <c>-eq</c>.
	/// </summary>
	/// <param name="text">The word.</param>
	/// <returns>
	/// <see langword="true" /> for <c>= == != &lt; &gt; -eq -ne -lt -le -gt -ge -nt -ot -ef</c>.
	/// </returns>
	internal static bool IsBinary(string text) =>
		text is "=" or "==" or "!=" or "<" or ">" or "-eq" or "-ne" or "-lt" or "-le" or "-gt" or "-ge" or "-nt" or "-ot" or "-ef";

	/// <summary>
	/// Runs a unary test.
	/// </summary>
	/// <param name="operator">An operator <see cref="IsUnary" /> accepts.</param>
	/// <param name="operand">The word after it.</param>
	/// <param name="variables">The shell variables, which <c>-v</c> reads before the environment.</param>
	/// <returns>
	/// Whether the test holds.
	/// </returns>
	internal static bool Unary(string @operator, string operand, IReadOnlyDictionary<string, string> variables) => @operator.ArgumentNotNull()[1] switch
	{
		'n' => operand.Length > 0,
		'z' => operand.Length == 0,
		'v' => variables.ArgumentNotNull().ContainsKey(operand) || Environment.GetEnvironmentVariable(operand) is not null,
		't' => IsTerminal(operand),
		'a' or 'e' => Exists(operand),
		'f' => OperatingSystem.IsWindows() ? File.Exists(operand) : IsType(operand, Regular),
		'd' => OperatingSystem.IsWindows() ? Directory.Exists(operand) : IsType(operand, Folder),
		'h' or 'L' => OperatingSystem.IsWindows() ? operand.Length > 0 && new FileInfo(operand).LinkTarget is not null : IsType(operand, Link, followLinks: false),
		'b' => IsType(operand, BlockDevice),
		'c' => IsType(operand, CharacterDevice),
		'p' => IsType(operand, Fifo),
		'S' => IsType(operand, Socket),
		'u' => HasBit(operand, SetUser),
		'g' => HasBit(operand, SetGroup),
		'k' => HasBit(operand, Sticky),
		's' => OperatingSystem.IsWindows() ? File.Exists(operand) && new FileInfo(operand).Length > 0 : Status(operand) is { Size: > 0 },
		'r' or 'w' or 'x' => MayUse(operand, @operator[1]),
		'O' => OperatingSystem.IsWindows() ? Exists(operand) : Status(operand) is { } owned && owned.User == NativeMethods.EffectiveUser(),
		'G' => OperatingSystem.IsWindows() ? Exists(operand) : Status(operand) is { } grouped && grouped.Group == NativeMethods.EffectiveGroup(),
		_ => Exists(operand) && File.GetLastWriteTimeUtc(operand) >= File.GetLastAccessTimeUtc(operand),
	};

	/// <summary>
	/// Runs a binary test.
	/// </summary>
	/// <param name="left">The word before the operator.</param>
	/// <param name="operator">An operator <see cref="IsBinary" /> accepts.</param>
	/// <param name="right">The word after it.</param>
	/// <param name="order">
	/// How <c>&lt;</c> and <c>&gt;</c> sort: in character code order for <c>test</c>, in the locale's order for
	/// <c>[[</c>, as the bash manual says.
	/// </param>
	/// <returns>
	/// Whether the test holds.
	/// </returns>
	/// <exception cref="Failure">A number operator's word isn't a whole number.</exception>
	internal static bool Binary(string left, string @operator, string right, StringComparer order) => @operator switch
	{
		"=" or "==" => string.Equals(left, right, StringComparison.Ordinal),
		"!=" => !string.Equals(left, right, StringComparison.Ordinal),
		"<" => order.ArgumentNotNull().Compare(left, right) < 0,
		">" => order.ArgumentNotNull().Compare(left, right) > 0,
		"-nt" => IsNewer(left, right),
		"-ot" => IsNewer(right, left),
		"-ef" => IsSameFile(left, right),
		_ => Compare(Integer(left), @operator, Integer(right)),
	};

	/// <summary>
	/// Compares two numbers.
	/// </summary>
	/// <param name="left">The number before the operator.</param>
	/// <param name="operator"><c>-eq</c>, <c>-ne</c>, <c>-lt</c>, <c>-le</c>, <c>-gt</c> or <c>-ge</c>.</param>
	/// <param name="right">The number after it.</param>
	/// <returns>
	/// Whether the comparison holds.
	/// </returns>
	private static bool Compare(long left, string @operator, long right) => @operator switch
	{
		"-eq" => left == right,
		"-ne" => left != right,
		"-lt" => left < right,
		"-le" => left <= right,
		"-gt" => left > right,
		_ => left >= right,
	};

	/// <summary>
	/// Reads a word that a number operator compares.
	/// </summary>
	/// <param name="text">The word.</param>
	/// <returns>
	/// The number.
	/// </returns>
	/// <exception cref="Failure">
	/// <paramref name="text" /> isn't digits with an optional sign and blanks around them, or doesn't fit in 64 bits.
	/// </exception>
	private static long Integer(string text) =>
		long.TryParse(text, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
			? number
			: throw new Failure(new ShellError("sharpie::test::not_a_number", $"`{text}` isn't a whole number")
			{
				Help = "-eq, -ne, -lt, -le, -gt and -ge compare whole numbers; use = or != to compare text",
			});

	/// <summary>
	/// Checks whether standard input, output or error is a terminal.
	/// </summary>
	/// <param name="operand">The stream's number.</param>
	/// <returns>
	/// <see langword="true" /> when it's 0, 1 or 2 and that stream isn't redirected.
	/// </returns>
	/// <remarks>
	/// shortcut: only the shell's own streams are seen, so <c>test -t 1 &gt; f</c> and <c>$(test -t 1)</c> still find a
	/// terminal, since a builtin's redirect and a substitution only swap <see cref="Console.Out" />. Pass the command's
	/// stream targets in if that's needed.
	/// </remarks>
	private static bool IsTerminal(string operand) => operand.Trim() switch
	{
		"0" => !Console.IsInputRedirected,
		"1" => !Console.IsOutputRedirected,
		"2" => !Console.IsErrorRedirected,
		_ => false,
	};

	/// <summary>
	/// Checks whether a file or folder exists, following symbolic links.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <returns>
	/// <see langword="true" /> when it exists, so a link to nothing doesn't.
	/// </returns>
	private static bool Exists(string path) => OperatingSystem.IsWindows() ? File.Exists(path) || Directory.Exists(path) : Status(path) is not null;

	/// <summary>
	/// Reads a file's <c>stat</c> details.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <param name="followLinks"><see langword="false" /> to read a symbolic link itself.</param>
	/// <returns>
	/// The details, or <see langword="null" /> on Windows or when the file doesn't exist, as for an empty path.
	/// </returns>
	private static UnixFileStatus? Status(string path, bool followLinks = true) =>
		OperatingSystem.IsWindows() ? null : NativeMethods.Status(path, followLinks);

	/// <summary>
	/// Checks a file's type.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <param name="type">One of the file type constants.</param>
	/// <param name="followLinks"><see langword="false" /> to check a symbolic link itself.</param>
	/// <returns>
	/// <see langword="true" /> when the file exists and is of that type.
	/// </returns>
	private static bool IsType(string path, int type, bool followLinks = true) => Status(path, followLinks) is { } status && (status.Mode & TypeMask) == type;

	/// <summary>
	/// Checks one of a file's mode bits.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <param name="bit">The bit.</param>
	/// <returns>
	/// <see langword="true" /> when the file exists and has the bit set.
	/// </returns>
	private static bool HasBit(string path, int bit) => Status(path) is { } status && (status.Mode & bit) != 0;

	/// <summary>
	/// Checks whether this process may read, write or run a file.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <param name="letter"><c>r</c>, <c>w</c> or <c>x</c>.</param>
	/// <returns>
	/// <see langword="true" /> when it may.
	/// </returns>
	private static bool MayUse(string path, char letter)
	{
		if (!OperatingSystem.IsWindows())
		{
			return NativeMethods.Access(path, letter switch { 'r' => 4, 'w' => 2, _ => 1 }) == 0;
		}

		var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);

		return letter switch
		{
			'r' => Exists(path),
			'w' => Directory.Exists(path) || (File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)),
			_ => Directory.Exists(path) || (File.Exists(path) && extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)),
		};
	}

	/// <summary>
	/// Checks whether one file was changed more recently than another, as <c>-nt</c> does.
	/// </summary>
	/// <param name="first">The file that should be newer.</param>
	/// <param name="second">The file it's compared with.</param>
	/// <returns>
	/// <see langword="true" /> when <paramref name="first" /> exists and <paramref name="second" /> doesn't, or both
	/// exist and <paramref name="first" /> was written later.
	/// </returns>
	private static bool IsNewer(string first, string second) =>
		Exists(first) && (!Exists(second) || File.GetLastWriteTimeUtc(first) > File.GetLastWriteTimeUtc(second));

	/// <summary>
	/// Checks whether two paths name the same file, as <c>-ef</c> does, so a hard link counts.
	/// </summary>
	/// <param name="first">One path.</param>
	/// <param name="second">The other.</param>
	/// <returns>
	/// <see langword="true" /> when both exist and are the same file.
	/// </returns>
	private static bool IsSameFile(string first, string second) =>
		!OperatingSystem.IsWindows() ? Status(first) is { } one && Status(second) is { } other && one.Device == other.Device && one.Inode == other.Inode
		: NativeMethods.FileIndex(first) is { } index ? index == NativeMethods.FileIndex(second)
		: Directory.Exists(first) && Directory.Exists(second) && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// A test that can't be run, like <c>-eq</c> on a word that isn't a number, which gives status 2.
	/// </summary>
	/// <param name="error">What went wrong, for the command to report.</param>
	[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only the tests here throw it, and always with a report.")]
	internal sealed class Failure(ShellError error) : Exception(error.ArgumentNotNull().Summary)
	{
		/// <summary>
		/// Gets the report.
		/// </summary>
		/// <value>
		/// What went wrong.
		/// </value>
		public ShellError Error => error;
	}
}
