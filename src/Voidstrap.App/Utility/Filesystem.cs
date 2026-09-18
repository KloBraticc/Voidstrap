using System;
using System.IO;
using System.Linq;

namespace Voidstrap.Utility;

internal static class Filesystem
{
	internal static long GetFreeDiskSpace(string path)
	{
		DriveInfo[] drives = DriveInfo.GetDrives();
		foreach (DriveInfo driveInfo in drives)
		{
			if (path.StartsWith(driveInfo.Name, StringComparison.InvariantCultureIgnoreCase))
			{
				return driveInfo.AvailableFreeSpace;
			}
		}
		return -1L;
	}

	internal static void AssertReadOnly(string filePath)
	{
		FileInfo fileInfo = new FileInfo(filePath);
		if (fileInfo.Exists && fileInfo.IsReadOnly)
		{
			fileInfo.IsReadOnly = false;
			App.Logger.WriteLine("Filesystem::AssertReadOnly", "The following file was made writable: " + filePath);
		}
	}

	internal static void CopyWritableFile(string sourcePath, string destinationPath, bool overwrite = true)
	{
		if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
		{
			AssertWritable(destinationPath);
			return;
		}
		string? directory = Path.GetDirectoryName(destinationPath);
		if (string.IsNullOrEmpty(directory))
		{
			throw new DirectoryNotFoundException("The destination folder could not be resolved.");
		}
		Directory.CreateDirectory(directory);
		AssertWritable(destinationPath);
		File.Copy(sourcePath, destinationPath, overwrite);
		AssertWritable(destinationPath);
	}

	internal static void DeleteWritableFile(string filePath)
	{
		if (!File.Exists(filePath))
		{
			return;
		}
		AssertWritable(filePath);
		File.Delete(filePath);
	}

	private static void AssertWritable(string filePath)
	{
		if (!File.Exists(filePath))
		{
			return;
		}
		FileAttributes attributes = File.GetAttributes(filePath);
		FileAttributes writableAttributes = attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
		if (writableAttributes != attributes)
		{
			File.SetAttributes(filePath, writableAttributes);
		}
	}

	internal static void AssertReadOnlyDirectory(string directoryPath)
	{
		DirectoryInfo directoryInfo = new DirectoryInfo(directoryPath);
		if (!directoryInfo.Exists)
		{
			return;
		}
		directoryInfo.Attributes = FileAttributes.Normal;
		FileSystemInfo[] fileSystemInfos = directoryInfo.GetFileSystemInfos("*", SearchOption.AllDirectories);
		foreach (FileSystemInfo fileSystemInfo in fileSystemInfos)
		{
			try
			{
				fileSystemInfo.Attributes = FileAttributes.Normal;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("Filesystem::AssertReadOnlyDirectory", "Failed to change attributes for " + fileSystemInfo.FullName + ": " + ex.Message);
			}
		}
		App.Logger.WriteLine("Filesystem::AssertReadOnlyDirectory", "Removed protected attributes from directory: " + directoryPath);
	}

	internal static void DeleteDirectoryRobust(string directoryPath)
	{
		if (!Directory.Exists(directoryPath))
		{
			return;
		}
		AssertReadOnlyDirectory(directoryPath);
		try
		{
			Directory.Delete(directoryPath, true);
			return;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			App.Logger.WriteLine("Filesystem::DeleteDirectoryRobust", "Bulk delete failed, removing file by file: " + ex.Message);
		}
		System.Collections.Generic.List<string> stubborn = new System.Collections.Generic.List<string>();
		foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
		{
			if (!TryDeleteFile(file))
			{
				stubborn.Add(file);
			}
		}
		if (stubborn.Count > 0)
		{
			System.Threading.Thread.Sleep(400);
			stubborn.RemoveAll(TryDeleteFile);
		}
		if (stubborn.Count > 0)
		{
			string graveyard = Path.Combine(Path.GetTempPath(), "Voidstrap", "PendingDelete", Guid.NewGuid().ToString("N"));
			foreach (string file in stubborn.ToArray())
			{
				try
				{
					Directory.CreateDirectory(graveyard);
					File.Move(file, Path.Combine(graveyard, Guid.NewGuid().ToString("N") + Path.GetExtension(file)), true);
					stubborn.Remove(file);
					App.Logger.WriteLine("Filesystem::DeleteDirectoryRobust", "Moved a locked file aside for later cleanup: " + file);
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("Filesystem::DeleteDirectoryRobust", "Could not remove " + file + ": " + ex.Message);
				}
			}
		}
		if (stubborn.Count > 0)
		{
			throw new IOException(stubborn.Count + " file(s) are still in use or protected, for example " + Path.GetFileName(stubborn[0]) + ". Close anything using the install (including Roblox and asset tools) and try again.");
		}
		foreach (string dir in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories).OrderByDescending(static path => path.Length))
		{
			try
			{
				Directory.Delete(dir, false);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("Filesystem::DeleteDirectoryRobust", "Could not remove folder " + dir + ": " + ex.Message);
			}
		}
		Directory.Delete(directoryPath, true);
	}

	private static bool TryDeleteFile(string file)
	{
		for (int attempt = 0; attempt < 3; attempt++)
		{
			try
			{
				File.SetAttributes(file, FileAttributes.Normal);
				File.Delete(file);
				return true;
			}
			catch (FileNotFoundException)
			{
				return true;
			}
			catch (DirectoryNotFoundException)
			{
				return true;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				if (attempt == 2)
				{
					return false;
				}
				System.Threading.Thread.Sleep(150);
			}
		}
		return false;
	}
}
