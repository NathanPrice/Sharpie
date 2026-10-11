using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Features.ManPages;

/// <summary>
/// Decides which lines open a man page in the window, and runs <c>man</c> to get the page.
/// </summary>
internal static class ManPageReader
{
	/// <summary>
	/// Runs <c>man</c> with formatting kept and waits for the whole page.
	/// </summary>
	/// <param name="arguments">The arguments for <c>man</c>.</param>
	/// <param name="columns">The width to format the page to.</param>
	/// <param name="directory">The folder to run in, or <see langword="null" /> to use the window's own.</param>
	/// <returns>
	/// The page with its overstrike, or <see langword="null" /> when <c>man</c> isn't installed, failed, or printed
	/// nothing.
	/// </returns>
	/// <remarks>
	/// Linux's man-db strips bold and underline on a pipe unless <c>MAN_KEEP_FORMATTING</c> is set, and <c>-c</c>
	/// makes groff mark them with overstrike instead of color codes. macOS's mandoc keeps overstrike on a pipe by
	/// itself and ignores both. Both read <c>MANWIDTH</c>.
	/// </remarks>
	public static string? Read(string[] arguments, int columns, string? directory) =>
		PathFinder.FindExecutable("man") is { } fullPath ? Read(fullPath, arguments, columns, directory) : null;

	/// <summary>
	/// Runs a <c>man</c> program with formatting kept and waits for the whole page.
	/// </summary>
	/// <param name="fullPath">The <c>man</c> program to run.</param>
	/// <param name="arguments">The arguments for <c>man</c>.</param>
	/// <param name="columns">The width to format the page to.</param>
	/// <param name="directory">The folder to run in, or <see langword="null" /> to use the window's own.</param>
	/// <returns>
	/// The page with its overstrike, or <see langword="null" /> when the program failed or printed nothing.
	/// </returns>
	internal static string? Read(string fullPath, string[] arguments, int columns, string? directory)
	{
		var startInfo = new ProcessStartInfo(fullPath)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Directory.Exists(directory) ? directory : string.Empty,
			Environment =
			{
				["MAN_KEEP_FORMATTING"] = "1",
				["MANROFFOPT"] = "-c",
				["MANWIDTH"] = columns.ToString(CultureInfo.InvariantCulture),
				["COLUMNS"] = columns.ToString(CultureInfo.InvariantCulture),
			},
		};

		foreach (var argument in arguments.ArgumentNotNull())
		{
			startInfo.ArgumentList.Add(argument);
		}

		try
		{
			using var process = Process.Start(startInfo);

			if (process is null)
			{
				return null;
			}

			// Standard error is drained alongside, or a page that also writes warnings could fill that pipe and hang.
			var errors = process.StandardError.ReadToEndAsync();
			var output = process.StandardOutput.ReadToEnd();

			errors.Wait();
			process.WaitForExit();

			return process.ExitCode == 0 && output.Length > 0 ? output : null;
		}
		catch (Win32Exception)
		{
			return null;
		}
	}
}
