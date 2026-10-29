using System.Collections.Generic;
using System.IO;
using Voidstrap.Models.Entities;
using Voidstrap.Models.SettingTasks.Base;
using Voidstrap.Utility;

namespace Voidstrap.Models.SettingTasks;

public class ModPresetTask : BoolBaseTask
{
	private Dictionary<string, ModPresetFileData> _fileDataMap = new Dictionary<string, ModPresetFileData>();

	private Dictionary<string, string> _pathMap;

	public ModPresetTask(string name, string path, string resource)
		: this(name, new Dictionary<string, string> { { path, resource } })
	{
	}

	public ModPresetTask(string name, Dictionary<string, string> pathMap)
		: base("ModPreset", name)
	{
		_pathMap = pathMap;
		List<ModPresetFileData> outdated = new List<ModPresetFileData>();
		foreach (KeyValuePair<string, string> item in _pathMap)
		{
			ModPresetFileData modPresetFileData = new ModPresetFileData(item.Key, item.Value);
			bool current = modPresetFileData.HashMatches();
			bool previous = !current && modPresetFileData.MatchesPreviousVersion();
			if ((current || previous) && !OriginalState)
			{
				OriginalState = true;
			}
			if (previous)
			{
				outdated.Add(modPresetFileData);
			}
			_fileDataMap[item.Key] = modPresetFileData;
		}
		foreach (ModPresetFileData data in outdated)
		{
			try
			{
				WriteResource(data);
				App.Logger.WriteLine("ModPresetTask", "Updated " + data.FilePath + " to the current " + Name + " file");
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("ModPresetTask", "Could not update " + data.FilePath + ": " + ex.Message);
			}
		}
	}

	private static void WriteResource(ModPresetFileData value)
	{
		string? folder = Path.GetDirectoryName(value.FullFilePath);
		if (!string.IsNullOrEmpty(folder))
		{
			Directory.CreateDirectory(folder);
		}
		using Stream stream = value.ResourceStream;
		using MemoryStream memoryStream = new MemoryStream();
		stream.CopyTo(memoryStream);
		Filesystem.AssertReadOnly(value.FullFilePath);
		File.WriteAllBytes(value.FullFilePath, memoryStream.ToArray());
	}

	public override void Execute()
	{
		if (NewState == OriginalState)
		{
			return;
		}
		FileModManager.FolderGate.Wait();
		try
		{
			ExecuteCore();
		}
		finally
		{
			FileModManager.FolderGate.Release();
		}
	}

	private void ExecuteCore()
	{
		int failed = 0;
		foreach (KeyValuePair<string, ModPresetFileData> item in _fileDataMap)
		{
			ModPresetFileData value = item.Value;
			if (!value.IsAvailable)
			{
				continue;
			}
			try
			{
				bool flag = value.HashMatches();
				if (NewState && !flag)
				{
					WriteResource(value);
				}
				else if (!NewState && (flag || value.MatchesPreviousVersion()))
				{
					Filesystem.AssertReadOnly(value.FullFilePath);
					File.Delete(value.FullFilePath);
				}
			}
			catch (Exception ex)
			{
				failed++;
				App.Logger.WriteLine("ModPresetTask::Execute", "Could not change " + item.Key + ": " + ex.Message);
			}
		}
		if (failed > 0)
		{
			App.Logger.WriteLine("ModPresetTask::Execute", failed + " file(s) in " + Name + " could not be changed, the setting has been left as it was.");
			return;
		}
		OriginalState = NewState;
	}
}
