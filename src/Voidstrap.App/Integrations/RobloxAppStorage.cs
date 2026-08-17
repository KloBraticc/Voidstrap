using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

internal static class RobloxAppStorage
{
	private const long MaximumBytes = 4 * 1024 * 1024;

	private const string TrayKey = "MinimizeToTray";

	private const string StartupKey = "LaunchAtStartup";

	private static readonly object Sync = new();

	private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

	public static string Location => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "LocalStorage", "appStorage.json");

	public static bool Apply()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return false;

		try
		{
			return Apply(App.Settings?.Prop?.RobloxMinimizeToTray == true, App.Settings?.Prop?.RobloxLaunchAtStartup == true);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("RobloxAppStorage::Apply", ex);
			return false;
		}
	}

	public static bool Apply(bool minimizeToTray, bool launchAtStartup)
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return false;

		lock (Sync)
		{
			try
			{
				JsonObject data = Load() ?? new JsonObject();
				data[TrayKey] = minimizeToTray ? "true" : "false";
				data[StartupKey] = launchAtStartup ? "true" : "false";
				Save(data);
				App.Logger?.WriteLine("RobloxAppStorage::Apply", $"System tray: {minimizeToTray}, launch at startup: {launchAtStartup}");
				return true;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteException("RobloxAppStorage::Apply", ex);
				return false;
			}
		}
	}

	public static void Reset()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return;

		lock (Sync)
		{
			try
			{
				JsonObject? data = Load();
				if (data == null)
					return;

				bool changed = data.Remove(TrayKey);
				changed |= data.Remove(StartupKey);
				if (changed)
					Save(data);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteException("RobloxAppStorage::Reset", ex);
			}
		}
	}

	private static JsonObject? Load()
	{
		string path = Location;
		if (!File.Exists(path))
			return null;

		string json = JsonFile.ReadText(path, MaximumBytes);
		if (string.IsNullOrWhiteSpace(json))
			return null;

		return JsonNode.Parse(json) as JsonObject;
	}

	private static void Save(JsonObject data)
	{
		string path = Location;
		string? directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);

		JsonFile.WriteAtomicText(path, data.ToJsonString(WriteOptions), false);
	}
}
