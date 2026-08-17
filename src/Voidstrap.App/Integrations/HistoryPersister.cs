using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Models.Entities;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

public static class PlayTimeStore
{
	private const int MaxStoreBytes = 16777216;

	private const int MaxUniverseEntries = 2000;

	private const int MaxSessionEntries = 500;

	private static readonly object _lock = new object();

	private static readonly string _filePath = Paths.PlayTimeStore;

	public sealed class UniverseTime
	{
		public double Minutes { get; set; }

		public DateTime? LastPlayed { get; set; }
	}

	public sealed class StoreData
	{
		public Dictionary<long, UniverseTime> Universes { get; set; } = new();

		public Dictionary<string, double> Sessions { get; set; } = new();

		public Dictionary<string, DateTime> SessionSeen { get; set; } = new();
	}

	public static StoreData Read()
	{
		lock (_lock)
		{
			return ReadCore();
		}
	}

	private static StoreData ReadCore()
	{
		try
		{
			if (!File.Exists(_filePath))
			{
				return new StoreData();
			}
			if (new FileInfo(_filePath).Length > MaxStoreBytes)
			{
				return new StoreData();
			}
			StoreData data = JsonFile.Deserialize<StoreData>(_filePath, JsonOptions.Tolerant, MaxStoreBytes);
			data.Universes = (data.Universes ?? new Dictionary<long, UniverseTime>()).Where((KeyValuePair<long, UniverseTime> item) => item.Key > 0 && item.Value != null).OrderByDescending((KeyValuePair<long, UniverseTime> item) => item.Value.LastPlayed ?? DateTime.MinValue).Take(MaxUniverseEntries).ToDictionary((KeyValuePair<long, UniverseTime> item) => item.Key, (KeyValuePair<long, UniverseTime> item) => item.Value);
			data.SessionSeen ??= new Dictionary<string, DateTime>();
			data.Sessions = (data.Sessions ?? new Dictionary<string, double>())
				.Where(item => !string.IsNullOrWhiteSpace(item.Key))
				.OrderByDescending(item => data.SessionSeen.TryGetValue(item.Key, out DateTime seen) ? seen : DateTime.MinValue)
				.Take(MaxSessionEntries)
				.ToDictionary(item => item.Key, item => item.Value);
			data.SessionSeen = data.SessionSeen.Where(item => data.Sessions.ContainsKey(item.Key)).ToDictionary(item => item.Key, item => item.Value);
			return data;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("PlayTimeStore", "Read failed: " + ex.Message);
			return new StoreData();
		}
	}

	public static void RemoveUniverse(long universeId, IEnumerable<string>? sessionKeys = null)
	{
		lock (_lock)
		{
			try
			{
				StoreData data = ReadCore();
				bool changed = data.Universes.Remove(universeId);
				if (sessionKeys != null)
				{
					foreach (string key in sessionKeys)
					{
						if (data.Sessions.Remove(key) | data.SessionSeen.Remove(key))
						{
							changed = true;
						}
						foreach (string matchingKey in data.Sessions.Keys.Where(candidate => candidate.StartsWith(key + "_", StringComparison.Ordinal)).ToArray())
						{
							data.Sessions.Remove(matchingKey);
							data.SessionSeen.Remove(matchingKey);
							changed = true;
						}
					}
				}
				if (changed)
				{
					Directory.CreateDirectory(Paths.Data);
					JsonFile.SerializeAtomic(_filePath, data, JsonOptions.Indented);
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("PlayTimeStore", "Remove failed: " + ex.Message);
			}
		}
	}

	public static void MergeRemote(Dictionary<long, UniverseTime> remote, bool replace)
	{
		lock (_lock)
		{
			try
			{
				StoreData data = ReadCore();
				bool changed = false;
				if (replace && data.Universes.Count > 0)
				{
					data.Universes.Clear();
					changed = true;
				}
				foreach (KeyValuePair<long, UniverseTime> kv in remote)
				{
					if (kv.Key <= 0 || kv.Value == null)
					{
						continue;
					}
					if (!data.Universes.TryGetValue(kv.Key, out UniverseTime? entry))
					{
						data.Universes[kv.Key] = new UniverseTime
						{
							Minutes = kv.Value.Minutes,
							LastPlayed = kv.Value.LastPlayed
						};
						changed = true;
						continue;
					}
					if (kv.Value.Minutes > entry.Minutes)
					{
						entry.Minutes = kv.Value.Minutes;
						changed = true;
					}
					if (kv.Value.LastPlayed.HasValue && (!entry.LastPlayed.HasValue || kv.Value.LastPlayed.Value > entry.LastPlayed.Value))
					{
						entry.LastPlayed = kv.Value.LastPlayed;
						changed = true;
					}
				}
				if (changed)
				{
					Directory.CreateDirectory(Paths.Data);
					JsonFile.SerializeAtomic(_filePath, data, JsonOptions.Indented);
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("PlayTimeStore", "MergeRemote failed: " + ex.Message);
			}
		}
	}

	public static void Accumulate(IEnumerable<ActivityData> sessions, string? liveKey = null)
	{
		lock (_lock)
		{
			try
			{
				StoreData data = ReadCore();
				bool changed = false;
				foreach (ActivityData session in sessions)
				{
					if (session == null || session.UniverseId == 0)
					{
						continue;
					}
					string key = GetSessionKey(session);
					if (!data.Universes.TryGetValue(session.UniverseId, out UniverseTime? entry))
					{
						entry = new UniverseTime();
						data.Universes[session.UniverseId] = entry;
						changed = true;
					}
					DateTime? end = session.TimeLeft;
					if (!end.HasValue && liveKey != null && key == liveKey && session.TimeJoined != default)
					{
						end = DateTime.Now;
					}
					DateTime seen = end ?? session.TimeJoined;
					if (seen != default && (entry.LastPlayed == null || seen > entry.LastPlayed.Value))
					{
						entry.LastPlayed = seen;
						changed = true;
					}
					if (!end.HasValue || end.Value <= session.TimeJoined)
					{
						continue;
					}
					double minutes = Math.Min((end.Value - session.TimeJoined).TotalMinutes, 1440.0);
					double counted = data.Sessions.TryGetValue(key, out double c) ? c : 0.0;
					if (minutes > counted)
					{
						entry.Minutes += minutes - counted;
						data.Sessions[key] = minutes;
						changed = true;
					}
					DateTime now = DateTime.UtcNow;
					if (!data.SessionSeen.TryGetValue(key, out DateTime lastSeen) || now - lastSeen > TimeSpan.FromMinutes(1))
					{
						data.SessionSeen[key] = now;
						changed = true;
					}
				}
				string[] stale = data.Sessions.Keys
					.OrderByDescending(key => data.SessionSeen.TryGetValue(key, out DateTime seen) ? seen : DateTime.MinValue)
					.Skip(MaxSessionEntries)
					.ToArray();
				foreach (string key in stale)
				{
					data.Sessions.Remove(key);
					data.SessionSeen.Remove(key);
					changed = true;
				}
				if (changed)
				{
					Directory.CreateDirectory(Paths.Data);
					JsonFile.SerializeAtomic(_filePath, data, JsonOptions.Indented);
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("PlayTimeStore", "Accumulate failed: " + ex.Message);
			}
		}
	}

	public static string GetSessionKey(ActivityData session)
	{
		return $"{session.PlaceId}_{session.JobId}_{session.TimeJoined.ToUniversalTime().Ticks}";
	}
}

public sealed class HistoryPersister : IDisposable
{
	private const int MaxHistoryEntries = 100;
	private static readonly TimeSpan DesktopHistoryRetention = TimeSpan.FromDays(5);

	private const string LOG_IDENT = "HistoryPersister";

	private readonly ActivityWatcher _activityWatcher;

	private readonly SemaphoreSlim _saveLock = new SemaphoreSlim(1, 1);

	private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();

	private readonly CancellationToken _lifetimeToken;

	private readonly string _historyFilePath = Paths.ServerHistory;

	private readonly Timer _liveTimer;

	private bool _disposed;

	public HistoryPersister(ActivityWatcher activityWatcher)
	{
		_activityWatcher = activityWatcher ?? throw new ArgumentNullException("activityWatcher");
		_lifetimeToken = _lifetimeCancellation.Token;
		_activityWatcher.OnGameLeave += OnActivityChanged;
		_activityWatcher.OnGameJoin += OnActivityChanged;
		_liveTimer = new Timer(OnLiveTick, null, TimeSpan.FromMinutes(10.0), TimeSpan.FromMinutes(10.0));
	}

	public static bool IsWithinDesktopRetention(ActivityData? activity)
	{
		if (activity == null)
			return false;
		DateTime latest = activity.TimeLeft ?? activity.TimeJoined;
		return latest != default && latest >= DateTime.Now.Subtract(DesktopHistoryRetention);
	}

	private void OnActivityChanged(object? sender, EventArgs e)
	{
		if (!_activityWatcher.InGame)
		{
			_ = SaveHistoryAsync(_lifetimeToken);
		}
	}

	private void OnLiveTick(object? state)
	{
		try
		{
			if (!_disposed && _activityWatcher.InGame)
			{
				List<ActivityData> sessions;
				lock (_activityWatcher.History)
				{
					sessions = _activityWatcher.History.Where(x => x != null && x.PlaceId != 0).ToList();
				}
				ActivityData live = _activityWatcher.Data;
				if (live != null && live.PlaceId != 0 && !sessions.Contains(live))
				{
					sessions.Add(live);
				}
				string? liveKey = live == null || live.PlaceId == 0 ? null : PlayTimeStore.GetSessionKey(live);
				_ = Task.Run(() => PlayTimeStore.Accumulate(sessions, liveKey), _lifetimeToken);
			}
		}
		catch (ObjectDisposedException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("HistoryPersister::OnLiveTick", ex);
		}
	}

	private async Task SaveHistoryAsync(CancellationToken token)
	{
		bool acquired = false;
		if (_disposed || token.IsCancellationRequested)
		{
			return;
		}
		try
		{
			await _saveLock.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
			acquired = true;
			await SaveHistoryCoreAsync(token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("HistoryPersister", ex);
		}
		finally
		{
			if (acquired)
			{
				_saveLock.Release();
			}
		}
	}

	private async Task SaveHistoryCoreAsync(CancellationToken token)
	{
		List<ActivityData> current;
		lock (_activityWatcher.History)
		{
			current = _activityWatcher.History.Where((ActivityData x) => x != null && x.PlaceId != 0).ToList();
		}
			string? liveKey = null;
			ActivityData live = _activityWatcher.Data;
			if (_activityWatcher.InGame && live != null && live.PlaceId != 0 && live.TimeJoined != default)
			{
				liveKey = PlayTimeStore.GetSessionKey(live);
				if (!current.Contains(live))
				{
					current.Add(live);
				}
			}
			List<ActivityData> value = Merge(await LoadExistingAsync(token).ConfigureAwait(continueOnCapturedContext: false), current);
			Directory.CreateDirectory(Paths.Data);
			token.ThrowIfCancellationRequested();
			JsonFile.SerializeAtomic(_historyFilePath, value, JsonOptions.Indented);
			if (current.Count != 0)
			{
				await Task.Run(() => PlayTimeStore.Accumulate(value, liveKey), token).ConfigureAwait(continueOnCapturedContext: false);
				WebsiteHistorySync.PushSoon();
			}
	}

	private async Task<List<ActivityData>> LoadExistingAsync(CancellationToken token)
	{
		try
		{
			if (!File.Exists(_historyFilePath))
			{
				return new List<ActivityData>();
			}
			if (new FileInfo(_historyFilePath).Length > 16777216)
			{
				return new List<ActivityData>();
			}
			token.ThrowIfCancellationRequested();
			List<ActivityData> entries = JsonFile.Deserialize<List<ActivityData>>(_historyFilePath, JsonOptions.Tolerant, 16777216);
			foreach (ActivityData entry in entries)
			{
				TrimLogs(entry);
			}
			return entries.Where(IsWithinDesktopRetention).OrderByDescending((ActivityData x) => x.TimeJoined).Take(MaxHistoryEntries).ToList();
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("HistoryPersister", ex);
			return new List<ActivityData>();
		}
	}

	private static List<ActivityData> Merge(IEnumerable<ActivityData> existing, IEnumerable<ActivityData> incoming)
	{
		Dictionary<string, ActivityData> dictionary = new Dictionary<string, ActivityData>();
		foreach (ActivityData item in existing.Where(IsWithinDesktopRetention))
		{
			if (item != null)
			{
				TrimLogs(item);
				string key = $"{item.PlaceId}_{item.JobId}";
				dictionary[key] = item;
			}
		}
		foreach (ActivityData item2 in incoming.Where(IsWithinDesktopRetention))
		{
			if (item2 == null)
			{
				continue;
			}
			string key2 = $"{item2.PlaceId}_{item2.JobId}";
			if (dictionary.TryGetValue(key2, out var value))
			{
				if (value.TimeJoined > item2.TimeJoined && item2.TimeJoined != default(DateTime))
				{
					value.TimeJoined = item2.TimeJoined;
				}
				if (item2.TimeLeft.HasValue && (!value.TimeLeft.HasValue || value.TimeLeft.Value < item2.TimeLeft.Value))
				{
					value.TimeLeft = item2.TimeLeft;
				}
				if (value.UniverseId == 0 && item2.UniverseId != 0)
				{
					value.UniverseId = item2.UniverseId;
				}
				if (value.RootActivity == null && item2.RootActivity != null)
				{
					value.RootActivity = item2.RootActivity;
				}
				if (value.UniverseDetails == null && item2.UniverseDetails != null)
				{
					value.UniverseDetails = item2.UniverseDetails;
				}
				foreach (KeyValuePair<int, ActivityData.UserLog> playerLog in item2.PlayerLogs)
				{
					value.PlayerLogs[playerLog.Key] = playerLog.Value;
				}
				foreach (KeyValuePair<int, ActivityData.UserMessage> messageLog in item2.MessageLogs)
				{
					value.MessageLogs[messageLog.Key] = messageLog.Value;
				}
				TrimLogs(value);
			}
			else
			{
				dictionary[key2] = item2;
			}
		}
		return dictionary.Values.Where(IsWithinDesktopRetention).OrderByDescending((ActivityData x) => x.TimeJoined).Take(MaxHistoryEntries).ToList();
	}

	private static void TrimLogs(ActivityData activity)
	{
		TrimDictionary(activity.PlayerLogs, ActivityWatcher.MaxPlayerLogEntries);
		TrimDictionary(activity.MessageLogs, ActivityWatcher.MaxMessageLogEntries);
	}

	private static void TrimDictionary<T>(Dictionary<int, T> values, int limit)
	{
		if (values.Count <= limit)
		{
			return;
		}
		KeyValuePair<int, T>[] retained = values.OrderByDescending((KeyValuePair<int, T> x) => x.Key).Take(limit).OrderBy((KeyValuePair<int, T> x) => x.Key).ToArray();
		values.Clear();
		foreach (KeyValuePair<int, T> item in retained)
		{
			values[item.Key] = item.Value;
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		try
		{
			_liveTimer.Dispose();
		}
		catch
		{
		}
		try
		{
			_activityWatcher.OnGameLeave -= OnActivityChanged;
			_activityWatcher.OnGameJoin -= OnActivityChanged;
		}
		catch
		{
		}
		_lifetimeCancellation.Cancel();
		bool acquired = false;
		try
		{
			acquired = _saveLock.Wait(TimeSpan.FromSeconds(2));
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("HistoryPersister::Dispose", ex);
		}
		finally
		{
			if (acquired)
			{
				_saveLock.Release();
			}
		}
		_lifetimeCancellation.Dispose();
		if (acquired)
		{
			_saveLock.Dispose();
		}
		else
		{
			_ = DisposeSaveLockWhenIdleAsync();
		}
		GC.SuppressFinalize(this);
	}

	private async Task DisposeSaveLockWhenIdleAsync()
	{
		try
		{
			await _saveLock.WaitAsync().ConfigureAwait(false);
			_saveLock.Dispose();
		}
		catch
		{
		}
	}
}
