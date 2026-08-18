using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.AssetProxy;

public static class RobuxSpoofer
{
	private const string LogIdent = "RobuxSpoofer";

	public const string EconomyHost = "economy.roblox.com";

	private static readonly string[] BalanceFields = { "robux", "balance", "credit" };

	public static bool IsEnabled => App.Settings.Prop.AssetWarpEnabled && TryGetAmount(out _);

	public static bool TryGetAmount(out long amount)
	{
		amount = 0;
		string raw = (App.Settings.Prop.RobuxSpoofAmount ?? string.Empty).Trim();

		if (raw.Length == 0)
		{
			return false;
		}

		raw = raw.Replace(",", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
		return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount) && amount >= 0;
	}

	public static bool CanProcessResponse(string host, string path)
	{
		return IsEnabled
			&& host.Equals(EconomyHost, StringComparison.OrdinalIgnoreCase)
			&& path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)
			&& path.Contains("/currency", StringComparison.OrdinalIgnoreCase);
	}

	public static byte[]? ProcessResponse(byte[] body)
	{
		if (body.Length == 0 || !TryGetAmount(out long amount))
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

		int replaced = Replace(parsed, amount);

		if (replaced == 0)
		{
			return null;
		}

		App.Logger?.WriteLine(LogIdent, "Showed a balance of " + amount + " on this client only, the real balance is unchanged");
		return Encoding.UTF8.GetBytes(parsed.ToJsonString());
	}

	private static int Replace(JsonNode node, long amount)
	{
		int count = 0;

		if (node is JsonArray array)
		{
			foreach (JsonNode? item in array)
			{
				if (item != null)
				{
					count += Replace(item, amount);
				}
			}
			return count;
		}

		if (node is not JsonObject entry)
		{
			return 0;
		}

		List<string> targets = new List<string>();

		foreach (KeyValuePair<string, JsonNode?> pair in entry)
		{
			if (pair.Value is JsonValue value && Array.Exists(BalanceFields, field => field.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)) && value.TryGetValue(out long _))
			{
				targets.Add(pair.Key);
			}
			else if (pair.Value != null)
			{
				count += Replace(pair.Value, amount);
			}
		}

		foreach (string key in targets)
		{
			entry[key] = amount;
			count++;
		}

		return count;
	}
}
