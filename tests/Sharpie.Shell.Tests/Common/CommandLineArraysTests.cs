using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Common;

/// <summary>
/// Tests for how <see cref="CommandLine.Split" /> reads arrays: <c>NAME=(...)</c> lists and subscripts. Each result was
/// checked against bash 5.3.
/// </summary>
public sealed class CommandLineArraysTests
{
	/// <summary>
	/// The shell's variables, with <c>a</c> holding <c>x</c>, <c>y z</c> and, at 5, <c>w</c>, and <c>i</c> holding 1.
	/// </summary>
	private readonly DeclareCommand declare = new(new ShellSettings());

	/// <summary>
	/// Initializes a new instance of the <see cref="CommandLineArraysTests" /> class.
	/// </summary>
	public CommandLineArraysTests()
	{
		this.declare.SetArray("a", "x 'y z' [5]=w", append: false);
		this.declare.Set("i", "1");
	}

	/// <summary>
	/// Checks the words each subscript form gives.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The words.</param>
	[Theory]
	[InlineData("echo $a ${a} ${a[0]} ${a[i]} ${a[$i+4]} ${a[-1]} ${a[9]}", new[] { "echo", "x", "x", "x", "y", "z", "w", "w" })]
	[InlineData("echo \"${a[@]}\"", new[] { "echo", "x", "y z", "w" })]
	[InlineData("echo ${a[@]}", new[] { "echo", "x", "y", "z", "w" })]
	[InlineData("echo \"${a[*]}\" \"${!a[*]}\"", new[] { "echo", "x y z w", "0 1 5" })]
	[InlineData("echo \"${!a[@]}\"", new[] { "echo", "0", "1", "5" })]
	[InlineData("echo ${#a[@]} ${#a[*]} ${#a[1]} ${#a}", new[] { "echo", "3", "3", "3", "1" })]
	[InlineData("echo \"${a[@]:1}\" ${a[@]:0:1}", new[] { "echo", "y z", "w", "x" })]
	[InlineData("echo \"${a[@]/z/Z}\" \"${a[1]^^}\"", new[] { "echo", "x", "y Z", "w", "Y Z" })]
	[InlineData("echo ${a[9]:-none} ${a[@]:+set} \"${none[@]}\"", new[] { "echo", "none", "set" })]
	[InlineData("echo x${a[@]}y", new[] { "echo", "xx", "y", "z", "wy" })]
	[InlineData("echo ${i[0]} ${i[@]} ${#i[@]} ${i[1]}", new[] { "echo", "1", "1", "1" })]
	[InlineData("echo ${a[$(echo 1)]}", new[] { "echo", "y", "z" })]
	[InlineData("echo ${a[$nope]} ${a[zz]}", new[] { "echo", "x", "x" })]
	[InlineData("echo \"${none[*]:+s}\"", new[] { "echo", "" })]
	[InlineData("echo \"${none[@]:+s}\" \"${none[*]:+s}\" \"${none[@]-unset}\"", new[] { "echo", "", "unset" })]
	public void Split_WithSubscripts_ExpandsElements(string line, string[] expected)
	{
		// Act
		var words = CommandLine.Split(line, this.declare.Variables, substitute: command => command == "echo 1" ? "1" : string.Empty);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a <c>NAME=(...)</c> list is one word, its own words expanded and quoted for
	/// <see cref="DeclareCommand.SetArray" />, while a quoted <c>(</c> or one after another word isn't a list.
	/// </summary>
	/// <param name="line">The command line.</param>
	/// <param name="expected">The words, a list word marked with a leading <c>*</c>.</param>
	[Theory]
	[InlineData("b=(\"${a[@]}\" $i 'q r')", new[] { "*b=('x' 'y z' 'w' '1' 'q r')" })]
	[InlineData("b+=() c=(1)", new[] { "*b+=()", "*c=('1')" })]
	[InlineData("b=(\n 1 # one\n 2\n)", new[] { "*b=('1' '2')" })]
	[InlineData("b='(x)'", new[] { "b=(x)" })]
	[InlineData("local b=(x)", new[] { "local", "*b=('x')" })]
	[InlineData("b=(x", new[] { "b=", "(", "x" })]
	[InlineData("b[1]=(x)", new[] { "b[1]=", "(", "x", ")" })]
	[InlineData("b=($nope x)", new[] { "*b=('x')" })]
	[InlineData("b=y z)", new[] { "b=y", "z", ")" })]
	public void Split_WithList_GivesOneQuotedWord(string line, string[] expected)
	{
		// Act
		var words = CommandLine.Split(line, this.declare.Variables);

		// Assert
		_ = words.Select(word => (word.Compound ? "*" : string.Empty) + word.Text.Replace("\"", "'", StringComparison.Ordinal)).Should().Equal(expected);
	}

	/// <summary>
	/// Checks that a word in a list that can't expand is reported at the whole list.
	/// </summary>
	[Fact]
	public void Split_WithBadWordInList_PointsAtList()
	{
		// Act
		var split = () => CommandLine.Split("x b=(${q:?gone})", this.declare.Variables);

		// Assert
		var error = split.Should().Throw<CommandLine.ExpansionError>().Which.Error;

		_ = (error.Source, error.Start, error.End).Should().Be(("x b=(${q:?gone})", 4, 16));
	}

	/// <summary>
	/// Checks that with nothing to expand a list stays as typed, still one word.
	/// </summary>
	[Fact]
	public void Split_WithListAndNoVariables_KeepsItAsTyped()
	{
		// Act
		var words = CommandLine.Split("b=($x \"y\")");

		// Assert
		_ = words.Should().ContainSingle().Which.Text.Should().Be("b=($x \"y\")");
	}

	/// <summary>
	/// Checks that <c>set -u</c> reports an unset element or a variable in a list at the whole <c>${...}</c> or list,
	/// but not a list form of an unset array.
	/// </summary>
	[Fact]
	public void Split_WithUnsetElement_ReportsIt()
	{
		// Arrange
		var unset = new List<Word>();

		// Act
		_ = CommandLine.Split("echo ${a[2]} \"${gone[@]}\" b=($nope) ${a[$no]}", this.declare.Variables, unset.Add);
		var subscript = () => CommandLine.Split("echo ${a[zz]}", this.declare.Variables, unset.Add);

		// Assert
		_ = unset.Should().Equal(new Word("a[2]", IsOperator: false, 5, 12), new Word("nope", IsOperator: false, 28, 35), new Word("no", IsOperator: false, 36, 45));
		_ = subscript.Should().Throw<CommandLine.ExpansionError>().Which.Error.Summary.Should().Contain("`zz` isn't set");
	}

	/// <summary>
	/// Checks that a subscript that isn't an arithmetic expression is reported at the whole <c>${...}</c>.
	/// </summary>
	[Fact]
	public void Split_WithBadSubscript_Throws()
	{
		// Act
		var split = () => CommandLine.Split("echo ${a[1+]}", this.declare.Variables);

		// Assert
		var thrown = split.Should().Throw<CommandLine.ExpansionError>().Which;

		_ = thrown.Error.Code.Should().Be("sharpie::arithmetic::syntax");
		_ = thrown.Fatal.Should().BeFalse();
	}

	/// <summary>
	/// Checks that <c>${a[i]:=value}</c> sets the element, and that <c>${a[@]:=value}</c> can't be assigned.
	/// </summary>
	[Fact]
	public void Split_WithAssignDefaultToElement_SetsIt()
	{
		// Act
		var words = CommandLine.Split("echo ${a[3]:=new}", this.declare.Variables);
		var list = () => CommandLine.Split("echo ${b[@]:=new}", this.declare.Variables);
		var before = () => CommandLine.Split("echo ${a[-9]:=new}", this.declare.Variables);

		// Assert
		_ = words.Select(word => word.Text).Should().Equal("echo", "new");
		_ = this.declare.Variables["a[3]"].Should().Be("new");
		_ = list.Should().Throw<CommandLine.ExpansionError>().Which.Error.Summary.Should().Contain("cannot assign");
		var failure = before.Should().Throw<CommandLine.ExpansionError>().Which;

		_ = failure.Error.Summary.Should().Be("a[-9]: bad array subscript");
		_ = failure.Fatal.Should().BeFalse();
	}

	/// <summary>
	/// Checks which words start a list at a <c>(</c>.
	/// </summary>
	/// <param name="typed">The word up to the <c>(</c>.</param>
	/// <param name="expected">Whether it starts a list.</param>
	[Theory]
	[InlineData("a=", true)]
	[InlineData("_a1+=", true)]
	[InlineData("a", false)]
	[InlineData("=", false)]
	[InlineData("+=", false)]
	[InlineData("a[1]=", false)]
	[InlineData("a-=", false)]
	[InlineData("\"a\"=", false)]
	public void IsCompoundStart_WithTypedWord_SaysWhetherListStarts(string typed, bool expected)
	{
		// Act
		var starts = CommandLine.IsCompoundStart(typed);

		// Assert
		_ = starts.Should().Be(expected);
	}
}
