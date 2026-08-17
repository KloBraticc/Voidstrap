using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer;

internal static class AutoSaveHelper
{
	public const long MaxSaveBytes = 536870912;
	private const int MaxSaveCount = 20;
	private const long MaxStoredBytes = 5368709120;
	private static readonly SemaphoreSlim _saveGate = new SemaphoreSlim(1, 1);

	public static async Task SaveAsync(Stream stream, bool shouldCompress, CancellationToken cancellationToken)
	{
		if (!await _saveGate.WaitAsync(0, cancellationToken))
		{
			throw new InvalidOperationException("An autosave is already in progress");
		}
		string path = "";
		string temporary = "";
		byte[]? buffer = null;
		try
		{
			Directory.CreateDirectory(PathHelper.AutoSaves);
			path = Path.Combine(PathHelper.AutoSaves, "VoidstrapClient Save " + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + " " + Guid.NewGuid().ToString("N") + ".rbxl.gz");
			temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			buffer = ArrayPool<byte>.Shared.Rent(131072);
			await using FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
			Stream destination = output;
			GZipStream? compressed = null;
			if (shouldCompress)
			{
				compressed = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
				destination = compressed;
			}
			try
			{
				long total = 0;
				while (true)
				{
					int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
					if (read == 0)
					{
						break;
					}
					total += read;
					if (total > MaxSaveBytes)
					{
						throw new InvalidDataException("The autosave exceeds the size limit");
					}
					await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
				}
			}
			finally
			{
				if (compressed != null)
				{
					await compressed.DisposeAsync();
				}
			}
			await output.FlushAsync(cancellationToken);
			await output.DisposeAsync();
			File.Move(temporary, path);
			TrimRetention();
		}
		catch
		{
			if (temporary.Length > 0)
			{
				TryDelete(temporary);
			}
			throw;
		}
		finally
		{
			if (buffer != null)
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
			_saveGate.Release();
		}
	}

	private static void TrimRetention()
	{
		try
		{
			PriorityQueue<FileInfo, long> retained = new PriorityQueue<FileInfo, long>();
			long storedBytes = 0;
			foreach (FileInfo file in new DirectoryInfo(PathHelper.AutoSaves).EnumerateFiles("VoidstrapClient Save *.rbxl.gz"))
			{
				retained.Enqueue(file, file.LastWriteTimeUtc.Ticks);
				storedBytes += file.Length;
				while (retained.Count > MaxSaveCount || storedBytes > MaxStoredBytes)
				{
					FileInfo oldest = retained.Dequeue();
					storedBytes -= oldest.Length;
					TryDelete(oldest.FullName);
				}
			}
		}
		catch
		{
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}
}
