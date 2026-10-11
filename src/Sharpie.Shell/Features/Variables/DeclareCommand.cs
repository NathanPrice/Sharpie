using System.Text.RegularExpressions;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// Creates and inspects shell variables.
/// </summary>
/// <remarks>
/// <c>declare NAME=VALUE</c> stores or overwrites a variable, and <c>declare -p NAME</c> prints it as
/// <c>declare -- NAME="VALUE"</c>. A name must start with a letter or underscore and contain only letters, digits
/// and underscores; anything else is reported as not a valid identifier and nothing is stored.
/// </remarks>
internal partial class DeclareCommand : ICommand
{
	/// <summary>
	/// The shell variables set so far, keyed by name.
	/// </summary>
	/// <remarks>
	/// Kept on the command itself because the shell creates one <see cref="DeclareCommand" /> for the whole session.
	/// </remarks>
	private readonly Dictionary<string, string> variables = [];

	/// <summary>
	/// Gets the shell variables set so far, keyed by name.
	/// </summary>
	/// <value>
	/// A live view, so a later <c>declare</c> shows up without fetching it again.
	/// </value>
	public IReadOnlyDictionary<string, string> Variables => this.variables;

	/// <inheritdoc />
	public string Name => "declare";
	/// <inheritdoc />
	public string Description => "Creates and inspects shell variables";

	/// <inheritdoc />
	public void Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (args is ["-p", .. var names])
		{
			foreach (var name in names)
			{
				if (this.variables.TryGetValue(name, out var value))
				{
					Console.WriteLine($@"{this.Name} -- {name}=""{value}""");
				}
				else
				{
					Console.Error.WriteLine($"{this.Name}: {name}: not found");
				}
			}

			return;
		}

		foreach (var arg in args)
		{
			// Split on the first '=' only, so a value can itself contain '='
			var parts = arg.Split('=', 2);

			if (!IsIdentifier(parts[0]))
			{
				Console.Error.WriteLine($"{this.Name}: `{arg}': not a valid identifier");
				continue;
			}

			if (parts.Length == 2)
			{
				this.Set(parts[0], parts[1]);
			}
		}
	}

	/// <summary>
	/// Checks whether <paramref name="name" /> is a valid shell variable name.
	/// </summary>
	/// <param name="name">The name to check.</param>
	/// <returns>
	/// <see langword="true" /> when it is a letter or underscore followed by any letters, digits or underscores.
	/// </returns>
	public static bool IsIdentifier(string name) => IdentifierRegex().IsMatch(name.ArgumentNotNull());

	/// <summary>
	/// Checks whether <paramref name="word" /> is a POSIX assignment such as <c>NAME=VALUE</c>.
	/// </summary>
	/// <param name="word">One word of a command line.</param>
	/// <returns>
	/// <see langword="true" /> when the text before the first <c>=</c> is a valid name.
	/// </returns>
	public static bool IsAssignment(string word) => word.ArgumentNotNull().Split('=', 2) is [var name, _] && IsIdentifier(name);

	/// <summary>
	/// Stores or overwrites the variable <paramref name="name" />.
	/// </summary>
	/// <param name="name">A valid variable name.</param>
	/// <param name="value">The value.</param>
	/// <remarks>
	/// A name that is already in the environment gets the new value there too, so <c>PATH=...</c> reaches the programs
	/// the shell starts, as it does in a POSIX shell where an exported variable stays exported.
	/// </remarks>
	public void Set(string name, string value)
	{
		_ = value.ArgumentNotNull();

		this.variables[name.ArgumentNotNull()] = value;

		if (Environment.GetEnvironmentVariable(name) is not null)
		{
			Environment.SetEnvironmentVariable(name, value);
		}
	}

	/// <summary>
	/// Removes the variable <paramref name="name" /> from the shell and from the environment.
	/// </summary>
	/// <param name="name">The variable name. A name that isn't set is ignored.</param>
	public void Remove(string name)
	{
		_ = this.variables.Remove(name.ArgumentNotNull());
		Environment.SetEnvironmentVariable(name, null);
	}

	/// <summary>
	/// Matches a valid shell variable name.
	/// </summary>
	/// <returns>
	/// A compiled regex that matches a letter or underscore followed by any letters, digits or underscores.
	/// </returns>
	/// <remarks>
	/// Ends with <c>\z</c> rather than <c>$</c>, because <c>$</c> would also accept a name with a trailing newline.
	/// </remarks>
	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z")]
	private static partial Regex IdentifierRegex();
}
