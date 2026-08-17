using System;
using System.Runtime.InteropServices;

namespace Voidstrap.Core;

public enum LinuxWebViewRuntime
{
	None,
	WpeWebKit,
	WebKitGtk
}

public static class LinuxWebViewRuntimeDetector
{
	private static readonly string[] WpeWebKitLibraries = ["libwpewebkit-2.0.so.1.0", "libwpewebkit-2.0.so.1", "libwpewebkit-2.0.so", "libWPEWebKit-1.1.so.0", "libWPEWebKit-1.1.so"];
	private static readonly string[] WebKitGtkLibraries = ["libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37", "libwebkit2gtk-4.0.so"];

	public static LinuxWebViewRuntime Detect()
	{
		if (!OperatingSystem.IsLinux())
		{
			return LinuxWebViewRuntime.None;
		}

		if (CanLoadAny(WpeWebKitLibraries))
		{
			return LinuxWebViewRuntime.WpeWebKit;
		}

		return CanLoadAny(WebKitGtkLibraries)
			? LinuxWebViewRuntime.WebKitGtk
			: LinuxWebViewRuntime.None;
	}

	public static bool ShouldPreferWebKitGtk()
	{
		return OperatingSystem.IsLinux()
			&& !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
			&& CanLoadAny(WebKitGtkLibraries);
	}

	private static bool CanLoadAny(string[] libraries)
	{
		foreach (string library in libraries)
		{
			try
			{
				if (NativeLibrary.TryLoad(library, out IntPtr handle))
				{
					NativeLibrary.Free(handle);
					return true;
				}
			}
			catch (Exception)
			{
			}
		}

		return false;
	}
}
