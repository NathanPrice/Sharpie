using FluentAssertions;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Variables;

/// <summary>
/// Tests for the arrays <see cref="DeclareCommand" /> keeps.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class DeclareCommandArraysTests : IDisposable
{
	private readonly ShellEnvironment environment = new();
	private readonly DeclareCommand sut = new(new ShellSettings());

	/// <summary>
	/// Checks which words <see cref="DeclareCommand.TryParseAssignment" /> takes apart, and into what.
	/// </summary>
	/// <param name="word">The word.</param>
	/// <param name="expected">The name, subscript, <c>+</c> and value, joined by <c>|</c>, or <see langword="null" />.</param>
	[Theory]
	[InlineData("a=1", "a||False|1")]
	[InlineData("_x9=", "_x9||False|")]
	[InlineData("a+=x", "a||True|x")]
	[InlineData("a[1]=v", "a|1|False|v")]
	[InlineData("a[${b[1]}]+=v=w", "a|${b[1]}|True|v=w")]
	[InlineData("a[]=v", "a||False|v")]
	[InlineData("1a=x", null)]
	[InlineData("a[1=x", null)]
	[InlineData("=x", null)]
	[InlineData("a", null)]
	[InlineData("a+x", null)]
	[InlineData("a-=1", null)]
	[InlineData("[1]=x", null)]
	[InlineData("a[1]", null)]
	public void TryParseAssignment_WithWord_TakesItApart(string word, string? expected)
	{
		// Act
		var parsed = DeclareCommand.TryParseAssignment(word, out var assignment);

		// Assert
		_ = (parsed ? $"{assignment.Name}|{assignment.Subscript}|{assignment.Append}|{assignment.Value}" : null).Should().Be(expected);
		_ = DeclareCommand.IsAssignment(word).Should().Be(parsed);
	}

	/// <summary>
	/// Checks which keys <see cref="DeclareCommand.TrySplitElement" /> reads as a name with a subscript.
	/// </summary>
	/// <param name="key">The key.</param>
	/// <param name="expected">The name and subscript, joined by <c>|</c>, or <see langword="null" />.</param>
	[Theory]
	[InlineData("a[1]", "a|1")]
	[InlineData("a[x[1]]", "a|x[1]")]
	[InlineData("a[@]", "a|@")]
	[InlineData("a", null)]
	[InlineData("a[1]x", null)]
	[InlineData("1[1]", null)]
	[InlineData("a[1", null)]
	[InlineData("[1]", null)]
	public void TrySplitElement_WithKey_FindsSubscript(string key, string? expected)
	{
		// Act
		var split = DeclareCommand.TrySplitElement(key, out var name, out var subscript);

		// Assert
		_ = (split ? $"{name}|{subscript}" : null).Should().Be(expected);
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.SetArray" /> puts each word in the next element, a <c>[i]=value</c> word at
	/// its index, and that the variables read the elements back.
	/// </summary>
	[Fact]
	public void SetArray_WithWords_StoresElements()
	{
		// Act
		this.sut.SetArray("a", "old ones", append: false);
		this.sut.SetArray("a", "x 'y z' [5]=q r '[1]'", append: false);
		this.sut.SetArray("e", string.Empty, append: false);
		this.sut.Set("s", "v");

		// Assert
		_ = Show(this.sut.Variables, "a").Should().Equal("0=x", "1=y z", "5=q", "6=r", "7=[1]");
		_ = this.sut.IsArray("a").Should().BeTrue();
		_ = this.sut.Variables["a"].Should().Be("x");
		_ = this.sut.Variables["a[5]"].Should().Be("q");
		_ = this.sut.Variables["a[-1]"].Should().Be("[1]");
		_ = this.sut.Variables["s[-1]"].Should().Be("v");
		_ = this.sut.Variables["s[0]"].Should().Be("v");
		_ = this.sut.Variables.ContainsKey("s[1]").Should().BeFalse();
		_ = this.sut.Variables.ContainsKey("e[-1]").Should().BeFalse();
		_ = this.sut.Variables.ContainsKey("e").Should().BeFalse();
		_ = this.sut.Variables["a[2+3]"].Should().Be("q");
		_ = this.sut.Variables.ContainsKey("a[2]").Should().BeFalse();
		_ = this.sut.Variables.ContainsKey("a[-9]").Should().BeFalse();
		_ = this.sut.Variables.ContainsKey("a[1/0]").Should().BeFalse();
		_ = this.sut.Variables.Keys.Should().ContainSingle(key => key == "a").And.NotContain("e").And.NotContain(key => key.Contains('[', StringComparison.Ordinal));
	}

	/// <summary>
	/// Checks the forms <see cref="DeclareCommand.AssignWord" /> carries out: a list, an added list, one element, added
	/// text, and a value in parentheses taken as a list or as text.
	/// </summary>
	[Fact]
	public void AssignWord_WithEachForm_SetsWhatBashSets()
	{
		// Act
		int[] statuses =
		[
			this.sut.AssignWord("a=(1 2)", compound: true),
			this.sut.AssignWord("a+=(3)", compound: true),
			this.sut.AssignWord("a[1]=B", compound: false),
			this.sut.AssignWord("a+=x", compound: false),
			this.sut.AssignWord("s=(x)", compound: false),
			this.sut.AssignWord("t=(x y)", compound: null),
			this.sut.AssignWord("u+=v", compound: false),
		];

		// Assert
		_ = statuses.Should().AllBeEquivalentTo(0);
		_ = DeclareCommand.Elements(this.sut.Variables, "a").Select(element => element.Value).Should().Equal("1x", "B", "3");
		_ = this.sut.IsArray("s").Should().BeFalse();
		_ = this.sut.Variables["s"].Should().Be("(x)");
		_ = DeclareCommand.Elements(this.sut.Variables, "t").Should().HaveCount(2);
		_ = this.sut.Variables["u"].Should().Be("v");
	}

	/// <summary>
	/// Checks that a subscript counting back past the first element is reported, gives 1 and changes nothing.
	/// </summary>
	[Fact]
	public void AssignWord_WithSubscriptBeforeFirst_ReportsIt()
	{
		// Arrange
		this.sut.SetArray("a", "x", append: false);

		// Act
		var status = this.sut.AssignWord("a[-2]=y", compound: false);

		// Assert
		_ = status.Should().Be(1);
		_ = this.environment.Error.Should().Contain("a[-2]: bad array subscript");
		_ = DeclareCommand.Elements(this.sut.Variables, "a").Should().ContainSingle();
	}

	/// <summary>
	/// Checks that a subscript counting back past the first element throws with a report that says so.
	/// </summary>
	[Fact]
	public void Set_WithSubscriptBeforeFirst_Throws()
	{
		// Act
		var set = () => this.sut.Set("a[-1]", "x");

		// Assert
		var error = set.Should().Throw<Sharpie.Shell.Features.Arithmetic.ArithmeticExpression.Failure>().Which.Error;

		_ = (error.Code, error.Summary).Should().Be(("sharpie::shell::bad_subscript", "a[-1]: bad array subscript"));
		_ = error.Help.Should().Contain("counts back from the end");
	}

	/// <summary>
	/// Checks that setting an element of a plain variable makes it an array with its value at 0, and that its plain
	/// name then sets element 0.
	/// </summary>
	[Fact]
	public void Set_WithElementOfPlainVariable_MakesArray()
	{
		// Arrange
		this.sut.Set("v", "first");

		// Act
		this.sut.Set("v[2]", "third");
		this.sut.Set("v", "zero");

		// Assert
		_ = Show(this.sut.Variables, "v").Should().Equal("0=zero", "2=third");
		_ = this.sut.Variables.Keys.Where(key => key.StartsWith('v')).Should().Equal("v");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Remove" /> takes one element out, keeping the others at their indexes, and
	/// that element 0 of a plain variable is the variable.
	/// </summary>
	[Fact]
	public void Remove_WithElement_TakesOnlyThatOne()
	{
		// Arrange
		this.sut.SetArray("a", "x y z", append: false);
		this.sut.Set("s", "v");

		// Act
		this.sut.Remove("a[1]");
		this.sut.Remove("s[1]");
		var kept = this.sut.Variables.ContainsKey("s");
		this.sut.Remove("s[0]");

		// Assert
		_ = Show(this.sut.Variables, "a").Should().Equal("0=x", "2=z");
		_ = kept.Should().BeTrue();
		_ = this.sut.Variables.ContainsKey("s").Should().BeFalse();
	}

	/// <summary>
	/// Checks that a function's local array goes back to the caller's array when it returns, and that an array a
	/// local name became is gone after.
	/// </summary>
	[Fact]
	public void RunFunction_WithLocalArrays_PutsCallersBack()
	{
		// Arrange
		this.sut.SetArray("kept", "outer", append: false);

		// Act
		_ = this.sut.RunFunction([], () =>
		{
			_ = this.sut.Localize("kept");
			_ = this.sut.Localize("fresh");
			this.sut.SetArray("kept", "inner one", append: false);
			this.sut.SetArray("fresh", "x", append: false);

			return 0;
		});

		// Assert
		_ = Show(this.sut.Variables, "kept").Should().Equal("0=outer");
		_ = this.sut.IsArray("fresh").Should().BeFalse();
		_ = this.sut.Variables.ContainsKey("fresh").Should().BeFalse();
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.Restore" /> puts back the arrays <see cref="DeclareCommand.Save" /> copied,
	/// and forgets ones made since.
	/// </summary>
	[Fact]
	public void Restore_WithSavedArrays_PutsThemBack()
	{
		// Arrange
		this.sut.SetArray("a", "x y", append: false);
		var saved = this.sut.Save();

		this.sut.Set("a[0]", "changed");
		this.sut.SetArray("b", "new", append: false);

		// Act
		this.sut.Restore(saved);

		// Assert
		_ = DeclareCommand.Elements(this.sut.Variables, "a").Select(element => element.Value).Should().Equal("x", "y");
		_ = this.sut.ArrayNames.Should().Equal("a");
	}

	/// <summary>
	/// Checks that <see cref="DeclareCommand.FormatArray" /> escapes what double quotes would read, and that
	/// <c>declare -a</c> and <c>declare -p</c> make and show arrays.
	/// </summary>
	[Fact]
	public void Run_WithArrayOptions_MakesAndShowsArrays()
	{
		// Arrange
		this.sut.SetArray("a", "'q\"$`\\' ''", append: false);
		this.sut.Set("s", "v");

		// Act
		var made = this.sut.Run(["-a", "e", "s"]);
		_ = this.sut.Run(["-p", "a", "e", "s"]);

		_ = made.Should().Be(0);
		_ = this.environment.Error.Should().BeEmpty();

		// Assert
		_ = this.environment.Output.Should().Be($"declare -a a=([0]=\"q\\\"\\$\\`\\\\\" [1]=\"\"){Environment.NewLine}declare -a e=(){Environment.NewLine}declare -a s=([0]=\"v\"){Environment.NewLine}");
	}

	/// <summary>
	/// Checks what <see cref="DeclareCommand.ForEnvironment" /> passes to a command for each word typed before it.
	/// </summary>
	/// <param name="word">The word.</param>
	/// <param name="expected">What the command's environment gets, or <see langword="null" />.</param>
	[Theory]
	[InlineData("P+=x", "P=ax")]
	[InlineData("Q=y", "Q=y")]
	[InlineData("a[1]=x", null)]
	public void ForEnvironment_WithWord_GivesEnvironmentEntry(string word, string? expected)
	{
		// Arrange
		this.sut.Set("P", "a");
		this.sut.Set("Q", "old");

		// Act
		var entry = this.sut.ForEnvironment(word);

		// Assert
		_ = entry.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a plain dictionary of variables gives a set name as one element and an unset one as none.
	/// </summary>
	[Fact]
	public void Elements_WithPlainDictionary_GivesValueAsOnlyElement()
	{
		// Arrange
		var variables = new Dictionary<string, string> { ["s"] = "v" };

		// Act
		var set = Show(variables, "s");
		var missing = Show(variables, "SHARPIE_SAMPLE_MISSING");

		// Assert
		_ = set.Should().Equal("0=v");
		_ = missing.Should().BeEmpty();
	}

	/// <inheritdoc />
	public void Dispose() => this.environment.Dispose();

	/// <summary>
	/// Shows an array's elements.
	/// </summary>
	/// <param name="variables">The variables.</param>
	/// <param name="name">The array's name.</param>
	/// <returns>
	/// Each element as <c>index=value</c>, in index order.
	/// </returns>
	private static string[] Show(IReadOnlyDictionary<string, string> variables, string name) =>
		[.. DeclareCommand.Elements(variables, name).Select(element => $"{element.Key}={element.Value}")];
}
