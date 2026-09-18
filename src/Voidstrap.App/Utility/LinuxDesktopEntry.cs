using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Resources;
using Voidstrap.Core;
using Voidstrap.Platform.Linux;
using Voidstrap.Resources;

namespace Voidstrap.Utility;

internal static class LinuxDesktopEntry
{
	private const string EntryFileName = "io.github.KloBraticc.Voidstrap.desktop";

	private const string IconName = "io.github.KloBraticc.Voidstrap";

	private const string LauncherFileName = "io.github.KloBraticc.Voidstrap";

	private static string DataHome => ResolveXdgDirectory("XDG_DATA_HOME", ".local", "share");

	private static string ApplicationsDirectory => Path.Combine(DataHome, "applications");

	private static string IconRootDirectory => Path.Combine(DataHome, "icons", "hicolor");

	private static string IconDirectory => Path.Combine(IconRootDirectory, "256x256", "apps");

	private static string LauncherPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", LauncherFileName);

	public static void EnsureInstalled(string executablePath)
	{
		if (!Platform.IsLinux || LinuxFlatpakHost.IsSandboxed || string.IsNullOrEmpty(executablePath))
		{
			return;
		}
		try
		{
			string launchPath = EnsureLauncher(executablePath);
			string entryPath = Path.Combine(ApplicationsDirectory, EntryFileName);
			string iconPath = Path.Combine(IconDirectory, IconName + ".png");
			if (File.Exists(entryPath) && File.Exists(iconPath)
				&& string.Equals(File.ReadAllText(entryPath), BuildDesktopEntry(launchPath), StringComparison.Ordinal)
				&& IconIsCurrent(iconPath))
			{
				RepairManagedShortcuts(launchPath);
				RemoveSupersededEntries();
				Refresh();
				return;
			}
		}
		catch
		{
		}
		Install(executablePath, false);
	}

	public static void Install(string executablePath, bool createDesktopShortcut = false)
	{
		if (!Platform.IsLinux || LinuxFlatpakHost.IsSandboxed || string.IsNullOrEmpty(executablePath))
		{
			return;
		}
		try
		{
			string launchPath = EnsureLauncher(executablePath);
			Directory.CreateDirectory(ApplicationsDirectory);
			Directory.CreateDirectory(IconDirectory);

			string iconPath = Path.Combine(IconDirectory, IconName + ".png");
			WriteIcon(iconPath);

			string entryPath = Path.Combine(ApplicationsDirectory, EntryFileName);
			string contents = BuildDesktopEntry(launchPath);

			WriteAtomic(entryPath, contents);
			MakeExecutable(entryPath);
			RemoveSupersededEntries();
			if (createDesktopShortcut)
				WriteDesktopShortcut(contents);
			else
				RepairCanonicalDesktopShortcut(contents);
			RepairManagedShortcuts(launchPath);
			Refresh();

			App.Logger?.WriteLine("LinuxDesktopEntry::Install", "Desktop entry written to " + entryPath);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxDesktopEntry::Install", "Could not create desktop entry: " + ex.Message);
		}
	}

	private static readonly string[] SupersededEntryFileNames =
	[
		"voidstrap.desktop",
		"voidstrap-roblox.desktop",
		"voidstrap-roblox-player.desktop",
		"voidstrap-roblox-studio.desktop",
		"voidstrap-roblox-studio-auth.desktop"
	];

	private static void RemoveSupersededEntries()
	{
		foreach (string name in SupersededEntryFileNames)
		{
			try
			{
				string path = Path.Combine(ApplicationsDirectory, name);
				if (!File.Exists(path))
					continue;

				string contents = File.ReadAllText(path);
				if (contents.IndexOf("Voidstrap", StringComparison.OrdinalIgnoreCase) < 0
					|| contents.IndexOf("Exec=", StringComparison.Ordinal) < 0)
					continue;

				File.Delete(path);
				App.Logger?.WriteLine("LinuxDesktopEntry::RemoveSupersededEntries", "Removed the superseded desktop entry " + name);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("LinuxDesktopEntry::RemoveSupersededEntries", "Could not remove " + name + ": " + ex.Message);
			}
		}

		try
		{
			string desktop = ResolveDesktopDirectory();
			string path = Path.Combine(desktop, "voidstrap.desktop");
			if (!string.IsNullOrEmpty(desktop) && File.Exists(path))
			{
				string contents = File.ReadAllText(path);
				if (contents.Contains("Voidstrap", StringComparison.OrdinalIgnoreCase)
                    && contents.Contains("Exec="))
				{
					File.Delete(path);
					App.Logger?.WriteLine("LinuxDesktopEntry::RemoveSupersededEntries", "Removed the superseded desktop shortcut voidstrap.desktop");
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxDesktopEntry::RemoveSupersededEntries", "Could not remove the legacy desktop shortcut: " + ex.Message);
		}
	}

	private static string BuildDesktopEntry(string executablePath)
	{
		return string.Join('\n',
			"[Desktop Entry]",
			"Type=Application",
			"Name=Voidstrap",
			"GenericName=Roblox Bootstrapper",
			"Comment=Customize and launch Roblox through Sober on Linux",
			"Exec=" + EscapeExecArgument(executablePath) + " %u",
			"Icon=" + IconName,
			"Terminal=false",
			"Categories=Game;",
			"Keywords=roblox;voidstrap;bootstrapper;sober;",
			"StartupNotify=true",
			"StartupWMClass=voidstrap",
			"X-GNOME-WMClass=voidstrap",
			"MimeType=x-scheme-handler/roblox;x-scheme-handler/roblox-player;x-scheme-handler/roblox-studio;x-scheme-handler/roblox-studio-auth;x-scheme-handler/voidstrap;",
			"Actions=LaunchRoblox;LaunchRobloxStudio;Settings;",
			"",
			"[Desktop Action LaunchRoblox]",
			"Name=Launch Roblox",
			"Exec=" + EscapeExecArgument(executablePath) + " -player",
			"",
			"[Desktop Action LaunchRobloxStudio]",
			"Name=Launch Roblox Studio",
			"Exec=" + EscapeExecArgument(executablePath) + " -studio",
			"",
			"[Desktop Action Settings]",
			"Name=Voidstrap Settings",
			"Exec=" + EscapeExecArgument(executablePath) + " -settings",
			"");
	}

	private static bool IconIsCurrent(string iconPath)
	{
		try
		{
			byte[]? selected = ReadSelectedIconPng();
			if (selected != null)
			{
				using FileStream current = File.OpenRead(iconPath);
				return CryptographicOperations.FixedTimeEquals(SHA256.HashData(selected), SHA256.HashData(current));
			}

			StreamResourceInfo? info = Application.GetResourceStream(new Uri("pack://application:,,,/Voidstrap.png", UriKind.Absolute));
			using Stream? source = info?.Stream;
			if (source == null)
				return false;
			using FileStream installed = File.OpenRead(iconPath);
			byte[] sourceHash = SHA256.HashData(source);
			byte[] installedHash = SHA256.HashData(installed);
			return CryptographicOperations.FixedTimeEquals(sourceHash, installedHash);
		}
		catch
		{
			return false;
		}
	}

	private static byte[]? ReadSelectedIconPng()
	{
		try
		{
			if (App.Settings?.Prop == null)
				return null;

			System.Windows.Media.Imaging.BitmapSource? source = Voidstrap.Extensions.IconEx.LoadPortableIcon(App.Settings.Prop.ActiveBootstrapperIcon, 256);
			if (source == null)
				return null;

			System.Windows.Media.Imaging.BitmapSource bgra = source.Format == System.Windows.Media.PixelFormats.Bgra32
				? source
				: new System.Windows.Media.Imaging.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);

			int width = bgra.PixelWidth;
			int height = bgra.PixelHeight;
			if (width <= 0 || height <= 0)
				return null;

			int stride = checked(width * 4);
			byte[] pixels = new byte[checked(stride * height)];
			bgra.CopyPixels(pixels, stride, 0);

			using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image =
				SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(pixels, width, height);
			using MemoryStream buffer = new();
			image.Save(buffer, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
			return buffer.ToArray();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxDesktopEntry::ReadSelectedIconPng", "Could not encode the selected icon: " + ex.Message);
			return null;
		}
	}

	public static void RefreshIcon()
	{
		if (!Platform.IsLinux || LinuxFlatpakHost.IsSandboxed)
			return;

		byte[]? encoded = ReadSelectedIconPng();
		if (encoded == null)
			return;

		Task.Run(delegate
		{
			try
			{
				Directory.CreateDirectory(IconDirectory);
				string iconPath = Path.Combine(IconDirectory, IconName + ".png");
				string temporary = iconPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
				File.WriteAllBytes(temporary, encoded);
				File.Move(temporary, iconPath, true);
				if (!OperatingSystem.IsWindows())
					File.SetUnixFileMode(iconPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
				RunQuiet("gtk-update-icon-cache", "-f", "-t", IconRootDirectory);
				App.Logger?.WriteLine("LinuxDesktopEntry::RefreshIcon", "Wrote the selected icon to " + iconPath);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("LinuxDesktopEntry::RefreshIcon", "Could not refresh the desktop icon: " + ex.Message);
			}
		});
	}

	public static string ApplicationsFolder => ApplicationsDirectory;

	public static string DefaultIconName => IconName;

	public static string IconFilePath => Path.Combine(IconDirectory, IconName + ".png");

	private static readonly string[] IconFileExtensions = [".png", ".svg", ".ico", ".xpm", ".jpg", ".jpeg"];

	public static bool CreateShortcut(string filePath, string name, string executablePath, string arguments, string? iconPath)
	{
		if (!Platform.IsLinux || string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(executablePath))
			return false;

		try
		{
			executablePath = EnsureLauncher(executablePath);
			string? folder = Path.GetDirectoryName(filePath);
			if (!string.IsNullOrEmpty(folder))
				Directory.CreateDirectory(folder);

			WriteAtomic(filePath, BuildShortcutEntry(name, executablePath, arguments, iconPath));
			MakeExecutable(filePath);
			RunQuiet("gio", "set", filePath, "metadata::trusted", "true");

			if (!string.IsNullOrEmpty(folder) && string.Equals(Path.GetFullPath(folder), Path.GetFullPath(ApplicationsDirectory), StringComparison.Ordinal))
				RunQuiet("update-desktop-database", ApplicationsDirectory);

			return File.Exists(filePath);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxDesktopEntry::CreateShortcut", "Could not write " + filePath + ": " + ex.Message);
			return false;
		}
	}

	private static string EnsureLauncher(string executablePath)
	{
		try
		{
			string target = Path.GetFullPath(executablePath);
			if (string.Equals(target, LauncherPath, StringComparison.Ordinal) || !File.Exists(target))
				return target;

			Directory.CreateDirectory(Path.GetDirectoryName(LauncherPath)!);
			FileInfo existing = new(LauncherPath);
			if (existing.Exists && existing.LinkTarget == null)
				return target;

			string temporary = LauncherPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				File.CreateSymbolicLink(temporary, target);
				File.Move(temporary, LauncherPath, true);
			}
			finally
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}

			return LauncherPath;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxDesktopEntry::EnsureLauncher", "Could not refresh the stable launcher: " + ex.Message);
			return executablePath;
		}
	}

	private static void RepairManagedShortcuts(string executablePath)
	{
		string desktop = ResolveDesktopDirectory();
		if (!string.IsNullOrEmpty(desktop))
		{
			RepairShortcutIfPresent(Path.Combine(desktop, "Voidstrap.desktop"), "Voidstrap", executablePath, "");
			RepairShortcutIfPresent(Path.Combine(desktop, Strings.Menu_Title + ".desktop"), Strings.Menu_Title, executablePath, "-settings");
			RepairShortcutIfPresent(Path.Combine(desktop, Strings.LaunchMenu_LaunchRoblox + ".desktop"), Strings.LaunchMenu_LaunchRoblox, executablePath, "-player");
			RepairShortcutIfPresent(Path.Combine(desktop, Strings.LaunchMenu_LaunchRobloxStudio + ".desktop"), Strings.LaunchMenu_LaunchRobloxStudio, executablePath, "-studio");
		}

		RepairShortcutIfPresent(Path.Combine(ApplicationsDirectory, "Voidstrap.desktop"), "Voidstrap", executablePath, "");
	}

	private static void RepairShortcutIfPresent(string path, string name, string executablePath, string arguments)
	{
		if (File.Exists(path))
			CreateShortcut(path, name, executablePath, arguments, IconFilePath);
	}

	private static string BuildShortcutEntry(string name, string executablePath, string arguments, string? iconPath)
	{
		return string.Join('\n',
			"[Desktop Entry]",
			"Type=Application",
			"Name=" + DesktopPathValue(string.IsNullOrWhiteSpace(name) ? "Voidstrap" : name),
			"Comment=Launch through Voidstrap",
			"Exec=" + BuildExecValue(executablePath, arguments),
			"Icon=" + ResolveIconValue(iconPath),
			"Terminal=false",
			"Categories=Game;",
			"StartupNotify=true",
			"StartupWMClass=voidstrap",
			"X-GNOME-WMClass=voidstrap",
			"X-Voidstrap-Managed=true",
			"");
	}

	private static string BuildExecValue(string executablePath, string arguments)
	{
		System.Text.StringBuilder builder = new(EscapeExecArgument(executablePath));
		foreach (string token in SplitArguments(arguments))
			builder.Append(' ').Append(EscapeExecArgument(token));
		return builder.ToString();
	}

	private static IEnumerable<string> SplitArguments(string? arguments)
	{
		if (string.IsNullOrWhiteSpace(arguments))
			yield break;

		System.Text.StringBuilder token = new();
		bool quoted = false;
		foreach (char character in arguments)
		{
			if (character == '"')
			{
				quoted = !quoted;
				continue;
			}

			if (!quoted && char.IsWhiteSpace(character))
			{
				if (token.Length > 0)
				{
					yield return token.ToString();
					token.Clear();
				}
				continue;
			}

			token.Append(character);
		}

		if (token.Length > 0)
			yield return token.ToString();
	}

	private static string ResolveIconValue(string? iconPath)
	{
		if (string.IsNullOrWhiteSpace(iconPath))
			return IconName;

		try
		{
			string extension = Path.GetExtension(iconPath);
			if (File.Exists(iconPath) && Array.Exists(IconFileExtensions, value => string.Equals(value, extension, StringComparison.OrdinalIgnoreCase)))
				return DesktopPathValue(Path.GetFullPath(iconPath));
		}
		catch (Exception)
		{
		}

		return IconName;
	}

	public static void Remove()
	{
		if (!Platform.IsLinux || LinuxFlatpakHost.IsSandboxed)
		{
			return;
		}
		try
		{
			string entryPath = Path.Combine(ApplicationsDirectory, EntryFileName);
			if (File.Exists(entryPath))
			{
				File.Delete(entryPath);
			}
			string iconPath = Path.Combine(IconDirectory, IconName + ".png");
			if (File.Exists(iconPath))
			{
				File.Delete(iconPath);
			}
			FileInfo launcher = new(LauncherPath);
			if (launcher.Exists && launcher.LinkTarget != null)
				File.Delete(LauncherPath);
			string desktop = ResolveDesktopDirectory();
			if (!string.IsNullOrEmpty(desktop))
			{
				string shortcutPath = Path.Combine(desktop, EntryFileName);
				if (File.Exists(shortcutPath))
				{
					File.Delete(shortcutPath);
				}
			}
			RunQuiet("update-desktop-database", ApplicationsDirectory);
			RunQuiet("gtk-update-icon-cache", "-f", "-t", IconRootDirectory);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxDesktopEntry::Remove", "Could not remove desktop entry: " + ex.Message);
		}
	}

	private static string ResolveDesktopDirectory()
	{
		string target = string.Empty;
		try
		{
			target = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		}
		catch
		{
		}
		if (string.IsNullOrEmpty(target))
		{
			target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
		}
		try
		{
			Directory.CreateDirectory(target);
			return target;
		}
		catch
		{
			return string.Empty;
		}
	}

	private static void WriteDesktopShortcut(string contents)
	{
		try
		{
			string desktop = ResolveDesktopDirectory();
			if (string.IsNullOrEmpty(desktop))
			{
				return;
			}
			if (File.Exists(Path.Combine(desktop, "Voidstrap.desktop")))
			{
				string duplicatePath = Path.Combine(desktop, EntryFileName);
				if (File.Exists(duplicatePath))
					File.Delete(duplicatePath);
				return;
			}
			string shortcutPath = Path.Combine(desktop, EntryFileName);
			WriteAtomic(shortcutPath, contents);
			MakeExecutable(shortcutPath);
			RunQuiet("gio", "set", shortcutPath, "metadata::trusted", "true");
			App.Logger?.WriteLine("LinuxDesktopEntry::WriteDesktopShortcut", "Desktop shortcut written to " + shortcutPath);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxDesktopEntry::WriteDesktopShortcut", "Could not create desktop shortcut: " + ex.Message);
		}
	}

	private static void RepairCanonicalDesktopShortcut(string contents)
	{
		string desktop = ResolveDesktopDirectory();
		if (string.IsNullOrEmpty(desktop))
			return;

		string shortcutPath = Path.Combine(desktop, EntryFileName);
		if (File.Exists(Path.Combine(desktop, "Voidstrap.desktop")))
		{
			if (File.Exists(shortcutPath))
				File.Delete(shortcutPath);
		}
		else if (File.Exists(shortcutPath))
			WriteDesktopShortcut(contents);
	}

	private static void WriteIcon(string iconPath)
	{
		string temporary = iconPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			byte[]? selected = ReadSelectedIconPng();
			if (selected != null)
			{
				File.WriteAllBytes(temporary, selected);
				File.Move(temporary, iconPath, true);
				if (!OperatingSystem.IsWindows())
					File.SetUnixFileMode(iconPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
				return;
			}

			StreamResourceInfo? info = Application.GetResourceStream(new Uri("pack://application:,,,/Voidstrap.png", UriKind.Absolute));
			if (info?.Stream == null)
			{
				return;
			}
			using Stream source = info.Stream;
			using (FileStream target = File.Create(temporary))
				source.CopyTo(target);
			File.Move(temporary, iconPath, true);
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(iconPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxDesktopEntry::WriteIcon", "Could not write icon: " + ex.Message);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	private static void MakeExecutable(string path)
	{
		try
		{
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
					| UnixFileMode.GroupRead | UnixFileMode.OtherRead);
		}
		catch
		{
		}
	}

	private static void Refresh()
	{
		RunQuiet("update-desktop-database", ApplicationsDirectory);
		RunQuiet("gtk-update-icon-cache", "-f", "-t", IconRootDirectory);
		RunQuiet("xdg-mime", "default", EntryFileName, "x-scheme-handler/roblox");
		RunQuiet("xdg-mime", "default", EntryFileName, "x-scheme-handler/voidstrap");
		RunQuiet("xdg-mime", "default", EntryFileName, "x-scheme-handler/roblox-player");
		RunQuiet("xdg-mime", "default", EntryFileName, "x-scheme-handler/roblox-studio");
		RunQuiet("xdg-mime", "default", EntryFileName, "x-scheme-handler/roblox-studio-auth");
	}

	private static string DesktopPathValue(string value)
	{
		return value
			.Replace("\r", string.Empty, StringComparison.Ordinal)
			.Replace("\n", string.Empty, StringComparison.Ordinal);
	}

	private static string EscapeExecArgument(string value)
	{
		string escaped = value
			.Replace("\\", "\\\\", StringComparison.Ordinal)
			.Replace("\"", "\\\"", StringComparison.Ordinal)
			.Replace("`", "\\`", StringComparison.Ordinal)
			.Replace("$", "\\$", StringComparison.Ordinal)
			.Replace("%", "%%", StringComparison.Ordinal);
		return CanRemainUnquoted(value) ? escaped : "\"" + escaped + "\"";
	}

	private static bool CanRemainUnquoted(string value)
	{
		if (string.IsNullOrEmpty(value))
			return false;

		foreach (char character in value)
		{
			if (!char.IsAsciiLetterOrDigit(character) && character is not '/' and not '.' and not '_' and not '-')
				return false;
		}

		return true;
	}

	private static void RunQuiet(string fileName, params string[] arguments)
	{
		try
		{
			string? executable = new SystemProcessService().FindExecutable(fileName);
			if (string.IsNullOrWhiteSpace(executable))
				return;
			ProcessStartInfo startInfo = new(executable)
			{
				UseShellExecute = false,
				CreateNoWindow = true
			};
			foreach (string argument in arguments)
				startInfo.ArgumentList.Add(argument);
			using Process? process = Process.Start(startInfo);
			process?.WaitForExit(5000);
		}
		catch
		{
		}
	}

	private static string ResolveXdgDirectory(string variable, params string[] fallback)
	{
		string? configured = Environment.GetEnvironmentVariable(variable);
		if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
			return Path.GetFullPath(configured);
		string path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		foreach (string segment in fallback)
			path = Path.Combine(path, segment);
		return path;
	}

	private static void WriteAtomic(string path, string contents)
	{
		string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(temporary, contents);
			File.Move(temporary, path, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}
}
