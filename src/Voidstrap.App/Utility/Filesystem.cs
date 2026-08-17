using System;
using System.IO;

namespace Voidstrap.Utility;

internal static class Filesystem
{
	internal static long GetFreeDiskSpace(string path)
	{
		DriveInfo[] drives = DriveInfo.GetDrives();
		foreach (DriveInfo driveInfo in drives)
		{
			if (path.ToUpperInvariant().StartsWith(driveInfo.Name.ToUpperInvariant()))
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
}
