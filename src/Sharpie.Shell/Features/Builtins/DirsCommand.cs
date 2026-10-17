using System.Globalization;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.LineEditing;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Shows or clears the folder stack that <c>pushd</c> and <c>popd</c> keep.
/// </summary>
/// <remarks>
/// The list starts with the current folder, then the saved folders, the most recent first, as in bash. The home folder
/// shows as <c>~</c> unless <c>-l</c> is given. bash's <c>+N</c> and <c>-N</c>, which pick one entry, aren't
/// supported.
/// </remarks>
internal sealed class DirsCommand : ICommand
{
	/// <inheritdoc />
	public string Name => "dirs";

	/// <inheritdoc />
	public string Description => "Shows the folder stack that pushd and popd keep";

	/// <summary>
	/// Gets the saved folders as full paths, the most recent first. The current folder isn't in it.
	/// </summary>
	internal List<string> Stack { get; } = [];

	/// <inheritdoc />
	/// <remarks>
	/// <c>-c</c> clears the stack and prints nothing, <c>-v</c> numbers the folders one per line, <c>-p</c> puts one per
	/// line without numbers, and <c>-l</c> shows the home folder in full. The status is 0, or 2 for anything else, as
	/// bash gives.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var options = args is ["--", .. var rest] ? rest : args;

		if (options.FirstOrDefault(option => option is not ("-c" or "-l" or "-p" or "-v")) is { } unknown)
		{
			new ShellError("sharpie::dirs::unknown_option", $"Unknown option `{unknown}`") { Help = "dirs takes -c, -l, -p and -v" }.Write();

			return 2;
		}

		if (options.Contains("-c"))
		{
			this.Stack.Clear();

			return 0;
		}

		this.Print(numbered: options.Contains("-v"), onePerLine: options.Contains("-p"), fullPaths: options.Contains("-l"));

		return 0;
	}

	/// <summary>
	/// Writes the current folder and then the stack to standard output, the way <c>dirs</c> shows them.
	/// </summary>
	/// <param name="numbered">Whether to put each folder on its own line after its position, as <c>dirs -v</c> does.</param>
	/// <param name="onePerLine">Whether to put each folder on its own line, as <c>dirs -p</c> does.</param>
	/// <param name="fullPaths">Whether to keep the home folder in full instead of showing it as <c>~</c>.</param>
	internal void Print(bool numbered = false, bool onePerLine = false, bool fullPaths = false)
	{
		var folders = new[] { Directory.GetCurrentDirectory() }.Concat(this.Stack)
			.Select(folder => fullPaths ? folder : PromptHeader.ShortenHome(folder));

		if (numbered)
		{
			// bash's format: the position right-aligned in two columns, then two spaces.
			folders = folders.Select((folder, index) => string.Create(CultureInfo.InvariantCulture, $"{index,2}  {folder}"));
		}

		Console.WriteLine(string.Join(numbered || onePerLine ? Environment.NewLine : " ", folders));
	}
}
