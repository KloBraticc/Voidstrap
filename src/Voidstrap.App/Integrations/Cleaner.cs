using System;
using System.Collections.Generic;
using System.IO;
using Voidstrap.Enums;

namespace Voidstrap.Integrations;

public class Cleaner
{
	public static Dictionary<string, string> Directories = new Dictionary<string, string>
	{
		{
			"VoidstrapLogs",
			Paths.Logs
		},
		{
			"VoidstrapCache",
			Paths.Downloads
		},
		{
			"RobloxLogs",
			Paths.RobloxLogs
		},
		{
			"RobloxCache",
			Paths.RobloxCache
		}
	};

	public static void DoCleaning()
	{
		App.Logger.WriteLine("Cleaner::DoCleaning", "Cleaner has started");
		int num = App.Settings.Prop.CleanerOptions switch
		{
			CleanerOptions.AfterLaunch => 0,
			CleanerOptions.OneDay => 1,
			CleanerOptions.OneWeek => 7,
			CleanerOptions.TwoWeeks => 14,
			CleanerOptions.ThreeWeeks => 21,
			CleanerOptions.OneMonth => 30,
			CleanerOptions.TwoMonths => 60,
			CleanerOptions.ThreeMonths => 90,
			CleanerOptions.Never => int.MaxValue,
			_ => int.MaxValue,
		};
		DateTime threshold = num == int.MaxValue ? DateTime.MinValue : DateTime.Now.AddDays(-num);
		if (App.Settings.Prop.CleanerOptions == CleanerOptions.Never)
		{
			App.Logger.WriteLine("Cleaner::DoCleaning", "Cleaner is set to Never, nothing to do");
			return;
		}
		foreach (KeyValuePair<string, string> directory in Directories)
		{
			string value = directory.Value;
			string key = directory.Key;
			if (!App.Settings.Prop.CleanerDirectories.Contains(key))
			{
				App.Logger.WriteLine("Cleaner::DoCleaning", "Skipping " + key);
			}
			else
			{
				if (string.IsNullOrEmpty(value) || !Directory.Exists(value))
				{
					continue;
				}
				try
				{
					string[] array = RecursivlyGetFiles(value);
					App.Logger.WriteLine("Cleaner::DoCleaning", $"Running cleaner in {key}, {array.Length} files found");
					string activeLog = App.Logger.FileLocation ?? string.Empty;
					string[] array2 = array;
					foreach (string text in array2)
					{
						if (activeLog.Length > 0 && string.Equals(Path.GetFullPath(text), Path.GetFullPath(activeLog), StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						if (VerifyFile(text, threshold))
						{
							try
							{
								File.Delete(text);
							}
							catch (Exception ex)
							{
								App.Logger.WriteLine("Cleaner::DoCleaning", "Unable to delete " + text);
								App.Logger.WriteException("Cleaner::DoCleaning", ex);
							}
						}
					}
				}
				catch (Exception ex2)
				{
					App.Logger.WriteLine("Cleaner::DoCleaning", "Failed to clean up " + value);
					App.Logger.WriteException("Cleaner::DoCleaning", ex2);
				}
			}
		}
		App.Logger.WriteLine("Cleaner::DoCleaning", "Cleaner finished");
	}

	private static bool VerifyFile(string file, DateTime Threshold)
	{
		if (!File.Exists(file))
		{
			return false;
		}
		DateTime lastWrite;
		DateTime created;
		try
		{
			lastWrite = File.GetLastWriteTime(file);
			created = File.GetCreationTime(file);
		}
		catch
		{
			return false;
		}
		DateTime age = lastWrite > created ? created : lastWrite;
		if (age > Threshold)
		{
			return false;
		}
		if (!file.Contains("Roblox") && !file.Contains("Voidstrap") && !file.Contains(Paths.Base))
		{
			throw new Exception(file + " was in disallowed directory");
		}
		if (file.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase))
		{
			throw new Exception(file + " was in Windows directory");
		}
		return true;
	}

	private static string[] RecursivlyGetFiles(string Folder)
	{
		List<string> list = new List<string>();
		if (string.IsNullOrEmpty(Folder) || !Directory.Exists(Folder))
		{
			throw new Exception("Folder was not found");
		}
		foreach (string item in Directory.EnumerateFiles(Folder, "*.*", SearchOption.AllDirectories))
		{
			list.Add(item);
		}
		return list.ToArray();
	}
}
