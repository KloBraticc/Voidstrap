using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.AssetProxy;

public static partial class AssetCaptureStore
{
	private sealed record IndexRecord(string Id, string Name, string Type);

	private sealed record PendingCapture(string AssetId, string Hash, byte[] Content, string Url);

	private const string LOG_IDENT = "AssetCaptureStore";

	private const int MaxItems = 5000;

	private const int MaxAssetBytes = 33554432;

	private const int MaxDecompressedBytes = 67108864;

	private const int MaxLogScanBytes = 4194304;

	private const int MaxIndexBytes = 4194304;

	private const int MaxJsonBytes = 4194304;

	private const long MaxQueuedCaptureBytes = 50331648;

	private const int CaptureQueueCapacity = 256;

	private static readonly ConcurrentDictionary<string, CapturedAsset> _items = new();

	private static readonly ConcurrentQueue<string> _order = new();

	private static readonly ConcurrentDictionary<string, string> _hashToId = new();

	private static readonly ConcurrentDictionary<string, string> _idToHash = new();

	private static readonly ConcurrentDictionary<string, int> _typeById = new();

	private static readonly ConcurrentDictionary<string, byte> _hashResolveAttempted = new();

	private static readonly ConcurrentDictionary<string, string> _fileById = new();

	private static readonly ConcurrentDictionary<string, string> _cacheFiles = new(StringComparer.OrdinalIgnoreCase);

	private static readonly System.Threading.Lock _diskLoadGate = new();

	private static readonly System.Threading.Lock _captureWriterGate = new();

	private static Channel<PendingCapture>? _captureQueue;

	private static CancellationTokenSource? _captureWriterCts;

	private static Task? _captureWriterTask;

	private static int _captureQueueDrops;

	private static long _queuedCaptureBytes;

	private static readonly ConcurrentDictionary<string, byte> _prefetchAttempted = new();

	[GeneratedRegex("([a-f0-9]{32})")]
	private static partial Regex BatchHashPattern();

	[GeneratedRegex("(?:placeid[\"':=\\s]+|joining game '[^']*' place )(\\d{4,})", RegexOptions.IgnoreCase)]
	private static partial Regex PlaceIdPattern();

	private static readonly ConcurrentDictionary<string, long> _fetchFailed = new();

	private static readonly ConcurrentDictionary<string, byte> _logsScanned = new();

	private static readonly HttpClient _robloxClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(25L), handler =>
	{
		handler.UseCookies = false;
		handler.AllowAutoRedirect = true;
	});

	private static long _currentPlaceId;

	private static volatile bool _suppressScan;

	private static bool _avatarScanned;

	private static DateTime _avatarCooldownUntil = DateTime.MinValue;

	private static readonly ConcurrentDictionary<string, IndexRecord> _index = new();

	private static readonly System.Threading.Lock _indexLock = new();

	private static DateTime _lastIndexWrite = DateTime.MinValue;

	private static bool _diskLoaded;

	private const int RbxhHeaderSize = 37;

	[GeneratedRegex("(?:rbxassetid://|rbxthumb://[^\\s\"']*?[?&]id=|[?&]id=|assetid[\"'=:\\s/\\\\]{1,3}|/asset/)(\\d{3,})", RegexOptions.IgnoreCase)]
	private static partial Regex AssetIdPattern();

	[GeneratedRegex("(\\d{3,})")]
	private static partial Regex NumericAssetIdPattern();

	private static readonly ConcurrentDictionary<string, byte> _modelScanned = new();

	private static string? _logPath;

	private static long _logOffset;

	public static int Count => _items.Count;

	public static string CacheDir => Paths.AssetCache;

	private static string RbxStorageDir => Path.Combine(Paths.LocalAppData, "Roblox", "rbx-storage");

	private static string RobloxLogsDir => Path.Combine(Paths.LocalAppData, "Roblox", "logs");

	public static void SetCurrentPlaceId(long placeId)
	{
		if (placeId > 0)
		{
			Interlocked.Exchange(ref _currentPlaceId, placeId);
		}
	}

	private static string CategoryDir(string category)
	{
		return Path.Combine(CacheDir, string.IsNullOrWhiteSpace(category) ? "Other" : category);
	}

	private static void TouchIndex(CapturedAsset a)
	{
		_index[a.Key] = new IndexRecord(Limit(a.AssetId, 64), Limit(a.ResolvedName, 512), Limit(a.Type, 128));
		TrimDictionary(_index, MaxItems);
		FlushIndex(force: false);
	}

	private static string Limit(string? value, int maxLength)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "";
		}
		return value.Length <= maxLength ? value : value[..maxLength];
	}

	private static void TrimDictionary<TKey, TValue>(ConcurrentDictionary<TKey, TValue> values, int limit) where TKey : notnull
	{
		if (values.Count <= limit)
		{
			return;
		}
		foreach (TKey key in values.Keys)
		{
			if (values.Count <= limit)
			{
				break;
			}
			values.TryRemove(key, out _);
		}
	}

	private static void FlushIndex(bool force)
	{
		try
		{
			lock (_indexLock)
			{
				if (force || !((DateTime.UtcNow - _lastIndexWrite).TotalSeconds < 30.0))
				{
					_lastIndexWrite = DateTime.UtcNow;
					Directory.CreateDirectory(CacheDir);
					string path = Path.Combine(CacheDir, "index.json");
					Voidstrap.Utility.JsonFile.SerializeAtomic(path, _index);
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "The asset cache index could not be saved: " + ex.Message);
		}
	}

	private static void LoadIndex()
	{
		try
		{
			string path = Path.Combine(CacheDir, "index.json");
			if (!File.Exists(path))
			{
				return;
			}
			Dictionary<string, IndexRecord> dictionary = Voidstrap.Utility.JsonFile.Deserialize<Dictionary<string, IndexRecord>>(path, Voidstrap.Utility.JsonOptions.Tolerant, MaxIndexBytes);
			foreach (KeyValuePair<string, IndexRecord> item in dictionary.Take(MaxItems))
			{
				_index[item.Key] = item.Value;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "The asset cache index could not be loaded: " + ex.Message);
		}
	}

	public static CapturedAsset? Add(string? assetId, string? hash, byte[]? content, string url)
	{
		if (content == null || content.Length == 0 || content.Length > MaxAssetBytes)
		{
			return null;
		}
		string text = CaptureKey(assetId, hash, true);
		_items.TryGetValue(text, out CapturedAsset? existing);
		if (existing != null && !string.IsNullOrEmpty(existing.FilePath) && File.Exists(existing.FilePath))
		{
			return null;
		}
		try
		{
			AssetTypeInfo assetTypeInfo = AssetTypeDetector.Detect(content);
			string text2 = CategoryDir(assetTypeInfo.Category);
			Directory.CreateDirectory(text2);
			string text3 = Path.Combine(text2, text + assetTypeInfo.Extension);
			try
			{
				WriteAllBytesAtomic(text3, content);
				_cacheFiles[text] = text3;
			}
			catch
			{
				text3 = "";
			}
			if (existing != null)
			{
				existing.AssetId = !string.IsNullOrWhiteSpace(assetId) ? assetId.Trim() : existing.AssetId;
				existing.Hash = !string.IsNullOrWhiteSpace(hash) ? hash.Trim() : existing.Hash;
				existing.Type = assetTypeInfo.Type;
				existing.Category = assetTypeInfo.Category;
				existing.Extension = assetTypeInfo.Extension;
				existing.Url = url ?? existing.Url;
				existing.FilePath = text3;
				existing.Size = content.Length;
				existing.IsImage = assetTypeInfo.IsImage;
				existing.IsMesh = assetTypeInfo.IsMesh;
				existing.CapturedAt = DateTime.Now;
				TouchIndex(existing);
				return existing;
			}
			CapturedAsset capturedAsset = new()
			{
				Key = text,
				AssetId = ((!string.IsNullOrWhiteSpace(assetId)) ? assetId.Trim() : ((!string.IsNullOrWhiteSpace(hash) && _hashToId.TryGetValue(hash.Trim(), out string value)) ? value : "")),
				Hash = (hash?.Trim() ?? ""),
				Type = assetTypeInfo.Type,
				Category = assetTypeInfo.Category,
				Extension = assetTypeInfo.Extension,
				Url = (url ?? ""),
				FilePath = text3,
				Size = content.Length,
				IsImage = assetTypeInfo.IsImage,
				IsMesh = assetTypeInfo.IsMesh
			};
			if (!_items.TryAdd(text, capturedAsset))
			{
				return null;
			}
			_order.Enqueue(text);
			EvictIfNeeded();
			TouchIndex(capturedAsset);
			return capturedAsset;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AssetCaptureStore", "Add failed: " + ex.Message);
			return null;
		}
	}

	public static void QueueAdd(string? assetId, string? hash, byte[]? content, string url)
	{
		if (_suppressScan || !App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled || content == null || content.Length == 0 || content.Length > MaxAssetBytes)
		{
			return;
		}
		Channel<PendingCapture> queue = EnsureCaptureWriter();
		PendingCapture pending = new(assetId?.Trim() ?? "", hash?.Trim() ?? "", content, url ?? "");
		long queuedBytes = Interlocked.Add(ref _queuedCaptureBytes, content.Length);
		if (queuedBytes > MaxQueuedCaptureBytes || !queue.Writer.TryWrite(pending))
		{
			Interlocked.Add(ref _queuedCaptureBytes, -content.Length);
			int dropped = Interlocked.Increment(ref _captureQueueDrops);
			if (dropped == 1 || dropped % 100 == 0)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Capture writer saturated, skipped " + dropped + " cache entries");
			}
		}
	}

	private static Channel<PendingCapture> EnsureCaptureWriter()
	{
		lock (_captureWriterGate)
		{
			if (_captureQueue != null)
			{
				return _captureQueue;
			}
			Channel<PendingCapture> queue = Channel.CreateBounded<PendingCapture>(new BoundedChannelOptions(CaptureQueueCapacity)
			{
				SingleReader = true,
				SingleWriter = false,
				FullMode = BoundedChannelFullMode.Wait
			});
			CancellationTokenSource cancellation = new();
			_captureQueue = queue;
			_captureWriterCts = cancellation;
			_captureWriterTask = Task.Run(() => ProcessCaptureQueueAsync(queue.Reader, cancellation.Token));
			return queue;
		}
	}

	private static async Task ProcessCaptureQueueAsync(ChannelReader<PendingCapture> reader, CancellationToken token)
	{
		try
		{
			await foreach (PendingCapture pending in reader.ReadAllAsync(token).ConfigureAwait(false))
			{
				try
				{
					byte[] content = MaybeGunzip(pending.Content);
					Add(pending.AssetId, pending.Hash, content, pending.Url);
				}
				catch (Exception ex)
				{
					App.Logger?.WriteLine(LOG_IDENT, "Capture write failed: " + ex.Message);
				}
				finally
				{
					Interlocked.Add(ref _queuedCaptureBytes, -pending.Content.Length);
				}
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Capture writer stopped: " + ex.Message);
		}
	}

	public static void Shutdown(TimeSpan? budget = null)
	{
		Channel<PendingCapture>? queue;
		CancellationTokenSource? cancellation;
		Task? worker;
		lock (_captureWriterGate)
		{
			queue = _captureQueue;
			cancellation = _captureWriterCts;
			worker = _captureWriterTask;
			_captureQueue = null;
			_captureWriterCts = null;
			_captureWriterTask = null;
		}
		queue?.Writer.TryComplete();
		TimeSpan limit = budget ?? TimeSpan.FromSeconds(1);
		long deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
		try
		{
			if (worker != null && limit > TimeSpan.Zero && !worker.Wait(limit))
			{
				cancellation?.Cancel();
				long remaining = deadline - Environment.TickCount64;
				if (remaining > 0)
				{
					worker.Wait(TimeSpan.FromMilliseconds(remaining));
				}
			}
		}
		catch
		{
		}
		cancellation?.Cancel();
		cancellation?.Dispose();
		Interlocked.Exchange(ref _queuedCaptureBytes, 0);
		if (queue != null || worker != null)
		{
			long maximumBytes = Math.Clamp(App.Settings.Prop.AssetWarpPreloadCacheMb, 256, 8192) * 1024L * 1024L;
			EnforceDiskLimit(maximumBytes);
			FlushIndex(force: true);
		}
		else if (!_index.IsEmpty)
		{
			FlushIndex(force: true);
		}
	}

	public static CapturedAsset? AddMetadata(string? assetId, string? hash, string url)
	{
		string text = CaptureKey(assetId, hash, false);
		if (text.Length == 0 || _items.ContainsKey(text))
		{
			return null;
		}
		CapturedAsset capturedAsset = new()
		{
			Key = text,
			AssetId = (assetId?.Trim() ?? ""),
			Hash = (hash?.Trim() ?? ""),
			Type = "Pending",
			Category = "Pending",
			Url = (url ?? ""),
			FilePath = ""
		};
		if (!_items.TryAdd(text, capturedAsset))
		{
			return null;
		}
		_order.Enqueue(text);
		EvictIfNeeded();
		return capturedAsset;
	}

	public static void NoteIdForHash(string? hash, string? id)
	{
		if (!string.IsNullOrWhiteSpace(hash) && hash.Trim().Length == 32 && !string.IsNullOrWhiteSpace(id) && long.TryParse(id.Trim(), out var result) && result > 0)
		{
			string key = hash.Trim();
			string text = id.Trim();
			_hashToId[key] = text;
			TrimDictionary(_hashToId, MaxItems * 2);
			if (_items.TryGetValue(key, out CapturedAsset value) && string.IsNullOrEmpty(value.AssetId))
			{
				value.AssetId = text;
			}
		}
	}

	private static void EvictIfNeeded()
	{
		while (_items.Count > MaxItems && _order.TryDequeue(out string result) && result != null)
		{
			if (_items.TryRemove(result, out CapturedAsset value))
			{
				_index.TryRemove(result, out _);
				if (IsCapturedCacheFile(value.FilePath))
				{
					try
					{
						File.Delete(value.FilePath);
					}
					catch
					{
					}
				}
			}
		}
	}

	private static string CaptureKey(string? assetId, string? hash, bool createFallback)
	{
		string normalizedHash = hash?.Trim() ?? "";
		if (normalizedHash.Length == 32 && normalizedHash.All(Uri.IsHexDigit))
			return normalizedHash.ToLowerInvariant();
		if (long.TryParse(assetId?.Trim(), out long numericId) && numericId > 0)
			return "id-" + numericId;
		return createFallback ? Guid.NewGuid().ToString("N") : "";
	}

	private static bool IsCapturedCacheFile(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}
		try
		{
			string root = Path.GetFullPath(CacheDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsRobloxCacheFile(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}
		try
		{
			string root = Path.GetFullPath(RbxStorageDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	public static void EnsureLoadedFromDisk()
	{
		lock (_diskLoadGate)
		{
			if (_diskLoaded)
			{
				return;
			}
			_diskLoaded = true;
		}
		try
		{
			if (!Directory.Exists(CacheDir))
			{
				return;
			}
			LoadIndex();
			FileInfo[] files = [.. (from f in new DirectoryInfo(CacheDir).EnumerateFiles("*", SearchOption.AllDirectories)
				orderby f.LastWriteTimeUtc descending
				select f)];
			foreach (FileInfo file in files)
			{
				if (!file.Name.Equals("index.json", StringComparison.OrdinalIgnoreCase))
				{
					_cacheFiles[Path.GetFileNameWithoutExtension(file.Name)] = file.FullName;
				}
			}
			foreach (FileInfo item in files.Take(1500))
			{
				string fullName = item.FullName;
				try
				{
					if (string.Equals(item.Name, "index.json", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fullName);
					if (string.IsNullOrEmpty(fileNameWithoutExtension) || _items.ContainsKey(fileNameWithoutExtension))
					{
						continue;
					}
					FileInfo fileInfo = new(fullName);
					if (fileInfo.Length > 0)
					{
						byte[] array = ReadHead(fullName, 4096);
						AssetTypeInfo assetTypeInfo = AssetTypeDetector.Detect(array);
						string assetId = (fileNameWithoutExtension.StartsWith("id-", StringComparison.Ordinal) ? fileNameWithoutExtension[3..] : "");
						string hash = ((fileNameWithoutExtension.Length == 32) ? fileNameWithoutExtension : "");
						CapturedAsset capturedAsset = new()
						{
							Key = fileNameWithoutExtension,
							AssetId = assetId,
							Hash = hash,
							Type = assetTypeInfo.Type,
							Category = assetTypeInfo.Category,
							Extension = assetTypeInfo.Extension,
							FilePath = fullName,
							Size = fileInfo.Length,
							IsImage = assetTypeInfo.IsImage,
							IsMesh = assetTypeInfo.IsMesh,
							CapturedAt = fileInfo.LastWriteTime
						};
						if (_index.TryGetValue(fileNameWithoutExtension, out IndexRecord value) && !string.IsNullOrEmpty(value.Name))
						{
							capturedAsset.ResolvedName = value.Name;
						}
						if (_items.TryAdd(fileNameWithoutExtension, capturedAsset))
						{
							_order.Enqueue(fileNameWithoutExtension);
						}
					}
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AssetCaptureStore", "Disk load failed: " + ex.Message);
		}
	}

	public static IReadOnlyList<CapturedAsset> Snapshot()
	{
		return [.. _items.Values.OrderByDescending(x => x.CapturedAt)];
	}

	public static bool TryGetCachedFile(string? assetId, string? hash, out string filePath)
	{
		filePath = "";
		EnsureLoadedFromDisk();
		string normalizedId = assetId?.Trim() ?? "";
		string normalizedHash = hash?.Trim().ToLowerInvariant() ?? "";
		CapturedAsset? hashAsset = null;
		if (normalizedHash.Length > 0 && _items.TryGetValue(normalizedHash, out hashAsset) && File.Exists(hashAsset.FilePath))
		{
			filePath = hashAsset.FilePath;
			return true;
		}
		if (normalizedId.Length > 0 && _items.TryGetValue("id-" + normalizedId, out CapturedAsset? idAsset) && File.Exists(idAsset.FilePath))
		{
			filePath = idAsset.FilePath;
			return true;
		}
		if (normalizedHash.Length > 0 && _cacheFiles.TryGetValue(normalizedHash, out string? cached) && File.Exists(cached))
		{
			CapturedAsset target = hashAsset ?? _items.GetOrAdd(normalizedHash, key => new CapturedAsset
			{
				Key = key,
				AssetId = normalizedId,
				Hash = normalizedHash
			});
			PopulateFromFile(target, cached);
			filePath = cached;
			return true;
		}
		if (normalizedId.Length > 0)
		{
			string? resolved = ResolveLocalFile(normalizedId, normalizedHash);
			if (resolved != null)
			{
				filePath = resolved;
				return true;
			}
		}
		return false;
	}

	public static async Task<bool> PrefetchAssetAsync(string assetId, string hash, string url, CancellationToken ct)
	{
		if (TryGetCachedFile(assetId, hash, out _))
		{
			return true;
		}
		CapturedAsset? asset = AddMetadata(assetId, hash, url);
		if (asset == null)
		{
			string key = !string.IsNullOrWhiteSpace(hash) ? hash.Trim().ToLowerInvariant() : "id-" + assetId.Trim();
			_items.TryGetValue(key, out asset);
		}
		asset ??= AddById(assetId);
		if (asset == null)
		{
			return false;
		}
		byte[]? content = await GetContentAsync(asset, ct).ConfigureAwait(false);
		return content is { Length: > 0 } && TryGetCachedFile(assetId, hash, out _);
	}

	public static void InvalidateCachedFile(string? assetId, string? hash, string? filePath)
	{
		string normalizedId = assetId?.Trim() ?? "";
		string normalizedHash = hash?.Trim().ToLowerInvariant() ?? "";
		if (normalizedHash.Length > 0)
		{
			_cacheFiles.TryRemove(normalizedHash, out _);
			if (_items.TryGetValue(normalizedHash, out CapturedAsset? hashAsset) && string.Equals(hashAsset.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
			{
				hashAsset.FilePath = "";
				hashAsset.Size = 0;
			}
		}
		if (normalizedId.Length > 0 && _items.TryGetValue("id-" + normalizedId, out CapturedAsset? idAsset) && string.Equals(idAsset.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
		{
			idAsset.FilePath = "";
			idAsset.Size = 0;
		}
		if (IsCapturedCacheFile(filePath) || IsRobloxCacheFile(filePath))
		{
			try
			{
				File.Delete(filePath!);
			}
			catch
			{
			}
		}
	}

	public static void EnforceDiskLimit(long maximumBytes)
	{
		if (maximumBytes <= 0 || !Directory.Exists(CacheDir))
		{
			return;
		}
		try
		{
			FileInfo[] files = [.. new DirectoryInfo(CacheDir)
				.EnumerateFiles("*", SearchOption.AllDirectories)
				.Where(file => !file.Name.Equals("index.json", StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(file => file.LastWriteTimeUtc)];
			long total = files.Sum(file => file.Length);
			if (total <= maximumBytes)
			{
				return;
			}
			HashSet<string> deleted = new(StringComparer.OrdinalIgnoreCase);
			foreach (FileInfo file in files.Reverse())
			{
				if (total <= maximumBytes)
				{
					break;
				}
				try
				{
					long length = file.Length;
					file.Delete();
					total -= length;
					deleted.Add(file.FullName);
					_cacheFiles.TryRemove(Path.GetFileNameWithoutExtension(file.Name), out _);
				}
				catch
				{
				}
			}
			foreach (KeyValuePair<string, CapturedAsset> item in _items.ToList())
			{
				if (deleted.Contains(item.Value.FilePath))
				{
					_items.TryRemove(item.Key, out _);
					_index.TryRemove(item.Key, out _);
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Cache cleanup failed: " + ex.Message);
		}
	}

	public static byte[]? ReadContent(CapturedAsset asset)
	{
		try
		{
			if (string.IsNullOrEmpty(asset.FilePath) || !File.Exists(asset.FilePath))
			{
				return null;
			}
			FileInfo file = new(asset.FilePath);
			if (file.Length > MaxAssetBytes + RbxhHeaderSize)
			{
				return null;
			}
			byte[] array = File.ReadAllBytes(asset.FilePath);
			int num = RbxhOffset(array);
			if (num == 0)
			{
				return array;
			}
			byte[] array2 = new byte[array.Length - num];
			Array.Copy(array, num, array2, 0, array2.Length);
			return array2;
		}
		catch
		{
			return null;
		}
	}

	private static int RbxhOffset(byte[] data)
	{
		if (data.Length <= 37 || data[0] != 82 || data[1] != 66 || data[2] != 88 || data[3] != 72)
		{
			return 0;
		}
		return 37;
	}

	public static int ScanRobloxCache(int max)
	{
		int num = 0;
		if (_suppressScan)
		{
			return 0;
		}
		try
		{
			if (!Directory.Exists(RbxStorageDir))
			{
				return 0;
			}
			foreach (FileInfo item in (from f in new DirectoryInfo(RbxStorageDir).EnumerateFiles("*", SearchOption.AllDirectories)
				where f.Name.Length == 32
				orderby f.LastWriteTimeUtc descending
				select f).Take(max))
			{
				string name = item.Name;
				if (_items.ContainsKey(name))
				{
					continue;
				}
				try
				{
					byte[] array = ReadHead(item.FullName, 128);
					if (array.Length >= 8)
					{
						int num2 = RbxhOffset(array);
						AssetTypeInfo assetTypeInfo = AssetTypeDetector.Detect(array, num2);
						CapturedAsset value = new()
						{
							Key = name,
							Hash = name,
							AssetId = (_hashToId.TryGetValue(name, out string value2) ? value2 : ""),
							Type = assetTypeInfo.Type,
							Category = assetTypeInfo.Category,
							Extension = assetTypeInfo.Extension,
							FilePath = item.FullName,
							Size = Math.Max(0L, item.Length - num2),
							IsImage = assetTypeInfo.IsImage,
							IsMesh = assetTypeInfo.IsMesh,
							CapturedAt = item.LastWriteTime
						};
						if (_items.TryAdd(name, value))
						{
							_order.Enqueue(name);
							EvictIfNeeded();
							num++;
						}
					}
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AssetCaptureStore", "Roblox cache scan failed: " + ex.Message);
		}
		EvictIfNeeded();
		return num;
	}

	public static CapturedAsset? AddById(string? id)
	{
		if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var result) || result <= 0)
		{
			return null;
		}
		string text = "id-" + id.Trim();
		if (_items.ContainsKey(text))
		{
			return null;
		}
		CapturedAsset capturedAsset = new()
		{
			Key = text,
			AssetId = id.Trim(),
			Hash = "",
			Type = "Asset",
			Category = "Pending",
			Extension = ".bin",
			Url = "https://assetdelivery.roblox.com/v1/asset/?id=" + id.Trim(),
			FilePath = "",
			Size = 0L,
			CapturedAt = DateTime.Now
		};
		if (!_items.TryAdd(text, capturedAsset))
		{
			return null;
		}
		_order.Enqueue(text);
		EvictIfNeeded();
		return capturedAsset;
	}

	public static int ScanRobloxLog(int max)
	{
		int num = 0;
		if (_suppressScan)
		{
			return 0;
		}
		try
		{
			if (!Directory.Exists(RobloxLogsDir))
			{
				return 0;
			}
			FileInfo fileInfo = (from f in new DirectoryInfo(RobloxLogsDir).EnumerateFiles("*_Player_*.log")
				orderby f.LastWriteTimeUtc descending
				select f).FirstOrDefault();
			if (fileInfo == null)
			{
				return 0;
			}
			if (!string.Equals(_logPath, fileInfo.FullName, StringComparison.OrdinalIgnoreCase))
			{
				_logPath = fileInfo.FullName;
				_logOffset = 0L;
			}
			using FileStream fileStream = new(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			if (fileStream.Length < _logOffset)
			{
				_logOffset = 0L;
			}
			long scanStart = Math.Max(_logOffset, fileStream.Length - MaxLogScanBytes);
			fileStream.Seek(scanStart, SeekOrigin.Begin);
			using StreamReader streamReader = new(fileStream);
			string input = streamReader.ReadToEnd();
			_logOffset = fileStream.Length;
			foreach (Match item in PlaceIdPattern().Matches(input))
			{
				if (long.TryParse(item.Groups[1].Value, out var result) && result > 0)
				{
					_currentPlaceId = result;
				}
			}
			foreach (Match item2 in AssetIdPattern().Matches(input))
			{
				if (num < max)
				{
					if (AddById(item2.Groups[1].Value) != null)
					{
						num++;
					}
					continue;
				}
				break;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AssetCaptureStore", "Roblox log scan failed: " + ex.Message);
		}
		return num;
	}

	public static void ScanRecentLogs(int maxFiles)
	{
		if (_suppressScan)
		{
			return;
		}
		try
		{
			if (!Directory.Exists(RobloxLogsDir))
			{
				return;
			}
			List<FileInfo> list = [.. (from f in new DirectoryInfo(RobloxLogsDir).EnumerateFiles("*_Player_*.log")
				orderby f.LastWriteTimeUtc descending
				select f).Take(12)];
			int num = 0;
			foreach (FileInfo item in list)
			{
				if (num >= maxFiles || _suppressScan)
				{
					break;
				}
				if (string.Equals(item.FullName, _logPath, StringComparison.OrdinalIgnoreCase) || !_logsScanned.TryAdd(item.FullName, 0))
				{
					continue;
				}
				TrimDictionary(_logsScanned, 256);
				try
				{
					string input;
					using FileStream stream = new(item.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
					stream.Seek(Math.Max(0L, stream.Length - MaxLogScanBytes), SeekOrigin.Begin);
					using StreamReader streamReader = new(stream);
					input = streamReader.ReadToEnd();
					int num2 = 0;
					foreach (Match item2 in AssetIdPattern().Matches(input))
					{
						AddById(item2.Groups[1].Value);
						if (++num2 >= 60000)
						{
							break;
						}
					}
					num++;
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	public static async Task<byte[]?> GetContentAsync(CapturedAsset asset, CancellationToken ct = default)
	{
		byte[] array = ReadContent(asset);
		if (array == null || array.Length == 0)
		{
			if (string.IsNullOrEmpty(asset.AssetId))
			{
				return null;
			}
			array = await FetchAssetAsync(asset.AssetId, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (array == null || array.Length == 0)
			{
				return null;
			}
			AssetTypeInfo assetTypeInfo = AssetTypeDetector.Detect(array);
			asset.Type = assetTypeInfo.Type;
			asset.Category = assetTypeInfo.Category;
			asset.Extension = assetTypeInfo.Extension;
			asset.Size = array.Length;
			try
			{
				string text = CategoryDir(assetTypeInfo.Category);
				Directory.CreateDirectory(text);
				string text2 = Path.Combine(text, asset.Key + assetTypeInfo.Extension);
				WriteAllBytesAtomic(text2, array);
				asset.FilePath = text2;
			}
			catch
			{
			}
		}
		else
		{
			array = MaybeGunzip(array);
			if (asset.Size <= 0)
			{
				asset.Size = array.Length;
			}
		}
		try
		{
			ExtractAndAddReferences(array);
		}
		catch
		{
		}
		return array;
	}

	private static byte[] MaybeGunzip(byte[] data)
	{
		if (data == null || data.Length < 3 || data[0] != 31 || data[1] != 139 || data[2] != 8)
		{
			return data ?? [];
		}
		try
		{
			using MemoryStream stream = new(data, writable: false);
			using GZipStream gzip = new(stream, CompressionMode.Decompress);
			using MemoryStream output = new(Math.Min(data.Length * 2, MaxDecompressedBytes));
			byte[] buffer = new byte[65536];
			while (true)
			{
				int read = gzip.Read(buffer, 0, buffer.Length);
				if (read == 0)
					break;
				if (output.Length + read > MaxDecompressedBytes)
					return data;
				output.Write(buffer, 0, read);
			}
			return output.Length > 0 ? output.ToArray() : data;
		}
		catch
		{
			return data;
		}
	}

	private static async Task<byte[]?> ReadResponseBytesAsync(HttpResponseMessage response, CancellationToken token, int maxBytes = MaxAssetBytes)
	{
		long? contentLength = response.Content.Headers.ContentLength;
		if (contentLength <= 0 || contentLength > maxBytes)
		{
			return null;
		}
		await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(continueOnCapturedContext: false);
		int capacity = contentLength.HasValue ? (int)contentLength.Value : 0;
		using MemoryStream output = capacity > 0 ? new(capacity) : new();
		byte[] buffer = new byte[65536];
		while (true)
		{
			int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(continueOnCapturedContext: false);
			if (read == 0)
			{
				break;
			}
			if (output.Length + read > maxBytes)
			{
				return null;
			}
			await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(continueOnCapturedContext: false);
		}
		return output.Length == 0 ? null : output.ToArray();
	}

	public static void ResetResolveState()
	{
		_hashResolveAttempted.Clear();
		_prefetchAttempted.Clear();
		_modelScanned.Clear();
		_fetchFailed.Clear();
		_logsScanned.Clear();
		_avatarScanned = false;
		_avatarCooldownUntil = DateTime.MinValue;
	}

	private static void AddRobloxAuth(HttpRequestMessage req)
	{
		try
		{
			string text = RobloxCookie.Get();
			if (!string.IsNullOrEmpty(text))
			{
				req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + text);
			}
		}
		catch
		{
		}
		long currentPlaceId = _currentPlaceId;
		if (currentPlaceId > 0)
		{
			req.Headers.TryAddWithoutValidation("Roblox-Place-Id", currentPlaceId.ToString());
		}
		req.Headers.TryAddWithoutValidation("Roblox-Browser-Asset-Request", "false");
		req.Headers.TryAddWithoutValidation("Accept", "*/*");
	}

	private static async Task<byte[]?> FetchAssetAsync(string id, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(id) || IsFetchSuppressed(id))
		{
			return null;
		}
		try
		{
			using HttpRequestMessage req = new(HttpMethod.Get, "https://assetdelivery.roblox.com/v1/asset/?id=" + id);
			AddRobloxAuth(req);
			using HttpResponseMessage res = await _robloxClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (res.IsSuccessStatusCode)
			{
				byte[]? array = await ReadResponseBytesAsync(res, ct).ConfigureAwait(continueOnCapturedContext: false);
				return array == null ? null : MaybeGunzip(array);
			}
			int statusCode = (int)res.StatusCode;
			if ((uint)(statusCode - 400) <= 1u || (uint)(statusCode - 403) <= 1u || statusCode == 410)
			{
				_fetchFailed[id] = DateTime.UtcNow.AddMinutes(2).Ticks;
				TrimDictionary(_fetchFailed, MaxItems);
			}
			return null;
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	private static bool IsFetchSuppressed(string id)
	{
		if (!_fetchFailed.TryGetValue(id, out long expires))
			return false;
		if (DateTime.UtcNow.Ticks < expires)
			return true;
		_fetchFailed.TryRemove(id, out _);
		return false;
	}

	public static async Task<byte[]?> FetchRawByIdAsync(string idOrUrl, CancellationToken ct = default)
	{
		if (string.IsNullOrEmpty(idOrUrl))
		{
			return null;
		}
		Match match = NumericAssetIdPattern().Match(idOrUrl);
		if (!match.Success)
		{
			return null;
		}
		return await FetchAssetAsync(match.Value, ct).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task<byte[]?> FetchRawForReplacementAsync(string idOrUrl, CancellationToken ct = default)
	{
		if (string.IsNullOrEmpty(idOrUrl))
		{
			return null;
		}
		Match match = NumericAssetIdPattern().Match(idOrUrl);
		if (!match.Success)
		{
			return null;
		}
		string value = match.Value;
		try
		{
			using HttpRequestMessage req = new(HttpMethod.Get, "https://assetdelivery.roblox.com/v1/asset/?id=" + value);
			AddRobloxAuth(req);
			using HttpResponseMessage res = await _robloxClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!res.IsSuccessStatusCode)
			{
				return null;
			}
			byte[]? array = await ReadResponseBytesAsync(res, ct).ConfigureAwait(continueOnCapturedContext: false);
			return array == null ? null : MaybeGunzip(array);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	public static async Task ScanAvatarAssetsAsync(CancellationToken ct = default)
	{
		if (_avatarScanned || _suppressScan || DateTime.UtcNow < _avatarCooldownUntil)
		{
			return;
		}
		_avatarCooldownUntil = DateTime.UtcNow.AddSeconds(30.0);
		try
		{
			RobloxAccount robloxAccount = await RobloxCookie.GetAccountAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			if (robloxAccount == null || robloxAccount.UserId <= 0)
			{
				return;
			}
			string requestUri = $"https://avatar.roblox.com/v1/users/{robloxAccount.UserId}/avatar";
			using HttpResponseMessage response = await App.HttpClient.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!response.IsSuccessStatusCode)
			{
				return;
			}
			byte[]? payload = await ReadResponseBytesAsync(response, ct, MaxJsonBytes).ConfigureAwait(continueOnCapturedContext: false);
			if (payload == null)
			{
				return;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(payload);
			if (!jsonDocument.RootElement.TryGetProperty("assets", out var value) || value.ValueKind != JsonValueKind.Array)
			{
				return;
			}
			int num = 0;
			foreach (JsonElement item in value.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object)
				{
					continue;
				}
				string text = ((item.TryGetProperty("id", out JsonElement value2) && value2.ValueKind == JsonValueKind.Number) ? value2.GetRawText() : "");
				if (text.Length == 0)
				{
					continue;
				}
				string t = "";
				if (item.TryGetProperty("assetType", out var value3) && value3.ValueKind == JsonValueKind.Object && value3.TryGetProperty("name", out var value4))
				{
					t = value4.GetString() ?? "";
				}
				CapturedAsset capturedAsset = AddById(text);
				if (capturedAsset != null)
				{
					var (text2, category) = MapAvatarType(t);
					if (text2.Length > 0)
					{
						capturedAsset.Type = text2;
						capturedAsset.Category = category;
					}
				}
				num++;
			}
			if (num > 0)
			{
				_avatarScanned = true;
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AssetCaptureStore", "Avatar scan failed: " + ex.Message);
		}
	}

	private static (string, string) MapAvatarType(string t)
	{
		if (string.IsNullOrEmpty(t))
		{
			return ("", "");
		}
		string text = t.ToLowerInvariant();
		if (text.Contains("shirt") || text.Contains("pants") || text.Contains("decal") || text == "face")
		{
			return (t, "Image");
		}
		if (text.Contains("animation") || text.Contains("emote"))
		{
			return (t, "Animation");
		}
		if (text.Contains("audio"))
		{
			return (t, "Audio");
		}
		return (t, "Model");
	}

	public static async Task ResolveHashesAsync(int max, CancellationToken ct = default)
	{
		List<string> pendingItems = [.. (from a in _items.Values
			where !string.IsNullOrEmpty(a.AssetId) && string.IsNullOrEmpty(a.Hash) && !_hashResolveAttempted.ContainsKey(a.AssetId)
			select a.AssetId).Distinct(StringComparer.Ordinal).Take(max)];
		if (pendingItems.Count == 0)
		{
			return;
		}
		foreach (string item in pendingItems)
		{
			_hashResolveAttempted[item] = 0;
			TrimDictionary(_hashResolveAttempted, MaxItems);
		}
		for (int i = 0; i < pendingItems.Count; i += 50)
		{
			if (ct.IsCancellationRequested)
			{
				break;
			}
			List<string> source = [.. pendingItems.Skip(i).Take(50)];
			try
			{
				string content = "[" + string.Join(",", source.Select(id => $"{{\"assetId\":\"{id}\",\"requestId\":\"{id}\"}}")) + "]";
				using HttpRequestMessage req = new(HttpMethod.Post, "https://assetdelivery.roblox.com/v1/assets/batch");
				req.Content = new StringContent(content, Encoding.UTF8, "application/json");
				AddRobloxAuth(req);
				using HttpResponseMessage res = await _robloxClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (!res.IsSuccessStatusCode)
				{
					continue;
				}
				byte[]? payload = await ReadResponseBytesAsync(res, ct, MaxJsonBytes).ConfigureAwait(continueOnCapturedContext: false);
				if (payload == null)
				{
					continue;
				}
				using JsonDocument jsonDocument = JsonDocument.Parse(payload);
				if (jsonDocument.RootElement.ValueKind != JsonValueKind.Array)
				{
					continue;
				}
				foreach (JsonElement item2 in jsonDocument.RootElement.EnumerateArray())
				{
					if (item2.ValueKind != JsonValueKind.Object)
					{
						continue;
					}
					string text = (item2.TryGetProperty("requestId", out JsonElement value) ? (value.GetString() ?? "") : "");
					if (text.Length != 0)
					{
						if (item2.TryGetProperty("assetTypeId", out var value2) && value2.TryGetInt32(out var value3))
						{
							_typeById[text] = value3;
							TrimDictionary(_typeById, MaxItems * 2);
						}
						string input = (item2.TryGetProperty("location", out JsonElement value4) ? (value4.GetString() ?? "") : "");
						Match match = BatchHashPattern().Match(input);
						if (match.Success)
						{
							string text2 = match.Groups[1].Value.ToLowerInvariant();
							_idToHash[text] = text2;
							_hashToId[text2] = text;
							AssetPreloadCache.ObserveBatchAsset(text, text2, input);
							TrimDictionary(_idToHash, MaxItems * 2);
							TrimDictionary(_hashToId, MaxItems * 2);
						}
					}
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("AssetCaptureStore", "Batch hash resolve failed: " + ex.Message);
			}
		}
	}

	public static async Task<string?> ResolveHashByIdAsync(string id, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return null;
		}
		id = id.Trim();
		if (_idToHash.TryGetValue(id, out string value) && !string.IsNullOrEmpty(value))
		{
			return value;
		}
		try
		{
			string content = $"[{{\"assetId\":\"{id}\",\"requestId\":\"{id}\"}}]";
			using HttpRequestMessage req = new(HttpMethod.Post, "https://assetdelivery.roblox.com/v1/assets/batch");
			req.Content = new StringContent(content, Encoding.UTF8, "application/json");
			AddRobloxAuth(req);
			using HttpResponseMessage res = await _robloxClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!res.IsSuccessStatusCode)
			{
				return null;
			}
			byte[]? payload = await ReadResponseBytesAsync(res, ct, MaxJsonBytes).ConfigureAwait(continueOnCapturedContext: false);
			if (payload == null)
			{
				return null;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(payload);
			if (jsonDocument.RootElement.ValueKind != JsonValueKind.Array)
			{
				return null;
			}
			foreach (JsonElement item in jsonDocument.RootElement.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object)
				{
					string input = (item.TryGetProperty("location", out JsonElement value2) ? (value2.GetString() ?? "") : "");
					Match match = BatchHashPattern().Match(input);
					if (match.Success)
					{
						string text = match.Groups[1].Value.ToLowerInvariant();
						_idToHash[id] = text;
						_hashToId[text] = id;
						TrimDictionary(_idToHash, MaxItems * 2);
						TrimDictionary(_hashToId, MaxItems * 2);
						return text;
					}
				}
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AssetCaptureStore", "Single hash resolve failed for " + id + ": " + ex.Message);
		}
		return null;
	}

	public static void ApplyLinks()
	{
		foreach (CapturedAsset value6 in _items.Values)
		{
			if (string.IsNullOrEmpty(value6.AssetId) && !string.IsNullOrEmpty(value6.Hash) && _hashToId.TryGetValue(value6.Hash, out string value))
			{
				value6.AssetId = value;
			}
			if (string.IsNullOrEmpty(value6.Hash) && !string.IsNullOrEmpty(value6.AssetId) && _idToHash.TryGetValue(value6.AssetId, out string value2))
			{
				value6.Hash = value2;
			}
			if (string.IsNullOrEmpty(value6.FilePath) && !string.IsNullOrEmpty(value6.AssetId))
			{
				string text = ResolveLocalFile(value6.AssetId, value6.Hash);
				if (text != null)
				{
					PopulateFromFile(value6, text);
				}
			}
			if ((value6.Type == "Asset" || value6.Type == "Other") && !string.IsNullOrEmpty(value6.AssetId) && _typeById.TryGetValue(value6.AssetId, out var value3))
			{
				var (text2, category) = MapTypeId(value3);
				if (text2.Length > 0)
				{
					value6.Type = text2;
					value6.Category = category;
				}
			}
		}
		foreach (KeyValuePair<string, CapturedAsset> item in _items)
		{
			CapturedAsset value4 = item.Value;
			if (value4.Key.StartsWith("id-", StringComparison.Ordinal) && !string.IsNullOrEmpty(value4.Hash) && _items.ContainsKey(value4.Hash))
			{
				_items.TryRemove(value4.Key, out CapturedAsset _);
			}
		}
	}

	private static string? ResolveLocalFile(string id, string hash)
	{
		if (string.IsNullOrEmpty(hash))
		{
			_idToHash.TryGetValue(id, out hash);
		}
		if (!string.IsNullOrEmpty(hash) && hash.Length >= 2)
		{
			try
			{
				string text = Path.Combine(RbxStorageDir, hash[..2], hash);
				if (File.Exists(text))
				{
					return text;
				}
			}
			catch
			{
			}
		}
		if (_fileById.TryGetValue(id, out string value) && File.Exists(value))
		{
			return value;
		}
		return null;
	}

	private static void PopulateFromFile(CapturedAsset a, string file)
	{
		try
		{
			byte[] data = ReadHead(file, 256);
			int num = RbxhOffset(data);
			AssetTypeInfo assetTypeInfo = AssetTypeDetector.Detect(data, num);
			a.FilePath = file;
			long length = new FileInfo(file).Length;
			long num2 = Math.Max(0L, length - num);
			if (num2 > 0)
			{
				a.Size = num2;
			}
			bool flag = a.Type == "Asset" || a.Type == "Other";
			if (assetTypeInfo.Type != "Other" && flag)
			{
				a.Type = assetTypeInfo.Type;
				a.Category = assetTypeInfo.Category;
				a.Extension = assetTypeInfo.Extension;
			}
		}
		catch
		{
		}
	}

	public static async Task PrefetchUnsizedAsync(int max, CancellationToken ct = default)
	{
		List<string> list = [.. (from a in _items.Values
			where !string.IsNullOrEmpty(a.AssetId) && a.Size <= 0 && string.IsNullOrEmpty(a.FilePath) && _hashResolveAttempted.ContainsKey(a.AssetId) && !_prefetchAttempted.ContainsKey(a.AssetId) && !IsFetchSuppressed(a.AssetId) && ResolveLocalFile(a.AssetId, a.Hash) == null
			select a.AssetId).Distinct(StringComparer.Ordinal).Take(max)];
		if (list.Count == 0)
		{
			return;
		}
		foreach (string id in list)
		{
			if (ct.IsCancellationRequested)
			{
				break;
			}
			_prefetchAttempted[id] = 0;
			TrimDictionary(_prefetchAttempted, MaxItems);
			try
			{
				byte[] array = await FetchAssetAsync(id, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (array != null && array.Length != 0)
				{
					AssetTypeInfo assetTypeInfo = AssetTypeDetector.Detect(array);
					string text = CategoryDir(assetTypeInfo.Category);
					Directory.CreateDirectory(text);
					string text2 = Path.Combine(text, "id-" + id + assetTypeInfo.Extension);
					WriteAllBytesAtomic(text2, array);
					_fileById[id] = text2;
					TrimDictionary(_fileById, MaxItems * 2);
				}
			}
			catch
			{
			}
		}
	}

	private static void WriteAllBytesAtomic(string path, byte[] content)
	{
		string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.SequentialScan))
			{
				stream.Write(content);
				stream.Flush();
			}
			if (File.Exists(path))
			{
				File.Replace(temporary, path, null, true);
			}
			else
			{
				File.Move(temporary, path);
			}
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	private static (string, string) MapTypeId(int t)
	{
		return t switch
		{
			1 => ("Image", "Image"), 
			2 => ("T-Shirt", "Image"), 
			3 => ("Audio", "Audio"), 
			4 => ("Mesh", "Mesh"), 
			5 => ("Script", "Data"), 
			8 => ("Hat", "Model"), 
			9 => ("Place", "Model"), 
			10 => ("Model", "Model"), 
			11 => ("Shirt", "Image"), 
			12 => ("Pants", "Image"), 
			13 => ("Decal", "Image"), 
			17 => ("Head", "Model"), 
			18 => ("Face", "Image"), 
			19 => ("Gear", "Model"), 
			24 => ("Animation", "Animation"), 
			27 => ("Torso", "Model"), 
			32 => ("Package", "Model"), 
			38 => ("Plugin", "Other"), 
			39 => ("SolidModel", "Mesh"), 
			40 => ("MeshPart", "Mesh"), 
			41 => ("Hair Accessory", "Model"), 
			42 => ("Face Accessory", "Model"), 
			43 => ("Neck Accessory", "Model"), 
			44 => ("Shoulder Accessory", "Model"), 
			45 => ("Front Accessory", "Model"), 
			46 => ("Back Accessory", "Model"), 
			47 => ("Waist Accessory", "Model"), 
			48 => ("Climb Animation", "Animation"), 
			50 => ("Fall Animation", "Animation"), 
			51 => ("Idle Animation", "Animation"), 
			52 => ("Jump Animation", "Animation"), 
			53 => ("Run Animation", "Animation"), 
			54 => ("Swim Animation", "Animation"), 
			55 => ("Walk Animation", "Animation"), 
			56 => ("Pose Animation", "Animation"), 
			61 => ("Emote Animation", "Animation"), 
			62 => ("Video", "Other"), 
			_ => ("", ""), 
		};
	}

	private static void ExtractAndAddReferences(byte[] data)
	{
		if (data == null || data.Length < 16)
		{
			return;
		}
		try
		{
			RbxmModelParser.ExtractAssetIds(data, AssetIdPattern(), id =>
			{
				AddById(id);
				if (_idToHash.TryGetValue(id, out string? hash))
				{
					AssetPreloadCache.ObserveBatchAsset(id, hash, "");
				}
			});
		}
		catch
		{
		}
	}

	public static void ScanModelReferences(int maxFiles)
	{
		if (_suppressScan)
		{
			return;
		}
		int num = 0;
		foreach (CapturedAsset value in _items.Values)
		{
			if (num >= maxFiles || _suppressScan)
			{
				break;
			}
			if (value.Category != "Model" || string.IsNullOrEmpty(value.FilePath) || !_modelScanned.TryAdd(value.Key, 0))
			{
				continue;
			}
			TrimDictionary(_modelScanned, MaxItems);
			try
			{
				FileInfo fileInfo = new(value.FilePath);
				if (fileInfo.Exists && fileInfo.Length <= MaxAssetBytes + RbxhHeaderSize)
				{
					byte[] array = ReadContent(value);
					if (array != null && array.Length != 0)
					{
						array = MaybeGunzip(array);
						ExtractAndAddReferences(array);
						num++;
					}
				}
			}
			catch
			{
			}
		}
	}

	private static byte[] ReadHead(string path, int count)
	{
		using FileStream fileStream = File.OpenRead(path);
		byte[] array = new byte[count];
		int num = fileStream.Read(array, 0, count);
		if (num == count)
		{
			return array;
		}
		byte[] array2 = new byte[num];
		Array.Copy(array, array2, num);
		return array2;
	}

	public static void Clear()
	{
		_suppressScan = true;
		Shutdown();
		_items.Clear();
		_index.Clear();
		_hashToId.Clear();
		_idToHash.Clear();
		_typeById.Clear();
		_fileById.Clear();
		_cacheFiles.Clear();
		ResetResolveState();
		_currentPlaceId = 0L;
		_logPath = null;
		_logOffset = 0L;
		lock (_diskLoadGate)
		{
			_diskLoaded = false;
		}
		while (_order.TryDequeue(out _))
		{
		}
		try
		{
			if (Directory.Exists(CacheDir))
			{
				Directory.Delete(CacheDir, recursive: true);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "The asset cache folder could not be removed: " + ex.Message);
		}
		_suppressScan = false;
	}

	public static void FullReset()
	{
		_suppressScan = true;
		Shutdown();
		_hashToId.Clear();
		_idToHash.Clear();
		_typeById.Clear();
		_fileById.Clear();
		_hashResolveAttempted.Clear();
		_prefetchAttempted.Clear();
		_modelScanned.Clear();
		_fetchFailed.Clear();
		_logsScanned.Clear();
		_avatarScanned = false;
		_avatarCooldownUntil = DateTime.MinValue;
		_items.Clear();
		_index.Clear();
		lock (_diskLoadGate)
		{
			_diskLoaded = false;
		}
		while (_order.TryDequeue(out _))
		{
		}
		try
		{
			FileInfo fileInfo = (Directory.Exists(RobloxLogsDir) ? (from f in new DirectoryInfo(RobloxLogsDir).EnumerateFiles("*_Player_*.log")
				orderby f.LastWriteTimeUtc descending
				select f).FirstOrDefault() : null);
			if (fileInfo != null)
			{
				_logPath = fileInfo.FullName;
				_logOffset = fileInfo.Length;
			}
			else
			{
				_logPath = null;
				_logOffset = 0L;
			}
		}
		catch
		{
			_logPath = null;
			_logOffset = 0L;
		}
		Task.Run(() =>
		{
			try
			{
				if (Directory.Exists(CacheDir))
				{
					Directory.Delete(CacheDir, recursive: true);
				}
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LOG_IDENT, "The asset cache folder could not be removed: " + ex.Message);
			}
			try
			{
				if (Directory.Exists(RbxStorageDir))
				{
					foreach (FileInfo item in new DirectoryInfo(RbxStorageDir).EnumerateFiles("*", SearchOption.AllDirectories))
					{
						try
						{
							item.Delete();
						}
						catch
						{
						}
					}
					return;
				}
			}
			catch
			{
			}
		}).ContinueWith(_ =>
		{
			_suppressScan = false;
		});
	}

}
