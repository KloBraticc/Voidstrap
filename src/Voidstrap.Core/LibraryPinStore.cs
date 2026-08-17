using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Voidstrap.Core;

public sealed record LibraryPin(long PlaceId, long UniverseId, string Name);

public static class LibraryPinStore
{
	private const string SettingKey = "LibraryPins";

	private const int MaximumPins = 1000;

	private const int MaximumNameLength = 256;

	private static readonly JsonSerializerOptions BrowserSerializerOptions = new(JsonSerializerDefaults.Web);

	public static IReadOnlyList<LibraryPin> Get(SettingsDocument document)
	{
		if (document is null)
		{
			throw new ArgumentNullException(nameof(document));
		}

		if (document.Root[SettingKey] is not JsonArray source)
		{
			return Array.Empty<LibraryPin>();
		}

		List<LibraryPin> pins = new(Math.Min(source.Count, MaximumPins));
		foreach (JsonNode? node in source.Take(MaximumPins))
		{
			if (node is not JsonObject value)
			{
				continue;
			}

			long placeId = GetInt64(value, "PlaceId");
			long universeId = GetInt64(value, "UniverseId");
			string name = GetString(value, "Name");
			pins.Add(new LibraryPin(placeId, universeId, name));
		}

		return Normalize(pins);
	}

	public static bool Add(SettingsDocument document, long placeId, long universeId, string? name)
	{
		if (document is null)
		{
			throw new ArgumentNullException(nameof(document));
		}

		if (placeId <= 0 && universeId <= 0)
		{
			return false;
		}

		string resolvedName = NormalizeName(name);
		List<LibraryPin> pins = Get(document).ToList();
		int index = pins.FindIndex(pin =>
			(universeId > 0 && pin.UniverseId == universeId) ||
			(placeId > 0 && pin.PlaceId == placeId));
		if (index < 0)
		{
			pins.Add(new LibraryPin(placeId, universeId, resolvedName));
			Set(document, pins);
			return true;
		}

		LibraryPin current = pins[index];
		LibraryPin updated = current with
		{
			PlaceId = current.PlaceId > 0 ? current.PlaceId : placeId,
			UniverseId = current.UniverseId > 0 ? current.UniverseId : universeId,
			Name = string.IsNullOrWhiteSpace(resolvedName) ? current.Name : resolvedName
		};
		if (updated == current)
		{
			return false;
		}

		pins[index] = updated;
		Set(document, pins);
		return true;
	}

	public static bool Remove(SettingsDocument document, long placeId)
	{
		if (document is null)
		{
			throw new ArgumentNullException(nameof(document));
		}

		if (placeId <= 0)
		{
			return false;
		}

		List<LibraryPin> pins = Get(document).ToList();
		int removed = pins.RemoveAll(pin => pin.PlaceId == placeId);
		if (removed == 0)
		{
			return false;
		}

		Set(document, pins);
		return true;
	}

	public static string BuildBrowserPayload(SettingsDocument document)
	{
		return JsonSerializer.Serialize(Get(document), BrowserSerializerOptions);
	}

	private static IReadOnlyList<LibraryPin> Normalize(IEnumerable<LibraryPin> pins)
	{
		HashSet<long> places = new();
		HashSet<long> universes = new();
		List<LibraryPin> normalized = new();
		foreach (LibraryPin pin in pins)
		{
			if (pin.PlaceId <= 0 && pin.UniverseId <= 0)
			{
				continue;
			}

			if (pin.PlaceId > 0 && places.Contains(pin.PlaceId))
			{
				continue;
			}

			if (pin.UniverseId > 0 && universes.Contains(pin.UniverseId))
			{
				continue;
			}

			if (pin.PlaceId > 0)
				places.Add(pin.PlaceId);
			if (pin.UniverseId > 0)
				universes.Add(pin.UniverseId);
			normalized.Add(pin with { Name = NormalizeName(pin.Name) });
			if (normalized.Count >= MaximumPins)
				break;
		}

		return normalized;
	}

	private static void Set(SettingsDocument document, IEnumerable<LibraryPin> pins)
	{
		document.Set(SettingKey, Normalize(pins));
	}

	private static string NormalizeName(string? name)
	{
		string value = name?.Trim() ?? string.Empty;
		return value.Length <= MaximumNameLength ? value : value[..MaximumNameLength];
	}

	private static long GetInt64(JsonObject value, string name)
	{
		if (!TryGetNode(value, name, out JsonNode? node) || node is not JsonValue jsonValue)
		{
			return 0;
		}

		try
		{
			long number = JsonSerializer.Deserialize<long>(jsonValue.ToJsonString());
			return number;
		}
		catch (JsonException)
		{
			try
			{
				string? text = JsonSerializer.Deserialize<string>(jsonValue.ToJsonString());
				return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
			}
			catch (JsonException)
			{
				return 0;
			}
		}
	}

	private static string GetString(JsonObject value, string name)
	{
		return TryGetNode(value, name, out JsonNode? node)
			&& node is JsonValue jsonValue
			&& jsonValue.TryGetValue<string>(out string? text)
			? text ?? string.Empty
			: string.Empty;
	}

	private static bool TryGetNode(JsonObject value, string name, out JsonNode? node)
	{
		if (value.TryGetPropertyValue(name, out node))
		{
			return true;
		}

		foreach ((string key, JsonNode? candidate) in value)
		{
			if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
			{
				node = candidate;
				return true;
			}
		}

		node = null;
		return false;
	}
}
