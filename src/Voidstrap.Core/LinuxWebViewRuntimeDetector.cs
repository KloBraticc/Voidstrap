using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

	private static readonly object SearchGate = new();

	private static string[]? _searchDirectories;

	private static bool CanLoadAny(string[] libraries)
	{
		foreach (string library in libraries)
		{
			if (IsInstalled(library))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsInstalled(string library)
	{
		foreach (string directory in SearchDirectories())
		{
			try
			{
				if (File.Exists(Path.Combine(directory, library)))
				{
					return true;
				}
			}
			catch (Exception)
			{
			}
		}

		return false;
	}

	private static string[] SearchDirectories()
	{
		lock (SearchGate)
		{
			if (_searchDirectories is not null)
			{
				return _searchDirectories;
			}

			List<string> directories = [];
			void Add(string? directory)
			{
				if (!string.IsNullOrWhiteSpace(directory) && Path.IsPathRooted(directory) && !directories.Contains(directory))
				{
					directories.Add(directory.TrimEnd('/'));
				}
			}

			foreach (string entry in (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				Add(entry);
			}

			ReadLinkerConfiguration("/etc/ld.so.conf", Add, 0);

			string triplet = RuntimeInformation.ProcessArchitecture switch
			{
				Architecture.Arm64 => "aarch64-linux-gnu",
				Architecture.Arm => "arm-linux-gnueabihf",
				_ => "x86_64-linux-gnu"
			};
			foreach (string directory in new[] { "/app/lib", "/app/lib64", "/usr/lib/" + triplet, "/lib/" + triplet, "/usr/lib64", "/lib64", "/usr/lib", "/lib", "/usr/local/lib", "/run/current-system/sw/lib" })
			{
				Add(directory);
			}

			_searchDirectories = [.. directories];
			return _searchDirectories;
		}
	}

	private static void ReadLinkerConfiguration(string path, Action<string?> add, int depth)
	{
		if (depth > 4)
		{
			return;
		}

		try
		{
			if (!File.Exists(path))
			{
				return;
			}

			foreach (string raw in File.ReadLines(path))
			{
				string line = raw.Split('#', 2)[0].Trim();
				if (line.Length == 0)
				{
					continue;
				}

				if (line.StartsWith("include ", StringComparison.Ordinal))
				{
					string pattern = line["include ".Length..].Trim();
					if (!Path.IsPathRooted(pattern))
					{
						pattern = Path.Combine(Path.GetDirectoryName(path) ?? "/etc", pattern);
					}

					string? directory = Path.GetDirectoryName(pattern);
					if (directory is null || !Directory.Exists(directory))
					{
						continue;
					}

					foreach (string included in Directory.GetFiles(directory, Path.GetFileName(pattern)).Order(StringComparer.Ordinal))
					{
						ReadLinkerConfiguration(included, add, depth + 1);
					}
					continue;
				}

				add(line);
			}
		}
		catch (Exception)
		{
		}
	}
}
