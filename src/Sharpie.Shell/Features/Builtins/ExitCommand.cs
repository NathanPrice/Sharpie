using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Ends the shell process with the status given, or the last command's status when none is.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which holds the last command's status.</param>
/// <param name="inSubshell">
/// Tells whether a command substitution is running, where <c>exit</c> ends only that, as in bash's subshell.
/// </param>
internal class ExitCommand(DeclareCommand declare, Func<bool>? inSubshell = null) : ICommand
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

	/// <inheritdoc />
	/// <exception cref="SubshellExit">A command substitution is running, which ends with the status.</exception>
	public int Run(string[] args)
	{
		var code = Code(args, declare.Status);

		if (inSubshell?.Invoke() == true)
		{
			throw new SubshellExit(code);
		}

		// Stryker disable once Statement : ending the process would end the test run, so the shell's process tests check it.
		Environment.Exit(code);

		return 0;
	}

	/// <summary>
	/// Ends the command substitution that ran <c>exit</c>, the way a subshell's process would end in bash.
	/// </summary>
	/// <param name="status">The status the substitution ends with.</param>
	[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only exit throws it, and always with a status.")]
	internal sealed class SubshellExit(int status) : Exception
	{
		/// <summary>
		/// Gets the status the substitution ends with.
		/// </summary>
		/// <value>
		/// The status <c>exit</c> worked out, already cut to eight bits.
		/// </value>
		public int Status => status;
	}
}
