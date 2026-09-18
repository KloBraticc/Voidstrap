using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Voidstrap.Utility;

internal static partial class LoaderHardening
{
	private const uint LoadLibrarySearchSystem32 = 0x800;

	private static readonly string[] ProxyProneModules = new[]
	{
		"winmm.dll",
		"version.dll",
		"dwmapi.dll",
		"uxtheme.dll",
		"dxgi.dll",
		"d3d9.dll",
		"d3d11.dll",
		"dinput8.dll",
		"dsound.dll",
		"xinput1_3.dll",
		"xinput1_4.dll",
		"msimg32.dll",
		"msacm32.dll",
		"opengl32.dll",
		"wininet.dll",
		"winhttp.dll",
		"cryptsp.dll",
		"profapi.dll",
		"propsys.dll",
		"mfplat.dll",
		"dbghelp.dll"
	};

	public static int PinnedModuleCount { get; private set; }

	[LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetDllDirectoryW(string path);

	[LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

	[ModuleInitializer]
	internal static void Apply()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		try
		{
			SetDllDirectoryW(string.Empty);
		}
		catch
		{
		}

		string system32;

		try
		{
			system32 = Environment.SystemDirectory;
		}
		catch
		{
			return;
		}

		if (string.IsNullOrEmpty(system32))
		{
			return;
		}

		int pinned = 0;

		foreach (string name in ProxyProneModules)
		{
			try
			{
				if (LoadLibraryExW(Path.Combine(system32, name), IntPtr.Zero, LoadLibrarySearchSystem32) != IntPtr.Zero)
				{
					pinned++;
				}
			}
			catch
			{
			}
		}

		PinnedModuleCount = pinned;
	}
}
