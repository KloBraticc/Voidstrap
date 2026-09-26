using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Integrations;

internal static class LinuxAudioDucker
{
	private const string LOG_IDENT = "AudioDucker";
	private const float DuckLevel = 0.2f;
	private const int FadeSteps = 4;
	private const int FadeIntervalMs = 150;
	private const int FocusPollIntervalMs = 250;
	private const int StreamRefreshIntervalMs = 3000;
	private const float RestoreTolerance = 0.02f;
	private const int OwnershipRetryMs = 2000;

	private static readonly object Gate = new();
	private static readonly Dictionary<int, float> Snapshots = [];
	private static CancellationTokenSource? _cts;
	private static Task? _loop;

	private static string RestoreFile => Path.Combine(
		Path.GetDirectoryName(LinuxEffectLayers.ConfigDirectory) ?? LinuxEffectLayers.ConfigDirectory,
		"sober-volume");

	public static bool Start()
	{
		lock (Gate)
		{
			if (_cts != null)
				return true;
			if (!LinuxAudioSessions.IsAvailable)
			{
				App.Logger?.WriteLine(LOG_IDENT, "PipeWire tools were not found, so Sober audio cannot be lowered while it is unfocused");
				return false;
			}
			CancellationTokenSource source = new();
			_cts = source;
			_loop = Task.Run(() => LoopAsync(source.Token));
		}
		App.Logger?.WriteLine(LOG_IDENT, "Audio ducking started for Sober");
		return true;
	}

	public static void Stop()
	{
		CancellationTokenSource? source;
		Task? loop;
		lock (Gate)
		{
			source = _cts;
			loop = _loop;
			_cts = null;
			_loop = null;
		}
		if (source == null)
			return;
		try
		{
			source.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		try
		{
			loop?.Wait(3000);
		}
		catch (AggregateException)
		{
		}
		source.Dispose();
		App.Logger?.WriteLine(LOG_IDENT, "Audio ducking stopped");
	}

	private static async Task LoopAsync(CancellationToken token)
	{
		IReadOnlyList<LinuxAudioStream> streams = [];
		long nextRefresh = 0;
		bool? lastFocused = null;
		bool soberWindowSeen = false;
		bool waylandReported = false;
		bool ownershipReported = false;
		FileStream? ownership = null;
		try
		{
			while (!token.IsCancellationRequested)
			{
				if (ownership == null)
				{
					ownership = LinuxAudioSessions.TryAcquireOwnership();
					if (ownership == null)
					{
						if (!ownershipReported)
						{
							ownershipReported = true;
							App.Logger?.WriteLine(LOG_IDENT, "Another Voidstrap process is handling Sober audio, taking over when it exits");
						}
						await Task.Delay(OwnershipRetryMs, token).ConfigureAwait(false);
						continue;
					}
				}

				bool focused = LinuxWindowInterop.IsRuntimeWindowActive();
				long now = Environment.TickCount64;
				if (now >= nextRefresh || focused != lastFocused)
				{
					streams = await LinuxAudioSessions.FindSoberStreamsAsync(token).ConfigureAwait(false) ?? streams;
					nextRefresh = now + StreamRefreshIntervalMs;
					soberWindowSeen = focused || (streams.Count > 0 && LinuxWindowInterop.FindSoberWindows().Count > 0);
					ForgetMissing(streams);
				}
				lastFocused = focused;

				if (streams.Count > 0 && !soberWindowSeen)
				{
					if (!waylandReported)
					{
						waylandReported = true;
						App.Logger?.WriteLine(LOG_IDENT, "Sober is playing audio but its window is not on X11, so Voidstrap cannot tell when it loses focus");
					}
				}
				else if (streams.Count > 0)
				{
					if (focused)
						await RestoreAsync(streams, token).ConfigureAwait(false);
					else
						await DuckAsync(streams, token).ConfigureAwait(false);
				}

				await Task.Delay(FocusPollIntervalMs, token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("LinuxAudioDucker::Loop", ex);
		}
		finally
		{
			await RestoreAllAsync().ConfigureAwait(false);
			ownership?.Dispose();
		}
	}

	private static async Task DuckAsync(IReadOnlyList<LinuxAudioStream> streams, CancellationToken token)
	{
		float? pending = ReadRestoreFile();
		bool lowered = false;
		float reported = 1f;
		foreach (LinuxAudioStream stream in streams)
		{
			float original;
			lock (Gate)
			{
				if (Snapshots.ContainsKey(stream.NodeId))
					continue;
				original = pending ?? stream.Volume;
				Snapshots[stream.NodeId] = original;
			}
			WriteRestoreFile(original);
			await FadeAsync(stream.NodeId, stream.Volume, original * DuckLevel, token).ConfigureAwait(false);
			lowered = true;
			reported = original;
		}
		if (lowered)
			App.Logger?.WriteLine(LOG_IDENT, $"Sober lost focus, its audio is lowered from {Percent(reported)} to {Percent(reported * DuckLevel)} percent");
	}

	private static async Task RestoreAsync(IReadOnlyList<LinuxAudioStream> streams, CancellationToken token)
	{
		float? pending = ReadRestoreFile();
		bool restored = false;
		float reported = 1f;
		foreach (LinuxAudioStream stream in streams)
		{
			float target;
			bool tracked;
			lock (Gate)
				tracked = Snapshots.Remove(stream.NodeId, out target);

			if (!tracked)
			{
				if (pending is not float saved)
					continue;
				if (Math.Abs(stream.Volume - saved * DuckLevel) > RestoreTolerance)
					continue;
				target = saved;
				App.Logger?.WriteLine(LOG_IDENT, "Sober started with the lowered volume from last time, putting it back to " + Percent(saved) + " percent");
			}

			await FadeAsync(stream.NodeId, stream.Volume, target, token).ConfigureAwait(false);
			restored = true;
			reported = target;
		}

		bool empty;
		lock (Gate)
			empty = Snapshots.Count == 0;
		if (empty && pending is not null)
			DeleteRestoreFile();
		if (restored)
			App.Logger?.WriteLine(LOG_IDENT, $"Sober is focused again, its audio is back to {Percent(reported)} percent");
	}

	private static async Task FadeAsync(int nodeId, float from, float to, CancellationToken token)
	{
		for (int step = 1; step <= FadeSteps; step++)
		{
			float volume = from + (to - from) * step / FadeSteps;
			await LinuxAudioSessions.SetVolumeAsync(nodeId, volume, token).ConfigureAwait(false);
			if (step < FadeSteps)
				await Task.Delay(FadeIntervalMs, token).ConfigureAwait(false);
		}
	}

	private static void ForgetMissing(IReadOnlyList<LinuxAudioStream> streams)
	{
		lock (Gate)
		{
			foreach (int nodeId in Snapshots.Keys.ToArray())
			{
				if (!streams.Any(stream => stream.NodeId == nodeId))
					Snapshots.Remove(nodeId);
			}
		}
	}

	private static async Task RestoreAllAsync()
	{
		KeyValuePair<int, float>[] snapshots;
		lock (Gate)
		{
			snapshots = Snapshots.ToArray();
			Snapshots.Clear();
		}
		if (snapshots.Length == 0)
			return;

		bool allRestored = true;
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
		foreach (KeyValuePair<int, float> snapshot in snapshots)
		{
			if (!await LinuxAudioSessions.SetVolumeAsync(snapshot.Key, snapshot.Value, timeout.Token).ConfigureAwait(false))
				allRestored = false;
		}
		if (allRestored)
			DeleteRestoreFile();
		App.Logger?.WriteLine(LOG_IDENT, allRestored
			? "Sober audio was put back to its normal volume"
			: "Sober audio could not be put back right now, it will be restored the next time Sober plays");
	}

	private static float? ReadRestoreFile()
	{
		try
		{
			string path = RestoreFile;
			if (!File.Exists(path))
				return null;
			return float.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && value > 0f && value <= 1.5f
				? value
				: null;
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static void WriteRestoreFile(float volume)
	{
		try
		{
			string path = RestoreFile;
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, volume.ToString("0.0000", CultureInfo.InvariantCulture));
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static void DeleteRestoreFile()
	{
		try
		{
			File.Delete(RestoreFile);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static string Percent(float volume)
	{
		return Math.Round(volume * 100f).ToString("0", CultureInfo.InvariantCulture);
	}
}
