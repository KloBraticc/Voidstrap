using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSInputAccess
{
	public static bool IsGranted => OperatingSystem.IsMacOS() && AXIsProcessTrusted();

	public static void Request()
	{
		if (!OperatingSystem.IsMacOS())
			return;
		_ = CGRequestPostEventAccess();
		using Process? process = Process.Start(new ProcessStartInfo("/usr/bin/open")
		{
			UseShellExecute = false,
			ArgumentList = { "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility" }
		});
	}

	[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool AXIsProcessTrusted();

	[LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool CGRequestPostEventAccess();
}
