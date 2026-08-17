using System;
using System.IO;

namespace Voidstrap.Utility;

internal static class InstallRecord
{
	private static string RecordPath
	{
		get
		{
			string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			if (string.IsNullOrEmpty(root))
			{
				root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			}
			return Path.Combine(root, "Voidstrap", "install.txt");
		}
	}

	public static string? Read()
	{
		try
		{
			string path = RecordPath;
			if (!File.Exists(path))
			{
				return null;
			}
			string value = File.ReadAllText(path).Trim();
			return string.IsNullOrWhiteSpace(value) ? null : value;
		}
		catch
		{
			return null;
		}
	}

	public static void Write(string installLocation)
	{
		if (string.IsNullOrWhiteSpace(installLocation))
		{
			return;
		}
		try
		{
			string path = RecordPath;
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			string temporary = path + ".tmp";
			File.WriteAllText(temporary, installLocation);
			File.Move(temporary, path, true);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("InstallRecord::Write", "Could not save install location: " + ex.Message);
		}
	}

	public static void Delete()
	{
		try
		{
			string path = RecordPath;
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}

	public static void MakeExecutable(string path)
	{
		if (Platform.IsWindows)
		{
			return;
		}
		try
		{
			if (File.Exists(path))
			{
				File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
					| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
					| UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("InstallRecord::MakeExecutable", "Could not set executable bit: " + ex.Message);
		}
	}
}
