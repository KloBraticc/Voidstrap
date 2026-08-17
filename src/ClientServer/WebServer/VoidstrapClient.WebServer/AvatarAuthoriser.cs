using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VoidstrapClient.Common.Enums;
using VoidstrapClient.WebServer.Models;

namespace VoidstrapClient.WebServer;

internal static class AvatarAuthoriser
{
	private const int MaxCacheEntries = 4096;
	private const int MaxCacheOrderEntries = MaxCacheEntries * 2;
	private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
	private static readonly ConcurrentDictionary<ulong, CachedAssetType> AssetTypes = new ConcurrentDictionary<ulong, CachedAssetType>();
	private static readonly ConcurrentQueue<(ulong Id, CachedAssetType Entry)> CacheOrder = new ConcurrentQueue<(ulong, CachedAssetType)>();
	private static readonly SemaphoreSlim InfoGate = new SemaphoreSlim(1, 1);

	private sealed record CachedAssetType(AvatarAssetType? Type, DateTime ExpiresUtc);

	private static bool TryGetCachedType(ulong id, out AvatarAssetType? type)
	{
		if (AssetTypes.TryGetValue(id, out CachedAssetType? cached))
		{
			if (cached.ExpiresUtc > DateTime.UtcNow)
			{
				type = cached.Type;
				return true;
			}
			AssetTypes.TryRemove(id, out _);
		}
		type = null;
		return false;
	}

	private static void CacheType(ulong id, AvatarAssetType? type)
	{
		CachedAssetType entry = new CachedAssetType(type, DateTime.UtcNow + CacheLifetime);
		AssetTypes[id] = entry;
		CacheOrder.Enqueue((id, entry));
		while ((AssetTypes.Count > MaxCacheEntries || CacheOrder.Count > MaxCacheOrderEntries) && CacheOrder.TryDequeue(out (ulong Id, CachedAssetType Entry) oldest))
		{
			if (AssetTypes.TryGetValue(oldest.Id, out CachedAssetType? current) && ReferenceEquals(current, oldest.Entry))
			{
				AssetTypes.TryRemove(oldest.Id, out _);
			}
		}
	}

	private static bool CanAddType(List<AvatarAssetType> usedTypes, AvatarAssetType type)
	{
		if (type == AvatarAssetType.Hat)
		{
			return usedTypes.Count(value => value == AvatarAssetType.Hat) < 3;
		}
		return !usedTypes.Contains(type);
	}

	private static AvatarAssetType? GetWhitelistedAvatarAssetTypeFromId(int id)
	{
		return id switch
		{
			2 => AvatarAssetType.TShirt,
			11 => AvatarAssetType.Shirt,
			12 => AvatarAssetType.Pants,
			_ => null,
		};
	}

	private static async Task EnsureAssetInfoAsync(IReadOnlyCollection<ulong> ids, CancellationToken cancellationToken)
	{
		await InfoGate.WaitAsync(cancellationToken);
		try
		{
			ulong[] missing = ids.Where(id => !TryGetCachedType(id, out _)).ToArray();
			if (missing.Length == 0)
			{
				return;
			}
			foreach (AssetInformation item in await AssetDelivery.BatchRequest(missing, cancellationToken))
			{
				if (!ulong.TryParse(item.RequestId, out ulong id))
				{
					continue;
				}
				CacheType(id, GetWhitelistedAvatarAssetTypeFromId(item.AssetTypeId));
			}
		}
		finally
		{
			InfoGate.Release();
		}
	}

	public static async Task<IReadOnlyList<ulong>> FilterUnsafeAssetsAsync(IEnumerable<ulong> assets, CancellationToken cancellationToken)
	{
		ulong[] requested = assets.Distinct().Take(100).ToArray();
		List<ulong> remote = new List<ulong>();
		foreach (ulong asset in requested)
		{
			if (AvatarItems.GetById(asset) == null && !TryGetCachedType(asset, out _))
			{
				remote.Add(asset);
			}
		}
		if (remote.Count > 0)
		{
			await EnsureAssetInfoAsync(remote, cancellationToken);
		}

		List<ulong> accepted = new List<ulong>();
		List<AvatarAssetType> usedTypes = new List<AvatarAssetType>();
		foreach (ulong asset in requested)
		{
			AvatarItem local = AvatarItems.GetById(asset);
			AvatarAssetType? type = local?.Type;
			if (type == null && TryGetCachedType(asset, out AvatarAssetType? remoteType))
			{
				type = remoteType;
			}
			if (type.HasValue && CharacterCompatibility.IsCompatible(type.Value) && CanAddType(usedTypes, type.Value))
			{
				accepted.Add(asset);
				usedTypes.Add(type.Value);
			}
		}
		return accepted;
	}
}
