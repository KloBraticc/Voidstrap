using System;
using System.IO;

namespace Voidstrap.Utility;

internal static class RobloxLocalReset
{
	private const string LogIdent = "RobloxLocalReset";

	private static readonly string[] CacheEntries =
	[
		"rbx-storage",
		"rbx-storage-sc",
		"rbx-storage.db",
		"rbx-storage.db-shm",
		"rbx-storage.db-wal",
		"rbx-storage.id",
		"OTAPatchBackups",
		"ClientSettings",
		"tmp-capture-storage",
		"shadercachevk.bin"
	];

	public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");

	public static void MakeCacheWritable()
	{
		ModAutoFixer.ClearOtaPatchBackups();
		string storage = Path.Combine(Root, "rbx-storage");
		if (!Directory.Exists(storage))
			return;
		int cleared = 0;
		try
		{
			foreach (string file in Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories))
			{
				try
				{
					FileAttributes attributes = File.GetAttributes(file);
					if ((attributes & FileAttributes.ReadOnly) == 0)
						continue;
					File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
					cleared++;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger.WriteLine(LogIdent, "The Roblox asset cache could not be scanned: " + ex.Message);
		}
		App.Logger.WriteLine(LogIdent, "Made " + cleared + " read only Roblox cache files writable again");
	}

	public static string? MoveCacheAside()
	{
		ModAutoFixer.UnlockOtaPatchBackups();
		string backup = Path.Combine(Root, "VoidstrapCacheBackup", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
		int moved = 0;
		foreach (string name in CacheEntries)
		{
			string source = Path.Combine(Root, name);
			string target = Path.Combine(backup, name);
			try
			{
				if (Directory.Exists(source))
				{
					Directory.CreateDirectory(backup);
					Directory.Move(source, target);
					moved++;
				}
				else if (File.Exists(source))
				{
					Directory.CreateDirectory(backup);
					File.SetAttributes(source, File.GetAttributes(source) & ~FileAttributes.ReadOnly);
					File.Move(source, target);
					moved++;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger.WriteLine(LogIdent, "Could not move " + name + " aside: " + ex.Message);
			}
		}
		App.Logger.WriteLine(LogIdent, "Moved " + moved + " Roblox cache entries aside to " + backup + ", the login and graphics settings were kept");
		return moved > 0 ? backup : null;
	}
}
