using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class TextFontInstaller
{
	private static string? _pendingFontCache;
	private static readonly string[] FontFiles = new[]
	{
		"Inter_18pt-Light.ttf",
		"Inter_18pt-Regular.ttf",
		"Inter_18pt-Medium.ttf",
		"Inter_18pt-SemiBold.ttf",
		"Inter_18pt-Bold.ttf",
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
				string refreshMarker = Path.Combine(fontDirectory, "cache-refresh-pending");
				bool wroteFonts = ExtractFonts(fontDirectory);
				bool wroteConfig = WriteAliasConfig(home);
				if (wroteFonts || wroteConfig)
					File.WriteAllText(refreshMarker, string.Empty);
				if (File.Exists(refreshMarker))
					_pendingFontCache = fontDirectory;
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

	public static async Task RefreshPendingAsync(CancellationToken token)
	{
		string? fontDirectory = Interlocked.Exchange(ref _pendingFontCache, null);
		if (fontDirectory == null)
			return;
		try
		{
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = "fc-cache",
				UseShellExecute = false,
				CreateNoWindow = true
			};
			startInfo.ArgumentList.Add("-f");
			startInfo.ArgumentList.Add(fontDirectory);
			using Process? process = Process.Start(startInfo);
			if (process != null)
			{
				using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
				timeout.CancelAfter(TimeSpan.FromSeconds(10));
				try
				{
					await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
					if (process.ExitCode == 0)
						File.Delete(Path.Combine(fontDirectory, "cache-refresh-pending"));
				}
				catch (OperationCanceledException)
				{
					if (!process.HasExited)
						process.Kill(entireProcessTree: true);
					if (token.IsCancellationRequested)
						throw;
				}
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch
		{
		}
	}
}
