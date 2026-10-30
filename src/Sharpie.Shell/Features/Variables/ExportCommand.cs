using System.Collections;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// Puts shell variables into the environment of the programs the shell starts.
/// </summary>
/// <param name="declare">The <c>declare</c> builtin, which holds the shell variables.</param>
/// <remarks>
/// <c>export NAME=VALUE</c> sets the variable and exports it, <c>export NAME</c> exports a variable set earlier, and
/// <c>export</c> or <c>export -p</c> lists every exported variable as <c>export NAME='VALUE'</c>, which POSIX requires
/// to be readable as input again. <c>export NAME+=VALUE</c> adds to the end of the value first.
/// </remarks>
internal sealed class ExportCommand(DeclareCommand declare) : ICommand
{
	/// <inheritdoc />
	public string Name => "export";

	/// <inheritdoc />
	public string Description => "Passes shell variables to the programs the shell starts";

	/// <inheritdoc />
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args is [] or ["-p"])
		{
			foreach (var entry in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().OrderBy(entry => (string)entry.Key, StringComparer.Ordinal))
			{
				Console.WriteLine($"{this.Name} {entry.Key}='{((string)entry.Value!).Replace("'", @"'\''", StringComparison.Ordinal)}'");
			}

			return 0;
		}

		var status = 0;

		foreach (var arg in args)
		{
			if (DeclareCommand.TryParseAssignment(arg, out var assignment) && assignment.Subscript is null)
			{
				var (name, _, append, value) = assignment;

				declare.Export(name, append ? CommandLine.Lookup(declare.Variables, name) + value : value);
			}
			else if (!DeclareCommand.IsIdentifier(arg))
			{
				new ShellError("sharpie::export::invalid_identifier", $"`{arg}` isn't a valid variable name") { Help = DeclareCommand.IdentifierHelp }.Write();
				status = 1;
			}
			else if (declare.Variables.TryGetValue(arg, out var value))
			{
				Environment.SetEnvironmentVariable(arg, value);
			}
		}

		return status;
	}
}
