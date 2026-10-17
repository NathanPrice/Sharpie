using FluentAssertions;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Runs <c>umask</c> through the built shell as a real process, so the mask changes only in that shell.
/// </summary>
[Collection(ShellProcess.Collection)]
public sealed class UmaskProcessTests
{
	/// <summary>
	/// Checks that a mask set by <c>umask</c> reads back in both forms and applies to files the shell creates.
	/// </summary>
	[Fact]
	public async Task Shell_WithMaskSet_ReadsBackAndAppliesToNewFiles()
	{
		// Arrange
		var folder = Directory.CreateTempSubdirectory("shell-umask-").FullName;
		var file = Path.Combine(folder, "sample.txt");

		try
		{
			// Act
			var run = await ShellProcess.RunAsync(string.Empty, arguments: ["-c", $"umask 027; umask; umask g+w; umask -S; umask 077; echo x > {file}"]).ConfigureAwait(true);

			// Assert
			_ = run.Output.Should().Be("0027\nu=rwx,g=rwx,o=\n");
			_ = run.ExitCode.Should().Be(0);
			_ = File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}
}
