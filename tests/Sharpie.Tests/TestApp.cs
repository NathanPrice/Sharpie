using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Sharpie.Tests.TestApp))]

namespace Sharpie.Tests;

/// <summary>
/// Starts the window app's <see cref="App" /> on Avalonia's headless platform, so tests get the same themes and
/// AvaloniaEdit styles without opening a real window.
/// </summary>
public static class TestApp
{
	/// <summary>
	/// Builds the app the headless test runner starts once for all tests.
	/// </summary>
	/// <returns>
	/// The app builder, set to the headless platform.
	/// </returns>
	public static AppBuilder BuildAvaloniaApp() =>
		AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
