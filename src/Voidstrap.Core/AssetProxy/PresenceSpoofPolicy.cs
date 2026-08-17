using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Voidstrap.Core.AssetProxy;

public enum PresenceSpoofMode
{
	Off,
	Online,
	Studio,
	Offline
}

public static class PresenceSpoofPolicy
{
	private const string PulseFragment = "/user-heartbeats-api/pulse";

	private const string ActionReportFragment = "/user-heartbeats-api/action-report";

	public static bool ShouldSuppress(PresenceSpoofMode mode, string host, string path, string method)
	{
		if (mode == PresenceSpoofMode.Off || !IsPost(host, method))
		{
			return false;
		}
		if (path.Contains(ActionReportFragment, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return mode == PresenceSpoofMode.Offline && path.Contains(PulseFragment, StringComparison.OrdinalIgnoreCase);
	}

	public static byte[]? TransformRequest(PresenceSpoofMode mode, string host, string path, string method, byte[] body, string sessionId)
	{
		if (!CanTransformRequest(mode, host, path, method))
		{
			return null;
		}

		try
		{
			JsonObject root;
			if (body.Length == 0)
			{
				root = new JsonObject();
			}
			else if (JsonNode.Parse(body) is JsonObject parsed)
			{
				root = parsed;
			}
			else
			{
				return null;
			}
			(string containerKey, JsonObject? session) = FindObject(root, "SessionInfo");
			if (session == null)
			{
				containerKey = "SessionInfo";
				session = new JsonObject();
				root[containerKey] = session;
			}

			bool lowerCamel = char.IsLower(containerKey[0]);
			SetString(session, "SessionId", lowerCamel, sessionId, false);
			SetString(session, "ClientType", lowerCamel, mode == PresenceSpoofMode.Studio ? "Studio" : "Player", true);
			SetString(session, "Location", lowerCamel, mode == PresenceSpoofMode.Studio ? "Studio" : "Website", true);
			return JsonSerializer.SerializeToUtf8Bytes(root);
		}
		catch
		{
			return null;
		}
	}

	public static bool CanTransformRequest(PresenceSpoofMode mode, string host, string path, string method)
	{
		return mode is PresenceSpoofMode.Online or PresenceSpoofMode.Studio &&
			IsPost(host, method) &&
			path.Contains(PulseFragment, StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsPost(string host, string method)
	{
		return host.Equals("apis.roblox.com", StringComparison.OrdinalIgnoreCase) &&
			method.Equals("POST", StringComparison.OrdinalIgnoreCase);
	}

	private static (string Key, JsonObject? Value) FindObject(JsonObject value, string name)
	{
		foreach ((string key, JsonNode? node) in value)
		{
			if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				return (key, node as JsonObject);
			}
		}
		return (name, null);
	}

	private static void SetString(JsonObject value, string name, bool lowerCamel, string replacement, bool overwrite)
	{
		string? existingKey = null;
		foreach ((string key, JsonNode? _) in value)
		{
			if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				existingKey = key;
				break;
			}
		}

		string keyName = existingKey ?? (lowerCamel ? char.ToLowerInvariant(name[0]) + name.Substring(1) : name);
		if (overwrite || value[keyName] == null)
		{
			value[keyName] = replacement;
		}
	}
}
