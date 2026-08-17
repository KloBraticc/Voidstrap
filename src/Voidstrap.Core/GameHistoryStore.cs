using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed record GameHistoryEntry(
	long PlaceId,
	long UniverseId,
	string JobId,
	string AccessCode,
	DateTimeOffset? JoinedAt,
	DateTimeOffset? LeftAt,
	string LaunchData)
{
	public string? BuildDeeplink()
	{
		if (PlaceId <= 0)
		{
			return null;
		}

		string deeplink = "roblox://experiences/start?placeId=" + PlaceId.ToString(CultureInfo.InvariantCulture);
		if (!string.IsNullOrWhiteSpace(AccessCode))
		{
			deeplink += "&accessCode=" + Uri.EscapeDataString(AccessCode);
		}
		else if (!string.IsNullOrWhiteSpace(JobId))
		{
			deeplink += "&gameInstanceId=" + Uri.EscapeDataString(JobId);
		}

		if (!string.IsNullOrWhiteSpace(LaunchData))
		{
			deeplink += "&launchData=" + Uri.EscapeDataString(LaunchData);
		}

		return deeplink;
	}
}

public sealed class GameHistoryStore
{
	private const int MaximumEntries = 100;
	private const int MaximumSourceEntries = 1000;
	private const int MaximumServerValueLength = 1024;
	private const int MaximumLaunchDataLength = 16384;
	private const long MaximumFileBytes = 8L * 1024 * 1024;

	private readonly IPlatformPaths _paths;

	public GameHistoryStore(IPlatformPaths paths)
	{
		_paths = paths;
	}

	public string FilePath => Path.Combine(_paths.Storage.Data, "ServerHistory.json");

	public async Task<OperationResult<IReadOnlyCollection<GameHistoryEntry>>> LoadAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			if (!File.Exists(FilePath))
			{
				return OperationResult<IReadOnlyCollection<GameHistoryEntry>>.Success(Array.Empty<GameHistoryEntry>());
			}
			string source = await ReadTextBoundedAsync(FilePath, cancellationToken);
			return OperationResult<IReadOnlyCollection<GameHistoryEntry>>.Success(Parse(source));
		}
		catch (OperationCanceledException)
		{
			return OperationResult<IReadOnlyCollection<GameHistoryEntry>>.Fail("OperationCanceled", "Game history loading was canceled");
		}
		catch (JsonException exception)
		{
			return OperationResult<IReadOnlyCollection<GameHistoryEntry>>.Fail("GameHistoryInvalid", exception.Message);
		}
		catch (InvalidDataException exception)
		{
			return OperationResult<IReadOnlyCollection<GameHistoryEntry>>.Fail("GameHistoryTooLarge", exception.Message);
		}
		catch (IOException exception)
		{
			return OperationResult<IReadOnlyCollection<GameHistoryEntry>>.Fail("GameHistoryReadFailed", exception.Message);
		}
		catch (UnauthorizedAccessException exception)
		{
			return OperationResult<IReadOnlyCollection<GameHistoryEntry>>.Fail("GameHistoryAccessDenied", exception.Message, CapabilityState.RequiresPermission);
		}
	}

	private static async Task<string> ReadTextBoundedAsync(string path, CancellationToken cancellationToken)
	{
		await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length > MaximumFileBytes)
			throw new InvalidDataException("Game history is too large");
		byte[] data = new byte[checked((int)stream.Length)];
		int offset = 0;
		while (offset < data.Length)
		{
			int read = await stream.ReadAsync(data.AsMemory(offset), cancellationToken);
			if (read == 0)
				throw new EndOfStreamException();
			offset += read;
		}
		if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
			throw new InvalidDataException("Game history is too large");
		using MemoryStream memory = new(data, writable: false);
		using StreamReader reader = new(memory, Encoding.UTF8, true);
		return await reader.ReadToEndAsync(cancellationToken);
	}

	public static IReadOnlyCollection<GameHistoryEntry> Parse(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return Array.Empty<GameHistoryEntry>();
		}

		using JsonDocument document = JsonDocument.Parse(source);
		if (document.RootElement.ValueKind != JsonValueKind.Array)
		{
			throw new JsonException("Game history must contain a JSON array");
		}
		if (document.RootElement.GetArrayLength() > MaximumSourceEntries)
		{
			throw new JsonException("Game history contains too many entries");
		}

		List<GameHistoryEntry> entries = new();
		foreach (JsonElement value in document.RootElement.EnumerateArray())
		{
			if (value.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			long placeId = GetInt64(value, "PlaceId");
			if (placeId <= 0)
			{
				continue;
			}

			entries.Add(new GameHistoryEntry(
				placeId,
				GetInt64(value, "UniverseId"),
				GetString(value, "JobId", MaximumServerValueLength),
				GetString(value, "AccessCode", MaximumServerValueLength),
				GetDateTimeOffset(value, "TimeJoined"),
				GetDateTimeOffset(value, "TimeLeft"),
				GetString(value, "RPCLaunchData", MaximumLaunchDataLength)));
		}

		return Normalize(entries);
	}

	private static IReadOnlyCollection<GameHistoryEntry> Normalize(IEnumerable<GameHistoryEntry> entries)
	{
		Dictionary<string, GameHistoryEntry> unique = new(StringComparer.Ordinal);
		foreach (GameHistoryEntry entry in entries)
		{
			string key = entry.PlaceId.ToString(CultureInfo.InvariantCulture) + "\u001F" + entry.JobId + "\u001F" + entry.AccessCode;
			if (!unique.TryGetValue(key, out GameHistoryEntry? existing) || GetSortValue(entry) > GetSortValue(existing))
			{
				unique[key] = entry;
			}
		}

		return unique.Values
			.OrderByDescending(GetSortValue)
			.Take(MaximumEntries)
			.ToArray();
	}

	private static DateTimeOffset GetSortValue(GameHistoryEntry entry)
	{
		return entry.JoinedAt ?? entry.LeftAt ?? DateTimeOffset.MinValue;
	}

	private static long GetInt64(JsonElement value, string name)
	{
		if (!TryGetProperty(value, name, out JsonElement property))
		{
			return 0;
		}

		if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long number))
		{
			return number;
		}

		return property.ValueKind == JsonValueKind.String
			&& long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
			? parsed
			: 0;
	}

	private static string GetString(JsonElement value, string name, int maximumLength)
	{
		if (!TryGetProperty(value, name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
		{
			return string.Empty;
		}

		string result = property.GetString() ?? string.Empty;
		return result.Length <= maximumLength ? result : result[..maximumLength];
	}

	private static DateTimeOffset? GetDateTimeOffset(JsonElement value, string name)
	{
		if (!TryGetProperty(value, name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
		{
			return null;
		}

		return DateTimeOffset.TryParse(
			property.GetString(),
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
			out DateTimeOffset parsed)
			? parsed
			: null;
	}

	private static bool TryGetProperty(JsonElement value, string name, out JsonElement property)
	{
		if (value.TryGetProperty(name, out property))
		{
			return true;
		}

		foreach (JsonProperty candidate in value.EnumerateObject())
		{
			if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				property = candidate.Value;
				return true;
			}
		}

		property = default;
		return false;
	}
}
