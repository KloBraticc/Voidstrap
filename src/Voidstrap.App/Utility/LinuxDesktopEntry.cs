using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Resources;

namespace Voidstrap.Utility;

internal static class LinuxDesktopEntry
{
	private const string EntryFileName = "voidstrap.desktop";

	private const string IconName = "voidstrap";

	private static string ApplicationsDirectory => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applications");

	private static string IconDirectory => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "icons", "hicolor", "256x256", "apps");

	public static void EnsureInstalled(string executablePath)
	{
		if (!Platform.IsLinux || string.IsNullOrEmpty(executablePath))
		{
			return;
		}
		try
		{
			string entryPath = Path.Combine(ApplicationsDirectory, EntryFileName);
			string iconPath = Path.Combine(IconDirectory, IconName + ".png");
			string desktop = ResolveDesktopDirectory();
			bool shortcutPresent = string.IsNullOrEmpty(desktop) || File.Exists(Path.Combine(desktop, EntryFileName));
			if (File.Exists(entryPath) && File.Exists(iconPath) && shortcutPresent
				&& File.ReadAllText(entryPath).Contains("Exec=" + Escape(executablePath), StringComparison.Ordinal))
			{
				return;
			}
		}
		catch
		{
		}
		Install(executablePath);
	}

	public static void Install(string executablePath)
	{
		if (!Platform.IsLinux || string.IsNullOrEmpty(executablePath))
		{
			return;
		}
		try
		{
			Directory.CreateDirectory(ApplicationsDirectory);
			Directory.CreateDirectory(IconDirectory);

			string iconPath = Path.Combine(IconDirectory, IconName + ".png");
			WriteIcon(iconPath);

			string entryPath = Path.Combine(ApplicationsDirectory, EntryFileName);
			string contents = string.Join('\n',
				"[Desktop Entry]",
				"Type=Application",
				"Name=Voidstrap",
				"GenericName=Roblox Bootstrapper",
				"Comment=Launch and manage Roblox",
				"Exec=" + Escape(executablePath) + " %u",
				"Icon=" + (File.Exists(iconPath) ? iconPath : IconName),
				"Terminal=false",
				"Categories=Game;",
				"Keywords=roblox;voidstrap;bootstrapper;",
				"StartupNotify=true",
				"StartupWMClass=Voidstrap",
				"MimeType=x-scheme-handler/roblox;x-scheme-handler/roblox-player;x-scheme-handler/roblox-studio;x-scheme-handler/roblox-studio-auth;",
				"");

			File.WriteAllText(entryPath, contents);
			MakeExecutable(entryPath);
			Refresh(entryPath);
			WriteDesktopShortcut(contents);

			App.Logger?.WriteLine("LinuxDesktopEntry::Install", "Desktop entry written to " + entryPath);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxDesktopEntry::Install", "Could not create desktop entry: " + ex.Message);
		}
	}

	public static void Remove()
	{
		if (!Platform.IsLinux)
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
			string desktop = ResolveDesktopDirectory();
			if (!string.IsNullOrEmpty(desktop))
			{
				string shortcutPath = Path.Combine(desktop, EntryFileName);
				if (File.Exists(shortcutPath))
				{
					File.Delete(shortcutPath);
				}
			}
			RunQuiet("update-desktop-database", Escape(ApplicationsDirectory));
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
			string shortcutPath = Path.Combine(desktop, EntryFileName);
			File.WriteAllText(shortcutPath, contents);
			MakeExecutable(shortcutPath);
			RunQuiet("gio", "set " + Escape(shortcutPath) + " metadata::trusted true");
			App.Logger?.WriteLine("LinuxDesktopEntry::WriteDesktopShortcut", "Desktop shortcut written to " + shortcutPath);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxDesktopEntry::WriteDesktopShortcut", "Could not create desktop shortcut: " + ex.Message);
		}
	}

	private static void WriteIcon(string iconPath)
	{
		try
		{
			StreamResourceInfo? info = Application.GetResourceStream(new Uri("pack://application:,,,/Voidstrap.png", UriKind.Absolute));
			if (info?.Stream == null)
			{
				return;
			}
			using Stream source = info.Stream;
			using FileStream target = File.Create(iconPath);
			source.CopyTo(target);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxDesktopEntry::WriteIcon", "Could not write icon: " + ex.Message);
		}
	}

	private static void MakeExecutable(string path)
	{
		try
		{
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
				| UnixFileMode.GroupRead | UnixFileMode.OtherRead);
		}
		catch
		{
		}
	}

	private static void Refresh(string entryPath)
	{
		RunQuiet("update-desktop-database", Escape(ApplicationsDirectory));
		RunQuiet("gtk-update-icon-cache", "-f -t " + Escape(Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "icons", "hicolor")));
		RunQuiet("xdg-mime", "default " + EntryFileName + " x-scheme-handler/roblox");
		RunQuiet("xdg-mime", "default " + EntryFileName + " x-scheme-handler/roblox-player");
		RunQuiet("xdg-mime", "default " + EntryFileName + " x-scheme-handler/roblox-studio");
		RunQuiet("xdg-mime", "default " + EntryFileName + " x-scheme-handler/roblox-studio-auth");
	}

	private static string Escape(string value)
	{
		return value.Contains(' ') ? "\"" + value + "\"" : value;
	}

	private static void RunQuiet(string fileName, string arguments)
	{
		try
		{
			Process.Start(new ProcessStartInfo(fileName, arguments)
			{
				UseShellExecute = false,
				CreateNoWindow = true
			})?.Dispose();
		}
		catch
		{
		}
	}
}
