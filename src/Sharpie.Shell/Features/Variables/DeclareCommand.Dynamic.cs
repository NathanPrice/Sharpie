using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Arithmetic;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// The variables whose value is worked out each time it's read, like <c>RANDOM</c> and <c>$$</c>.
/// </summary>
/// <remarks>
/// bash keeps these in its variable table with a function that gives the value on each read, so <c>set</c>,
/// <c>export</c> and <c>set -u</c> see them like any other variable. <see cref="Variables" /> does the same here.
/// </remarks>
internal partial class DeclareCommand
{
	/// <summary>
	/// <c>RANDOM</c> gives a number from 0 up to, but not including, this one, as bash's 15-bit generator does.
	/// </summary>
	private const int RandomLimit = 32768;

	/// <summary>
	/// The names whose value is worked out on read: <c>RANDOM</c>, <c>SECONDS</c>, <c>LINENO</c>, <c>$</c>,
	/// <c>PPID</c>, <c>-</c> and <c>!</c>.
	/// </summary>
	/// <remarks>
	/// <c>unset</c> takes a name out, after which it's an ordinary variable, as in bash.
	/// </remarks>
	private readonly HashSet<string> computed = ["RANDOM", "SECONDS", "LINENO", "$", "PPID", "-", "!"];

	/// <summary>
	/// The shell options, which <c>$-</c> lists.
	/// </summary>
	private readonly ShellSettings settings;

	/// <summary>
	/// The id of the process that started the shell, read once since bash's <c>PPID</c> doesn't follow a new parent.
	/// </summary>
	private readonly int parentId = NativeMethods.ParentProcessId();

	/// <summary>
	/// What <c>RANDOM</c> draws from, replaced with a seeded one by <c>RANDOM=N</c>.
	/// </summary>
	private Random random = new();

	/// <summary>
	/// The value <c>SECONDS</c> was last given, 0 when the shell started.
	/// </summary>
	private long secondsBase;

	/// <summary>
	/// When <c>SECONDS</c> was last given a value, in <see cref="Environment.TickCount64" /> milliseconds.
	/// </summary>
	private long secondsStart = Environment.TickCount64;

	/// <summary>
	/// Initializes a new instance of the <see cref="DeclareCommand" /> class.
	/// </summary>
	/// <param name="settings">The shell options, which <c>$-</c> lists.</param>
	public DeclareCommand(ShellSettings settings)
	{
		this.settings = settings;
		this.Variables = new View(this);
	}

	/// <summary>
	/// Gets the shell variables set so far, keyed by name, with the worked-out ones among them.
	/// </summary>
	/// <value>
	/// A live view, so a later <c>declare</c> shows up without fetching it again. <c>RANDOM</c>, <c>SECONDS</c>,
	/// <c>LINENO</c>, <c>$</c>, <c>PPID</c>, <c>-</c> and <c>!</c> give their value at the moment they're read.
	/// </value>
	public IReadOnlyDictionary<string, string> Variables { get; }

	/// <summary>
	/// Gets or sets the line of the script, <c>-c</c> text or typed input the running command starts on.
	/// </summary>
	/// <value>
	/// The line number, which <c>$LINENO</c> expands to. 0 before any command has run.
	/// </value>
	public int LineNumber { get; set; }

	/// <summary>
	/// Gets the shell's nesting level as the environment's <c>SHLVL</c> gives it.
	/// </summary>
	/// <value>
	/// The number, or 0 when <c>SHLVL</c> is unset, not a number, or below 0. A new shell exports one more with
	/// <see cref="RaiseShellLevel" />, so each nested shell sits a level deeper, as in bash.
	/// </value>
	public static int ShellLevel =>
		int.TryParse(Environment.GetEnvironmentVariable("SHLVL"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? Math.Max(level, 0) : 0;

	/// <summary>
	/// Exports <c>SHLVL</c> one above <see cref="ShellLevel" />, as a shell does when it starts.
	/// </summary>
	public void RaiseShellLevel() => this.Export("SHLVL", (ShellLevel + 1).ToString(CultureInfo.InvariantCulture));

	/// <summary>
	/// Saves which names are worked out on read, the <c>RANDOM</c> generator, the <c>SECONDS</c> count and
	/// <c>$!</c>, for a command substitution to put back.
	/// </summary>
	/// <returns>
	/// An action that puts them back, so an <c>unset RANDOM</c>, a <c>SECONDS=0</c> or a background program inside a
	/// <c>$(...)</c> doesn't reach the shell, as bash's subshell keeps them to itself.
	/// </returns>
	public Action SaveDynamic()
	{
		string[] names = [.. this.computed];
		var (generator, secondsBase, secondsStart, lastBackground) = (this.random, this.secondsBase, this.secondsStart, ExternalProgram.LastBackground);

		return () =>
		{
			this.computed.UnionWith(names);
			(this.random, this.secondsBase, this.secondsStart, ExternalProgram.LastBackground) = (generator, secondsBase, secondsStart, lastBackground);
		};
	}

	/// <summary>
	/// Gives the worked-out value of <paramref name="name" />.
	/// </summary>
	/// <param name="name">One of the names in <see cref="computed" />.</param>
	/// <param name="value">The value, or <see langword="null" /> when there's none.</param>
	/// <returns>
	/// <see langword="false" /> for <c>!</c> before any program ran in the background, which leaves it unset as bash
	/// does.
	/// </returns>
	[SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "RANDOM is bash's repeatable generator, which RANDOM=N seeds, and nothing secret comes from it.")]
	private bool TryCompute(string name, [NotNullWhen(true)] out string? value)
	{
		value = name switch
		{
			"RANDOM" => this.random.Next(RandomLimit).ToString(CultureInfo.InvariantCulture),
			"SECONDS" => (this.secondsBase + ((Environment.TickCount64 - this.secondsStart) / 1000)).ToString(CultureInfo.InvariantCulture),
			"LINENO" => this.LineNumber.ToString(CultureInfo.InvariantCulture),
			"$" => Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
			"PPID" => this.parentId.ToString(CultureInfo.InvariantCulture),
			"-" => this.settings.Flags,
			_ => ExternalProgram.LastBackground?.ToString(CultureInfo.InvariantCulture),
		};

		return value is not null;
	}

	/// <summary>
	/// Reseeds <c>RANDOM</c>, restarts <c>SECONDS</c> or moves <c>LINENO</c> for an assignment to one of them while
	/// it's still worked out on read. Does nothing for any other name.
	/// </summary>
	/// <param name="name">The variable name.</param>
	/// <param name="value">The value being assigned.</param>
	/// <remarks>
	/// A value that isn't a number counts as 0, as bash's arithmetic reads it. <see cref="Set" /> still stores the
	/// value, which <see cref="Variables" /> hides until <c>unset</c> takes the name off the worked-out ones, and an
	/// exported one reaches the environment as it is.
	/// </remarks>
	private void AssignComputed(string name, string value)
	{
		if (!this.computed.Contains(name) || name is not ("RANDOM" or "SECONDS" or "LINENO"))
		{
			return;
		}

		var number = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

		switch (name)
		{
			case "RANDOM":
				this.random = new Random((int)number);
				break;
			case "SECONDS":
				(this.secondsBase, this.secondsStart) = (number, Environment.TickCount64);
				break;
			default:
				this.LineNumber = (int)number;
				break;
		}
	}

	/// <summary>
	/// Stores a variable through the variables <see cref="CommandLine" /> expands from, as <c>${X:=value}</c> does.
	/// </summary>
	/// <param name="variables"><see cref="Variables" />, or a plain dictionary of variables.</param>
	/// <param name="name">A valid variable name, or one with a subscript for one element.</param>
	/// <param name="value">The value.</param>
	/// <exception cref="ArithmeticExpression.Failure">The subscript names no element.</exception>
	/// <remarks>
	/// Only a dictionary that can be written keeps the value; any other leaves it unchanged.
	/// </remarks>
	internal static void Assign(IReadOnlyDictionary<string, string> variables, string name, string value)
	{
		if (variables is View view)
		{
			view.Owner.Set(name, value);
		}
		else if (variables is IDictionary<string, string> plain)
		{
			plain[name] = value;
		}
	}

	/// <summary>
	/// The read-only view <see cref="Variables" /> gives: the stored variables, with the worked-out ones in place of
	/// any stored value of the same name.
	/// </summary>
	/// <param name="owner">The command whose variables it shows.</param>
	private sealed class View(DeclareCommand owner) : IReadOnlyDictionary<string, string>
	{
		/// <summary>
		/// Gets the command whose variables it shows.
		/// </summary>
		/// <value>
		/// The owner, which <see cref="Assign" /> stores through.
		/// </value>
		public DeclareCommand Owner => owner;

		/// <inheritdoc />
		public int Count => this.Keys.Count();

		/// <inheritdoc />
		public IEnumerable<string> Keys => this.Select(pair => pair.Key);

		/// <inheritdoc />
		public IEnumerable<string> Values => this.Select(pair => pair.Value);

		/// <inheritdoc />
		public string this[string key] => this.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"No variable named `{key}`");

		/// <inheritdoc />
		public bool ContainsKey(string key) => this.TryGetValue(key, out _);

		/// <inheritdoc />
		/// <remarks>
		/// An array gives its element 0, and a name with a subscript, as in <c>a[1]</c>, gives that element.
		/// </remarks>
		public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) =>
			owner.computed.Contains(key) ? owner.TryCompute(key, out value)
			: owner.arrays.TryGetValue(key, out var elements) ? elements.TryGetValue(0, out value)
			: TrySplitElement(key, out var name, out var subscript) ? owner.TryGetElement(name, subscript, out value)
			: owner.variables.TryGetValue(key, out value);

		/// <inheritdoc />
		public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
		{
			foreach (var pair in owner.variables.Where(pair => !owner.computed.Contains(pair.Key)))
			{
				yield return pair;
			}

			foreach (var name in owner.computed)
			{
				if (owner.TryCompute(name, out var value))
				{
					yield return new(name, value);
				}
			}

			foreach (var (name, elements) in owner.arrays)
			{
				if (elements.TryGetValue(0, out var first))
				{
					yield return new(name, first);
				}
			}
		}

		/// <inheritdoc />
		IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
	}
}
