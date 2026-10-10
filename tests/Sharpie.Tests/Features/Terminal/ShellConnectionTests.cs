using System.Diagnostics;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using Sharpie.Features.Terminal;

namespace Sharpie.Tests.Features.Terminal;

/// <summary>
/// Tests for <see cref="ShellConnection" />, against the real shell built next to the tests.
/// </summary>
public sealed class ShellConnectionTests
{
	/// <summary>
	/// Checks that a line typed into the terminal runs, and what it prints comes back through the output callback.
	/// </summary>
	[AvaloniaFact]
	public void Send_EchoLine_OutputComesBack()
	{
		// Arrange
		var output = new StringBuilder();
		using var shell = ShellConnection.Start(text => output.Append(text), useSavedHistory: false, 80, 24, workingDirectory: null);

		// Act
		shell.Write("echo hi-from-test\r");

		// Assert
		var clock = Stopwatch.StartNew();

		while (!output.ToString().Contains("hi-from-test\r\n", StringComparison.Ordinal)
			&& clock.Elapsed < TimeSpan.FromSeconds(15))
		{
			Dispatcher.UIThread.RunJobs();
			Thread.Sleep(20);
		}

		_ = output.ToString().Should().Contain("hi-from-test\r\n");
	}

	/// <summary>
	/// Checks that the Spargine guard rejects a missing output callback before any shell starts.
	/// </summary>
	[AvaloniaFact]
	public void Constructor_WithNullCallback_ThrowsArgumentNullException()
	{
		// Act
		var act = () => ShellConnection.Start(null!, useSavedHistory: false, 80, 24, workingDirectory: null);

		// Assert
		_ = act.Should().Throw<ArgumentNullException>();
	}
}
