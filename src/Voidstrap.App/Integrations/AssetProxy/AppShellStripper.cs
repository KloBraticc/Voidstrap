using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.AssetProxy;

public static class AppShellStripper
{
	private const string LogIdent = "AppShellStripper";

	public const string ThumbnailsHost = "thumbnails.roblox.com";

	public static bool IsEnabled =>
		App.Settings.Prop.AssetWarpEnabled &&
		(App.Settings.Prop.AssetWarpDisableAllImages ||
		App.Settings.Prop.AssetWarpDisableAllTextures ||
		App.Settings.Prop.AssetWarpDisableAllDecals);

	public static bool CanProcessResponse(string host, string path, string? userAgent)
	{
		return IsEnabled
			&& host.Equals(ThumbnailsHost, StringComparison.OrdinalIgnoreCase)
			&& path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)
			&& IsRobloxClient(userAgent);
	}

	public static bool IsRobloxClient(string? userAgent)
	{
		return !string.IsNullOrEmpty(userAgent) && userAgent.Contains("Roblox", StringComparison.OrdinalIgnoreCase);
	}

	public static byte[]? ProcessResponse(byte[] body)
	{
		if (body.Length == 0)
		{
			return null;
		}

		JsonNode? parsed;
		try
		{
			parsed = JsonNode.Parse(body);
		}
		catch
		{
			return null;
		}

		if (parsed == null)
		{
			return null;
		}

		int blanked = Blank(parsed);

		if (blanked == 0)
		{
			return null;
		}

		App.Logger?.WriteLine(LogIdent, "Blanked " + blanked + " thumbnails outside of a game");
		return Encoding.UTF8.GetBytes(parsed.ToJsonString());
	}

	private static int Blank(JsonNode node)
	{
		int count = 0;

		if (node is JsonArray array)
		{
			foreach (JsonNode? item in array)
			{
				if (item != null)
				{
					count += Blank(item);
				}
			}
			return count;
		}

		if (node is not JsonObject entry)
		{
			return 0;
		}

		if (entry.ContainsKey("imageUrl") && entry.ContainsKey("state"))
		{
			entry["imageUrl"] = "";
			entry["state"] = "Blocked";
			return 1;
		}

		foreach (KeyValuePair<string, JsonNode?> pair in entry)
		{
			if (pair.Value != null)
			{
				count += Blank(pair.Value);
			}
		}

		return count;
	}
}
