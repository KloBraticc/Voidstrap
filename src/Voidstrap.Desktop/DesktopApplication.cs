using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace Voidstrap.Desktop;

public sealed class DesktopApplication : Application
{
	public override void Initialize()
	{
		Styles.Add(new FluentTheme());
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = new PlatformOverviewWindow(DesktopRuntime.Host);
		}

		base.OnFrameworkInitializationCompleted();
	}
}
