using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

// The tests set Unix execute bits to control what PathFinder finds, and Windows has no execute bit.
[assembly: UnsupportedOSPlatform("windows")]

namespace Sharpie.Shell.Tests;

/// <summary>
/// Gives one test its own console output, <c>PATH</c> and <c>HOME</c> values, working directory, and temporary folder,
/// and puts the originals back when it's disposed.
/// </summary>
public sealed class ShellEnvironment : IDisposable
{
	[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The process-wide writer, borrowed only to put it back.")]
	private readonly TextWriter originalOut = Console.Out;
	private readonly string? originalPath = Environment.GetEnvironmentVariable("PATH");
	private readonly string? originalHome = Environment.GetEnvironmentVariable("HOME");
	private readonly string originalDirectory = Directory.GetCurrentDirectory();
	private readonly StringWriter output = new();
	private readonly string root = Directory.CreateTempSubdirectory("shell-tests-").FullName;

	/// <summary>
	/// Initializes a new instance of the <see cref="ShellEnvironment" /> class and redirects <see cref="Console.Out" />.
	/// </summary>
	public ShellEnvironment()
	{
		Console.SetOut(this.output);
	}

	/// <summary>
	/// Gets everything written to the console since this instance was created.
	/// </summary>
	/// <value>
	/// The captured text, with each line ending in <see cref="Environment.NewLine" />.
	/// </value>
	public string Output => this.output.ToString();

	/// <summary>
	/// Creates a new, empty folder inside this environment's temporary folder.
	/// </summary>
	/// <returns>
	/// The full path of the new folder.
	/// </returns>
	public string CreateFolder()
	{
		return Directory.CreateDirectory(Path.Combine(this.root, Path.GetRandomFileName())).FullName;
	}

	/// <summary>
	/// Writes a file named <paramref name="name" /> into <paramref name="folder" />.
	/// </summary>
	/// <param name="folder">The folder to create the file in.</param>
	/// <param name="name">The file name, which is also the command name a test looks up.</param>
	/// <param name="executable"><see langword="true" /> to set the user execute bit; otherwise, <see langword="false" />.</param>
	/// <param name="contents">The text to write into the file.</param>
	/// <returns>
	/// The full path of the file.
	/// </returns>
	public static string CreateFile(string folder, string name, bool executable, string contents = "")
	{
		var fullPath = Path.Combine(folder, name);

		File.WriteAllText(fullPath, contents);

		var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

		File.SetUnixFileMode(fullPath, executable ? mode | UnixFileMode.UserExecute : mode);

		return fullPath;
	}

	/// <summary>
	/// Sets <c>PATH</c> to <paramref name="folders" /> joined by <see cref="Path.PathSeparator" />.
	/// </summary>
	/// <param name="folders">The folders to search, in order. An empty string adds an empty entry.</param>
	public static void SetPath(params string[] folders)
	{
		Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, folders));
	}

	/// <summary>
	/// Sets <c>HOME</c> to <paramref name="folder" />.
	/// </summary>
	/// <param name="folder">The folder that <c>cd</c> and <c>~</c> should treat as the user's home.</param>
	/// <remarks>
	/// On Unix, <see cref="Environment.SpecialFolder.UserProfile" /> reads <c>HOME</c>, so this keeps tests out of the
	/// real home folder.
	/// </remarks>
	public static void SetHome(string folder)
	{
		Environment.SetEnvironmentVariable("HOME", folder);
	}

	/// <summary>
	/// Restores the original console output, <c>PATH</c>, <c>HOME</c>, and working directory, then deletes the
	/// temporary folder.
	/// </summary>
	public void Dispose()
	{
		Console.SetOut(this.originalOut);
		this.output.Dispose();
		Environment.SetEnvironmentVariable("PATH", this.originalPath);
		Environment.SetEnvironmentVariable("HOME", this.originalHome);

		// Restored before the delete, since a test may have moved into the temporary folder and the next test would
		// start in a folder that no longer exists.
		Directory.SetCurrentDirectory(this.originalDirectory);
		Directory.Delete(this.root, recursive: true);
	}
}

/// <summary>
/// Groups every test class that touches <see cref="Console.Out" />, <c>PATH</c>, <c>HOME</c>, or the working directory
/// so they run one at a time.
/// </summary>
/// <remarks>
/// All of them are process-wide. Two tests swapping them in parallel would read each other's output and folders.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalState
{
	/// <summary>
	/// The collection name that test classes pass to <see cref="CollectionAttribute" />.
	/// </summary>
	public const string Name = "Global state";
}
