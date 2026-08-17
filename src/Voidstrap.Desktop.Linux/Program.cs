using Avalonia;
using Avalonia.Skia;
using Voidstrap.Desktop;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Desktop.Linux;

internal static class Program
{
	[STAThread]
	public static void Main(string[] args)
	{
		if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
			&& string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
		{
			Console.Error.WriteLine("Voidstrap requires an X11 or Wayland desktop session.");
			Environment.ExitCode = 1;
			return;
		}

		try
		{
			DesktopRuntime.Configure(new LinuxPlatformHost(), args);
			BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine("Voidstrap could not start: " + exception.Message);
			Environment.ExitCode = 1;
		}
	}

	public static AppBuilder BuildAvaloniaApp()
	{
		AppBuilder builder = AppBuilder.Configure<DesktopApplication>()
			.UsePlatformDetect()
			.With(new SkiaOptions
			{
				MaxGpuResourceSizeBytes = 128 * 1024 * 1024
			});

		string? waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
		string? x11Display = Environment.GetEnvironmentVariable("DISPLAY");
		bool forceWayland = string.Equals(Environment.GetEnvironmentVariable("VOIDSTRAP_NATIVE_WAYLAND"), "1", StringComparison.Ordinal);
		bool useWayland = !string.IsNullOrWhiteSpace(waylandDisplay) && (forceWayland || string.IsNullOrWhiteSpace(x11Display));
		return useWayland ? builder.UseWayland() : builder;
	}
}
