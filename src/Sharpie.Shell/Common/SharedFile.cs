using Microsoft.Win32.SafeHandles;

namespace Sharpie.Shell.Common;

/// <summary>
/// A file a redirect opened for writing, which builtins write through .NET and programs through the system, each at
/// the system's position in it.
/// </summary>
/// <remarks>
/// .NET keeps a position of its own and writes there without moving the system's, which is where a program writes,
/// and it moves the system's back to its own each time the handle is handed to a program. With one file behind every
/// command of <c>{ a; b; } &gt; f</c>, each would write over the others' output. So each write here starts at the
/// system's position and leaves it after the text, as bash's own writes do. Windows copies a program's output through
/// .NET, so there .NET's position is the only one.
/// </remarks>
internal sealed class SharedFile : FileStream
{
	/// <summary>
	/// <c>lseek</c>'s whence for the start of the file.
	/// </summary>
	private const int FromStart = 0;

	/// <summary>
	/// <c>lseek</c>'s whence for the current position.
	/// </summary>
	private const int FromCurrent = 1;

	/// <summary>
	/// The handle, taken without the seek that <see cref="FileStream.SafeFileHandle" /> does.
	/// </summary>
	private readonly SafeFileHandle handle;

	/// <summary>
	/// Initializes a new instance of the <see cref="SharedFile" /> class, opening <paramref name="path" /> to write,
	/// with no buffer, so every write reaches the system at once.
	/// </summary>
	/// <param name="path">The system's name for the file.</param>
	/// <param name="mode">How to open it.</param>
	public SharedFile(string path, FileMode mode)
		: base(path, mode, FileAccess.Write, Redirection.Shared, bufferSize: 0) => this.handle = base.SafeFileHandle;

	/// <inheritdoc />
	/// <remarks>
	/// Takes the system's position first, so handing the file to a program doesn't move it back over what another
	/// program wrote.
	/// </remarks>
	public override SafeFileHandle SafeFileHandle
	{
		get
		{
			this.FollowSystem();

			return base.SafeFileHandle;
		}
	}

	/// <inheritdoc />
	public override void Write(byte[] buffer, int offset, int count)
	{
		this.FollowSystem();
		base.Write(buffer, offset, count);

		if (this.Tracks)
		{
			_ = NativeMethods.Seek(this.handle, this.Position, FromStart);
		}
	}

	/// <inheritdoc />
	/// <remarks>
	/// <see cref="FileStream" /> sends this to the array overload for a type derived from it, so this does the same
	/// rather than go around the position handling.
	/// </remarks>
	public override void Write(ReadOnlySpan<byte> buffer) => this.Write(buffer.ToArray(), 0, buffer.Length);

	/// <inheritdoc />
	public override void WriteByte(byte value) => this.Write([value], 0, 1);

	/// <summary>
	/// Gets a value indicating whether the system keeps a position of its own that programs write at.
	/// </summary>
	/// <value>
	/// <see langword="true" /> for a file that can seek, outside Windows.
	/// </value>
	// Stryker disable once all : the Windows check only matters on Windows.
	private bool Tracks => !OperatingSystem.IsWindows() && this.CanSeek;

	/// <summary>
	/// Moves .NET's position to the system's.
	/// </summary>
	private void FollowSystem()
	{
		if (this.Tracks && NativeMethods.Seek(this.handle, 0, FromCurrent) is >= 0 and var position)
		{
			this.Position = position;
		}
	}
}
