using System.Security.Cryptography;
using System.Text;
using DotNetTips.Spargine.Core;

namespace Sharpie.Shell.Features.History;

/// <summary>
/// Reads and writes the shell's history as an AES-256-GCM encrypted file.
/// </summary>
/// <remarks>
/// The layout follows the user's Cryptor project, minus its salt because the key is random rather than derived from a
/// password: <c>[version][nonce][cipher][tag]</c>. The version byte is authenticated as associated data, so changing
/// it fails decryption, and a new format or key can bump it later. GCM with random nonces is safe for about 2^32
/// encryptions under one key (OWASP Key Management Cheat Sheet). One save per command line stays far below that, so
/// the key isn't rotated.
/// </remarks>
internal static class HistoryFile
{
	private const byte Version = 1;
	private const int NonceSize = 12;
	private const int TagSize = 16;

	/// <summary>
	/// How many times <see cref="Append" /> tries for the lock that another tab's shell holds.
	/// </summary>
	/// <remarks>
	/// The lock is held only while the file is read and rewritten. With <see cref="LockWait" /> this gives up after
	/// about a second, so a stuck holder can't freeze Enter for good.
	/// </remarks>
	private const int LockAttempts = 100;

	/// <summary>
	/// How long <see cref="Append" /> waits between tries for the lock.
	/// </summary>
	private static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(10);

	/// <summary>
	/// The read and write bits for the owner only, which every file this class writes gets on Unix.
	/// </summary>
	internal const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

	/// <summary>
	/// Creates <paramref name="folder" /> if it's missing, then checks it's safe to keep history in.
	/// </summary>
	/// <param name="folder">The folder the history file lives in.</param>
	/// <returns>
	/// <see langword="true" /> when the folder is a real directory that only its owner can write to; otherwise
	/// <see langword="false" />, after printing the reason.
	/// </returns>
	/// <remarks>
	/// A symlinked or group-writable folder would let another account swap files in under the shell, so it's refused
	/// instead of fixed.
	/// </remarks>
	public static bool PrepareFolder(string folder)
	{
		_ = folder.ArgumentNotNullOrEmpty();

		try
		{
			var info = OperatingSystem.IsWindows()
				? Directory.CreateDirectory(folder)
				: Directory.CreateDirectory(folder, OwnerOnly | UnixFileMode.UserExecute);

			if (info.LinkTarget is not null)
			{
				Console.Error.WriteLine($"history: {folder} is a symbolic link, so history won't be saved");

				return false;
			}

			if (OperatingSystem.IsWindows() || (info.UnixFileMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
			{
				return true;
			}

			Console.Error.WriteLine($"history: {folder} is writable by other users, so history won't be saved");

			return false;

		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"history: {exception.Message}");

			return false;
		}
	}

	/// <summary>
	/// Reads and decrypts the history file at <paramref name="path" />.
	/// </summary>
	/// <param name="path">The encrypted history file.</param>
	/// <param name="key">The 32-byte AES key the file was saved with.</param>
	/// <returns>
	/// The newest <see cref="HistoryCommand.MaxLines" /> lines, oldest first; an empty list when the file doesn't
	/// exist; or <see langword="null" />, after printing the reason, when it can't be read or fails authentication.
	/// </returns>
	public static List<string>? Load(string path, byte[] key)
	{
		_ = path.ArgumentNotNullOrEmpty();
		_ = key.ArgumentNotNull();

		try
		{
			if (!File.Exists(path))
			{
				return [];
			}

			var plaintext = Decrypt(key, File.ReadAllBytes(path));

			try
			{
				var lines = new List<string>();

				// Split as bytes and decoded line by line, because decoding the whole file would make a second plain
				// copy of the history that ZeroMemory can't wipe, big enough to land on the large object heap. The
				// byte 0x0A never appears inside a multi-byte UTF-8 character, so every split is a real line break.
				foreach (var range in plaintext.AsSpan().Split((byte)'\n'))
				{
					if (range.Start.Value < range.End.Value)
					{
						lines.Add(Encoding.UTF8.GetString(plaintext.AsSpan(range)));
					}
				}

				HistoryCommand.Trim(lines);

				return lines;
			}
			finally
			{
				CryptographicOperations.ZeroMemory(plaintext);
			}
		}
		catch (CryptographicException)
		{
			Console.Error.WriteLine($"history: {path} was changed or saved with another key, so it was left alone");

			return null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"history: {exception.Message}");

			return null;
		}
	}

	/// <summary>
	/// Adds <paramref name="lines" /> to the end of the history file at <paramref name="path" />, keeping whatever
	/// other shells saved there.
	/// </summary>
	/// <param name="path">The encrypted history file.</param>
	/// <param name="key">The 32-byte AES key the file is saved with.</param>
	/// <param name="lines">The new lines, oldest first.</param>
	/// <returns>
	/// <see langword="true" /> once the file holds the new lines; otherwise <see langword="false" />, after printing the
	/// reason, including when the file fails authentication and is left alone.
	/// </returns>
	/// <remarks>
	/// Every tab and window runs its own shell on the same file. Each one used to write its own list over the file, so
	/// the last shell to save dropped the lines the others had saved since it started. This reads the file again under
	/// an exclusive lock on a file next to it, then adds the new lines. The lock is <c>flock</c> on Unix and a sharing
	/// lock on Windows, and the system releases it if the shell dies holding it.
	/// </remarks>
	public static bool Append(string path, byte[] key, IReadOnlyCollection<string> lines)
	{
		_ = path.ArgumentNotNullOrEmpty();
		_ = key.ArgumentNotNull();
		_ = lines.ArgumentNotNull();

		try
		{
			using var lockFile = OpenLock($"{path}.lock");

			if (Load(path, key) is not { } saved)
			{
				return false;
			}

			saved.AddRange(lines);
			HistoryCommand.Trim(saved);

			return Save(path, key, saved);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"history: {exception.Message}");

			return false;
		}
	}

	/// <summary>
	/// Opens <paramref name="lockPath" /> so no other shell can open it, waiting while another one has it.
	/// </summary>
	/// <param name="lockPath">The lock file next to the history file.</param>
	/// <returns>
	/// The open lock file. Disposing it releases the lock.
	/// </returns>
	/// <exception cref="IOException">Another shell kept the lock through every try.</exception>
	private static FileStream OpenLock(string lockPath)
	{
		var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };

		if (!OperatingSystem.IsWindows())
		{
			options.UnixCreateMode = OwnerOnly;
		}

		for (var attempt = 1; ; attempt++)
		{
			try
			{
				return new FileStream(lockPath, options);
			}
			catch (IOException) when (attempt < LockAttempts)
			{
				Thread.Sleep(LockWait);
			}
		}
	}

	/// <summary>
	/// Encrypts <paramref name="lines" /> and replaces the history file at <paramref name="path" /> with them.
	/// </summary>
	/// <param name="path">The encrypted history file.</param>
	/// <param name="key">The 32-byte AES key to save with.</param>
	/// <param name="lines">The lines to save, oldest first.</param>
	/// <returns>
	/// <see langword="true" /> once the file is replaced; otherwise <see langword="false" />, after printing the
	/// reason.
	/// </returns>
	/// <remarks>
	/// Writes a new file next to <paramref name="path" /> and moves it over, so a crash mid-write never leaves a
	/// half-written history. The new file is created exclusively, which fails instead of following a planted link.
	/// </remarks>
	public static bool Save(string path, byte[] key, IReadOnlyList<string> lines)
	{
		_ = path.ArgumentNotNullOrEmpty();
		_ = key.ArgumentNotNull();
		_ = lines.ArgumentNotNull();

		// Encoded straight into one buffer, because joining the lines into a string first would make a plain copy of
		// the history that ZeroMemory can't wipe.
		var plaintext = new byte[lines.Sum(line => Encoding.UTF8.GetByteCount(line)) + Math.Max(0, lines.Count - 1)];
		var written = 0;

		for (var index = 0; index < lines.Count; index++)
		{
			if (index > 0)
			{
				plaintext[written++] = (byte)'\n';
			}

			written += Encoding.UTF8.GetBytes(lines[index], plaintext.AsSpan(written));
		}

		var temporaryPath = $"{path}.{Path.GetRandomFileName()}";

		try
		{
			var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

			if (!OperatingSystem.IsWindows())
			{
				options.UnixCreateMode = OwnerOnly;
			}

			using (var stream = new FileStream(temporaryPath, options))
			{
				stream.Write(Encrypt(key, plaintext));
			}

			File.Move(temporaryPath, path, overwrite: true);

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"history: {exception.Message}");
			File.Delete(temporaryPath);

			return false;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plaintext);
		}
	}

	/// <summary>
	/// Encrypts <paramref name="plaintext" /> with a fresh random nonce.
	/// </summary>
	/// <param name="key">The 32-byte AES key.</param>
	/// <param name="plaintext">The bytes to encrypt.</param>
	/// <returns>
	/// The version byte, nonce, ciphertext and tag, in that order.
	/// </returns>
	internal static byte[] Encrypt(byte[] key, byte[] plaintext)
	{
		_ = key.ArgumentNotNull();
		_ = plaintext.ArgumentNotNull();

		var data = new byte[1 + NonceSize + plaintext.Length + TagSize];
		data[0] = Version;

		var nonce = data.AsSpan(1, NonceSize);
		RandomNumberGenerator.Fill(nonce);

		using var aes = new AesGcm(key, TagSize);
		aes.Encrypt(nonce, plaintext, data.AsSpan(1 + NonceSize, plaintext.Length), data.AsSpan(^TagSize), data.AsSpan(0, 1));

		return data;
	}

	/// <summary>
	/// Checks and decrypts bytes produced by <see cref="Encrypt" />.
	/// </summary>
	/// <param name="key">The 32-byte AES key.</param>
	/// <param name="data">The version byte, nonce, ciphertext and tag.</param>
	/// <returns>
	/// The plaintext bytes.
	/// </returns>
	/// <exception cref="CryptographicException">
	/// <paramref name="data" /> is too short, has an unknown version, or fails authentication because it was changed
	/// or encrypted with another key.
	/// </exception>
	internal static byte[] Decrypt(byte[] key, byte[] data)
	{
		_ = key.ArgumentNotNull();
		_ = data.ArgumentNotNull();

		if (data.Length < 1 + NonceSize + TagSize || data[0] != Version)
		{
			throw new CryptographicException(Properties.Resources.UnknownHistoryFormat);
		}

		var plaintext = new byte[data.Length - 1 - NonceSize - TagSize];

		using var aes = new AesGcm(key, TagSize);
		aes.Decrypt(data.AsSpan(1, NonceSize), data.AsSpan(1 + NonceSize, plaintext.Length), data.AsSpan(^TagSize), plaintext, data.AsSpan(0, 1));

		return plaintext;
	}
}
