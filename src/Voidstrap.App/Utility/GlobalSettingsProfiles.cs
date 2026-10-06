using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voidstrap.Resources;

namespace Voidstrap.Utility;

internal static class GlobalSettingsProfiles
{
	public static string DirectoryPath => Path.Combine(SettingsProfiles.DirectoryPath, "Global");

	internal sealed class Profile
	{
		public int FormatVersion { get; set; }
		public Dictionary<string, string>? Values { get; set; }
	}

	public static void Save(string name)
	{
		string path = SettingsProfiles.GetPath(DirectoryPath, name);
		Dictionary<string, string> values = new(StringComparer.Ordinal);
		foreach (string key in GBSEditor.KnownProperties.Keys)
		{
			string? value = App.GlobalSettings.GetProperty(key);
			if (value != null)
				values[key] = value;
		}
		Directory.CreateDirectory(DirectoryPath);
		JsonFile.SerializeAtomic(path, new Profile { FormatVersion = 1, Values = values }, JsonOptions.Indented);
	}

	public static Profile Read(string name)
	{
		Profile profile = JsonFile.Deserialize<Profile>(SettingsProfiles.GetPath(DirectoryPath, name), JsonOptions.Compact);
		if (profile.FormatVersion != 1 || profile.Values == null || profile.Values.Any(pair => !GBSEditor.KnownProperties.ContainsKey(pair.Key) || pair.Value == null))
			throw new InvalidDataException(Strings.SettingsProfiles_InvalidFile);
		return profile;
	}

	public static void Apply(Profile profile)
	{
		Dictionary<string, string> values = profile.Values ?? throw new InvalidDataException(Strings.SettingsProfiles_InvalidFile);
		App.GlobalSettings.ResetProperties();
		foreach (KeyValuePair<string, string> pair in values)
		{
			if (!App.GlobalSettings.SetProperty(pair.Key, pair.Value))
			{
				App.GlobalSettings.Load();
				throw new InvalidDataException(Strings.SettingsProfiles_InvalidFile);
			}
		}
		if (!App.GlobalSettings.Save())
		{
			App.GlobalSettings.Load();
			throw new IOException(Strings.GlobalProfiles_SaveFailed);
		}
		Integrations.FrameGeneration.FrameGenManager.SetTargetCap(App.GlobalSettings.GetInt("FramerateCap", 0));
	}
}
