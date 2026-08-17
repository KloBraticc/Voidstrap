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
		foreach (KeyValuePair<string, string> item in _pathMap)
		{
			ModPresetFileData modPresetFileData = new ModPresetFileData(item.Key, item.Value);
			if (modPresetFileData.HashMatches() && !OriginalState)
			{
				OriginalState = true;
			}
			_fileDataMap[item.Key] = modPresetFileData;
		}
	}

	public override void Execute()
	{
		if (NewState == OriginalState)
		{
			return;
		}
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
				else if (!NewState && flag)
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
