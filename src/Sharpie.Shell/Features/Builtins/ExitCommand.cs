using System.Globalization;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Ends the shell process with the status given, or the last command's status when none is.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which holds the last command's status.</param>
internal class ExitCommand(DeclareCommand declare) : ICommand
{
	/// <inheritdoc />
	public string Name => "exit";
	/// <inheritdoc />
	public string Description => "Closes the app";

	/// <summary>
	/// Works out the status <c>exit</c> ends the shell with, as POSIX defines it.
	/// </summary>
	/// <param name="args">The words after <c>exit</c>.</param>
	/// <param name="status">The last command's status.</param>
	/// <returns>
	/// <paramref name="status" /> with no argument, the number's low eight bits with one, or 2 after printing an error
	/// when the argument isn't a number.
	/// </returns>
	/// <remarks>
	/// A process can only report eight bits, so <c>exit 256</c> gives 0 and <c>exit -1</c> gives 255, as in bash.
	/// </remarks>
	internal static int Code(string[] args, int status)
	{
		_ = args.ArgumentNotNull();

		if (args is [])
		{
			return status;
		}

		if (int.TryParse(args[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code))
		{
			return code & 0xFF;
		}

		new ShellError("sharpie::exit::not_a_number", $"`{args[0]}` isn't a number") { Help = "exit takes a whole number as its status" }.Write();

		return 2;
	}

	// Stryker disable once Block,Statement : ending the process would end the test run, so the shell's process tests check it.
	/// <inheritdoc />
	public int Run(string[] args)
	{
		Environment.Exit(Code(args, declare.Status));

		return 0;
	}
}
