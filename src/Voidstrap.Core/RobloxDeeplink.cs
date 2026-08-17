using System;
using System.Linq;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public static class RobloxDeeplink
{
	private static readonly string[] Schemes = ["roblox-player:", "roblox-studio-auth:", "roblox-studio:", "roblox:"];

	public static RuntimeKind GetRuntimeKind(string? value)
	{
		return TryExtract(value, out Uri? deeplink) && deeplink is not null &&
			deeplink.Scheme.StartsWith("roblox-studio", StringComparison.OrdinalIgnoreCase)
			? RuntimeKind.Studio
			: RuntimeKind.Player;
	}

	public static bool TryExtract(string? value, out Uri? deeplink)
	{
		deeplink = null;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		if (value.Length > 32768 || value.Any(char.IsControl))
		{
			return false;
		}

		int index = -1;
		foreach (string scheme in Schemes)
		{
			int found = value.IndexOf(scheme, StringComparison.OrdinalIgnoreCase);
			if (found >= 0 && (index < 0 || found < index))
				index = found;
		}
		if (index < 0)
		{
			return TryConvertWebsiteLink(value, out deeplink);
		}

		string candidate = ExtractCandidate(value[index..]).Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
		if (candidate.StartsWith("roblox://placeId=", StringComparison.OrdinalIgnoreCase))
			candidate = "roblox://experiences/start?" + candidate["roblox://".Length..];

		if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed))
		{
			return false;
		}

		if (!string.Equals(parsed.Scheme, "roblox", StringComparison.OrdinalIgnoreCase) &&
			!string.Equals(parsed.Scheme, "roblox-player", StringComparison.OrdinalIgnoreCase) &&
			!string.Equals(parsed.Scheme, "roblox-studio", StringComparison.OrdinalIgnoreCase) &&
			!string.Equals(parsed.Scheme, "roblox-studio-auth", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		deeplink = parsed;
		return true;
	}

	private static string ExtractCandidate(string value)
	{
		int quote = value.IndexOfAny(['"', '\'']);
		if (quote >= 0)
			value = value[..quote];
		for (int i = 0; i < value.Length; i++)
		{
			if (!char.IsWhiteSpace(value[i]))
				continue;
			int next = i;
			while (next < value.Length && char.IsWhiteSpace(value[next]))
				next++;
			if (next < value.Length && value[next] == '-')
				return value[..i].Trim();
		}
		return value.Trim();
	}

	private static bool TryConvertWebsiteLink(string value, out Uri? deeplink)
	{
		deeplink = null;
		string candidate = ExtractCandidate(value.Trim().Trim('"', '\''));
		if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? website) ||
			website.Scheme != Uri.UriSchemeHttps ||
			(!string.Equals(website.Host, "roblox.com", StringComparison.OrdinalIgnoreCase) &&
			 !string.Equals(website.Host, "www.roblox.com", StringComparison.OrdinalIgnoreCase)))
		{
			return false;
		}
		if (website.AbsolutePath.Equals("/games/start", StringComparison.OrdinalIgnoreCase))
			return Uri.TryCreate("roblox://experiences/start" + website.Query, UriKind.Absolute, out deeplink);
		string[] segments = website.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length >= 2 && segments[0].Equals("games", StringComparison.OrdinalIgnoreCase) && long.TryParse(segments[1], out long placeId) && placeId > 0)
			return Uri.TryCreate("roblox://experiences/start?placeId=" + placeId, UriKind.Absolute, out deeplink);
		return false;
	}
}
