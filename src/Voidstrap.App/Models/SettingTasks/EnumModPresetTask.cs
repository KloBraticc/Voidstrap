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
		if (!NewState.Equals(default(T)))
		{
			foreach (KeyValuePair<string, ModPresetFileData> item in _fileDataMap[NewState])
			{
				ModPresetFileData value = item.Value;
				if (value.HashMatches())
				{
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(value.FullFilePath));
				using (value.ResourceStream)
				{
					using MemoryStream memoryStream = new MemoryStream();
					value.ResourceStream.CopyTo(memoryStream);
					Filesystem.AssertReadOnly(value.FullFilePath);
					File.WriteAllBytes(value.FullFilePath, memoryStream.ToArray());
				}
			}
		}
		else
		{
			foreach (KeyValuePair<string, ModPresetFileData> item2 in _fileDataMap.First().Value)
			{
				Filesystem.AssertReadOnly(item2.Value.FullFilePath);
				File.Delete(item2.Value.FullFilePath);
			}
		}
		OriginalState = NewState;
	}
}
