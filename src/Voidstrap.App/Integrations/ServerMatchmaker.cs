using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.AppData;
using Voidstrap.Enums;
using Voidstrap.Models.Entities;
using Voidstrap.Models.Persistable;
using Voidstrap.UI;
using MatchmakerAttempt = Voidstrap.Models.Persistable.MatchmakerAttempt;

namespace Voidstrap.Integrations;

public sealed class ServerMatchmaker : IDisposable
{
	private const string LOG_IDENT = "ServerMatchmaker";

	private const double MinRttGainMs = 8.0;

	private const int JoinSettleDelayMs = 2000;

	private const int HandoffPollIntervalMs = 500;

	private const int HandoffPollCount = 120;

	private static readonly TimeSpan AttemptResetWindow = TimeSpan.FromMinutes(10.0);

	private static readonly TimeSpan HopCooldown = TimeSpan.FromSeconds(90.0);

	private static DateTime _lastHopUtc = DateTime.MinValue;

	private readonly ActivityWatcher _activityWatcher;

	private readonly Watcher _watcher;

	private CancellationTokenSource? _currentCts;

	private bool _disposed;

	public Func<NotifyIconWrapper?>? NotifyIconResolver { get; set; }

	public ServerMatchmaker(ActivityWatcher activityWatcher, Watcher watcher)
	{
		_activityWatcher = activityWatcher ?? throw new ArgumentNullException(nameof(activityWatcher));
		_watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
		_activityWatcher.OnGameJoin += OnGameJoin;
	}

	private void OnGameJoin(object? sender, EventArgs e)
	{
		_ = HandleGameJoinAsync();
	}

	private async Task HandleGameJoinAsync()
	{
		CancellationTokenSource? ownedCts = null;
		try
		{
			if (_disposed)
				return;

			ActivityData? data = _activityWatcher.Data;
			if (data == null)
			{
				App.Logger.WriteLine(LOG_IDENT, "Activity data unavailable, skipping");
				return;
			}
			if (!data.MachineAddressValid)
			{
				App.Logger.WriteLine(LOG_IDENT, $"No routable server address for job {data.JobId}, skipping (address was '{data.MachineAddress}')");
				return;
			}

			_ = RecordLearningAsync(data);

			if (!App.Settings.Prop.VoidstrapMatchmakerEnabled && !HasPerGamePreference(data.PlaceId))
			{
				App.Logger.WriteLine(LOG_IDENT, "Matchmaker is turned off and this place has no per game preference, staying put");
				return;
			}

			if (data.ServerType != ServerType.Public)
			{
				App.Logger.WriteLine(LOG_IDENT, $"Server is {data.ServerType}, the matchmaker only reroutes from public servers");
				ClearAttempt(data.PlaceId);
				return;
			}

			ownedCts = new CancellationTokenSource();
			CancellationTokenSource? previous = Interlocked.Exchange(ref _currentCts, ownedCts);
			try
			{
				previous?.Cancel();
			}
			catch
			{
			}
			await EvaluateAsync(data, ownedCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Game join handling failed: " + ex.Message);
			App.Logger.WriteException(LOG_IDENT, ex);
		}
		finally
		{
			if (ownedCts != null)
			{
				Interlocked.CompareExchange(ref _currentCts, null, ownedCts);
				ownedCts.Dispose();
			}
		}
	}

	private static async Task RecordLearningAsync(ActivityData data)
	{
		try
		{
			using CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
			RobloxDatacenter? dc = await VoidstrapMatchmaker.LookupUnknownIpAsync(data.MachineAddress, timeoutCts.Token).ConfigureAwait(false);
			if (dc != null)
			{
				ServerFetchStore.RecordSighting(data.MachineAddress, dc.City, dc.Region, dc.Country, dc.Lat, dc.Lon);
				App.Logger.WriteLine(LOG_IDENT, $"Logged join: {data.MachineAddress} in {dc.City}, {dc.Country}");
			}
			else
			{
				ServerFetchStore.RecordSighting(data.MachineAddress);
				App.Logger.WriteLine(LOG_IDENT, "Logged join, datacenter could not be resolved: " + data.MachineAddress);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Learning store write failed: " + ex.Message);
		}

	}

	public static bool HasPerGamePreference(long placeId)
	{
		if (placeId == 0L)
			return false;
		try
		{
			Dictionary<long, string>? map = App.Settings.Prop.PerGamePreferredDatacenters;
			return map != null && map.TryGetValue(placeId, out string? key) && !string.IsNullOrWhiteSpace(key);
		}
		catch
		{
			return false;
		}
	}

	public static string ResolvePreferredDatacenterKey(long placeId)
	{
		try
		{
			Dictionary<long, string>? map = App.Settings.Prop.PerGamePreferredDatacenters;
			if (map != null && map.TryGetValue(placeId, out string? key) && !string.IsNullOrWhiteSpace(key))
				return key.Trim();
		}
		catch
		{
		}
		return (App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter ?? "").Trim();
	}

	private async Task EvaluateAsync(ActivityData data, CancellationToken token)
	{
		try
		{
			await Task.Delay(JoinSettleDelayMs, token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		if (_disposed || token.IsCancellationRequested)
			return;

		UserGeo? geo = await VoidstrapMatchmaker.GetUserGeoAsync(token).ConfigureAwait(false);
		if (geo == null)
		{
			App.Logger.WriteLine(LOG_IDENT, "No user location available, staying put");
			return;
		}

		RobloxDatacenter? currentDc = RobloxDatacenterMap.Map(data.MachineAddress)
			?? await VoidstrapMatchmaker.LookupUnknownIpAsync(data.MachineAddress, token).ConfigureAwait(false);
		if (currentDc == null)
		{
			App.Logger.WriteLine(LOG_IDENT, "Current datacenter could not be resolved, staying put instead of hopping blind");
			ClearAttempt(data.PlaceId);
			return;
		}

		double currentKm = VoidstrapMatchmaker.HaversineKm(geo.Lat, geo.Lon, currentDc.Lat, currentDc.Lon);
		int currentPing = VoidstrapMatchmaker.EstimatePingMs(currentKm);
		string currentKey = VoidstrapMatchmaker.DatacenterKey(currentDc);
		bool currentIsBlocked = VoidstrapMatchmaker.GetBlockedDatacenters().Contains(currentKey);

		string preferredKey = ResolvePreferredDatacenterKey(data.PlaceId);
		bool wantsOtherDc = preferredKey.Length > 0 && !VoidstrapMatchmaker.MatchesPreferredDc(currentDc, preferredKey);

		string rejoinTarget = (App.LaunchSettings.MatchmakerTargetFlag.Data ?? "").Trim();
		bool landedOnRejoinTarget = rejoinTarget.Length == 0 || string.Equals(currentDc.City, rejoinTarget, StringComparison.OrdinalIgnoreCase);
		if (App.LaunchSettings.MatchmakerRejoinFlag.Active && !currentIsBlocked && !wantsOtherDc && landedOnRejoinTarget)
		{
			App.Logger.WriteLine(LOG_IDENT, $"Landed via matchmaker rejoin in {currentDc.City} ({currentPing}ms), accepting this server");
			ClearAttempt(data.PlaceId);
			ShowAlert($"Connected to {currentDc.City}, about {currentPing}ms", 6);
			return;
		}
		if (App.LaunchSettings.MatchmakerRejoinFlag.Active && !landedOnRejoinTarget)
			App.Logger.WriteLine(LOG_IDENT, $"Requested {rejoinTarget} but Roblox landed in {currentDc.City}, searching again");

		if (DateTime.UtcNow - _lastHopUtc < HopCooldown)
		{
			App.Logger.WriteLine(LOG_IDENT, "Hop cooldown active, staying put");
			return;
		}

		HashSet<string> tried = GetTriedJobIds(data.PlaceId);
		if (!string.IsNullOrEmpty(data.JobId))
			tried.Add(data.JobId);

		MatchmakerCandidate? best = await VoidstrapMatchmaker
			.PickBestJobIdAsync(data.PlaceId, tried, VoidstrapMatchmaker.ResolveEffectiveCandidateCount(), token, preferredKey)
			.ConfigureAwait(false);

		if (best == null)
		{
			ClearAttempt(data.PlaceId);
			if (currentIsBlocked)
			{
				App.Logger.WriteLine(LOG_IDENT, "Current datacenter is blocked but no alternative was found, staying put");
				ShowAlert($"You are in {currentDc.City} which you blocked, but no other datacenter is available right now", 7);
			}
			else
			{
				App.Logger.WriteLine(LOG_IDENT, "No better server found, staying put");
			}
			return;
		}

		bool sameDc = string.Equals(currentKey, VoidstrapMatchmaker.DatacenterKey(best.Datacenter), StringComparison.OrdinalIgnoreCase);
		bool bestIsPreferred = preferredKey.Length > 0 && VoidstrapMatchmaker.MatchesPreferredDc(best.Datacenter, preferredKey);
		double gainMs = currentPing - best.EstimatedPingMs;

		string? reason = null;
		if (currentIsBlocked && !sameDc)
			reason = $"leaving blocked datacenter {currentDc.City}";
		else if (wantsOtherDc && bestIsPreferred)
			reason = $"moving to your preferred datacenter {best.Datacenter?.City}";
		else if (!sameDc && gainMs >= MinRttGainMs)
			reason = $"saving about {(int)gainMs}ms";

		if (reason == null)
		{
			ClearAttempt(data.PlaceId);
			if (currentIsBlocked)
			{
				App.Logger.WriteLine(LOG_IDENT, $"Best alternative is in the same blocked datacenter {currentDc.City}, staying put");
				ShowAlert($"You are in {currentDc.City} which you blocked, but every server there is the same, staying put", 7);
			}
			else
			{
				App.Logger.WriteLine(LOG_IDENT, $"Current {currentDc.City} at {currentPing}ms vs best {best.DatacenterName} at {best.EstimatedPingMs}ms, not worth reconnecting");
				ShowAlert($"You are already on a good server, about {currentPing}ms", 5);
			}
			return;
		}

		int maxRetries = Math.Max(1, App.Settings.Prop.ServerMatchmakerMaxRetries);
		int attempt = IncrementAndGetAttempt(data.PlaceId);
		if (attempt > maxRetries)
		{
			App.Logger.WriteLine(LOG_IDENT, $"Reached the retry limit of {maxRetries} for place {data.PlaceId}");
			ClearAttempt(data.PlaceId);
			ShowAlert($"Could not reach a better datacenter after {maxRetries} tries, staying in {currentDc.City}", 10);
			return;
		}

		RecordTriedJobId(data.PlaceId, data.JobId);
		RecordTriedJobId(data.PlaceId, best.JobId);
		App.Logger.WriteLine(LOG_IDENT, $"Rerouting from {currentDc.City} ({currentPing}ms) to {best.DatacenterName} ({best.EstimatedPingMs}ms), {reason}");

		string blockedNote = string.IsNullOrEmpty(best.BlockedClosestCity)
			? ""
			: $"\n{best.BlockedClosestCity} is closer but you blocked it";
		string attemptNote = attempt > 1 ? $" Attempt {attempt} of {maxRetries}" : "";
		ShowAlert($"Moving you to {best.Datacenter?.City ?? "the best server"}, about {best.EstimatedPingMs}ms{attemptNote}{blockedNote}", 8);

		_lastHopUtc = DateTime.UtcNow;
		await TriggerRejoinAsync(data.PlaceId, attempt, token, best.JobId, best.Datacenter?.City).ConfigureAwait(false);
	}

	private async Task TriggerRejoinAsync(long placeId, int attemptNumber, CancellationToken token, string? explicitJobId, string? targetName)
	{
		if (_disposed || token.IsCancellationRequested)
			return;

		string? authUri = null;
		try
		{
			authUri = await RobloxAuthLauncher.BuildRobloxPlayerUriAsync(placeId, explicitJobId, token).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Auth URI build failed: " + ex.Message);
		}
		if (_disposed || token.IsCancellationRequested)
			return;

		string launchUri = !string.IsNullOrEmpty(authUri)
			? authUri
			: string.IsNullOrEmpty(explicitJobId)
				? $"roblox://experiences/start?placeId={placeId}"
				: $"roblox://experiences/start?placeId={placeId}&gameInstanceId={Uri.EscapeDataString(explicitJobId)}";

		HashSet<int> existingPids = SnapshotRobloxPids();
		bool spawnedSuccessor = false;
		try
		{
			string process = Paths.Process;
			if (!string.IsNullOrEmpty(process))
			{
				ProcessStartInfo startInfo = new ProcessStartInfo
				{
					FileName = process,
					UseShellExecute = false,
					CreateNoWindow = true,
					WorkingDirectory = Path.GetDirectoryName(process) ?? ""
				};
				startInfo.ArgumentList.Add("-player");
				startInfo.ArgumentList.Add(launchUri);
				startInfo.ArgumentList.Add("-matchmakerrejoin");
				startInfo.ArgumentList.Add("-matchmakerattempt");
				startInfo.ArgumentList.Add(attemptNumber.ToString());
				if (!string.IsNullOrWhiteSpace(targetName))
				{
					string clean = targetName.Replace("\n", " ").Replace("\r", " ");
					if (clean.Length > 60)
						clean = clean.Substring(0, 60);
					startInfo.ArgumentList.Add("-matchmakertarget");
					startInfo.ArgumentList.Add(clean);
				}
				using Process? successor = Process.Start(startInfo);
				spawnedSuccessor = successor != null;
				if (spawnedSuccessor)
					App.Logger.WriteLine(LOG_IDENT, "Spawned successor Voidstrap");
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Could not spawn successor Voidstrap: " + ex.Message);
		}

		if (!spawnedSuccessor)
		{
			try
			{
				string playerPath = new RobloxPlayerData().ExecutablePath;
				ProcessStartInfo startInfo = new ProcessStartInfo
				{
					FileName = playerPath,
					UseShellExecute = false,
					WorkingDirectory = Path.GetDirectoryName(playerPath) ?? ""
				};
				startInfo.ArgumentList.Add(launchUri);
				using Process? player = Process.Start(startInfo);
				if (player == null)
					throw new InvalidOperationException("RobloxPlayerBeta did not start");
				App.Logger.WriteLine(LOG_IDENT, "Spawned RobloxPlayerBeta directly");
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LOG_IDENT, "Fallback launch failed: " + ex.Message);
				App.Logger.WriteException(LOG_IDENT, ex);
				return;
			}
		}

		if (!await WaitForNewRobloxAsync(existingPids, token).ConfigureAwait(false))
		{
			App.Logger.WriteLine(LOG_IDENT, $"The successor did not start Roblox within {HandoffPollCount * HandoffPollIntervalMs / 1000}s, keeping the current session");
			return;
		}

		try
		{
			_watcher.KillRobloxProcess();
			App.Logger.WriteLine(LOG_IDENT, "Closed the old Roblox, the successor takes over");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Closing the old Roblox failed: " + ex.Message);
		}
	}

	private static HashSet<int> SnapshotRobloxPids()
	{
		HashSet<int> pids = new HashSet<int>();
		try
		{
			foreach (Process p in Process.GetProcessesByName("RobloxPlayerBeta"))
			{
				try
				{
					pids.Add(p.Id);
				}
				finally
				{
					p.Dispose();
				}
			}
		}
		catch
		{
		}
		return pids;
	}

	private async Task<bool> WaitForNewRobloxAsync(HashSet<int> existingPids, CancellationToken token)
	{
		for (int i = 0; i < HandoffPollCount; i++)
		{
			try
			{
				await Task.Delay(HandoffPollIntervalMs, token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return false;
			}
			if (_disposed || token.IsCancellationRequested)
				return false;
			try
			{
				bool oldStillRunning = false;
				foreach (Process p in Process.GetProcessesByName("RobloxPlayerBeta"))
				{
					try
					{
						if (p.HasExited)
							continue;
						if (!existingPids.Contains(p.Id))
							return true;
						oldStillRunning = true;
					}
					finally
					{
						p.Dispose();
					}
				}
				if (!oldStillRunning && i > 0)
				{
					App.Logger.WriteLine(LOG_IDENT, "The old Roblox already closed, the successor has taken over");
					return false;
				}
			}
			catch
			{
			}
		}
		return false;
	}

	private static HashSet<string> GetTriedJobIds(long placeId)
	{
		try
		{
			Dictionary<long, MatchmakerAttempt>? attempts = App.State.Prop.MatchmakerAttempts;
			if (attempts != null && attempts.TryGetValue(placeId, out MatchmakerAttempt? entry) && DateTime.UtcNow - entry.LastUtc <= AttemptResetWindow)
				return new HashSet<string>(entry.TriedJobIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
		}
		catch
		{
		}
		return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	}

	private static void RecordTriedJobId(long placeId, string? jobId)
	{
		if (string.IsNullOrWhiteSpace(jobId))
			return;
		try
		{
			State state = App.State.Prop;
			Dictionary<long, MatchmakerAttempt> attempts = state.MatchmakerAttempts ??= new Dictionary<long, MatchmakerAttempt>();
			if (!attempts.TryGetValue(placeId, out MatchmakerAttempt? entry))
				entry = attempts[placeId] = new MatchmakerAttempt { Count = 0, LastUtc = DateTime.UtcNow };
			entry.TriedJobIds ??= new List<string>();
			if (!entry.TriedJobIds.Contains(jobId, StringComparer.OrdinalIgnoreCase))
			{
				entry.TriedJobIds.Add(jobId);
				if (entry.TriedJobIds.Count > 50)
					entry.TriedJobIds.RemoveRange(0, entry.TriedJobIds.Count - 50);
			}
			App.State.Save();
		}
		catch
		{
		}
	}

	private static int IncrementAndGetAttempt(long placeId)
	{
		try
		{
			State state = App.State.Prop;
			Dictionary<long, MatchmakerAttempt> attempts = state.MatchmakerAttempts ??= new Dictionary<long, MatchmakerAttempt>();
			PruneOldAttempts(attempts);
			if (!attempts.TryGetValue(placeId, out MatchmakerAttempt? entry))
				entry = attempts[placeId] = new MatchmakerAttempt { Count = 0, LastUtc = DateTime.UtcNow };
			else if (DateTime.UtcNow - entry.LastUtc > AttemptResetWindow)
				entry.Count = 0;
			entry.Count++;
			entry.LastUtc = DateTime.UtcNow;
			App.State.Save();
			return entry.Count;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Attempt counter failed: " + ex.Message);
			return 1;
		}
	}

	private static void ClearAttempt(long placeId)
	{
		try
		{
			Dictionary<long, MatchmakerAttempt>? attempts = App.State.Prop.MatchmakerAttempts;
			if (attempts != null && attempts.Remove(placeId))
				App.State.Save();
		}
		catch
		{
		}
	}

	private static void PruneOldAttempts(Dictionary<long, MatchmakerAttempt> history)
	{
		DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(1.0);
		foreach (long key in history.Where(kv => kv.Value.LastUtc < cutoff).Select(kv => kv.Key).ToList())
			history.Remove(key);
	}

	private void ShowAlert(string text, int durationSeconds)
	{
		try
		{
			NotifyIconWrapper? icon = NotifyIconResolver?.Invoke();
			if (icon != null && icon.EnableAppNotifications)
				icon.ShowAlert("Voidstrap Matchmaker", text, durationSeconds, null);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Notification failed: " + ex.Message);
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		try
		{
			_activityWatcher.OnGameJoin -= OnGameJoin;
		}
		catch
		{
		}
		try
		{
			CancellationTokenSource? current = Interlocked.Exchange(ref _currentCts, null);
			current?.Cancel();
		}
		catch
		{
		}
		GC.SuppressFinalize(this);
	}
}
