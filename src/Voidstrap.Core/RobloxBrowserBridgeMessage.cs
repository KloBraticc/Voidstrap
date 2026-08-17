using System;
using System.Text.Json;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public enum RobloxBrowserBridgeAction
{
	Unknown,
	OpenExternal,
	ScriptRan,
	Matchmake,
	PrivateServer,
	UpdateHeatmap,
	Underrated,
	Users,
	ProfileBanner,
	TranslateTexts,
	PinGame,
	UnpinGame,
	Launch,
	ApiError
}

public sealed record RobloxBrowserBridgeMessage(
	RobloxBrowserBridgeAction Action,
	string? Url,
	long? PlaceId,
	long? UniverseId,
	long? UserId,
	string? ServerId,
	string? Name,
	string? ScriptName);

public static class RobloxBrowserBridgeMessageParser
{
	private const int MaximumMessageLength = 65536;
	private const int MaximumActionLength = 64;
	private const int MaximumUrlLength = 8192;
	private const int MaximumIdentifierLength = 1024;
	private const int MaximumNameLength = 256;

	public static OperationResult<RobloxBrowserBridgeMessage> Parse(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return OperationResult<RobloxBrowserBridgeMessage>.Fail("BrowserBridgeMessageMissing", "The browser bridge message is empty");
		}
		if (message.Length > MaximumMessageLength)
		{
			return OperationResult<RobloxBrowserBridgeMessage>.Fail("BrowserBridgeMessageTooLarge", "The browser bridge message is too large");
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(message);
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object ||
				!root.TryGetProperty("voidstrap", out JsonElement actionElement) ||
				actionElement.ValueKind != JsonValueKind.String)
			{
				return OperationResult<RobloxBrowserBridgeMessage>.Fail("BrowserBridgeActionMissing", "The browser bridge message does not contain an action");
			}

			string action = actionElement.GetString() ?? string.Empty;
			if (!IsValidString(action, MaximumActionLength) ||
				!TryGetString(root, "url", MaximumUrlLength, out string? url) ||
				!TryGetString(root, "serverId", MaximumIdentifierLength, out string? serverId) ||
				!TryGetString(root, "name", MaximumNameLength, out string? name))
			{
				return OperationResult<RobloxBrowserBridgeMessage>.Fail("BrowserBridgeMessageInvalid", "The browser bridge message contains an invalid value");
			}

			return OperationResult<RobloxBrowserBridgeMessage>.Success(new RobloxBrowserBridgeMessage(
				ParseAction(action),
				url,
				GetInt64(root, "placeId"),
				GetInt64(root, "universeId"),
				GetInt64(root, "userId"),
				serverId,
				name,
				name));
		}
		catch (JsonException exception)
		{
			return OperationResult<RobloxBrowserBridgeMessage>.Fail("BrowserBridgeMessageInvalid", exception.Message);
		}
	}

	public static OperationResult<string> BuildPrivateServerDeeplink(long? placeId, string? serverId)
	{
		if (placeId is null || placeId.Value <= 0)
		{
			return OperationResult<string>.Fail("PrivateServerPlaceMissing", "A valid Roblox place is required");
		}
		if (serverId is not null && !IsValidString(serverId, MaximumIdentifierLength))
		{
			return OperationResult<string>.Fail("PrivateServerIdInvalid", "The Roblox server identifier is invalid");
		}

		string deeplink = "roblox://experiences/start?placeId=" + placeId.Value;
		if (!string.IsNullOrWhiteSpace(serverId))
		{
			deeplink += "&gameInstanceId=" + Uri.EscapeDataString(serverId);
		}

		return OperationResult<string>.Success(deeplink);
	}

	private static RobloxBrowserBridgeAction ParseAction(string action)
	{
		return action switch
		{
			"open" => RobloxBrowserBridgeAction.OpenExternal,
			"scriptran" => RobloxBrowserBridgeAction.ScriptRan,
			"matchmake" => RobloxBrowserBridgeAction.Matchmake,
			"privateServer" => RobloxBrowserBridgeAction.PrivateServer,
			"updateHeatmap" => RobloxBrowserBridgeAction.UpdateHeatmap,
			"underrated" => RobloxBrowserBridgeAction.Underrated,
			"vsUsers" => RobloxBrowserBridgeAction.Users,
			"profileBanner" => RobloxBrowserBridgeAction.ProfileBanner,
			"translateTexts" => RobloxBrowserBridgeAction.TranslateTexts,
			"pinGame" => RobloxBrowserBridgeAction.PinGame,
			"unpinGame" => RobloxBrowserBridgeAction.UnpinGame,
			"launch" => RobloxBrowserBridgeAction.Launch,
			"apierror" => RobloxBrowserBridgeAction.ApiError,
			_ => RobloxBrowserBridgeAction.Unknown
		};
	}

	private static bool TryGetString(JsonElement root, string name, int maximumLength, out string? result)
	{
		result = null;
		if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
		{
			return true;
		}
		if (value.ValueKind != JsonValueKind.String)
		{
			return false;
		}

		result = value.GetString();
		return result is null || IsValidString(result, maximumLength);
	}

	private static bool IsValidString(string value, int maximumLength)
	{
		if (value.Length > maximumLength)
		{
			return false;
		}

		foreach (char character in value)
		{
			if (char.IsControl(character))
			{
				return false;
			}
		}

		return true;
	}

	private static long? GetInt64(JsonElement root, string name)
	{
		return root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : null;
	}
}
