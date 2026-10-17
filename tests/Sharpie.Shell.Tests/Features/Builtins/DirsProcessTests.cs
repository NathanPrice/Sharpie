using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Runs <c>pushd</c>, <c>popd</c> and <c>dirs</c> through the built shell as a real process.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class DirsProcessTests
{
	/// <summary>
	/// Checks the output and status of the folder stack builtins against what bash gives for the same lines, starting
	/// in a temporary home folder so it shows as <c>~</c>.
	/// </summary>
	/// <param name="command">The command string, with <c>{O}</c> for a second temporary folder.</param>
	/// <param name="output">What it should print, with <c>{O}</c> and <c>{H}</c> for the folders.</param>
	/// <param name="expected">The shell's exit code.</param>
	[Theory]
	[InlineData("pushd {O}", "{O} ~\n", 0)]
	[InlineData("pushd {O}; pushd /; dirs", "{O} ~\n/ {O} ~\n/ {O} ~\n", 0)]
	[InlineData("pushd {O} >/dev/null; dirs -v", " 0  {O}\n 1  ~\n", 0)]
	[InlineData("pushd {O}; popd; pwd", "{O} ~\n~\n{H}\n", 0)]
	[InlineData("pushd {O}; dirs -c; dirs", "{O} ~\n{O}\n", 0)]
	[InlineData("popd", "", 1)]
	[InlineData("pushd", "", 1)]
	[InlineData("pushd {O}/missing", "", 1)]
	public async Task Shell_WithFolderStack_MatchesBash(string command, string output, int expected)
	{
		// Arrange
		var home = Directory.CreateTempSubdirectory("shell-home-").FullName;
		var other = Directory.CreateTempSubdirectory("shell-other-").FullName;

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(
				string.Empty,
				workingDirectory: home,
				environment: new Dictionary<string, string> { ["HOME"] = home },
				arguments: ["-c", command.Replace("{O}", other, StringComparison.Ordinal)]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be(output.Replace("{O}", other, StringComparison.Ordinal).Replace("{H}", home, StringComparison.Ordinal));
			_ = run.ExitCode.Should().Be(expected);
		}
		finally
		{
			Directory.Delete(home, recursive: true);
			Directory.Delete(other, recursive: true);
		}
	}
}
