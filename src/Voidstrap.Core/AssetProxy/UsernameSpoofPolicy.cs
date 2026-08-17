using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Core.AssetProxy;

public sealed record UsernameSpoofState(
	string OthersName,
	bool OthersApplyIngame,
	bool OthersVerified,
	string SelfName,
	bool SelfApplyIngame,
	bool SelfVerified,
	bool SelfGameCreator)
{
	public static UsernameSpoofState Default { get; } = new("", false, false, "", false, false, false);

	public bool ProfileEnabled => OthersApplyIngame || OthersVerified || SelfApplyIngame || SelfVerified;

	public bool IsEnabled => ProfileEnabled || SelfGameCreator;
}

public readonly record struct UsernameSpoofIdentity(long UserId, string Username);

public sealed class UsernameSpoofRuntime
{
	private readonly object _gate = new();

	private UsernameSpoofState _state;

	public UsernameSpoofRuntime(UsernameSpoofState initialState)
	{
		_state = Normalize(initialState);
	}

	public UsernameSpoofState Current
	{
		get
		{
			lock (_gate)
			{
				return _state;
			}
		}
	}

	public void Set(UsernameSpoofState state)
	{
		lock (_gate)
		{
			_state = Normalize(state);
		}
	}

	private static UsernameSpoofState Normalize(UsernameSpoofState state)
	{
		return state with
		{
			OthersName = state.OthersName ?? "",
			SelfName = state.SelfName ?? ""
		};
	}
}

public sealed class UsernameSpoofProcessor
{
	private static readonly string[] GameJoinFragments =
	[
		"/v1/join-game",
		"/v1/join-game-instance",
		"/v1/join-reserved-game",
		"/v2/join-game",
		"/v2/join-game-instance",
		"/v2/join-reserved-game"
	];

	private static readonly string[] ProfileFragments =
	[
		"/user-profile-api/v1/user/profiles/get-profiles",
		"/v1/user/profiles/get-profiles"
	];

	private readonly Func<CancellationToken, Task<UsernameSpoofIdentity?>> _resolveIdentity;

	public UsernameSpoofProcessor(Func<CancellationToken, Task<UsernameSpoofIdentity?>> resolveIdentity)
	{
		_resolveIdentity = resolveIdentity;
	}

	public static bool CanProcessResponse(string host, string path, UsernameSpoofState state)
	{
		if (!state.IsEnabled)
		{
			return false;
		}
		if (host.Equals("gamejoin.roblox.com", StringComparison.OrdinalIgnoreCase) && ContainsAny(path, GameJoinFragments))
		{
			return state.SelfGameCreator;
		}
		return host.Equals("apis.roblox.com", StringComparison.OrdinalIgnoreCase) &&
			ContainsAny(path, ProfileFragments) &&
			state.ProfileEnabled;
	}

	public async Task<byte[]?> ProcessResponseAsync(string host, string path, byte[] body, UsernameSpoofState state, CancellationToken ct)
	{
		if (body.Length == 0 || !CanProcessResponse(host, path, state))
		{
			return null;
		}
		if (host.Equals("gamejoin.roblox.com", StringComparison.OrdinalIgnoreCase) && ContainsAny(path, GameJoinFragments))
		{
			UsernameSpoofIdentity? identity = await _resolveIdentity(ct).ConfigureAwait(false);
			return identity.HasValue ? UsernameSpoofPolicy.TransformGameJoin(body, state, identity.Value.UserId) : null;
		}
		UsernameSpoofIdentity? current = await _resolveIdentity(ct).ConfigureAwait(false);
		return current.HasValue ? UsernameSpoofPolicy.TransformProfiles(body, state, current.Value.UserId, current.Value.Username) : null;
	}

	private static bool ContainsAny(string value, string[] fragments)
	{
		foreach (string fragment in fragments)
		{
			if (value.Contains(fragment, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}
}

public static class UsernameSpoofPolicy
{
	private const string EmptyNameSentinel = "\u200b";

	private static readonly string[] NameKeys =
	[
		"username",
		"displayName",
		"combinedName",
		"inExperienceCombinedName",
		"contactName",
		"platformName",
		"alias"
	];

	private static readonly (string Id, string Type)[] CreatorPairs =
	[
		("CreatorId", "CreatorType"),
		("CreatorId", "CreatorTypeEnum"),
		("CreatorTargetId", "CreatorType"),
		("CreatorTargetId", "CreatorTypeEnum"),
		("creatorId", "creatorType"),
		("creatorTargetId", "creatorType")
	];

	public static byte[]? TransformProfiles(byte[] body, UsernameSpoofState state, long? currentUserId, string currentUsername)
	{
		if (!state.ProfileEnabled || body.Length == 0)
		{
			return null;
		}
		try
		{
			JsonNode? root = JsonNode.Parse(body);
			if (root is not JsonObject rootObject || rootObject["profileDetails"] is not JsonArray profiles)
			{
				return null;
			}

			int changed = 0;
			foreach (JsonNode? node in profiles)
			{
				if (node is not JsonObject profile)
				{
					continue;
				}
				bool own = IsOwnProfile(profile, currentUserId, currentUsername);
				if (own)
				{
					if (state.SelfApplyIngame)
					{
						changed += SetNameFields(profile, state.SelfName);
					}
					if (state.SelfVerified)
					{
						changed += SetVerified(profile);
					}
				}
				else
				{
					if (state.OthersApplyIngame)
					{
						changed += SetNameFields(profile, state.OthersName);
					}
					if (state.OthersVerified)
					{
						changed += SetVerified(profile);
					}
				}
			}

			return changed == 0 ? null : JsonSerializer.SerializeToUtf8Bytes(rootObject);
		}
		catch
		{
			return null;
		}
	}

	public static byte[]? TransformGameJoin(byte[] body, UsernameSpoofState state, long currentUserId)
	{
		if (!state.SelfGameCreator || currentUserId <= 0 || body.Length == 0)
		{
			return null;
		}
		try
		{
			JsonNode? root = JsonNode.Parse(body);
			if (root == null)
			{
				return null;
			}
			int changed = RewriteCreatorFields(root, currentUserId);
			return changed == 0 ? null : JsonSerializer.SerializeToUtf8Bytes(root);
		}
		catch
		{
			return null;
		}
	}

	private static bool IsOwnProfile(JsonObject profile, long? currentUserId, string currentUsername)
	{
		if (currentUserId.HasValue && TryReadInt64(profile["userId"], out long profileUserId))
		{
			return profileUserId == currentUserId.Value;
		}
		if (currentUsername.Length == 0 || profile["names"] is not JsonObject names)
		{
			return false;
		}
		return TryReadString(names["username"], out string username) && username.Equals(currentUsername, StringComparison.Ordinal);
	}

	private static int SetNameFields(JsonObject profile, string value)
	{
		if (profile["names"] is not JsonObject names)
		{
			names = new JsonObject();
			profile["names"] = names;
		}
		string effective = value.Length == 0 ? EmptyNameSentinel : value;
		int changed = 0;
		foreach (string key in NameKeys)
		{
			if (!TryReadString(names[key], out string current) || !current.Equals(effective, StringComparison.Ordinal))
			{
				names[key] = effective;
				changed++;
			}
		}
		return changed;
	}

	private static int SetVerified(JsonObject profile)
	{
		if (profile["isVerified"] is JsonValue value && value.TryGetValue(out bool current) && current)
		{
			return 0;
		}
		profile["isVerified"] = true;
		return 1;
	}

	private static int RewriteCreatorFields(JsonNode node, long userId)
	{
		if (node is JsonArray array)
		{
			int arrayChanged = 0;
			foreach (JsonNode? child in array)
			{
				if (child != null)
				{
					arrayChanged += RewriteCreatorFields(child, userId);
				}
			}
			return arrayChanged;
		}
		if (node is not JsonObject value)
		{
			return 0;
		}

		int changed = 0;
		foreach ((string idKey, string typeKey) in CreatorPairs)
		{
			if (!value.ContainsKey(idKey) && !value.ContainsKey(typeKey))
			{
				continue;
			}
			if (value.ContainsKey(idKey) && (!TryReadInt64(value[idKey], out long currentId) || currentId != userId))
			{
				value[idKey] = userId;
				changed++;
			}
			if (value.ContainsKey(typeKey))
			{
				JsonNode replacement = CreatorTypeValue(value[typeKey], typeKey);
				if (!JsonNode.DeepEquals(value[typeKey], replacement))
				{
					value[typeKey] = replacement;
					changed++;
				}
			}
		}

		foreach ((string _, JsonNode? child) in value)
		{
			if (child != null)
			{
				changed += RewriteCreatorFields(child, userId);
			}
		}
		return changed;
	}

	private static JsonNode CreatorTypeValue(JsonNode? value, string key)
	{
		if (TryReadString(value, out string current))
		{
			return JsonValue.Create(current.StartsWith("Enum.CreatorType.", StringComparison.Ordinal) ? "Enum.CreatorType.User" : "User")!;
		}
		if (value is JsonValue number && (number.TryGetValue(out int _) || number.TryGetValue(out long _)))
		{
			return JsonValue.Create(key.Equals("CreatorType", StringComparison.Ordinal) ? 0 : 1)!;
		}
		return JsonValue.Create("User")!;
	}

	private static bool TryReadString(JsonNode? node, out string value)
	{
		if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? result) && result != null)
		{
			value = result;
			return true;
		}
		value = "";
		return false;
	}

	private static bool TryReadInt64(JsonNode? node, out long value)
	{
		if (node is JsonValue jsonValue)
		{
			if (jsonValue.TryGetValue(out long number))
			{
				value = number;
				return true;
			}
			if (jsonValue.TryGetValue(out string? text) && long.TryParse(text, out number))
			{
				value = number;
				return true;
			}
		}
		value = 0;
		return false;
	}
}
