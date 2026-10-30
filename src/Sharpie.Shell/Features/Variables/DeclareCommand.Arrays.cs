using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Arithmetic;

namespace Sharpie.Shell.Features.Variables;

/// <summary>
/// One <c>NAME=VALUE</c> word taken apart.
/// </summary>
/// <param name="Name">The variable name.</param>
/// <param name="Subscript">The text between the brackets of <c>NAME[subscript]=VALUE</c>, or <see langword="null" />.</param>
/// <param name="Append"><see langword="true" /> for <c>+=</c>, which adds to the value instead of replacing it.</param>
/// <param name="Value">The text after the <c>=</c>.</param>
internal readonly record struct Assignment(string Name, string? Subscript, bool Append, string Value)
{
	/// <summary>
	/// Gets the name the value goes to.
	/// </summary>
	/// <value>
	/// <see cref="Name" />, or <c>NAME[subscript]</c> for one element, as <see cref="DeclareCommand.Set" /> takes it.
	/// </value>
	public string Target => this.Subscript is null ? this.Name : $"{this.Name}[{this.Subscript}]";
}

/// <content>
/// Indexed arrays, which bash keeps sparse: each element sits under a whole number, and only the ones given a value
/// exist.
/// </content>
internal partial class DeclareCommand
{
	/// <summary>
	/// The arrays, by name, with their elements in index order.
	/// </summary>
	/// <remarks>
	/// A name is either here or in <see cref="variables" />, never both. <c>$name</c> reads element 0, as in bash, and
	/// a plain <c>name=value</c> sets it.
	/// </remarks>
	private readonly Dictionary<string, SortedDictionary<long, string>> arrays = [];

	/// <summary>
	/// Takes apart a <c>NAME=VALUE</c>, <c>NAME+=VALUE</c> or <c>NAME[subscript]=VALUE</c> word.
	/// </summary>
	/// <param name="word">One word of a command line.</param>
	/// <param name="assignment">The parts, when it is one.</param>
	/// <returns>
	/// <see langword="true" /> when the text before the <c>=</c> is a valid name, with an optional subscript whose
	/// brackets pair up and an optional <c>+</c>.
	/// </returns>
	public static bool TryParseAssignment(string word, out Assignment assignment)
	{
		assignment = default;

		var end = 0;

		while (end < word.ArgumentNotNull().Length && (char.IsAsciiLetter(word[end]) || word[end] == '_' || (end > 0 && char.IsAsciiDigit(word[end]))))
		{
			end++;
		}

		var name = word[..end];
		string? subscript = null;

		if (end > 0 && end < word.Length && word[end] == '[')
		{
			var close = SubscriptEnd(word, end);

			if (close < 0)
			{
				return false;
			}

			subscript = word[(end + 1)..close];
			end = close + 1;
		}

		var append = end < word.Length && word[end] == '+';

		end += append ? 1 : 0;

		if (name.Length == 0 || end >= word.Length || word[end] != '=')
		{
			return false;
		}

		assignment = new Assignment(name, subscript, append, word[(end + 1)..]);

		return true;
	}

	/// <summary>
	/// Gives a <c>NAME=VALUE</c> word typed in front of a command as it goes into the command's environment.
	/// </summary>
	/// <param name="word">The word, which <see cref="IsAssignment" /> accepts.</param>
	/// <returns>
	/// <c>NAME=VALUE</c>, with the old value in front for <c>NAME+=VALUE</c>, or <see langword="null" /> for an element
	/// or a list, which no environment can hold.
	/// </returns>
	public string? ForEnvironment(string word)
	{
		_ = TryParseAssignment(word, out var assignment);

		var (name, subscript, append, value) = assignment;

		return subscript is not null ? null
			: append ? $"{name}={CommandLine.Lookup(this.Variables, name)}{value}"
			: word;
	}

	/// <summary>
	/// Finds the <c>]</c> that closes the <c>[</c> at <paramref name="open" />.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="open">The position of the <c>[</c>.</param>
	/// <returns>
	/// The position of the <c>]</c>, counting the brackets nested inside, as in <c>a[${b[1]}]</c>, or -1 when there is
	/// none.
	/// </returns>
	internal static int SubscriptEnd(string text, int open)
	{
		var depth = 0;

		for (var index = open; index < text.ArgumentNotNull().Length; index++)
		{
			depth += text[index] switch
			{
				'[' => 1,
				']' => -1,
				_ => 0,
			};

			if (depth == 0)
			{
				return index;
			}
		}

		return -1;
	}

	/// <summary>
	/// Takes apart a name with a subscript, such as <c>a[1]</c>.
	/// </summary>
	/// <param name="key">The text.</param>
	/// <param name="name">The name before the <c>[</c>.</param>
	/// <param name="subscript">The text between the brackets.</param>
	/// <returns>
	/// <see langword="true" /> when <paramref name="key" /> is a valid name followed by one subscript.
	/// </returns>
	public static bool TrySplitElement(string key, out string name, out string subscript)
	{
		var open = key.ArgumentNotNull().IndexOf('[', StringComparison.Ordinal);

		if (open > 0 && SubscriptEnd(key, open) == key.Length - 1 && IsIdentifier(key[..open]))
		{
			(name, subscript) = (key[..open], key[(open + 1)..^1]);

			return true;
		}

		(name, subscript) = (string.Empty, string.Empty);

		return false;
	}

	/// <summary>
	/// Gets the elements of <paramref name="name" /> from the variables <see cref="CommandLine" /> expands from.
	/// </summary>
	/// <param name="variables"><see cref="Variables" />, or a plain dictionary of variables.</param>
	/// <param name="name">A variable name.</param>
	/// <returns>
	/// The elements in index order. A variable that isn't an array is one element at 0, as in bash, and one that
	/// isn't set has none.
	/// </returns>
	internal static IReadOnlyList<KeyValuePair<long, string>> Elements(IReadOnlyDictionary<string, string> variables, string name) =>
		variables.ArgumentNotNull() is View view && view.Owner.arrays.TryGetValue(name, out var elements) ? [.. elements]
		: CommandLine.IsSet(variables, name) ? [new(0, CommandLine.Lookup(variables, name))]
		: [];

	/// <summary>
	/// Sets an array through the variables <see cref="CommandLine" /> expands from, as <c>[[ =~ ]]</c> sets
	/// <c>BASH_REMATCH</c>.
	/// </summary>
	/// <param name="variables"><see cref="Variables" />, or a plain dictionary of variables, which keeps nothing.</param>
	/// <param name="name">A valid variable name.</param>
	/// <param name="values">The elements, from index 0.</param>
	internal static void AssignArray(IReadOnlyDictionary<string, string> variables, string name, IReadOnlyList<string> values)
	{
		if (variables is View view)
		{
			view.Owner.SetArray(name, string.Join(' ', values.Select(CommandLine.Quote)), append: false);
		}
	}

	/// <summary>
	/// Checks whether <paramref name="name" /> is an array.
	/// </summary>
	/// <param name="name">A variable name.</param>
	/// <returns>
	/// <see langword="true" /> once it's been made one, with <c>name=(...)</c>, <c>name[i]=value</c> or
	/// <c>declare -a</c>, until <c>unset</c> takes it away.
	/// </returns>
	public bool IsArray(string name) => this.arrays.ContainsKey(name);

	/// <summary>
	/// Sets an array from the list of a compound assignment, as <c>name=(a b c)</c> does.
	/// </summary>
	/// <param name="name">A valid variable name.</param>
	/// <param name="list">The text between the parentheses, its words already expanded and quoted.</param>
	/// <param name="append">
	/// <see langword="true" /> for <c>name+=(...)</c>, which adds the words after the last element instead of
	/// replacing them.
	/// </param>
	/// <exception cref="ArithmeticExpression.Failure">A <c>[subscript]=value</c> word names no element.</exception>
	/// <remarks>
	/// Each word is the next element, and a <c>[subscript]=value</c> word puts its value at that index, the words
	/// after it following on from there, as in bash. shortcut: a quoted word that looks like <c>[1]=x</c> is read as an
	/// index too, where bash keeps it as text; tell the two apart while the word is expanded if a script needs it.
	/// </remarks>
	public void SetArray(string name, string list, bool append)
	{
		var elements = append ? this.ToArray(name) : [];
		var next = elements.Count > 0 ? elements.Keys.Last() + 1 : 0;

		foreach (var word in CommandLine.Split(list.ArgumentNotNull()).Where(word => !word.IsOperator).Select(word => word.Text))
		{
			var value = word;

			if (word is ['[', ..] && SubscriptEnd(word, 0) is var close and > 0 && close + 1 < word.Length && word[close + 1] == '=')
			{
				next = this.Index(name, word[1..close]);
				value = word[(close + 2)..];
			}

			elements[next++] = value;
		}

		if (!append)
		{
			_ = this.variables.Remove(name);
			this.arrays[name] = elements;
		}
	}

	/// <summary>
	/// Makes <paramref name="name" /> an array, as <c>declare -a name</c> does, keeping a value it has as element 0.
	/// </summary>
	/// <param name="name">A valid variable name.</param>
	public void MakeArray(string name) => _ = this.ToArray(name);

	/// <summary>
	/// Gives the array <paramref name="name" />, making one first when it isn't one yet.
	/// </summary>
	/// <param name="name">A valid variable name.</param>
	/// <returns>
	/// The elements. A variable that wasn't an array becomes element 0 of the new one, as in bash.
	/// </returns>
	private SortedDictionary<long, string> ToArray(string name)
	{
		if (!this.arrays.TryGetValue(name, out var elements))
		{
			elements = [];

			if (this.variables.Remove(name, out var value))
			{
				elements[0] = value;
			}

			this.arrays[name] = elements;
		}

		return elements;
	}

	/// <summary>
	/// Works out which element a subscript names.
	/// </summary>
	/// <param name="name">The variable name.</param>
	/// <param name="subscript">The subscript, an arithmetic expression.</param>
	/// <returns>
	/// The index. A negative one counts back from just past the last element, as in bash.
	/// </returns>
	/// <exception cref="ArithmeticExpression.Failure">
	/// The expression can't be worked out, or it counts back past the first element.
	/// </exception>
	private long Index(string name, string subscript)
	{
		var index = ArithmeticExpression.Evaluate(subscript, this.Variables);

		if (index < 0)
		{
			index += this.arrays.TryGetValue(name, out var elements) ? elements.Count > 0 ? elements.Keys.Last() + 1 : 0
				: this.Variables.ContainsKey(name) ? 1 : 0;
		}

		return index >= 0 ? index : throw new ArithmeticExpression.Failure(new ShellError("sharpie::shell::bad_subscript", $"{name}[{subscript}]: bad array subscript")
		{
			Help = "a negative subscript counts back from the end, and can't go past the first element",
		});
	}

	/// <summary>
	/// Gets the element a subscript names.
	/// </summary>
	/// <param name="name">The variable name.</param>
	/// <param name="subscript">The subscript.</param>
	/// <param name="value">The element's value.</param>
	/// <returns>
	/// <see langword="true" /> when the element is set. A variable that isn't an array has only element 0, its value.
	/// A subscript that can't be worked out names nothing.
	/// </returns>
	private bool TryGetElement(string name, string subscript, [NotNullWhen(true)] out string? value)
	{
		value = null;

		long index;

		try
		{
			index = this.Index(name, subscript);
		}
		catch (ArithmeticExpression.Failure)
		{
			return false;
		}

		return this.arrays.TryGetValue(name, out var elements) ? elements.TryGetValue(index, out value)
			: index == 0 && this.Variables.TryGetValue(name, out value);
	}

	/// <summary>
	/// Sets one element, making <paramref name="name" /> an array when it isn't one.
	/// </summary>
	/// <param name="name">The variable name.</param>
	/// <param name="subscript">The subscript.</param>
	/// <param name="value">The value.</param>
	/// <exception cref="ArithmeticExpression.Failure">The subscript names no element.</exception>
	private void SetElement(string name, string subscript, string value)
	{
		var index = this.Index(name, subscript);

		this.ToArray(name)[index] = value;
	}

	/// <summary>
	/// Removes one element, as <c>unset 'a[1]'</c> does.
	/// </summary>
	/// <param name="name">The variable name.</param>
	/// <param name="subscript">The subscript.</param>
	/// <exception cref="ArithmeticExpression.Failure">The subscript names no element.</exception>
	/// <remarks>
	/// The array stays, with no element left at that index, so the elements after it keep their indexes, as in bash.
	/// Element 0 of a variable that isn't an array is the variable itself.
	/// </remarks>
	private void RemoveElement(string name, string subscript)
	{
		var index = this.Index(name, subscript);

		if (this.arrays.TryGetValue(name, out var elements))
		{
			_ = elements.Remove(index);
		}
		else if (index == 0)
		{
			this.Remove(name);
		}
	}

	/// <summary>
	/// Writes an array as <c>declare -p</c> and <c>set</c> show it.
	/// </summary>
	/// <param name="name">The name of an array.</param>
	/// <returns>
	/// <c>([0]="a" [1]="b")</c>, each value in double quotes with <c>"</c>, <c>\</c>, <c>$</c> and <c>`</c> escaped,
	/// so the text read back gives the same array.
	/// </returns>
	public string FormatArray(string name)
	{
		var text = new StringBuilder("(");

		foreach (var (index, value) in this.arrays[name])
		{
			_ = text.Append(text.Length > 1 ? " [" : "[").Append(index.ToString(CultureInfo.InvariantCulture)).Append("]=\"");

			foreach (var character in value)
			{
				_ = (character is '"' or '\\' or '$' or '`' ? text.Append('\\') : text).Append(character);
			}

			_ = text.Append('"');
		}

		return text.Append(')').ToString();
	}

	/// <summary>
	/// Gets the names of the arrays.
	/// </summary>
	/// <value>
	/// The names, in no set order.
	/// </value>
	public IEnumerable<string> ArrayNames => this.arrays.Keys;

	/// <summary>
	/// Everything <see cref="Restore" /> puts back.
	/// </summary>
	/// <param name="Variables">The variables that aren't arrays.</param>
	/// <param name="Arrays">The arrays.</param>
	internal sealed record Snapshot(Dictionary<string, string> Variables, Dictionary<string, SortedDictionary<long, string>> Arrays);

	/// <summary>
	/// Copies every shell variable and array, for <see cref="Restore" /> to put back.
	/// </summary>
	/// <returns>
	/// The copy, which later changes don't reach.
	/// </returns>
	public Snapshot Save() => new(new(this.variables), this.arrays.ToDictionary(pair => pair.Key, pair => new SortedDictionary<long, string>(pair.Value)));
}
