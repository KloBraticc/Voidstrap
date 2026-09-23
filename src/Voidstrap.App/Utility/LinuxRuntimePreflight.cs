using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Voidstrap.Utility;

public static class LinuxRuntimePreflight
{
	private static readonly (string Library, string Purpose)[] RequiredLibraries =
	{
		("libX11.so.6", "opening application windows"),
		("libfontconfig.so.1", "locating system fonts"),
		("libfreetype.so.6", "rendering text")
	};

	private static readonly (string Library, string Purpose)[] RenderLibraries =
	{
		("libGL.so.1", "hardware accelerated drawing"),
		("libEGL.so.1", "hardware accelerated drawing")
	};

	private static readonly string[] Families = { "debian", "fedora", "arch", "suse", "alpine" };

	private static readonly (string Installer, string[] Packages)[] FamilyPackages =
	{
		("sudo apt install", new[] { "libx11-6", "libfontconfig1", "libfreetype6", "libvulkan1", "mesa-vulkan-drivers", "libgl1" }),
		("sudo dnf install", new[] { "libX11", "fontconfig", "freetype", "vulkan-loader", "mesa-vulkan-drivers", "mesa-libGL" }),
		("sudo pacman -S", new[] { "libx11", "fontconfig", "freetype2", "vulkan-icd-loader", "vulkan-swrast", "mesa" }),
		("sudo zypper install", new[] { "libX11-6", "fontconfig", "freetype2", "libvulkan1", "libvulkan_lvp", "Mesa-libGL1" }),
		("sudo apk add", new[] { "libx11", "fontconfig", "freetype", "vulkan-loader", "mesa-vulkan-swrast", "mesa-gl" })
	};

	private static readonly string[] PackageKeys =
	{
		"libX11.so.6", "libfontconfig.so.1", "libfreetype.so.6", "libvulkan.so.1", "a Vulkan driver", "libGL.so.1"
	};

	public static bool Verify()
	{
		if (!OperatingSystem.IsLinux())
			return true;

		List<(string Library, string Purpose)> missing = new();
		foreach ((string library, string purpose) in RequiredLibraries)
		{
			if (!CanLoad(library))
				missing.Add((library, purpose));
		}

		if (!CanLoad("libvulkan.so.1"))
		{
			missing.Add(("libvulkan.so.1", "the graphics renderer"));
		}
		else if (!HasVulkanDriver())
		{
			missing.Add(("a Vulkan driver", "the graphics renderer, install your distribution's Vulkan driver package"));
		}

		bool hasRenderer = false;
		foreach ((string library, _) in RenderLibraries)
		{
			if (CanLoad(library))
			{
				hasRenderer = true;
				break;
			}
		}

		if (!hasRenderer)
			missing.Add((RenderLibraries[0].Library, RenderLibraries[0].Purpose));

		if (missing.Count == 0)
			return true;

		StringBuilder message = new();
		message.AppendLine("Voidstrap cannot start because these system libraries are missing:");
		foreach ((string library, string purpose) in missing)
			message.AppendLine("  " + library + ", needed for " + purpose);

		message.AppendLine();
		message.AppendLine("Voidstrap ships everything else it needs, but the graphics and font libraries have to come from your distribution.");
		string? hint = ResolveInstallHint(missing);
		if (hint is not null)
		{
			message.AppendLine("On this system, install them with:");
			message.AppendLine("  " + hint);
		}
		else
		{
			message.AppendLine("Install the matching runtime packages for your distribution.");
		}

		Report(message.ToString());
		return false;
	}

	private static bool HasVulkanDriver()
	{
		string? overrides = Environment.GetEnvironmentVariable("VK_ICD_FILENAMES")
			?? Environment.GetEnvironmentVariable("VK_DRIVER_FILES");
		if (!string.IsNullOrWhiteSpace(overrides))
			return true;

		foreach (string directory in new[]
		{
			"/usr/share/vulkan/icd.d",
			"/usr/local/share/vulkan/icd.d",
			"/etc/vulkan/icd.d",
			"/usr/lib/x86_64-linux-gnu/vulkan/icd.d"
		})
		{
			try
			{
				if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").GetEnumerator().MoveNext())
					return true;
			}
			catch (Exception)
			{
			}
		}

		try
		{
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string local = Path.Combine(home, ".local", "share", "vulkan", "icd.d");
			if (Directory.Exists(local) && Directory.EnumerateFiles(local, "*.json").GetEnumerator().MoveNext())
				return true;
		}
		catch (Exception)
		{
		}

		return false;
	}

	private static bool CanLoad(string library)
	{
		try
		{
			if (NativeLibrary.TryLoad(library, out nint handle))
			{
				if (handle != 0)
					NativeLibrary.Free(handle);
				return true;
			}
		}
		catch (Exception)
		{
		}

		foreach (string directory in ResolveBundledLibraryDirectories())
		{
			try
			{
				string candidate = Path.Combine(directory, library);
				if (!File.Exists(candidate))
					continue;

				if (NativeLibrary.TryLoad(candidate, out nint bundled))
				{
					if (bundled != 0)
						NativeLibrary.Free(bundled);
					return true;
				}
			}
			catch (Exception)
			{
			}
		}

		LastLoadFailure = DescribeLoadFailure(library);
		return false;
	}

	private static string LastLoadFailure = string.Empty;

	private static string DescribeLoadFailure(string library)
	{
		try
		{
			NativeLibrary.Load(library);
			return "loaded on retry";
		}
		catch (Exception ex)
		{
			return ex.Message.Replace('\n', ' ');
		}
	}

	private static IEnumerable<string> ResolveBundledLibraryDirectories()
	{
		string? appDir = Environment.GetEnvironmentVariable("APPDIR");
		if (!string.IsNullOrWhiteSpace(appDir))
		{
			yield return Path.Combine(appDir, "usr", "lib");
			yield return Path.Combine(appDir, "lib");
		}

		string? search = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
		if (string.IsNullOrWhiteSpace(search))
			yield break;

		foreach (string entry in search.Split(':', StringSplitOptions.RemoveEmptyEntries))
			yield return entry;
	}

	private static string? ResolveInstallHint(List<(string Library, string Purpose)> missing)
	{
		int family = ResolveFamilyIndex();
		if (family < 0)
			return null;

		(string installer, string[] packages) = FamilyPackages[family];
		List<string> wanted = new();
		foreach ((string library, _) in missing)
		{
			for (int index = 0; index < PackageKeys.Length; index++)
			{
				if (!library.StartsWith(PackageKeys[index], StringComparison.Ordinal))
					continue;

				if (!wanted.Contains(packages[index]))
					wanted.Add(packages[index]);
				break;
			}
		}

		if (wanted.Count == 0)
			return null;

		return installer + " " + string.Join(" ", wanted);
	}

	private static int ResolveFamilyIndex()
	{
		string identifiers = ReadOsReleaseIdentifiers();
		if (identifiers.Length == 0)
			return -1;

		for (int index = 0; index < Families.Length; index++)
		{
			if (identifiers.Contains(Families[index], StringComparison.OrdinalIgnoreCase))
				return index;
		}

		foreach ((string[] names, int index) in new[]
		{
			(new[] { "ubuntu", "mint", "pop", "elementary", "zorin", "kali" }, 0),
			(new[] { "rhel", "centos", "rocky", "alma", "nobara", "amzn" }, 1),
			(new[] { "manjaro", "endeavour", "garuda", "cachyos" }, 2),
			(new[] { "opensuse", "sles" }, 3)
		})
		{
			foreach (string name in names)
			{
				if (identifiers.Contains(name, StringComparison.OrdinalIgnoreCase))
					return index;
			}
		}

		return -1;
	}

	private static string ReadOsReleaseIdentifiers()
	{
		foreach (string path in new[] { "/etc/os-release", "/usr/lib/os-release" })
		{
			try
			{
				if (!File.Exists(path))
					continue;

				StringBuilder builder = new();
				foreach (string line in File.ReadLines(path))
				{
					if (line.StartsWith("ID=", StringComparison.Ordinal) || line.StartsWith("ID_LIKE=", StringComparison.Ordinal))
						builder.Append(line).Append(' ');
				}

				if (builder.Length > 0)
					return builder.ToString();
			}
			catch (Exception)
			{
			}
		}

		return string.Empty;
	}

	private static void Report(string message)
	{
		try
		{
			Console.Error.WriteLine(message);
			Console.Error.Flush();
		}
		catch (Exception)
		{
		}

		try
		{
			string directory = ResolveReportDirectory();
			Directory.CreateDirectory(directory);
			File.WriteAllText(
				Path.Combine(directory, "startup-requirements.log"),
				DateTime.UtcNow.ToString("u", System.Globalization.CultureInfo.InvariantCulture)
					+ Environment.NewLine
					+ message
					+ Environment.NewLine);
		}
		catch (Exception)
		{
		}
	}

	private static string ResolveReportDirectory()
	{
		string? state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
		if (string.IsNullOrWhiteSpace(state))
		{
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			state = Path.Combine(home, ".local", "state");
		}

		return Path.Combine(state, "voidstrap");
	}
}
