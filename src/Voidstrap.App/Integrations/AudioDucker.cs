using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Voidstrap.Integrations.Overlays;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Voidstrap.Integrations;

public static class AudioDucker
{
	private sealed class SessionSnapshot
	{
		public uint ProcessId { get; init; }
		public float Volume { get; init; }
		public bool Muted { get; init; }
	}

	private const string LOG_IDENT = "AudioDucker";
	private const string RobloxProcess = "RobloxPlayerBeta";
	private const float DuckLevel = 0.2f;
	private const float FadeStep = 0.25f;
	private const int FadeIntervalMs = 150;
	private const int FocusPollIntervalMs = 500;
	private const int SessionRefreshIntervalMs = 5000;

	private static readonly object _gate = new object();
	private static readonly Dictionary<string, SessionSnapshot> _snapshots = new Dictionary<string, SessionSnapshot>(StringComparer.Ordinal);
	private static readonly Dictionary<string, uint> _normalizedSessions = new Dictionary<string, uint>(StringComparer.Ordinal);
	private static CancellationTokenSource? _cts;
	private static Task? _loopTask;
	private static CancellationTokenSource? _restoreCts;
	private static Task? _restoreTask;
	private static CancellationTokenSource? _launchResetCts;
	private static Task? _launchResetTask;
	private static int _generation;
	private static int _pendingResetGeneration;

	public static bool IsRunning { get; private set; }

	public static void ApplyFromSettings()
	{
		if (App.Settings.Prop.DuckRobloxAudioOnUnfocus)
			Start();
		else
			Stop();
		NotifyRobloxLaunched(0);
	}

	public static void MarkResetOnNextLaunch()
	{
		lock (_gate)
		{
			_pendingResetGeneration++;
			App.Settings.Prop.ResetRobloxAudioOnNextLaunch = true;
		}
	}

	public static void NotifyRobloxLaunched(int processId)
	{
		if (processId < 0)
			return;

		InvalidateRobloxPidCache();

		bool continuousReset = App.Settings.Prop.DuckRobloxAudioOnUnfocus;
		bool pendingReset = App.Settings.Prop.ResetRobloxAudioOnNextLaunch;
		if (!continuousReset && !pendingReset)
			return;

		CancellationTokenSource? previous;
		CancellationTokenSource owner;
		int pendingGeneration;
		lock (_gate)
		{
			previous = _launchResetCts;
			owner = new CancellationTokenSource();
			_launchResetCts = owner;
			pendingGeneration = _pendingResetGeneration;
			_launchResetTask = Task.Run(() => ResetLaunchedProcessAsync((uint)processId, continuousReset, pendingReset, pendingGeneration, owner));
		}
		try
		{
			previous?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	public static bool Start()
	{
		CancellationTokenSource? owner = null;
		try
		{
			CancelRestoreRetries();
			RestoreAllSnapshots();
			lock (_gate)
			{
				_normalizedSessions.Clear();
				if (IsRunning)
					return true;

				owner = new CancellationTokenSource();
				int generation = ++_generation;
				_cts = owner;
				IsRunning = true;
				_loopTask = Task.Run(() => LoopAsync(generation, owner));
			}
			App.Logger?.WriteLine(LOG_IDENT, "Audio ducking started");
			return true;
		}
		catch (Exception ex)
		{
			lock (_gate)
			{
				if (ReferenceEquals(_cts, owner))
				{
					_cts = null;
					_loopTask = null;
					IsRunning = false;
				}
			}
			owner?.Dispose();
			App.Logger?.WriteException("AudioDucker::Start", ex);
			return false;
		}
	}

	public static void Stop()
	{
		CancellationTokenSource? cts;
		lock (_gate)
		{
			cts = _cts;
			if (cts == null && _loopTask == null)
			{
				IsRunning = false;
			}
			else
			{
				_generation++;
				IsRunning = false;
				_cts = null;
				_loopTask = null;
			}
		}

		try
		{
			cts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		RestoreAllSnapshots();
		StartRestoreRetries();
		App.Logger?.WriteLine(LOG_IDENT, "Audio ducking stopped");
	}

	public static void Shutdown()
	{
		Stop();
		CancelRestoreRetries();
		CancelLaunchReset();
		RestoreAllSnapshots();
	}

	private static async Task ResetLaunchedProcessAsync(uint processId, bool continuousReset, bool clearPending, int pendingGeneration, CancellationTokenSource owner)
	{
		try
		{
			for (int attempt = 0; attempt < 90 && !owner.IsCancellationRequested; attempt++)
			{
				HashSet<uint> pids = processId == 0 ? GetRobloxPids() : new HashSet<uint> { processId };
				if (SetProcessesVolumeToMax(pids, continuousReset))
				{
					if (clearPending)
					{
						lock (_gate)
						{
							if (_pendingResetGeneration == pendingGeneration)
								App.Settings.Prop.ResetRobloxAudioOnNextLaunch = false;
						}
						App.Settings.SaveDeferred();
					}
					return;
				}
				await Task.Delay(500, owner.Token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(_launchResetCts, owner))
				{
					_launchResetCts = null;
					_launchResetTask = null;
				}
			}
			owner.Dispose();
		}
	}

	private static bool SetProcessesVolumeToMax(HashSet<uint> pids, bool duckOnUnfocus)
	{
		bool found = false;
		ForEachRobloxSession(pids, (key, session) =>
		{
			lock (_gate)
			{
				if (duckOnUnfocus && !IsForegroundRoblox(pids))
				{
					session.SimpleAudioVolume.Volume = DuckLevel;
					_snapshots[key] = new SessionSnapshot
					{
						ProcessId = session.GetProcessID,
						Volume = 1.0f,
						Muted = session.SimpleAudioVolume.Mute
					};
					App.Logger?.WriteLine(LOG_IDENT, "Roblox audio session initialized at 20 percent while unfocused");
				}
				else
				{
					session.SimpleAudioVolume.Volume = 1.0f;
					_snapshots.Remove(key);
					App.Logger?.WriteLine(LOG_IDENT, "Roblox audio volume reset to 100 percent");
				}
				_normalizedSessions[key] = session.GetProcessID;
				found = true;
			}
		});
		return found;
	}

	private static async Task LoopAsync(int generation, CancellationTokenSource owner)
	{
		CancellationToken token = owner.Token;
		bool wasFocused = true;
		bool fading = false;
		long nextSessionRefreshMs = 0;
		try
		{
			while (!token.IsCancellationRequested && generation == Volatile.Read(ref _generation))
			{
				HashSet<uint> pids = GetRobloxPidsCached();
				if (pids.Count == 0)
				{
					RestoreAllSnapshots();
					ClearNormalizedSessions();
					wasFocused = true;
					fading = false;
					nextSessionRefreshMs = 0;
					await Task.Delay(FocusPollIntervalMs, token).ConfigureAwait(false);
					continue;
				}

				bool focused = IsForegroundRoblox(pids);
				long now = Environment.TickCount64;
				if (!fading && focused == wasFocused && now < nextSessionRefreshMs)
				{
					await Task.Delay(FocusPollIntervalMs, token).ConfigureAwait(false);
					continue;
				}
				bool complete = ProcessSessions(pids, focused, generation);
				fading = !complete;
				nextSessionRefreshMs = now + (fading ? FadeIntervalMs : SessionRefreshIntervalMs);
				if (focused && complete && HasSnapshots())
				{
					ClearSnapshots();
				}
				wasFocused = focused;
				await Task.Delay(fading ? FadeIntervalMs : FocusPollIntervalMs, token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Audio loop error: " + ex.Message);
		}
		finally
		{
			bool retryRestore;
			lock (_gate)
			{
				retryRestore = !IsRunning;
				if (ReferenceEquals(_cts, owner))
				{
					_cts = null;
					_loopTask = null;
					IsRunning = false;
				}
			}
			owner.Dispose();
			if (retryRestore)
			{
				RestoreAllSnapshots();
				StartRestoreRetries();
			}
		}
	}

	private static bool ProcessSessions(HashSet<uint> pids, bool focused, int generation)
	{
		lock (_gate)
		{
			foreach (KeyValuePair<string, uint> item in _normalizedSessions.ToArray())
			{
				if (!pids.Contains(item.Value))
					_normalizedSessions.Remove(item.Key);
			}
		}

		Dictionary<string, SessionSnapshot> snapshots = CopySnapshots();
		bool complete = true;
		var seen = new HashSet<string>(StringComparer.Ordinal);
		ForEachRobloxSession(pids, (key, session) =>
		{
			bool normalized;
			lock (_gate)
			{
				if (generation != _generation || !IsRunning)
					return;
				normalized = _normalizedSessions.ContainsKey(key);
				if (!normalized)
				{
					if (!focused)
					{
						session.SimpleAudioVolume.Volume = DuckLevel;
						SessionSnapshot seeded = new()
						{
							ProcessId = session.GetProcessID,
							Volume = 1.0f,
							Muted = session.SimpleAudioVolume.Mute
						};
						_snapshots[key] = seeded;
						snapshots[key] = seeded;
					}
					else
					{
						session.SimpleAudioVolume.Volume = 1.0f;
						_snapshots.Remove(key);
						snapshots.Remove(key);
					}
					_normalizedSessions[key] = session.GetProcessID;
				}
				if (!focused && !_snapshots.ContainsKey(key))
				{
					SessionSnapshot captured = new()
					{
						ProcessId = session.GetProcessID,
						Volume = HeadsetAudio.BaseVolume ?? session.SimpleAudioVolume.Volume,
						Muted = session.SimpleAudioVolume.Mute
					};
					_snapshots[key] = captured;
					snapshots[key] = captured;
				}
				if (snapshots.TryGetValue(key, out SessionSnapshot? snapshot))
				{
					seen.Add(key);
					float target = focused ? snapshot.Volume : snapshot.Volume * DuckLevel;
					float current = session.SimpleAudioVolume.Volume;
					float next = StepToward(current, target, FadeStep);
					session.SimpleAudioVolume.Volume = next;
					if (focused)
						session.SimpleAudioVolume.Mute = snapshot.Muted;
					if (Math.Abs(next - target) > 0.005f)
						complete = false;
					else
						session.SimpleAudioVolume.Volume = target;
				}
			}
			if (!normalized)
				App.Logger?.WriteLine(LOG_IDENT, focused ? "Roblox audio session initialized at 100 percent" : "Roblox audio session initialized at 20 percent while unfocused");
		});

		if (focused)
		{
			foreach (KeyValuePair<string, SessionSnapshot> item in snapshots)
			{
				if (!seen.Contains(item.Key) && pids.Contains(item.Value.ProcessId))
					complete = false;
			}
		}
		return complete;
	}

	private const int PidCacheIntervalMs = 5000;

	private static HashSet<uint> _cachedRobloxPids = new HashSet<uint>();

	private static long _cachedRobloxPidsMs;

	private static void InvalidateRobloxPidCache()
	{
		_cachedRobloxPidsMs = 0;
	}

	private static HashSet<uint> GetRobloxPidsCached()
	{
		long now = Environment.TickCount64;
		if (_cachedRobloxPidsMs != 0 && now - _cachedRobloxPidsMs < PidCacheIntervalMs)
			return _cachedRobloxPids;
		_cachedRobloxPids = GetRobloxPids();
		_cachedRobloxPidsMs = now;
		return _cachedRobloxPids;
	}

	private static HashSet<uint> GetRobloxPids()
	{
		var result = new HashSet<uint>();
		try
		{
			Process[] processes = Process.GetProcessesByName(RobloxProcess);
			foreach (Process process in processes)
			{
				try
				{
					result.Add((uint)process.Id);
				}
				catch
				{
				}
				finally
				{
					process.Dispose();
				}
			}
		}
		catch
		{
		}
		return result;
	}

	private static bool IsForegroundRoblox(HashSet<uint> pids)
	{
		try
		{
			HWND foreground = PInvoke.GetForegroundWindow();
			if (foreground == HWND.Null)
				return false;
			IntPtr foregroundValue = foreground;
			if (OverlayDiagnostics.IsOverlayHandle(foregroundValue))
				return true;
			PInvoke.GetWindowThreadProcessId(foreground, out uint pid);
			return pids.Contains(pid);
		}
		catch
		{
			return false;
		}
	}

	private static bool RestoreAllSnapshots()
	{
		Dictionary<string, SessionSnapshot> snapshots = CopySnapshots();
		if (snapshots.Count == 0)
			return true;

		HashSet<uint> pids = GetRobloxPids();
		var restored = new HashSet<string>(StringComparer.Ordinal);
		bool enumerated = ForEachRobloxSession(pids, (key, session) =>
		{
			if (!snapshots.TryGetValue(key, out SessionSnapshot? snapshot))
				return;
			lock (_gate)
			{
				session.SimpleAudioVolume.Volume = snapshot.Volume;
				session.SimpleAudioVolume.Mute = snapshot.Muted;
				restored.Add(key);
			}
		});
		if (!enumerated)
			return false;
		lock (_gate)
		{
			foreach (KeyValuePair<string, SessionSnapshot> item in snapshots)
			{
				if (restored.Contains(item.Key) || !pids.Contains(item.Value.ProcessId))
					_snapshots.Remove(item.Key);
			}
			return _snapshots.Count == 0;
		}
	}

	private static void StartRestoreRetries()
	{
		lock (_gate)
		{
			if (_snapshots.Count == 0 || IsRunning || _restoreTask is { IsCompleted: false })
				return;
			var cts = new CancellationTokenSource();
			_restoreCts = cts;
			_restoreTask = Task.Run(() => RestoreUntilCompleteAsync(cts));
		}
	}

	private static async Task RestoreUntilCompleteAsync(CancellationTokenSource owner)
	{
		try
		{
			for (int attempt = 0; attempt < 20 && !owner.IsCancellationRequested; attempt++)
			{
				if (RestoreAllSnapshots())
					break;
				await Task.Delay(500, owner.Token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(_restoreCts, owner))
				{
					_restoreCts = null;
					_restoreTask = null;
				}
			}
			owner.Dispose();
		}
	}

	private static void CancelRestoreRetries()
	{
		CancellationTokenSource? cts;
		lock (_gate)
		{
			cts = _restoreCts;
			_restoreCts = null;
			_restoreTask = null;
		}
		try
		{
			cts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private static void CancelLaunchReset()
	{
		CancellationTokenSource? cts;
		lock (_gate)
		{
			cts = _launchResetCts;
			_launchResetCts = null;
			_launchResetTask = null;
		}
		try
		{
			cts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private static bool ForEachRobloxSession(HashSet<uint> pids, Action<string, AudioSessionControl> action)
	{
		if (pids.Count == 0)
			return true;
		try
		{
			using var enumerator = new MMDeviceEnumerator();
			MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
			foreach (MMDevice device in devices)
			{
				try
				{
					SessionCollection sessions = device.AudioSessionManager.Sessions;
					for (int i = 0; i < sessions.Count; i++)
					{
						AudioSessionControl session = sessions[i];
						try
						{
							if (!pids.Contains(session.GetProcessID))
								continue;
							string instance = session.GetSessionInstanceIdentifier;
							if (string.IsNullOrEmpty(instance))
								instance = session.GetSessionIdentifier + ":" + session.GetProcessID;
							action(device.ID + "|" + instance, session);
						}
						catch
						{
						}
					}
				}
				catch
				{
				}
				finally
				{
					device.Dispose();
				}
			}
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Audio device access failed: " + ex.Message);
			return false;
		}
	}

	private static Dictionary<string, SessionSnapshot> CopySnapshots()
	{
		lock (_gate)
			return new Dictionary<string, SessionSnapshot>(_snapshots, StringComparer.Ordinal);
	}

	private static bool HasSnapshots()
	{
		lock (_gate)
			return _snapshots.Count > 0;
	}

	private static void ClearSnapshots()
	{
		lock (_gate)
			_snapshots.Clear();
	}

	private static void ClearNormalizedSessions()
	{
		lock (_gate)
			_normalizedSessions.Clear();
	}

	private static float StepToward(float current, float target, float step)
	{
		if (current < target)
			return Math.Min(current + step, target);
		if (current > target)
			return Math.Max(current - step, target);
		return target;
	}
}
