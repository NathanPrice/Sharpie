using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Sharpie.Features.Terminal;

namespace Sharpie;

/// <summary>
/// Starts the terminal window app.
/// </summary>
internal static class Program
{
	/// <summary>
	/// Builds the Avalonia app and runs it until the last window closes.
	/// </summary>
	/// <param name="args">
	/// The command-line arguments, passed through to Avalonia, which hands them to the window as
	/// <see cref="IClassicDesktopStyleApplicationLifetime.Args" />.
	/// </param>
	[STAThread]
	private static void Main(string[] args) => _ = AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

/// <summary>
/// Sets the dark theme and opens the <see cref="TerminalWindow" />.
/// </summary>
internal sealed class App : Application
{
	/// <inheritdoc />
	public override void Initialize()
	{
		this.Styles.Add(new FluentTheme());

		// AvaloniaEdit's TextEditor has no look of its own until its theme is added.
		this.Styles.Add(new StyleInclude(new Uri("avares://Sharpie"))
		{
			Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
		});

		this.RequestedThemeVariant = ThemeVariant.Dark;
	}

	/// <inheritdoc />
	public override void OnFrameworkInitializationCompleted()
	{
		if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = new TerminalWindow(useSavedHistory: true, LaunchArguments.Command(desktop.Args ?? []));
		}

		base.OnFrameworkInitializationCompleted();
	}
}
