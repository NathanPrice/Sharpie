using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.History;

/// <summary>
/// Keeps the history file's AES key in the operating system's per-user secret store, which only unlocks once the user
/// has logged in.
/// </summary>
/// <remarks>
/// <para>
/// OWASP's storage guidance is a random key, never kept in plaintext, held in an OS store apart from the data. So
/// the key is 32 bytes from <see cref="RandomNumberGenerator" /> and lives in DPAPI on Windows, the login Keychain
/// on macOS, and the Secret Service (GNOME Keyring or KWallet) on Linux. A Linux session with no keyring gets no key,
/// and its history stays in memory.
/// </para>
/// <para>
/// The helper tools are run by absolute path so a fake <c>secret-tool</c> or <c>security</c> earlier on
/// <c>PATH</c> can't capture the key, and the key goes over standard input so it never shows up in the process
/// list. Any program already running as the same logged-in user can still ask the store for it.
/// </para>
/// </remarks>
internal static class HistoryKey
{
	private const int KeySize = 32;
	private const string Service = "sharpie-history";
	private const string SecretTool = "/usr/bin/secret-tool";
	private const string Security = "/usr/bin/security";

	/// <summary>
	/// Fetches the history key from the user's secret store, creating it first when allowed.
	/// </summary>
	/// <param name="folder">The history folder, which holds the DPAPI-wrapped key on Windows.</param>
	/// <param name="allowCreate">
	/// <see langword="true" /> to store a new key when none is found. The shell passes <see langword="false" /> once
	/// a history file exists, because a key that's only missing for now, like in a keyring that's still locked,
	/// would otherwise be replaced and the old history could never be read again.
	/// </param>
	/// <returns>
	/// The 32-byte key, or <see langword="null" /> when no store is available or the key couldn't be read or saved.
	/// </returns>
	public static byte[]? LoadOrCreate(string folder, bool allowCreate)
	{
		_ = folder.ArgumentNotNullOrEmpty();

		try
		{
			return OperatingSystem.IsWindows() ? LoadOrCreateWindows(Path.Combine(folder, "history.key"), allowCreate)
				: OperatingSystem.IsMacOS() ? LoadOrCreateWithTool(allowCreate, MacLookup, MacStore)
				: LoadOrCreateWithTool(allowCreate, LinuxLookup, LinuxStore);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
		{
			new ShellError("sharpie::history::key_error", exception.Message).Write();
			return null;
		}
	}

	/// <summary>
	/// Reads the key from a helper tool's store, or makes and stores a new one.
	/// </summary>
	/// <param name="allowCreate"><see langword="true" /> to store a new key when <paramref name="lookup" /> finds none.</param>
	/// <param name="lookup">Returns the stored key in Base64, or <see langword="null" /> when there isn't one.</param>
	/// <param name="store">Stores a Base64 key and returns <see langword="true" /> when it worked.</param>
	/// <returns>
	/// The key, or <see langword="null" /> when it can't be found or created.
	/// </returns>
	internal static byte[]? LoadOrCreateWithTool(bool allowCreate, Func<string?> lookup, Func<string, bool> store)
	{
		if (lookup() is { } stored)
		{
			var bytes = new byte[KeySize];
			return Convert.TryFromBase64String(stored.Trim(), bytes, out var length) && length == KeySize ? bytes : null;
		}

		if (!allowCreate)
		{
			return null;
		}

		var key = RandomNumberGenerator.GetBytes(KeySize);
		return store(Convert.ToBase64String(key)) ? key : null;
	}

	/// <summary>
	/// Reads the DPAPI-wrapped key file, or wraps and writes a new key.
	/// </summary>
	/// <param name="path">The wrapped key file.</param>
	/// <param name="allowCreate"><see langword="true" /> to write a new key when the file doesn't exist.</param>
	/// <returns>
	/// The key, or <see langword="null" /> when the file is missing and <paramref name="allowCreate" /> is
	/// <see langword="false" />.
	/// </returns>
	/// <remarks>
	/// <see cref="DataProtectionScope.CurrentUser" /> ties the wrapping to the user's logon credentials, so the file
	/// is useless to other accounts or to a copy of the disk.
	/// </remarks>
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static byte[]? LoadOrCreateWindows(string path, bool allowCreate)
	{
		if (File.Exists(path))
		{
			return ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
		}

		if (!allowCreate)
		{
			return null;
		}

		var key = RandomNumberGenerator.GetBytes(KeySize);

		using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
		{
			stream.Write(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
		}

		return key;
	}

	/// <summary>
	/// Looks the key up in the Secret Service.
	/// </summary>
	/// <returns>
	/// The Base64 key, or <see langword="null" /> when it isn't stored or no keyring is running.
	/// </returns>
	private static string? LinuxLookup() => Run(SecretTool, ["lookup", "service", Service], null);

	/// <summary>
	/// Stores the key in the Secret Service, which reads it from standard input.
	/// </summary>
	/// <param name="key">The Base64 key.</param>
	/// <returns>
	/// <see langword="true" /> when <c>secret-tool</c> reports success.
	/// </returns>
	private static bool LinuxStore(string key) =>
		Run(SecretTool, ["store", "--label=sharpie history key", "service", Service], key) is not null;

	/// <summary>
	/// Looks the key up in the login Keychain.
	/// </summary>
	/// <returns>
	/// The Base64 key, or <see langword="null" /> when it isn't stored.
	/// </returns>
	private static string? MacLookup() =>
		Run(Security, ["find-generic-password", "-s", Service, "-a", Environment.UserName, "-w"], null);

	/// <summary>
	/// Stores the key in the login Keychain.
	/// </summary>
	/// <param name="key">The Base64 key.</param>
	/// <returns>
	/// <see langword="true" /> when <c>security</c> reports success.
	/// </returns>
	/// <remarks>
	/// <c>add-generic-password</c> only takes the secret as an argument, so it's sent as a command to
	/// <c>security -i</c> on standard input instead, where other processes can't list it.
	/// </remarks>
	private static bool MacStore(string key) =>
		Run(Security, ["-i"], $"add-generic-password -U -s {Service} -a \"{Environment.UserName}\" -w {key}\n") is not null;

	/// <summary>
	/// Runs a helper tool and collects its standard output.
	/// </summary>
	/// <param name="path">The tool's absolute path.</param>
	/// <param name="arguments">The arguments, none of them secret.</param>
	/// <param name="input">Text to write to standard input, or <see langword="null" /> for none.</param>
	/// <returns>
	/// The standard output when the tool exits with code 0, or <see langword="null" /> when it fails or isn't
	/// installed.
	/// </returns>
	private static string? Run(string path, string[] arguments, string? input)
	{
		var startInfo = new ProcessStartInfo(path, arguments)
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		try
		{
			using var process = Process.Start(startInfo)!;

			process.StandardInput.Write(input);
			process.StandardInput.Close();

			var output = process.StandardOutput.ReadToEnd();
			_ = process.StandardError.ReadToEnd();
			process.WaitForExit();

			return process.ExitCode == 0 ? output : null;
		}
		// The tool isn't installed, as on a Linux box without libsecret.
		catch (Win32Exception)
		{
			return null;
		}
	}
}
