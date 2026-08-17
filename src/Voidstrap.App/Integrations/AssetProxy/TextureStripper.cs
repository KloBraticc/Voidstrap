using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Voidstrap.Core.AssetWarp;

namespace Voidstrap.Integrations.AssetProxy;

internal enum AssetWarpRouteKind
{
	Local,
	Cdn
}

internal sealed record AssetWarpRoute(AssetWarpRouteKind Kind, string Value, bool Immutable = false);

internal sealed class AssetBatchContext
{
	public long PlaceId { get; init; }

	public List<string> RequestIds { get; } = [];

	public Dictionary<string, string> AssetIds { get; } = new(StringComparer.Ordinal);

	public Dictionary<string, AssetWarpRoute> Routes { get; } = new(StringComparer.Ordinal);
}

public static class TextureStripper
{
	private sealed class RuleSet
	{
		public Dictionary<string, string> IdReplacements { get; } = new(StringComparer.OrdinalIgnoreCase);

		public HashSet<string> Removals { get; } = new(StringComparer.OrdinalIgnoreCase);

		public Dictionary<string, AssetWarpRoute> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);
	}

	private const int MaxRouteEntries = 20000;

	private const int MaxRuleFiles = 64;

	private const long MaxRuleFileBytes = 8388608;

	private static readonly ConcurrentDictionary<string, AssetWarpRoute> CdnRoutes = new(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentDictionary<string, string> CdnAssetIds = new(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentQueue<string> RouteOrder = new();

	private static readonly System.Threading.Lock RulesGate = new();

	private static RuleSet _rules = new();

	private static string _rulesSignature = "";

	private static DateTime _nextRuleScanUtc;

	public static bool IsEnabled =>
		App.Settings.Prop.AssetWarpEnabled &&
		(App.Settings.Prop.AssetWarpDisableAllTextures ||
		App.Settings.Prop.AssetWarpDisableAllDecals ||
		App.Settings.Prop.AssetWarpDisableAllImages ||
		App.Settings.Prop.AssetWarpDisableAllAnimations ||
		App.Settings.Prop.AssetWarpDisableAllMeshes);

	public static bool HasConfiguredRules
	{
		get
		{
			if (!App.Settings.Prop.AssetWarpEnabled)
			{
				return false;
			}
			RuleSet rules = GetRules();
			return rules.IdReplacements.Count > 0 || rules.Removals.Count > 0 || rules.Routes.Count > 0;
		}
	}

	public static bool RequiresCacheReset => IsEnabled || HasConfiguredRules;

	public static bool IsBatchRequest(string host, string path)
	{
		return App.Settings.Prop.AssetWarpEnabled &&
			host.Equals("assetdelivery.roblox.com", StringComparison.OrdinalIgnoreCase) &&
			path.Contains("/v1/assets/batch", StringComparison.OrdinalIgnoreCase);
	}

	internal static byte[] PrepareBatchRequest(byte[] body, long placeId, out AssetBatchContext? context)
	{
		context = null;
		if (body.Length == 0)
		{
			return body;
		}

		JsonNode? parsed;
		try
		{
			parsed = JsonNode.Parse(body);
		}
		catch
		{
			return body;
		}

		if (parsed is not JsonArray source)
		{
			return body;
		}

		RuleSet rules = GetRules();
		AssetBatchContext batch = new() { PlaceId = placeId };
		JsonArray output = [];
		bool modified = false;

		foreach (JsonNode? node in source)
		{
			if (node is not JsonObject entry)
			{
				continue;
			}

			string requestId = ReadString(entry["requestId"]);
			string assetId = NormalizeKey(entry["assetId"]);
			string typeId = NormalizeKey(entry["assetTypeId"]);
			string typeName = ReadString(entry["assetType"]).ToLowerInvariant();
			string[] keys = [assetId, typeId, typeName];
			string? matchedKey = keys.FirstOrDefault(key => key.Length > 0 && (rules.IdReplacements.ContainsKey(key) || rules.Routes.ContainsKey(key)));

			if (matchedKey == null && ShouldRemove(keys, typeId, typeName, rules))
			{
				modified = true;
				continue;
			}

			JsonObject outbound = (JsonObject)entry.DeepClone();
			if (matchedKey != null && rules.IdReplacements.TryGetValue(matchedKey, out string? replacementId))
			{
				outbound["assetId"] = long.TryParse(replacementId, out long numericId) ? JsonValue.Create(numericId) : JsonValue.Create(replacementId);
				modified = true;
			}
			if (matchedKey != null && rules.Routes.TryGetValue(matchedKey, out AssetWarpRoute? route) && requestId.Length > 0)
			{
				batch.Routes[requestId] = route;
			}

			batch.RequestIds.Add(requestId);
			if (requestId.Length > 0 && assetId.Length > 0)
			{
				batch.AssetIds[requestId] = assetId;
			}
			output.Add(outbound);
		}

		context = batch;
		if (!modified)
		{
			return body;
		}

		byte[] result = JsonSerializer.SerializeToUtf8Bytes(output);
		App.Logger?.WriteLine("TextureStripper", "Asset batch updated: " + source.Count + " to " + output.Count);
		return result;
	}

	internal static void ObserveBatchResponse(AssetBatchContext? context, byte[] body)
	{
		if (context == null || body.Length == 0)
		{
			return;
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(body);
			if (document.RootElement.ValueKind != JsonValueKind.Array)
			{
				return;
			}

			int index = 0;
			foreach (JsonElement item in document.RootElement.EnumerateArray())
			{
				string requestId = item.TryGetProperty("requestId", out JsonElement requestElement) ? ReadString(requestElement) : "";
				if (requestId.Length == 0 && index < context.RequestIds.Count)
				{
					requestId = context.RequestIds[index];
				}
				index++;
				if (!item.TryGetProperty("location", out JsonElement locationElement))
				{
					continue;
				}
				string location = locationElement.GetString() ?? "";
				if (location.Length == 0)
				{
					continue;
				}

				string key = BaseUrl(location);
				RouteOrder.Enqueue(key);
				if (context.AssetIds.TryGetValue(requestId, out string? assetId))
				{
					CdnAssetIds[key] = assetId;
					string? hash = ExtractHash(location);
					AssetCaptureStore.NoteIdForHash(hash, assetId);
					AssetCaptureStore.AddMetadata(assetId, hash, location);
					AssetPreloadCache.ObserveBatchAssetForPlace(context.PlaceId, assetId, hash, location);
				}
				if (context.Routes.TryGetValue(requestId, out AssetWarpRoute? route))
				{
					CdnRoutes[key] = route;
				}
			}
			TrimRoutes();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("TextureStripper", "Batch response mapping failed: " + ex.Message);
		}
	}

	internal static bool TryGetRoute(string host, string path, out AssetWarpRoute? route)
	{
		GetRules();
		return CdnRoutes.TryGetValue(BaseUrl("https://" + host + path), out route);
	}

	public static void InvalidateRuntimeState()
	{
		lock (RulesGate)
		{
			_nextRuleScanUtc = DateTime.MinValue;
			ClearRoutes();
		}
	}

	internal static string? ResolveAssetId(string host, string path)
	{
		return CdnAssetIds.TryGetValue(BaseUrl("https://" + host + path), out string? assetId) ? assetId : null;
	}

	private static bool ShouldRemove(IEnumerable<string> keys, string typeId, string typeName, RuleSet rules)
	{
		if (keys.Any(key => key.Length > 0 && rules.Removals.Contains(key)))
		{
			return true;
		}
		return AssetTypeRemovalPolicy.ShouldRemove(
			typeId,
			typeName,
			App.Settings.Prop.AssetWarpDisableAllTextures,
			App.Settings.Prop.AssetWarpDisableAllDecals,
			App.Settings.Prop.AssetWarpDisableAllImages,
			App.Settings.Prop.AssetWarpDisableAllAnimations,
			App.Settings.Prop.AssetWarpDisableAllMeshes);
	}

	private static RuleSet GetRules()
	{
		lock (RulesGate)
		{
			if (DateTime.UtcNow < _nextRuleScanUtc)
			{
				return _rules;
			}
			_nextRuleScanUtc = DateTime.UtcNow.AddSeconds(2);

			try
			{
				List<string> files = GetRuleFiles();
				string signature = string.Join("|", files.Select(BuildFileSignature));
				if (signature == _rulesSignature)
				{
					return _rules;
				}

				RuleSet loaded = new();
				foreach (string file in files)
				{
					LoadRules(file, loaded);
				}
				ClearRoutes();
				_rules = loaded;
				_rulesSignature = signature;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("TextureStripper", "Replacement rule scan failed: " + ex.Message);
			}
			return _rules;
		}
	}

	private static string BuildFileSignature(string path)
	{
		try
		{
			FileInfo file = new(path);
			return path + ":" + file.LastWriteTimeUtc.Ticks + ":" + file.Length;
		}
		catch
		{
			return path + ":0:0";
		}
	}

	private static List<string> GetRuleFiles()
	{
		Directory.CreateDirectory(Paths.AssetProxy);
		string configDirectory = Path.Combine(Paths.AssetProxy, "Configs");
		Directory.CreateDirectory(configDirectory);
		List<string> files = [.. Directory.EnumerateFiles(configDirectory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(MaxRuleFiles)];
		string direct = Path.Combine(Paths.AssetProxy, "Replacements.json");
		if (File.Exists(direct))
		{
			files.Add(direct);
		}
		return files;
	}

	private static void LoadRules(string path, RuleSet target)
	{
		try
		{
			FileInfo fileInfo = new(path);
			if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > MaxRuleFileBytes)
				return;
			using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
			JsonElement rules = document.RootElement;
			if (rules.ValueKind == JsonValueKind.Object && rules.TryGetProperty("replacement_rules", out JsonElement nested))
			{
				rules = nested;
			}
			if (rules.ValueKind != JsonValueKind.Array)
			{
				return;
			}

			foreach (JsonElement rule in rules.EnumerateArray())
			{
				if (rule.ValueKind != JsonValueKind.Object || rule.TryGetProperty("enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.False)
				{
					continue;
				}
				if (!rule.TryGetProperty("replace_ids", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array)
				{
					continue;
				}
				string mode = rule.TryGetProperty("mode", out JsonElement modeElement) ? modeElement.GetString()?.ToLowerInvariant() ?? "id" : "id";
				foreach (JsonElement id in ids.EnumerateArray())
				{
					string key = NormalizeKey(id);
					if (key.Length == 0)
					{
						continue;
					}
					ApplyRule(target, key, mode, rule, Path.GetDirectoryName(path)!);
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("TextureStripper", "Could not load replacement rules: " + ex.Message);
		}
	}

	private static void ApplyRule(RuleSet target, string key, string mode, JsonElement rule, string baseDirectory)
	{
		target.IdReplacements.Remove(key);
		target.Removals.Remove(key);
		target.Routes.Remove(key);
		if (mode == "remove")
		{
			target.Removals.Add(key);
			return;
		}
		if (mode == "id")
		{
			string replacement = rule.TryGetProperty("with_id", out JsonElement withId) ? NormalizeKey(withId) : rule.TryGetProperty("replace_with", out JsonElement replaceWith) ? NormalizeKey(replaceWith) : "";
			if (replacement.Length > 0 && replacement is not "0" and not "1")
			{
				target.IdReplacements[key] = replacement;
			}
			else
			{
				target.Removals.Add(key);
			}
			return;
		}
		if (mode == "cdn" && rule.TryGetProperty("cdn_url", out JsonElement cdnElement))
		{
			string value = cdnElement.GetString() ?? "";
			if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && !value.Contains('\r') && !value.Contains('\n'))
			{
				target.Routes[key] = new AssetWarpRoute(AssetWarpRouteKind.Cdn, uri.AbsoluteUri);
			}
			return;
		}
		if (mode == "local" && rule.TryGetProperty("local_path", out JsonElement localElement))
		{
			string value = localElement.GetString() ?? "";
			if (!Path.IsPathRooted(value))
			{
				value = Path.GetFullPath(Path.Combine(baseDirectory, value));
			}
			if (File.Exists(value))
			{
				target.Routes[key] = new AssetWarpRoute(AssetWarpRouteKind.Local, value);
			}
		}
	}

	private static string NormalizeKey(JsonNode? node)
	{
		return node == null ? "" : node.ToJsonString().Trim('"').Trim().ToLowerInvariant();
	}

	private static string NormalizeKey(JsonElement element)
	{
		return element.ValueKind == JsonValueKind.String ? (element.GetString() ?? "").Trim().ToLowerInvariant() : element.GetRawText().Trim().ToLowerInvariant();
	}

	private static string ReadString(JsonNode? node)
	{
		return node == null ? "" : node.ToJsonString().Trim('"');
	}

	private static string ReadString(JsonElement element)
	{
		return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText().Trim('"');
	}

	private static string BaseUrl(string value)
	{
		int query = value.IndexOf('?');
		return query >= 0 ? value[..query] : value;
	}

	private static string? ExtractHash(string location)
	{
		string value = BaseUrl(location);
		string candidate = value[(value.LastIndexOf('/') + 1)..];
		return candidate.Length == 32 && candidate.All(Uri.IsHexDigit) ? candidate.ToLowerInvariant() : null;
	}

	private static void TrimRoutes()
	{
		while ((CdnRoutes.Count > MaxRouteEntries || CdnAssetIds.Count > MaxRouteEntries) && RouteOrder.TryDequeue(out string? key))
		{
			CdnRoutes.TryRemove(key, out _);
			CdnAssetIds.TryRemove(key, out _);
		}
	}

	private static void ClearRoutes()
	{
		CdnRoutes.Clear();
		CdnAssetIds.Clear();
		while (RouteOrder.TryDequeue(out _))
		{
		}
	}
}
