using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

public static class WebsiteSaveQueue
{
	private const int MaxPayloadFileBytes = 4_000_000;
	private static readonly object Sync = new();
	private static readonly SemaphoreSlim PostGate = new(1, 1);
	private static int _running;
	private static int _shutdown;
	private static CancellationTokenSource? _cts;

	private sealed record PendingEntry(Guid Version, string Payload);

	private static string FilePath => Path.Combine(Paths.Config, "WebsiteSaveQueue.dat");

	public static void Enqueue(string jsonPayload)
	{
		if (Stage(jsonPayload) != null)
		{
			Start();
		}
	}

	internal static async Task<(PostOutcome Outcome, string? Error)> PostLatestAsync(string jsonPayload, CancellationToken cancellationToken)
	{
		PendingEntry? entry = Stage(jsonPayload);
		if (entry == null)
		{
			return (PostOutcome.Permanent, "The profile payload is empty.");
		}
		var (outcome, error, superseded) = await PostEntryAsync(entry, cancellationToken).ConfigureAwait(false);
		if (superseded)
		{
			return (PostOutcome.Ok, null);
		}
		if (outcome == PostOutcome.Transient)
		{
			Start();
		}
		return (outcome, error);
	}

	private static PendingEntry? Stage(string jsonPayload)
	{
		if (string.IsNullOrEmpty(jsonPayload))
		{
			return null;
		}
		lock (Sync)
		{
			try
			{
				PendingEntry entry = new(Guid.NewGuid(), jsonPayload);
				WritePendingUnlocked(entry);
				return entry;
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("WebsiteSaveQueue::Stage", ex);
				return null;
			}
		}
	}

	private static void WritePendingUnlocked(PendingEntry entry)
	{
		Directory.CreateDirectory(Paths.Config);
		string encoded = WebsiteAuth.ProtectString(JsonSerializer.Serialize(entry));
		if (string.IsNullOrEmpty(encoded))
		{
			throw new InvalidDataException("The queued profile could not be protected");
		}
		string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(temporary, encoded);
			File.Move(temporary, FilePath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	private static PendingEntry? ReadPending()
	{
		lock (Sync)
		{
			return ReadPendingUnlocked();
		}
	}

	private static PendingEntry? ReadPendingUnlocked()
	{
		try
		{
			if (!File.Exists(FilePath))
			{
				return null;
			}
			FileInfo info = new(FilePath);
			if (info.Length <= 0 || info.Length > MaxPayloadFileBytes)
			{
				ClearPendingUnlocked();
				return null;
			}
			string? plain = WebsiteAuth.UnprotectString(File.ReadAllText(FilePath));
			if (string.IsNullOrEmpty(plain))
			{
				ClearPendingUnlocked();
				return null;
			}
			try
			{
				PendingEntry? entry = JsonSerializer.Deserialize<PendingEntry>(plain);
				if (entry != null && entry.Payload.Length > 0)
				{
					return entry;
				}
			}
			catch (JsonException)
			{
			}
			return new PendingEntry(Guid.Empty, plain);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("WebsiteSaveQueue::ReadPending", ex);
			return null;
		}
	}

	private static void ClearPending(Guid version)
	{
		lock (Sync)
		{
			PendingEntry? current = ReadPendingUnlocked();
			if (current?.Version == version)
			{
				ClearPendingUnlocked();
			}
		}
	}

	private static void ClearPendingUnlocked()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				File.Delete(FilePath);
			}
		}
		catch
		{
		}
	}

	private static bool IsCurrent(Guid version)
	{
		return ReadPending()?.Version == version;
	}

	public static void Start()
	{
		if (Volatile.Read(ref _shutdown) != 0 || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
		{
			return;
		}
		CancellationTokenSource cts = new();
		Interlocked.Exchange(ref _cts, cts);
		if (Volatile.Read(ref _shutdown) != 0)
		{
			cts.Cancel();
		}
		_ = Task.Run(async delegate
		{
			try
			{
				await WorkerAsync(cts.Token).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("WebsiteSaveQueue::Worker", ex);
			}
			finally
			{
				bool cancelled = cts.IsCancellationRequested;
				Interlocked.CompareExchange(ref _cts, null, cts);
				cts.Dispose();
				Interlocked.Exchange(ref _running, 0);
				if (!cancelled && ReadPending() != null)
				{
					Start();
				}
			}
		});
	}

	public static void Shutdown()
	{
		Interlocked.Exchange(ref _shutdown, 1);
		try
		{
			_cts?.Cancel();
		}
		catch
		{
		}
	}

	private static async Task WorkerAsync(CancellationToken cancellationToken)
	{
		int attempt = 0;
		while (!cancellationToken.IsCancellationRequested)
		{
			if (!WebsiteAuth.IsSignedIn())
			{
				lock (Sync)
				{
					ClearPendingUnlocked();
				}
				return;
			}
			PendingEntry? entry = ReadPending();
			if (entry == null)
			{
				return;
			}
			var (outcome, error, superseded) = await PostEntryAsync(entry, cancellationToken).ConfigureAwait(false);
			if (superseded)
			{
				attempt = 0;
				continue;
			}
			if (outcome == PostOutcome.Ok)
			{
				App.Logger.WriteLine("WebsiteSaveQueue", "Queued profile changes were applied automatically after the service recovered");
				return;
			}
			if (outcome == PostOutcome.Permanent)
			{
				App.Logger.WriteLine("WebsiteSaveQueue", "Queued profile save was dropped, the server rejected it: " + (error ?? "unknown"));
				return;
			}
			attempt++;
			int shift = Math.Min(attempt - 1, 5);
			int delayMilliseconds = Math.Min(300000, 15000 * (1 << shift));
			try
			{
				await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	private static async Task<(PostOutcome Outcome, string? Error, bool Superseded)> PostEntryAsync(PendingEntry entry, CancellationToken cancellationToken)
	{
		await PostGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!IsCurrent(entry.Version))
			{
				return (PostOutcome.Ok, null, true);
			}
			var (outcome, error) = await WebsiteProfileEditor.PostProfileAsync(entry.Payload, cancellationToken).ConfigureAwait(false);
			if (outcome is PostOutcome.Ok or PostOutcome.Permanent)
			{
				ClearPending(entry.Version);
			}
			return (outcome, error, false);
		}
		finally
		{
			PostGate.Release();
		}
	}
}
