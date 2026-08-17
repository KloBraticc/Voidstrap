using System.Collections.Generic;
using System;
using System.Linq;
using System.Text;

namespace VoidstrapClient.WebServer.Services;

internal class DataPersistenceService
{
	private static readonly byte[] _emptyBlobBytes = Common.CompressGzip(Encoding.UTF8.GetBytes("<Table></Table>"));

	public const int MaxBlobSize = 280000;
	private const int MaxEntries = 512;
	private const long MaxTotalBytes = 64L * 1024 * 1024;
	private static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(1);
	private readonly object _sync = new object();
	private readonly Dictionary<long, BlobEntry> _blobs = new Dictionary<long, BlobEntry>();
	private long _totalBytes;

	private sealed class BlobEntry
	{
		public required byte[] Value { get; init; }
		public DateTime LastAccessUtc { get; set; }
	}

	public static DataPersistenceService Instance { get; } = new DataPersistenceService();

	public void SaveBlob(long userId, byte[] blob, bool alreadyCompressed)
	{
		ArgumentNullException.ThrowIfNull(blob);
		if (blob.Length > MaxBlobSize)
		{
			throw new ArgumentOutOfRangeException(nameof(blob));
		}

		byte[] value = alreadyCompressed ? blob : Common.CompressGzip(blob);
		DateTime now = DateTime.UtcNow;
		lock (_sync)
		{
			RemoveExpired(now);
			if (_blobs.Remove(userId, out BlobEntry? previous))
			{
				_totalBytes -= previous.Value.Length;
			}
			while (_blobs.Count >= MaxEntries || _totalBytes + value.Length > MaxTotalBytes)
			{
				KeyValuePair<long, BlobEntry> oldest = _blobs.MinBy(pair => pair.Value.LastAccessUtc);
				_blobs.Remove(oldest.Key);
				_totalBytes -= oldest.Value.Value.Length;
			}
			_blobs[userId] = new BlobEntry { Value = value, LastAccessUtc = now };
			_totalBytes += value.Length;
		}
	}

	public byte[] GetBlob(long userId)
	{
		DateTime now = DateTime.UtcNow;
		lock (_sync)
		{
			RemoveExpired(now);
			if (_blobs.TryGetValue(userId, out BlobEntry? blob))
			{
				blob.LastAccessUtc = now;
				return blob.Value;
			}
			return _emptyBlobBytes;
		}
	}

	private void RemoveExpired(DateTime now)
	{
		foreach (long userId in _blobs.Where(pair => now - pair.Value.LastAccessUtc > EntryLifetime).Select(pair => pair.Key).ToArray())
		{
			BlobEntry removed = _blobs[userId];
			_blobs.Remove(userId);
			_totalBytes -= removed.Value.Length;
		}
	}
}
