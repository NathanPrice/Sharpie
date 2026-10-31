using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Functions;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// <c>declare -f</c> and <c>declare -F</c>, which list the shell's functions or test that names are functions.
/// </summary>
/// <remarks>
/// bash-completion scripts check for a function this way before they call it, as git's does before it runs
/// <c>_git_&lt;subcommand&gt;</c>.
/// </remarks>
internal partial class DeclareCommand
{
	/// <summary>
	/// The builtins and functions, by name, which <c>declare -f</c> reads the functions from.
	/// </summary>
	/// <remarks>
	/// <see langword="null" /> when the shell gave none, as in tests, and then there are no functions.
	/// </remarks>
	private readonly IReadOnlyDictionary<string, ICommand>? commands;

	/// <summary>
	/// Prints the functions in <paramref name="names" />, or every function when there are none.
	/// </summary>
	/// <param name="names">The function names, or none for every function.</param>
	/// <param name="definitions">
	/// <see langword="true" /> to print each function's definition, as <c>declare -f</c> does, or
	/// <see langword="false" /> for only its name, as <c>declare -F</c> does.
	/// </param>
	/// <returns>
	/// 0, or 1 when any name isn't a function. As in bash, nothing is printed for such a name, so
	/// <c>declare -F NAME &gt;/dev/null</c> is a quiet test.
	/// </returns>
	/// <remarks>
	/// With no names, <c>declare -F</c> prints <c>declare -f NAME</c> for each function, sorted, as bash does. A
	/// definition is the body as it was typed, where bash prints one it formats again from its parse tree. The shell
	/// keeps no parse tree to print from, and the typed body still reads back in through <c>eval</c>.
	/// </remarks>
	private int PrintFunctions(string[] names, bool definitions)
	{
		var functions = this.commands?.Values.OfType<FunctionCommand>() ?? [];

		if (names is [])
		{
			foreach (var function in functions.OrderBy(function => function.Name, StringComparer.Ordinal))
			{
				Console.WriteLine(definitions ? Definition(function) : $"{this.Name} -f {function.Name}");
			}

			return 0;
		}

		var status = 0;

		foreach (var name in names)
		{
			if (this.commands?.GetValueOrDefault(name) is FunctionCommand function)
			{
				Console.WriteLine(definitions ? Definition(function) : name);
			}
			else
			{
				status = 1;
			}
		}

		return status;
	}

	/// <summary>
	/// Writes <paramref name="function" /> as a definition that defines it again.
	/// </summary>
	/// <param name="function">The function.</param>
	/// <returns>
	/// <c>NAME () </c>, a newline, and the body, laid out as bash starts its own.
	/// </returns>
	private static string Definition(FunctionCommand function) => $"{function.Name} () \n{function.Body}";
}
