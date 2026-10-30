using FluentAssertions;
using Sharpie.Shell.Features.History;

namespace Sharpie.Shell.Tests.Features.History;

/// <summary>
/// Tests for <see cref="HistoryExpansion" />. Each expected line is what bash 5.3 printed for the same history and input.
/// </summary>
public sealed class HistoryExpansionTests
{
	/// <summary>
	/// Checks that each kind of reference is replaced with what it names in the history.
	/// </summary>
	/// <param name="history">The earlier lines, oldest first.</param>
	/// <param name="line">The typed line.</param>
	/// <param name="expected">The expanded line.</param>
	[Theory]
	[InlineData(new[] { "echo aa bb" }, "!!", "echo aa bb")]
	[InlineData(new[] { "echo aa bb" }, "echo !! x", "echo echo aa bb x")]
	[InlineData(new[] { "echo aa bb" }, "echo !$!$", "echo bbbb")]
	[InlineData(new[] { "echo aa bb" }, "echo !$$", "echo bb$")]
	[InlineData(new[] { "echo aa \"a!=by\"" }, "echo !$", "echo \"a!=by\"")]
	[InlineData(new[] { "echo aa bb >f" }, "echo !$", "echo f")]
	[InlineData(new[] { "echo aa bb|cat" }, "echo !$", "echo cat")]
	[InlineData(new[] { "echo aa", "ls" }, "echo !1", "echo echo aa")]
	[InlineData(new[] { "echo aa", "ls" }, "echo !2", "echo ls")]
	[InlineData(new[] { "echo aa", "ls" }, "echo !-2", "echo echo aa")]
	[InlineData(new[] { "echo aa", "ls" }, "echo !-1", "echo ls")]
	[InlineData(new[] { "echo aa", "ls" }, "echo !1x", "echo echo aax")]
	[InlineData(new[] { "echo aa", "ls" }, "echo !-1x", "echo lsx")]
	[InlineData(new[] { "echo aa", "ls", "echo bb" }, "!ec", "echo bb")]
	[InlineData(new[] { "echo aa", "ls", "echo bb" }, "!l", "ls")]
	[InlineData(new[] { "-x aa", "ls" }, "!-x", "-x aa")]
	[InlineData(new[] { "+5 aa", "ls" }, "!+5", "+5 aa")]
	[InlineData(new[] { "echo aa bb" }, "echo !ec;echo hi", "echo echo aa bb;echo hi")]
	[InlineData(new[] { "echo aa bb" }, "echo !ec|cat", "echo echo aa bb|cat")]
	[InlineData(new[] { "echo aa bb" }, "echo \"!ec\"x", "echo \"echo aa bb\"x")]
	[InlineData(new[] { "echo aa bb" }, "echo \"!$\"", "echo \"bb\"")]
	[InlineData(new[] { "echo aa bb" }, "echo \"'!!'\"", "echo \"'echo aa bb'\"")]
	[InlineData(new[] { "echo aa bb" }, "echo 'it''s' !!", "echo 'it''s' echo aa bb")]
	[InlineData(new[] { "echo aa bb" }, "echo \\\\!!", "echo \\\\echo aa bb")]
	[InlineData(new[] { "echo aa bb" }, "^bb^cc^ tail", "echo aa cc tail")]
	[InlineData(new[] { "echo aa bb" }, "^b^", "echo aa b")]
	[InlineData(new[] { "echo aa bb" }, "^a", "echo a bb")]
	[InlineData(new[] { "echo aa bb" }, "^aa", "echo  bb")]
	[InlineData(new[] { "echo aa bb" }, "^echo^printf", "printf aa bb")]
	[InlineData(new[] { "echo aa bb" }, "echo !! x]", "echo echo aa bb x]")]
	[InlineData(new[] { "echo aa bb" }, "echo [!! x", "echo [echo aa bb x")]
	[InlineData(new[] { "echo aa bb" }, "^aa^x !!", "echo x !! bb")]
	[InlineData(new[] { "echo aa bb" }, "^aa^x^ !$", "echo x bb bb")]
	[InlineData(new[] { "echo aa bb" }, "^bb^x^ '!!'", "echo aa x '!!'")]
	public void Expand_WithReference_ReplacesIt(string[] history, string line, string expected)
	{
		// Act
		var expanded = HistoryExpansion.Expand(line, history, out var error);

		// Assert
		_ = error.Should().BeNull();
		_ = expanded.Should().Be(expected);
	}

	/// <summary>
	/// Checks that a <c>!</c> bash doesn't expand is kept as typed, and the line itself is returned.
	/// </summary>
	/// <param name="line">The typed line.</param>
	[Theory]
	[InlineData("echo hi")]
	[InlineData("echo '!!'")]
	[InlineData("echo \\!!")]
	[InlineData("echo \"\\!!\"")]
	[InlineData("echo !")]
	[InlineData("echo a!")]
	[InlineData("echo ! x")]
	[InlineData("echo !\tx")]
	[InlineData("echo !=x")]
	[InlineData("echo !(x)")]
	[InlineData("echo \"!\" \"a!\"")]
	[InlineData("echo $!")]
	[InlineData("echo ${!x}")]
	[InlineData("echo [!a]")]
	[InlineData("echo a!;echo b")]
	[InlineData("echo !|cat")]
	[InlineData(" ^a^b")]
	[InlineData("echo a\\")]
	[InlineData("echo $'\\'!!'")]
	[InlineData("echo '!!")]
	public void Expand_WithoutReference_ReturnsLine(string line)
	{
		// Act
		var expanded = HistoryExpansion.Expand(line, ["echo aa bb"], out var error);

		// Assert
		_ = error.Should().BeNull();
		_ = expanded.Should().BeSameAs(line);
	}

	/// <summary>
	/// Checks that a reference naming nothing fails the whole line and underlines the reference.
	/// </summary>
	/// <param name="history">The earlier lines, oldest first.</param>
	/// <param name="line">The typed line.</param>
	/// <param name="code">The expected error code.</param>
	/// <param name="start">Where the underline should start.</param>
	/// <param name="end">Where it should end.</param>
	[Theory]
	[InlineData(new string[0], "!!", "sharpie::history::event_not_found", 0, 2)]
	[InlineData(new string[0], "echo !$", "sharpie::history::event_not_found", 5, 7)]
	[InlineData(new[] { "echo aa bb" }, "echo !zzz", "sharpie::history::event_not_found", 5, 9)]
	[InlineData(new[] { "echo aa bb" }, "echo a!b", "sharpie::history::event_not_found", 6, 8)]
	[InlineData(new[] { "echo aa bb" }, "echo !2", "sharpie::history::event_not_found", 5, 7)]
	[InlineData(new[] { "echo aa bb" }, "echo !0", "sharpie::history::event_not_found", 5, 7)]
	[InlineData(new[] { "echo aa bb" }, "echo !-2", "sharpie::history::event_not_found", 5, 8)]
	[InlineData(new[] { "echo aa bb" }, "echo !-0", "sharpie::history::event_not_found", 5, 8)]
	[InlineData(new[] { "echo aa bb" }, "echo !! !zzz", "sharpie::history::event_not_found", 8, 12)]
	[InlineData(new[] { "echo aa bb" }, "echo \"!zzz\"", "sharpie::history::event_not_found", 6, 10)]
	[InlineData(new[] { "echo aa bb" }, "echo !ec:x", "sharpie::history::unsupported", 8, 9)]
	[InlineData(new[] { "echo aa bb" }, "echo !!:1", "sharpie::history::unsupported", 7, 8)]
	[InlineData(new string[0], "^a^b", "sharpie::history::event_not_found", 0, 4)]
	[InlineData(new[] { "echo aa bb" }, "^qq^rr", "sharpie::history::substitution_failed", 0, 6)]
	[InlineData(new[] { "echo aa bb" }, "^qq^rr^ x", "sharpie::history::substitution_failed", 0, 7)]
	[InlineData(new[] { "echo aa bb" }, "^^x", "sharpie::history::substitution_failed", 0, 3)]
	public void Expand_WithMissingEvent_ReturnsNullAndError(string[] history, string line, string code, int start, int end)
	{
		// Act
		var expanded = HistoryExpansion.Expand(line, history, out var error);

		// Assert
		_ = expanded.Should().BeNull();
		_ = error.Should().NotBeNull();
		_ = error!.Code.Should().Be(code);
		_ = error.Source.Should().Be(line);
		_ = (error.Start, error.End).Should().Be((start, end));
		_ = error.Summary.Should().NotBeNullOrEmpty();
		_ = error.Label.Should().NotBeNullOrEmpty();
	}

	/// <summary>
	/// Checks that a failed <c>^old^new</c> says why under the line.
	/// </summary>
	/// <param name="line">The typed line.</param>
	/// <param name="label">The note expected under it.</param>
	[Theory]
	[InlineData("^^x", "nothing to replace")]
	[InlineData("^qq^rr", "`qq` isn't in the last line")]
	public void Expand_WithFailedSubstitution_LabelsReason(string line, string label)
	{
		// Act
		_ = HistoryExpansion.Expand(line, ["echo aa bb"], out var error);

		// Assert
		_ = error!.Label.Should().Be(label);
	}
}
