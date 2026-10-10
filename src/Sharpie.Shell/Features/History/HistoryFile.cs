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
				var lines = Encoding.UTF8.GetString(plaintext).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

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
	public static bool Save(string path, byte[] key, IEnumerable<string> lines)
	{
		_ = path.ArgumentNotNullOrEmpty();
		_ = key.ArgumentNotNull();
		_ = lines.ArgumentNotNull();

		var plaintext = Encoding.UTF8.GetBytes(string.Join('\n', lines));
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
