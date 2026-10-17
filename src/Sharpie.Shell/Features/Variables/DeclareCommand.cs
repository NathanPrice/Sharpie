using System.Globalization;
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
	/// The positional parameters live here too, under <c>1</c>, <c>2</c> and so on, with their count under <c>#</c>, so
	/// <c>$1</c>, <c>${10}</c> and <c>$#</c> expand like any other variable.
	/// </remarks>
	private readonly Dictionary<string, string> variables = new() { ["#"] = "0" };

	/// <summary>
	/// Gets the shell variables set so far, keyed by name.
	/// </summary>
	/// <value>
	/// A live view, so a later <c>declare</c> shows up without fetching it again.
	/// </value>
	public IReadOnlyDictionary<string, string> Variables => this.variables;

	/// <summary>
	/// Gets or sets the last command's exit status.
	/// </summary>
	/// <value>
	/// The status, kept as the variable <c>?</c> so <c>$?</c> expands to it. 0 before any command has run.
	/// </value>
	public int Status
	{
		get => int.TryParse(this.variables.GetValueOrDefault("?"), CultureInfo.InvariantCulture, out var status) ? status : 0;
		set => this.Set("?", value.ToString(CultureInfo.InvariantCulture));
	}

	/// <summary>
	/// Gets the positional parameters, <c>$1</c> onwards.
	/// </summary>
	/// <value>
	/// The parameters in order, empty when there are none.
	/// </value>
	public IReadOnlyList<string> Positional => ReadPositional(this.variables);

	/// <inheritdoc />
	public string Name => "declare";
	/// <inheritdoc />
	public string Description => "Creates and inspects shell variables";

	/// <inheritdoc />
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var status = 0;

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
					new ShellError("sharpie::declare::not_found", $"No variable named `{name}`").Write();
					status = 1;
				}
			}

			return status;
		}

		foreach (var arg in args)
		{
			// Split on the first '=' only, so a value can itself contain '='
			var parts = arg.Split('=', 2);

			if (!IsIdentifier(parts[0]))
			{
				new ShellError("sharpie::declare::invalid_identifier", $"`{arg}` isn't a valid variable name") { Help = IdentifierHelp }.Write();
				status = 1;

				continue;
			}

			if (parts.Length == 2)
			{
				this.Set(parts[0], parts[1]);
			}
		}

		return status;
	}

	/// <summary>
	/// The hint shown when a variable name breaks the rule <see cref="IsIdentifier" /> checks.
	/// </summary>
	internal const string IdentifierHelp = "a name starts with a letter or _ and holds only letters, digits and _";

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
	/// Runs <paramref name="run" /> with <paramref name="assignments" /> set as shell variables, then puts the old
	/// values back.
	/// </summary>
	/// <param name="assignments">The <c>NAME=VALUE</c> words typed in front of a builtin.</param>
	/// <param name="run">Runs the builtin.</param>
	/// <returns>
	/// The status <paramref name="run" /> gives.
	/// </returns>
	/// <remarks>
	/// POSIX makes these variables for the builtin's run. They're written straight to the variables, not through
	/// <see cref="Set" />, which would also change the environment and leave the new value there. A name that wasn't
	/// set before is removed again afterwards.
	/// </remarks>
	public int RunWith(IReadOnlyList<string> assignments, Func<int> run)
	{
		_ = run.ArgumentNotNull();

		// Every old value is read before any is set, so A=1 A=2 still puts back the value from before both.
		var saved = assignments.ArgumentNotNull()
			.Select(assignment => assignment.Split('=', 2))
			.Select(parts => (Name: parts[0], Value: parts[1], Found: this.variables.TryGetValue(parts[0], out var old), Old: old))
			.ToArray();

		foreach (var (name, value, _, _) in saved)
		{
			this.variables[name] = value;
		}

		try
		{
			return run();
		}
		finally
		{
			foreach (var (name, _, found, old) in saved)
			{
				if (found)
				{
					this.variables[name] = old!;
				}
				else
				{
					_ = this.variables.Remove(name);
				}
			}
		}
	}

	/// <summary>
	/// Replaces the positional parameters, as <c>set --</c> does.
	/// </summary>
	/// <param name="values">The new parameters, which become <c>$1</c> onwards.</param>
	/// <remarks>
	/// Written straight into the variables and never the environment, since no program could read a variable named
	/// <c>1</c> or <c>#</c>.
	/// </remarks>
	public void SetPositional(IReadOnlyList<string> values)
	{
		_ = values.ArgumentNotNull();

		for (var number = values.Count + 1; number <= this.Positional.Count; number++)
		{
			_ = this.variables.Remove(number.ToString(CultureInfo.InvariantCulture));
		}

		for (var index = 0; index < values.Count; index++)
		{
			this.variables[(index + 1).ToString(CultureInfo.InvariantCulture)] = values[index];
		}

		this.variables["#"] = values.Count.ToString(CultureInfo.InvariantCulture);
	}

	/// <summary>
	/// Reads the positional parameters out of a set of shell variables.
	/// </summary>
	/// <param name="variables">The shell variables, which hold the count under <c>#</c>.</param>
	/// <returns>
	/// <c>$1</c> onwards, in order, or none when <c>#</c> is missing.
	/// </returns>
	public static IReadOnlyList<string> ReadPositional(IReadOnlyDictionary<string, string> variables)
	{
		_ = variables.ArgumentNotNull();

		var count = int.TryParse(variables.GetValueOrDefault("#"), CultureInfo.InvariantCulture, out var number) ? number : 0;

		return [.. Enumerable.Range(1, count).Select(index => variables.GetValueOrDefault(index.ToString(CultureInfo.InvariantCulture), string.Empty))];
	}

	/// <summary>
	/// Stores the variable <paramref name="name" /> and puts it in the environment, as <c>export NAME=VALUE</c> does.
	/// </summary>
	/// <param name="name">A valid variable name.</param>
	/// <param name="value">The value.</param>
	public void Export(string name, string value)
	{
		Environment.SetEnvironmentVariable(name, value);
		this.Set(name, value);
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
