using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Core.AssetProxy;

namespace Voidstrap.Integrations.AssetProxy;

internal static class AssetPreloadCache
{
	private const int MaximumGames = 20;

	private const int MaximumAssetsPerGame = 2000;

	private const long MaximumCatalogBytes = 33554432;

	private const int CriticalAssetCount = 64;

	private const int BackgroundAssetCount = 128;

	private const int MaximumTrackedFiles = 40000;

	private static readonly System.Threading.Lock Gate = new();

	private static readonly System.Threading.Lock FileGate = new();

	private static readonly ConcurrentDictionary<string, string> VerifiedFiles = new(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentDictionary<string, long> TouchedFiles = new(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentDictionary<string, byte> WarmingAssets = new(StringComparer.OrdinalIgnoreCase);

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = false
	};

	private static AssetPreloadCatalog _catalog = new();

	private static Timer? _saveTimer;

	private static CancellationTokenSource? _warmCts;

	private static Task? _warmTask;

	private static bool _loaded;

	private static bool _dirty;

	private static long _activePlaceId;

	private static long _authoritativePlaceId;

	public static long ActivePlaceId => Interlocked.Read(ref _activePlaceId);

	private static string CatalogPath => Path.Combine(Paths.AssetProxy, "AssetPreload.json");

	public static void BeginSession(long placeId)
	{
		if (placeId <= 0)
		{
			return;
		}
		EnsureLoaded();
		Interlocked.Exchange(ref _activePlaceId, placeId);
		AssetCaptureStore.SetCurrentPlaceId(placeId);
		lock (Gate)
		{
			_catalog.TouchGame(placeId, DateTime.UtcNow);
			_dirty = true;
			ScheduleSaveLocked();
		}
	}

	public static void SwitchSession(long placeId)
	{
		if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled)
		{
			return;
		}
		if (placeId > 0)
		{
			Interlocked.Exchange(ref _authoritativePlaceId, placeId);
		}
		if (placeId <= 0 || placeId == Interlocked.Read(ref _activePlaceId))
		{
			return;
		}
		AssetCaptureStore.ResetResolveState();
		BeginSession(placeId);
		if (App.Settings.Prop.AssetWarpEnabled && App.Settings.Prop.AssetWarpPreloadEnabled && AssetProxyServer.IsRunning)
		{
			StartBackgroundWarm(placeId, CancellationToken.None);
		}
	}

	public static void ObservePlaceHeader(string? value)
	{
		if (long.TryParse(value, out long placeId) && placeId > 0 && placeId != Interlocked.Read(ref _activePlaceId) && (Interlocked.Read(ref _authoritativePlaceId) is long authoritative && (authoritative <= 0 || authoritative == placeId)))
		{
			BeginSession(placeId);
		}
	}

	public static void ObserveBatchAsset(string? assetId, string? hash, string? url)
	{
		long placeId = Interlocked.Read(ref _activePlaceId);
		ObserveBatchAssetForPlace(placeId, assetId, hash, url);
	}

	public static void ObserveBatchAssetForPlace(long placeId, string? assetId, string? hash, string? url)
	{
		if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled || !AssetPreloadCatalog.IsValidHash(hash))
		{
			return;
		}
		if (placeId <= 0)
		{
			return;
		}
		EnsureLoaded();
		lock (Gate)
		{
			if (_catalog.Observe(placeId, assetId, hash, url, DateTime.UtcNow))
			{
				_catalog.Trim(MaximumGames, MaximumAssetsPerGame);
				_dirty = true;
				ScheduleSaveLocked();
			}
		}
	}

	public static bool TryResolveCachedRoute(string? assetId, string? hash, out AssetWarpRoute? route)
	{
		route = null;
		if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled || string.IsNullOrWhiteSpace(assetId) || !AssetPreloadCatalog.IsValidHash(hash))
		{
			return false;
		}
		string normalizedHash = hash!.ToLowerInvariant();
		if (!AssetCaptureStore.TryGetCachedFile(assetId, normalizedHash, out string filePath) || !ValidateFile(normalizedHash, filePath))
		{
			return false;
		}
		TouchFile(filePath);
		route = new AssetWarpRoute(AssetWarpRouteKind.Local, filePath, true);
		return true;
	}

	public static async Task WarmCriticalAsync(long placeId, CancellationToken ct)
	{
		if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled || placeId <= 0)
		{
			return;
		}
		BeginSession(placeId);
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(TimeSpan.FromSeconds(4));
		try
		{
			await Task.WhenAll(
				AssetPreloadProactive.DiscoverAsync(placeId, deadline.Token),
				AssetPreloadAvatar.PreloadAsync(deadline.Token)).ConfigureAwait(false);
			AssetPreloadHint[] hints = [.. Snapshot(placeId).Take(CriticalAssetCount)];
			await WarmAsync(hints, deadline.Token).ConfigureAwait(false);
			await AssetCaptureStore.ResolveHashesAsync(CriticalAssetCount * 4, deadline.Token).ConfigureAwait(false);
			AssetPreloadHint[] expanded = [.. Snapshot(placeId).Take(CriticalAssetCount)];
			await WarmAsync(expanded, deadline.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
		}
	}

	public static void StartBackgroundWarm(long placeId, CancellationToken ct)
	{
		if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled || placeId <= 0)
		{
			return;
		}
		CancellationTokenSource current = CancellationTokenSource.CreateLinkedTokenSource(ct);
		CancellationTokenSource? previous = Interlocked.Exchange(ref _warmCts, current);
		SafeCancel(previous);
		Task task = Task.Run(() => WarmBackgroundAsync(placeId, current), current.Token);
		Volatile.Write(ref _warmTask, task);
	}

	public static void Flush()
	{
		string contents;
		lock (Gate)
		{
			if (!_loaded || !_dirty)
			{
				return;
			}
			_catalog.Trim(MaximumGames, MaximumAssetsPerGame);
			contents = JsonSerializer.Serialize(_catalog, JsonOptions);
			_dirty = false;
		}
		try
		{
			lock (FileGate)
			{
				Voidstrap.Utility.JsonFile.WriteAtomicText(CatalogPath, contents);
			}
		}
		catch (Exception ex)
		{
			lock (Gate)
			{
				_dirty = true;
				ScheduleSaveLocked();
			}
			App.Logger?.WriteLine("AssetPreloadCache", "Catalog save failed: " + ex.Message);
		}
	}

	public static void Shutdown()
	{
		StopBackgroundWarm();
		Timer? timer;
		lock (Gate)
		{
			timer = _saveTimer;
			_saveTimer = null;
		}
		timer?.Dispose();
		Flush();
		lock (Gate)
		{
			_loaded = false;
			_dirty = false;
			_catalog = new AssetPreloadCatalog();
		}
		VerifiedFiles.Clear();
		TouchedFiles.Clear();
		WarmingAssets.Clear();
		Interlocked.Exchange(ref _activePlaceId, 0);
		Interlocked.Exchange(ref _authoritativePlaceId, 0);
	}

	public static void StopBackgroundWarm(TimeSpan? budget = null)
	{
		CancellationTokenSource? warm = Interlocked.Exchange(ref _warmCts, null);
		if (warm == null)
		{
			return;
		}
		SafeCancel(warm);
		TimeSpan limit = budget ?? TimeSpan.FromSeconds(1);
		Task? pending = Volatile.Read(ref _warmTask);
		bool settled = pending == null;
		if (!settled && limit > TimeSpan.Zero)
		{
			try
			{
				settled = pending!.Wait(limit);
			}
			catch
			{
				settled = true;
			}
		}
		if (settled)
		{
			warm.Dispose();
			return;
		}
		_ = pending!.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), warm, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}

	private static void SafeCancel(CancellationTokenSource? source)
	{
		if (source == null)
		{
			return;
		}
		try
		{
			source.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		catch (AggregateException)
		{
		}
	}

	private static async Task WarmBackgroundAsync(long placeId, CancellationTokenSource owner)
	{
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(45), owner.Token).ConfigureAwait(false);
			await Task.WhenAll(
				AssetPreloadProactive.DiscoverAsync(placeId, owner.Token),
				AssetPreloadAvatar.PreloadAsync(owner.Token)).ConfigureAwait(false);
			if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled)
			{
				return;
			}
			AssetPreloadHint[] hints = [.. Snapshot(placeId).Take(BackgroundAssetCount)];
			await WarmAsync(hints, owner.Token).ConfigureAwait(false);
			await AssetCaptureStore.ResolveHashesAsync(BackgroundAssetCount * 2, owner.Token).ConfigureAwait(false);
			if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled)
			{
				return;
			}
			long maximumBytes = Math.Clamp(App.Settings.Prop.AssetWarpPreloadCacheMb, 256, 8192) * 1024L * 1024L;
			AssetCaptureStore.EnforceDiskLimit(maximumBytes);
		}
		catch (OperationCanceledException) when (owner.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AssetPreloadCache", "Background preload failed: " + ex.Message);
		}
		finally
		{
			Interlocked.CompareExchange(ref _warmCts, null, owner);
			owner.Dispose();
		}
	}

	internal static async Task WarmCatalogAsync(long placeId, int maximumAssets, CancellationToken ct)
	{
		if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled || placeId <= 0 || maximumAssets <= 0)
		{
			return;
		}
		AssetPreloadHint[] hints = [.. Snapshot(placeId).Take(maximumAssets)];
		await WarmAsync(hints, ct).ConfigureAwait(false);
	}

	private static async Task WarmAsync(AssetPreloadHint[] hints, CancellationToken ct)
	{
		if (hints.Length == 0)
		{
			return;
		}
		int concurrency = 1;
		ParallelOptions options = new()
		{
			CancellationToken = ct,
			MaxDegreeOfParallelism = concurrency
		};
		await Parallel.ForEachAsync(hints, options, async (hint, token) =>
		{
			if (!WarmingAssets.TryAdd(hint.Hash, 0))
			{
				return;
			}
			try
			{
				if (AssetCaptureStore.TryGetCachedFile(hint.AssetId, hint.Hash, out string existing))
				{
					if (ValidateFile(hint.Hash, existing))
					{
						TouchFile(existing);
						return;
					}
					AssetCaptureStore.InvalidateCachedFile(hint.AssetId, hint.Hash, existing);
				}
				await AssetCaptureStore.PrefetchAssetAsync(hint.AssetId, hint.Hash, hint.Url, token).ConfigureAwait(false);
			}
			finally
			{
				WarmingAssets.TryRemove(hint.Hash, out _);
			}
		}).ConfigureAwait(false);
	}

	private static AssetPreloadHint[] Snapshot(long placeId)
	{
		EnsureLoaded();
		lock (Gate)
		{
			return [.. _catalog.GetForGame(placeId).Select(hint => new AssetPreloadHint
			{
				AssetId = hint.AssetId,
				Hash = hint.Hash,
				Url = hint.Url,
				Priority = hint.Priority,
				LastSeenUtc = hint.LastSeenUtc
			})];
		}
	}

	private static void EnsureLoaded()
	{
		lock (Gate)
		{
			if (_loaded)
			{
				return;
			}
			_loaded = true;
			_saveTimer = new Timer(OnSaveTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			try
			{
				if (File.Exists(CatalogPath))
					_catalog = Voidstrap.Utility.JsonFile.Deserialize<AssetPreloadCatalog>(CatalogPath, JsonOptions, MaximumCatalogBytes);
			}
			catch (Exception ex)
			{
				_catalog = new AssetPreloadCatalog();
				App.Logger?.WriteLine("AssetPreloadCache", "Catalog load failed: " + ex.Message);
			}
			_catalog.Trim(MaximumGames, MaximumAssetsPerGame);
		}
	}

	private static void ScheduleSaveLocked()
	{
		_saveTimer?.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
	}

	private static void OnSaveTimer(object? state)
	{
		try
		{
			Flush();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("AssetPreloadCache::OnSaveTimer", ex);
		}
	}

	private static bool ValidateFile(string hash, string filePath)
	{
		try
		{
			FileInfo file = new(filePath);
			if (!file.Exists || file.Length <= 0 || file.Length > 134217765)
			{
				return false;
			}
			string signature = file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
			if (VerifiedFiles.TryGetValue(hash, out string? verified) && string.Equals(verified, signature, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			using FileStream stream = file.OpenRead();
			Span<byte> header = stackalloc byte[37];
			int read = stream.Read(header);
			if (read < 4)
			{
				return false;
			}
			if (header[..4].SequenceEqual("RBXH"u8))
			{
				if (read != header.Length)
				{
					return false;
				}
				int payloadLength = BitConverter.ToInt32(header.Slice(25, 4));
				if (payloadLength <= 0 || payloadLength + header.Length != file.Length)
				{
					return false;
				}
			}
			else
			{
				stream.Position = 0;
			}
			byte[] digest = System.Security.Cryptography.MD5.HashData(stream);
			if (!Convert.ToHexStringLower(digest).Equals(hash, StringComparison.OrdinalIgnoreCase))
				return false;
			if (VerifiedFiles.Count > MaximumTrackedFiles)
			{
				VerifiedFiles.Clear();
			}
			VerifiedFiles[hash] = signature;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void TouchFile(string filePath)
	{
		long now = DateTime.UtcNow.Ticks;
		long threshold = TimeSpan.FromMinutes(5).Ticks;
		if (TouchedFiles.TryGetValue(filePath, out long previous) && now - previous < threshold)
		{
			return;
		}
		if (TouchedFiles.Count > MaximumTrackedFiles)
		{
			TouchedFiles.Clear();
		}
		TouchedFiles[filePath] = now;
		try
		{
			File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow);
		}
		catch
		{
		}
	}
}
