using System.Globalization;
using System.Text.RegularExpressions;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Arithmetic;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// Creates and inspects shell variables.
/// </summary>
/// <remarks>
/// <c>declare NAME=VALUE</c> stores or overwrites a variable, and <c>declare -p NAME</c> prints it as
/// <c>declare -- NAME="VALUE"</c>. A name must start with a letter or underscore and contain only letters, digits
/// and underscores; anything else is reported as not a valid identifier and nothing is stored. <c>declare -a NAME</c>
/// makes an array, and <c>declare -p</c> prints one as <c>declare -a NAME=([0]="a" [1]="b")</c>.
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
	/// One scope for each function that's running, innermost on top, holding each name <c>local</c> made in it, in
	/// order, with its shell value, environment value and array from before, <see langword="null" /> where it had none.
	/// </summary>
	private readonly Stack<List<Local>> scopes = new();

	/// <summary>
	/// Gets how many functions are running, one inside another.
	/// </summary>
	/// <value>
	/// 0 outside any function.
	/// </value>
	public int FunctionDepth => this.scopes.Count;

	/// <summary>
	/// Gets or sets how many loops are running around the current command, which <c>break</c> and <c>continue</c>
	/// can leave.
	/// </summary>
	/// <value>
	/// 0 outside any loop. Each function call starts again from 0 and puts the caller's count back after, as in bash,
	/// so a <c>break</c> in a function can't leave a loop around the call.
	/// </value>
	public int LoopDepth { get; set; }

	/// <summary>
	/// Gets how many names the innermost running function has made local so far.
	/// </summary>
	/// <value>
	/// 0 outside any function. A command substitution reads it first and hands it to <see cref="Restore" />.
	/// </value>
	public int LocalCount => this.scopes.TryPeek(out var scope) ? scope.Count : 0;

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
	/// <remarks>
	/// The options <c>-p</c>, <c>-a</c>, <c>-f</c> and <c>-F</c> can be joined, as in <c>-fF</c>, and <c>--</c> ends
	/// them. Any other word starting with <c>-</c> is taken as a name, and reported as not a valid one.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		var options = new HashSet<char>();
		var first = 0;

		for (; first < args.Length && args[first] is ['-', _, ..] option; first++)
		{
			if (option == "--")
			{
				first++;

				break;
			}

			if (!option[1..].All(letter => letter is 'p' or 'a' or 'f' or 'F'))
			{
				break;
			}

			options.UnionWith(option[1..]);
		}

		var names = args[first..];

		// bash lets -F win over -f, and either over -p.
		return options.Contains('F') ? this.PrintFunctions(names, definitions: false)
			: options.Contains('f') ? this.PrintFunctions(names, definitions: true)
			: options.Contains('p') ? this.Print(names)
			: this.AssignAll(names, array: options.Contains('a'));
	}

	/// <summary>
	/// Prints each variable in <paramref name="names" /> as a <c>declare</c> line that sets it again, as
	/// <c>declare -p</c> does.
	/// </summary>
	/// <param name="names">The variable names.</param>
	/// <returns>
	/// 0, or 1 when any name isn't set.
	/// </returns>
	private int Print(string[] names)
	{
		var status = 0;

		foreach (var name in names)
		{
			if (this.arrays.ContainsKey(name))
			{
				Console.WriteLine($"{this.Name} -a {name}={this.FormatArray(name)}");
			}
			else if (this.Variables.TryGetValue(name, out var value))
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

	/// <summary>
	/// Carries out the names and <c>NAME=VALUE</c> words of a <c>declare</c> with no printing option.
	/// </summary>
	/// <param name="args">The words after the options.</param>
	/// <param name="array"><see langword="true" /> for <c>declare -a</c>, which makes each name an array.</param>
	/// <returns>
	/// 0, or 1 when any word isn't a valid name or assignment.
	/// </returns>
	private int AssignAll(string[] args, bool array)
	{
		var status = 0;

		foreach (var arg in args)
		{
			if (!IsIdentifier(arg) && !IsAssignment(arg))
			{
				new ShellError("sharpie::declare::invalid_identifier", $"`{arg}` isn't a valid variable name") { Help = IdentifierHelp }.Write();
				status = 1;
			}
			else if (IsIdentifier(arg))
			{
				// declare NAME on its own changes nothing, though declare -a NAME makes it an array.
				if (array)
				{
					this.MakeArray(arg);
				}
			}
			else
			{
				status = Math.Max(status, this.AssignWord(arg, compound: null));
			}
		}

		return status;
	}

	/// <summary>
	/// Carries out one <c>NAME=VALUE</c> word, as a line of only such words or <c>declare</c> and <c>local</c> do.
	/// </summary>
	/// <param name="word">The word, which <see cref="IsAssignment" /> accepts.</param>
	/// <param name="compound">
	/// <see langword="true" /> when the value is a <c>(...)</c> list the shell read, <see langword="false" /> when it's
	/// plain text, or <see langword="null" /> to take a value in parentheses as a list, as bash's <c>declare</c> does.
	/// </param>
	/// <returns>
	/// 0, or 1 after reporting a subscript that names no element.
	/// </returns>
	/// <remarks>
	/// <c>NAME+=VALUE</c> adds to the end of the value, <c>NAME=(...)</c> sets an array, <c>NAME+=(...)</c> adds to
	/// one, and <c>NAME[i]=VALUE</c> sets one element, as in bash.
	/// </remarks>
	public int AssignWord(string word, bool? compound)
	{
		_ = TryParseAssignment(word, out var assignment);

		var (name, subscript, append, value) = assignment;

		try
		{
			if (subscript is null && (compound ?? value is ['(', .., ')']))
			{
				this.SetArray(name, value[1..^1], append);
			}
			else
			{
				var target = assignment.Target;

				this.Set(target, append ? CommandLine.Lookup(this.Variables, target) + value : value);
			}

			return 0;
		}
		catch (ArithmeticExpression.Failure failure)
		{
			failure.Error.Write();

			return 1;
		}
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
	/// <see langword="true" /> when the text before the first <c>=</c> is a valid name, as
	/// <see cref="TryParseAssignment" /> describes.
	/// </returns>
	public static bool IsAssignment(string word) => TryParseAssignment(word, out _);

	/// <summary>
	/// Stores or overwrites the variable <paramref name="name" />.
	/// </summary>
	/// <param name="name">A valid variable name, or one with a subscript, as in <c>a[1]</c>, for one element.</param>
	/// <param name="value">The value.</param>
	/// <exception cref="ArithmeticExpression.Failure">The subscript names no element.</exception>
	/// <remarks>
	/// A name that is already in the environment gets the new value there too, so <c>PATH=...</c> reaches the programs
	/// the shell starts, as it does in a POSIX shell where an exported variable stays exported. An array's plain name
	/// sets its element 0, as in bash.
	/// </remarks>
	public void Set(string name, string value)
	{
		_ = value.ArgumentNotNull();

		if (TrySplitElement(name.ArgumentNotNull(), out var array, out var subscript))
		{
			this.SetElement(array, subscript, value);

			return;
		}

		if (this.arrays.TryGetValue(name, out var elements))
		{
			elements[0] = value;

			return;
		}

		this.AssignComputed(name, value);
		this.variables[name] = value;

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
	/// Runs a function's body with <paramref name="arguments" /> as the positional parameters, then puts back the
	/// caller's and every variable <see cref="Localize" /> saved.
	/// </summary>
	/// <param name="arguments">The words after the function's name, which become <c>$1</c> onwards.</param>
	/// <param name="run">Runs the body.</param>
	/// <returns>
	/// The status <paramref name="run" /> gives.
	/// </returns>
	/// <remarks>
	/// <c>$0</c> stays the shell's, as in bash. A local's old value goes back into the environment too, so an
	/// exported variable the function made local is exported again with its old value.
	/// </remarks>
	public int RunFunction(IReadOnlyList<string> arguments, Func<int> run)
	{
		_ = run.ArgumentNotNull();

		var caller = this.Positional;
		var loops = this.LoopDepth;
		var scope = new List<Local>();

		this.SetPositional(arguments);
		this.scopes.Push(scope);
		this.LoopDepth = 0;

		try
		{
			return run();
		}
		finally
		{
			this.LoopDepth = loops;
			_ = this.scopes.Pop();

			foreach (var (name, value, exported, array) in scope)
			{
				if (value is null)
				{
					_ = this.variables.Remove(name);
				}
				else
				{
					this.variables[name] = value;
				}

				if (array is null)
				{
					_ = this.arrays.Remove(name);
				}
				else
				{
					this.arrays[name] = array;
				}

				// Only when it changed, since setting an empty value would take the variable out of the environment.
				if (Environment.GetEnvironmentVariable(name) != exported)
				{
					Environment.SetEnvironmentVariable(name, exported);
				}
			}

			this.SetPositional(caller);
		}
	}

	/// <summary>
	/// Saves the variable <paramref name="name" />, for the function that's running to put back when it returns.
	/// </summary>
	/// <param name="name">A valid variable name.</param>
	/// <returns>
	/// <see langword="true" /> when it was saved, or <see langword="false" /> when this function already made it local.
	/// </returns>
	/// <exception cref="InvalidOperationException">No function is running.</exception>
	public bool Localize(string name)
	{
		_ = name.ArgumentNotNull();

		var scope = this.scopes.Peek();

		if (scope.Exists(local => local.Name == name))
		{
			return false;
		}

		var array = this.arrays.TryGetValue(name, out var elements) ? new SortedDictionary<long, string>(elements) : null;

		scope.Add(new Local(name, this.variables.GetValueOrDefault(name), Environment.GetEnvironmentVariable(name), array));

		return true;
	}

	/// <summary>
	/// Gets or sets how many times <c>set</c> has replaced the positional parameters outside any function.
	/// </summary>
	/// <value>
	/// A count that only goes up, except that a subshell puts it back with the parameters. <c>.</c> compares it before
	/// and after a file to keep the file's parameters, as bash does.
	/// </value>
	public int PositionalSetCount { get; set; }

	/// <summary>
	/// Replaces the positional parameters for the <c>set</c> builtin.
	/// </summary>
	/// <param name="values">The new parameters, which become <c>$1</c> onwards.</param>
	/// <remarks>
	/// Counted only outside a function, since the function puts the caller's parameters back when it returns anyway.
	/// </remarks>
	public void SetPositionalFromSet(IReadOnlyList<string> values)
	{
		this.SetPositional(values);

		if (this.FunctionDepth == 0)
		{
			// Stryker disable once Update : . only checks that the count changed, so counting down works the same.
			this.PositionalSetCount++;
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
	/// <param name="name">
	/// The variable name, or one with a subscript, as in <c>a[1]</c>, for one element. A name that isn't set is
	/// ignored.
	/// </param>
	/// <exception cref="ArithmeticExpression.Failure">The subscript names no element.</exception>
	public void Remove(string name)
	{
		if (TrySplitElement(name.ArgumentNotNull(), out var array, out var subscript))
		{
			this.RemoveElement(array, subscript);

			return;
		}

		_ = this.variables.Remove(name);
		_ = this.arrays.Remove(name);
		_ = this.computed.Remove(name);
		Environment.SetEnvironmentVariable(name, null);
	}

	/// <summary>
	/// Replaces every shell variable and array with the ones in <paramref name="saved" />.
	/// </summary>
	/// <param name="saved">A copy <see cref="Save" /> took earlier.</param>
	/// <param name="locals">
	/// <see cref="LocalCount" /> from when <paramref name="saved" /> was taken, or <see langword="null" /> to keep every
	/// local.
	/// </param>
	/// <remarks>
	/// The environment is left alone, so a caller that wants it back puts it back itself. With
	/// <paramref name="locals" />, the names made local since are forgotten too. A command substitution passes it, so
	/// a <c>local</c> inside <c>$(...)</c> doesn't make the variable local to the function around it, whose later
	/// changes then stay when it returns, as in bash.
	/// </remarks>
	public void Restore(Snapshot saved, int? locals = null)
	{
		this.variables.Clear();
		this.arrays.Clear();

		foreach (var (name, value) in saved.ArgumentNotNull().Variables)
		{
			this.variables[name] = value;
		}

		foreach (var (name, elements) in saved.Arrays)
		{
			this.arrays[name] = new(elements);
		}

		if (locals is { } count && this.scopes.TryPeek(out var scope))
		{
			scope.RemoveRange(count, scope.Count - count);
		}
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

	/// <summary>
	/// A name <c>local</c> made in a running function, with what it held before.
	/// </summary>
	/// <param name="Name">The name.</param>
	/// <param name="Value">Its shell value, or <see langword="null" /> when it had none.</param>
	/// <param name="Exported">Its environment value, or <see langword="null" /> when it had none.</param>
	/// <param name="Array">Its elements when it was an array, or <see langword="null" />.</param>
	private sealed record Local(string Name, string? Value, string? Exported, SortedDictionary<long, string>? Array);
}
