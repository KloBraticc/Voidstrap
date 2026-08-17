using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Voidstrap.Utility;

internal static class LinuxStartup
{
	private const string ConfiguredFlag = "VOIDSTRAP_GL_CONFIGURED";

	private const string ForceGpuFlag = "VOIDSTRAP_USE_GPU";

	private const string GpuRetryFlag = "VOIDSTRAP_GPU_RETRY";

	private const string ApplicationName = "Voidstrap";

	private static readonly int SoftwareThreadCount = Math.Clamp((Environment.ProcessorCount + 1) / 2, 1, 8);

	private static bool _subscribed;

	[ModuleInitializer]
	internal static void Initialize()
	{
		AppDomain.CurrentDomain.UnhandledException += OnFatalException;
		_subscribed = true;
		if (OperatingSystem.IsMacOS())
		{
			TextFontInstaller.Install();
		}
		if (!OperatingSystem.IsLinux())
		{
			return;
		}
		if (Environment.GetEnvironmentVariable(ConfiguredFlag) == "1")
		{
			return;
		}
		TextFontInstaller.Install();
		Environment.SetEnvironmentVariable(ConfiguredFlag, "1");
		Environment.SetEnvironmentVariable("RESOURCE_NAME", ApplicationName);
		Environment.SetEnvironmentVariable("SDL_VIDEO_X11_WMCLASS", ApplicationName);
	}

	public static void Shutdown()
	{
		if (!_subscribed)
		{
			return;
		}
		AppDomain.CurrentDomain.UnhandledException -= OnFatalException;
		_subscribed = false;
	}

	private static void OnFatalException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is not Exception exception)
		{
			return;
		}
		string text = exception.ToString();
		bool gpuFailure = text.Contains("WebGPU", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("WgpuContext", StringComparison.Ordinal)
			|| text.Contains("No suitable adapter", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("failed to create surface", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("EGL_NOT_INITIALIZED", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("VK_ERROR", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("GLXBad", StringComparison.OrdinalIgnoreCase);
		if (!gpuFailure)
		{
			return;
		}
		if (Environment.GetEnvironmentVariable(GpuRetryFlag) == "1")
		{
			try
			{
				Console.Error.WriteLine("Voidstrap could not start a GPU or software renderer on this system. Update your graphics drivers and try again.");
			}
			catch
			{
			}
			Environment.Exit(1);
			return;
		}
		if (Environment.GetEnvironmentVariable(ForceGpuFlag) == "1")
		{
			try
			{
				Console.Error.WriteLine("Voidstrap could not start the requested GPU renderer. Update your graphics drivers or remove VOIDSTRAP_USE_GPU.");
			}
			catch
			{
			}
			Environment.Exit(1);
			return;
		}
		try
		{
			string? executable = Environment.ProcessPath;
			if (string.IsNullOrEmpty(executable))
			{
				return;
			}
			ProcessStartInfo startInfo = new ProcessStartInfo(executable)
			{
				UseShellExecute = false
			};
			string[] arguments = Environment.GetCommandLineArgs();
			for (int i = 1; i < arguments.Length; i++)
			{
				startInfo.ArgumentList.Add(arguments[i]);
			}
			startInfo.Environment[GpuRetryFlag] = "1";
			startInfo.Environment[ConfiguredFlag] = "1";
			startInfo.Environment["RESOURCE_NAME"] = ApplicationName;
			startInfo.Environment["SDL_VIDEO_X11_WMCLASS"] = ApplicationName;
			startInfo.Environment["WGPU_BACKEND"] = "gl";
			startInfo.Environment["WGPU_POWER_PREF"] = "low";
			if (OperatingSystem.IsLinux())
			{
				startInfo.Environment["LIBGL_ALWAYS_SOFTWARE"] = "1";
				startInfo.Environment["LP_NUM_THREADS"] = SoftwareThreadCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
			}
			if (Process.Start(startInfo) != null)
			{
				Environment.Exit(0);
			}
		}
		catch
		{
		}
	}

}
