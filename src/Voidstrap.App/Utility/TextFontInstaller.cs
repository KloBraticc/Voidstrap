using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Voidstrap.Utility;

internal static class TextFontInstaller
{
	private static readonly string[] FontFiles = new[]
	{
		"selawk.ttf",
		"selawkb.ttf",
		"selawkl.ttf",
		"selawksb.ttf",
		"selawksl.ttf"
	};

	private const string AliasConfig = """
<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "fonts.dtd">
<fontconfig>
  <match target="pattern">
    <test name="family"><string>Segoe UI</string></test>
    <edit name="family" mode="prepend" binding="strong"><string>Selawik</string></edit>
  </match>
  <match target="pattern">
    <test name="family"><string>Segoe UI Semibold</string></test>
    <edit name="family" mode="prepend" binding="strong"><string>Selawik Semibold</string></edit>
  </match>
  <match target="pattern">
    <test name="family"><string>Segoe UI Light</string></test>
    <edit name="family" mode="prepend" binding="strong"><string>Selawik Light</string></edit>
  </match>
  <match target="pattern">
    <test name="family"><string>Segoe UI Semilight</string></test>
    <edit name="family" mode="prepend" binding="strong"><string>Selawik Semilight</string></edit>
  </match>
  <alias>
    <family>Segoe UI</family>
    <prefer><family>Selawik</family></prefer>
  </alias>
</fontconfig>
""";

	public static void Install()
	{
		try
		{
			if (OperatingSystem.IsLinux())
			{
				string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				string fontDirectory = Path.Combine(home, ".local", "share", "fonts", "voidstrap");
				bool wroteFonts = ExtractFonts(fontDirectory);
				bool wroteConfig = WriteAliasConfig(home);
				if (wroteFonts || wroteConfig)
				{
					RefreshFontCache(fontDirectory);
				}
			}
			else if (OperatingSystem.IsMacOS())
			{
				string fontDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts");
				ExtractFonts(fontDirectory);
			}
		}
		catch
		{
		}
	}

	private static bool ExtractFonts(string directory)
	{
		bool wroteAny = false;
		Directory.CreateDirectory(directory);
		Assembly assembly = Assembly.GetExecutingAssembly();
		foreach (string fileName in FontFiles)
		{
			try
			{
				string destination = Path.Combine(directory, fileName);
				using Stream? source = assembly.GetManifestResourceStream("Voidstrap.Resources.Fonts." + fileName);
				if (source == null)
				{
					continue;
				}
				if (File.Exists(destination) && new FileInfo(destination).Length == source.Length)
				{
					continue;
				}
				using FileStream target = File.Create(destination);
				source.CopyTo(target);
				wroteAny = true;
			}
			catch
			{
			}
		}
		return wroteAny;
	}

	private static bool WriteAliasConfig(string home)
	{
		try
		{
			string configDirectory = Path.Combine(home, ".config", "fontconfig", "conf.d");
			Directory.CreateDirectory(configDirectory);
			string configPath = Path.Combine(configDirectory, "60-voidstrap-segoe.conf");
			if (File.Exists(configPath) && File.ReadAllText(configPath) == AliasConfig)
			{
				return false;
			}
			File.WriteAllText(configPath, AliasConfig);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void RefreshFontCache(string fontDirectory)
	{
		try
		{
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = "fc-cache",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			startInfo.ArgumentList.Add("-f");
			startInfo.ArgumentList.Add(fontDirectory);
			using Process? process = Process.Start(startInfo);
			process?.WaitForExit(10000);
		}
		catch
		{
		}
	}
}
