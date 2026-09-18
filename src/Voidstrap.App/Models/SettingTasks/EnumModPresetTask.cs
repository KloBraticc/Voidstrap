using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voidstrap.Models.Entities;
using Voidstrap.Models.SettingTasks.Base;
using Voidstrap.Utility;

namespace Voidstrap.Models.SettingTasks;

public class EnumModPresetTask<T> : EnumBaseTask<T> where T : struct, Enum
{
	private readonly Dictionary<T, Dictionary<string, ModPresetFileData>> _fileDataMap = new Dictionary<T, Dictionary<string, ModPresetFileData>>();

	private readonly Dictionary<T, Dictionary<string, string>> _map;

	public EnumModPresetTask(string name, Dictionary<T, Dictionary<string, string>> map)
		: base("ModPreset", name)
	{
		_map = map;
		foreach (KeyValuePair<T, Dictionary<string, string>> item in _map)
		{
			Dictionary<string, ModPresetFileData> dictionary = new Dictionary<string, ModPresetFileData>();
			foreach (KeyValuePair<string, string> item2 in item.Value)
			{
				ModPresetFileData modPresetFileData = new ModPresetFileData(item2.Key, item2.Value);
				if (modPresetFileData.HashMatches() && OriginalState.Equals(default(T)))
				{
					OriginalState = item.Key;
				}
				dictionary[item2.Key] = modPresetFileData;
			}
			_fileDataMap[item.Key] = dictionary;
		}
	}

	public override void Execute()
	{
		if (NewState.Equals(default(T)))
		{
			foreach (Dictionary<string, ModPresetFileData> files in _fileDataMap.Values)
				DeleteMatchingFiles(files, null);
		}
		else if (_fileDataMap.TryGetValue(NewState, out Dictionary<string, ModPresetFileData>? target))
		{
			foreach (KeyValuePair<T, Dictionary<string, ModPresetFileData>> other in _fileDataMap)
			{
				if (!other.Key.Equals(NewState))
					DeleteMatchingFiles(other.Value, target);
			}
			foreach (ModPresetFileData value in target.Values)
			{
				if (value.HashMatches())
					continue;
				Directory.CreateDirectory(Path.GetDirectoryName(value.FullFilePath)!);
				using Stream resource = value.ResourceStream;
				using MemoryStream memoryStream = new MemoryStream();
				resource.CopyTo(memoryStream);
				Filesystem.AssertReadOnly(value.FullFilePath);
				File.WriteAllBytes(value.FullFilePath, memoryStream.ToArray());
			}
		}
		OriginalState = NewState;
	}

	private static void DeleteMatchingFiles(Dictionary<string, ModPresetFileData> files, Dictionary<string, ModPresetFileData>? keep)
	{
		foreach (KeyValuePair<string, ModPresetFileData> entry in files)
		{
			if (keep != null && keep.ContainsKey(entry.Key))
				continue;
			if (!entry.Value.HashMatches())
				continue;
			Filesystem.AssertReadOnly(entry.Value.FullFilePath);
			File.Delete(entry.Value.FullFilePath);
		}
	}
}
