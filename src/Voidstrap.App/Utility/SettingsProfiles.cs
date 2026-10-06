using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Voidstrap.Models.Persistable;
using Voidstrap.Resources;
using Voidstrap.UI;

namespace Voidstrap.Utility;

internal static class SettingsProfiles
{
	public static string DirectoryPath => Path.Combine(Paths.DocumentsData, "Profiles");

	internal sealed class Profile
	{
		public int FormatVersion { get; set; }
		public AppSettings? Settings { get; set; }
		public Dictionary<string, object>? FastFlags { get; set; }
	}

	public static string[] List() => List(DirectoryPath);

	public static string[] List(string directory)
	{
		return Directory.Exists(directory)
			? Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension)
				.OfType<string>().OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray()
			: [];
	}

	public static string GetPath(string name) => GetPath(DirectoryPath, name);

	public static string GetPath(string directory, string name)
	{
		name = name.Trim();
		if (name.Length is 0 or > 80 || name.EndsWith('.') || name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character))
			|| Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])($|\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
			throw new ArgumentException(Strings.SettingsProfiles_InvalidName);
		return Path.Combine(directory, name + ".json");
	}

	public static void Save(string name)
	{
		string path = GetPath(name);
		AppSettings settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(App.Settings.Prop))!;
		if (RestartNotificationService.TryGetPendingValue("application.hardwareAcceleration", out bool softwareRender))
			settings.WPFSoftwareRender = softwareRender;
		if (RestartNotificationService.TryGetPendingValue("appearance.clearFont", out bool clearFont))
			settings.ClearFont = clearFont;
		Profile profile = new() { FormatVersion = 1, Settings = settings, FastFlags = new(App.FastFlags.Prop) };
		Directory.CreateDirectory(DirectoryPath);
		JsonFile.SerializeAtomic(path, profile, JsonOptions.Indented);
	}

	public static Profile Read(string name)
	{
		Profile profile = JsonFile.Deserialize<Profile>(GetPath(name), JsonOptions.Compact);
		if (profile.FormatVersion != 1 || profile.Settings == null || profile.FastFlags == null
			|| profile.FastFlags.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null
				|| pair.Value is JsonElement element && element.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)))
			throw new InvalidDataException(Strings.SettingsProfiles_InvalidFile);
		NullabilityInfoContext nullability = new();
		foreach (PropertyInfo property in typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
			if (property.CanRead && property.CanWrite && property.GetCustomAttribute<JsonIgnoreAttribute>() == null
				&& nullability.Create(property).ReadState == NullabilityState.NotNull && property.GetValue(profile.Settings) == null)
				throw new InvalidDataException(Strings.SettingsProfiles_InvalidFile);
		return profile;
	}

	public static void Apply(Profile profile)
	{
		App.Settings.FlushDeferred();
		App.FastFlags.FlushDeferred();
		App.Settings.RefreshFromDisk();
		AppSettings previous = App.Settings.Prop;
		Dictionary<string, object> previousFlags = App.FastFlags.Prop;
		bool settingsSaved = false;
		try
		{
			App.Settings.Prop = profile.Settings ?? throw new InvalidDataException(Strings.SettingsProfiles_InvalidFile);
			App.FastFlags.Prop = profile.FastFlags ?? throw new InvalidDataException(Strings.SettingsProfiles_InvalidFile);
			App.Settings.SaveChecked();
			settingsSaved = true;
			App.FastFlags.SaveChecked();
			App.FastFlags.OriginalProp = new(App.FastFlags.Prop);
		}
		catch (Exception failure)
		{
			App.Settings.Prop = previous;
			App.FastFlags.Prop = previousFlags;
			if (settingsSaved)
			{
				try
				{
					App.Settings.SaveChecked(false);
				}
				catch (Exception rollbackFailure)
				{
					throw new AggregateException(Strings.SettingsProfiles_RestoreFailed, failure, rollbackFailure);
				}
			}
			throw;
		}
	}

	public static void Delete(string name) => Delete(DirectoryPath, name);

	public static void Delete(string directory, string name)
	{
		string path = GetPath(directory, name);
		File.Delete(path);
		File.Delete(path + ".bak");
	}
}
