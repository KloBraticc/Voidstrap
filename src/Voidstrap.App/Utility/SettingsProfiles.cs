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
		AppSettings settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(App.Settings.Prop, JsonOptions.Compact), JsonOptions.Tolerant)!;
		NormalizeSettings(settings);
		if (RestartNotificationService.TryGetPendingValue("application.hardwareAcceleration", out bool softwareRender))
			settings.WPFSoftwareRender = softwareRender;
		if (RestartNotificationService.TryGetPendingValue("appearance.clearFont", out bool clearFont))
			settings.ClearFont = clearFont;
		Dictionary<string, object> fastFlags = new(StringComparer.Ordinal);
		if (App.FastFlags.Prop != null)
		{
			foreach (KeyValuePair<string, object> pair in App.FastFlags.Prop)
			{
				if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
					fastFlags[pair.Key] = pair.Value;
			}
		}
		Profile profile = new() { FormatVersion = 1, Settings = settings, FastFlags = fastFlags };
		Directory.CreateDirectory(DirectoryPath);
		JsonFile.SerializeAtomic(path, profile, JsonOptions.Indented);
	}

	public static Profile Read(string name)
	{
		string path = GetPath(name);
		Profile profile = JsonFile.Deserialize<Profile>(path, JsonOptions.Tolerant);
		if (profile.FormatVersion == 0 && profile.Settings == null && profile.FastFlags == null)
		{
			string raw = JsonFile.ReadText(path);
			using JsonDocument doc = JsonDocument.Parse(raw, new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip
			});
			if (doc.RootElement.ValueKind == JsonValueKind.Object
				&& (doc.RootElement.TryGetProperty("BootstrapperStyle", out _) || doc.RootElement.TryGetProperty("Channel", out _)))
			{
				AppSettings? directSettings = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions.Tolerant);
				if (directSettings != null)
				{
					NormalizeSettings(directSettings);
					return new Profile
					{
						FormatVersion = 1,
						Settings = directSettings,
						FastFlags = new Dictionary<string, object>(App.FastFlags.Prop, StringComparer.Ordinal)
					};
				}
			}
		}
		if (profile.FormatVersion != 1 || profile.Settings == null)
			throw new InvalidDataException(Strings.SettingsProfiles_InvalidFile);
		Dictionary<string, object> cleanedFlags = new(StringComparer.Ordinal);
		if (profile.FastFlags != null)
		{
			foreach (KeyValuePair<string, object> pair in profile.FastFlags)
			{
				if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
					continue;
				if (pair.Value is JsonElement element)
				{
					if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
						continue;
					if (element.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
						throw new InvalidDataException(Strings.SettingsProfiles_InvalidFile);
				}
				cleanedFlags[pair.Key] = pair.Value;
			}
		}
		profile.FastFlags = cleanedFlags;
		NormalizeSettings(profile.Settings);
		return profile;
	}

	public static void NormalizeSettings(AppSettings settings)
	{
		AppSettings defaults = new();
		NullabilityInfoContext nullability = new();
		foreach (PropertyInfo property in typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (!property.CanRead || !property.CanWrite || property.GetCustomAttribute<JsonIgnoreAttribute>() != null)
				continue;
			if (nullability.Create(property).ReadState == NullabilityState.NotNull && property.GetValue(settings) == null)
				property.SetValue(settings, property.GetValue(defaults));
		}
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
			AppSettings nextSettings = profile.Settings ?? throw new InvalidDataException(Strings.SettingsProfiles_InvalidFile);
			NormalizeSettings(nextSettings);
			App.Settings.Prop = nextSettings;
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
