using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

internal static class LibraryStore
{
	private const string LOG_IDENT = "LibraryStore";

	private const long MaximumPinBytes = 8L * 1024 * 1024;

	private static readonly object Sync = new();

	private static List<AppSettings.LibraryPin>? _pins;

	public static string SnapshotPath => Path.Combine(Paths.Library, "LibrarySnapshot.json");

	private static string PinsPath => Path.Combine(Paths.Library, "Pins.json");

	public static List<AppSettings.LibraryPin> Pins
	{
		get
		{
			lock (Sync)
			{
				return _pins ??= Load();
			}
		}
	}

	public static void Save()
	{
		lock (Sync)
		{
			Write(_pins ?? []);
		}
	}

	private static void Write(List<AppSettings.LibraryPin> pins)
	{
		try
		{
			JsonFile.SerializeAtomic(PinsPath, pins, JsonOptions.Indented, false);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "The library pins could not be saved: " + ex.Message);
		}
	}

	private static List<AppSettings.LibraryPin> Load()
	{
		try
		{
			if (File.Exists(PinsPath))
			{
				return JsonFile.Deserialize<List<AppSettings.LibraryPin>>(PinsPath, JsonOptions.Tolerant, MaximumPinBytes) ?? [];
			}

			List<AppSettings.LibraryPin> settingsPins = App.Settings.Prop.LibraryPins ?? [];
			if (settingsPins.Count == 0)
			{
				return [];
			}

			List<AppSettings.LibraryPin> imported = JsonSerializer.Deserialize<List<AppSettings.LibraryPin>>(JsonSerializer.Serialize(settingsPins)) ?? [];
			Write(imported);
			App.Logger?.WriteLine(LOG_IDENT, "Moved " + imported.Count + " pinned games into the library folder");
			return imported;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "The library pins could not be read: " + ex.Message);
			return [];
		}
	}
}
