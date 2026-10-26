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
	/// <param name="name">The builtin the status is for, <c>exit</c> or <c>return</c>, which the error names.</param>
	/// <returns>
	/// <paramref name="status" /> with no argument, or the number's low eight bits. <see langword="null" /> after
	/// printing an error when the first argument isn't a number, where bash 5.3 gives 2 and stays in the shell.
	/// </returns>
	/// <exception cref="CommandLine.ExpansionError">
	/// A number is followed by more arguments, which skips the rest of the line, as bash does.
	/// </exception>
	/// <remarks>
	/// A process can only report eight bits, so <c>exit 256</c> gives 0 and <c>exit -1</c> gives 255, as in bash. One
	/// leading <c>--</c> is skipped.
	/// </remarks>
	internal static int? Code(string[] args, int status, string name = "exit")
	{
		_ = args.ArgumentNotNull();

		args = args is ["--", .. var rest] ? rest : args;

		if (args is [])
		{
			return status;
		}

		if (!int.TryParse(args[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code))
		{
			new ShellError($"sharpie::{name}::not_a_number", $"`{args[0]}` isn't a number") { Help = $"{name} takes a whole number as its status" }.Write();

			return null;
		}

		return args is [_] ? code & 0xFF : throw new CommandLine.ExpansionError(new ShellError($"sharpie::{name}::too_many_arguments", $"`{name}` takes at most one status"), fatal: false);
	}

	/// <inheritdoc />
	/// <exception cref="SubshellExit">A command substitution is running, which ends with the status.</exception>
	/// <remarks>
	/// A first argument that isn't a number gives 2 after an error and leaves the shell running, as bash 5.3 does.
	/// </remarks>
	public int Run(string[] args)
	{
		if (Code(args, declare.Status) is not { } code)
		{
			return 2;
		}

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
	/// <remarks>
	/// <c>return</c>'s signal derives from it, so a substitution that a <c>return</c> reaches ends with its status too.
	/// </remarks>
	[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only exit and return throw it, and always with a status.")]
	internal class SubshellExit(int status) : Exception
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
