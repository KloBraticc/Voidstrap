using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Enums;
using Voidstrap.Models.APIs;
using Voidstrap.Models.Entities;
using Voidstrap.Models.Persistable;
using Voidstrap.Models.VoidstrapRPC;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

public class ActivityWatcher : IDisposable
{
	private const string GameMessageEntry = "[FLog::Output] [VoidstrapRPC]";

	private const string GameJoiningEntry = "[FLog::Output] ! Joining game";

	private const string GameTeleportEntry = "[FLog::UgcExperienceController] UgcExperienceController: doTeleport: joinScriptUrl";

	private const string GameJoiningUniverseEntry = "[FLog::GameJoinLoadTime] Report game_join_loadtime:";

	private const string GameJoiningUDMUXEntry = "[FLog::Network] UDMUX Address = ";

	private const string GameJoinedEntry = "[FLog::Network] serverId:";

	private const string GameDisconnectedEntry = "[FLog::Network] Time to disconnect replication data:";

	private const string GameLeavingEntry = "[FLog::SingleSurfaceApp] leaveUGCGameInternal";

	private const string GamePlayerJoinLeaveEntry = "[ExpChat/mountClientApp (Trace)] - Player ";

	private const string GameMessageLogEntry = "[ExpChat/mountClientApp (Debug)] - Incoming MessageReceived Status: ";
	private const string GamePlayerDiscoveryEntry = "[DFLog::SocialCounterpartyManager]";

	private const string GameJoiningEntryPattern = "! Joining game '([0-9a-f\\-]{36})' place ([0-9]+) at ([0-9\\.]+)";

	private const string GameJoinReferralPattern = "referral_page:([^,]+)";

	private const string GameTeleportJoinTypePattern = "JoinTypeId(?:\"|%22)?(?::|%3a)(\\d+)";

	private const string GameJoiningUniversePattern = "universeid:([0-9]+).*userid:([0-9]+)";

	private const string GameJoiningUDMUXPattern = "UDMUX Address = ([0-9\\.]+), Port = [0-9]+ \\| RCC Server Address = ([0-9\\.]+), Port = [0-9]+";

	private const string GameJoinedEntryPattern = "serverId:\\s*([0-9a-f\\-]{36})";

	private const string GameMessageEntryPattern = "\\[VoidstrapRPC\\] (.*)";

	private const string GamePlayerJoinLeavePattern = "(added|removed): (.*) ([0-9]+)\\s*$";

	private const string GameMessageLogPattern = "Success Text: (.*)";
	private static readonly Regex PlayerAddedRegex = new Regex("playerAdded:\\s*userId=(?<id>[0-9]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex PlayerRemovedRegex = new Regex("(?:playerRemoving:\\s*userId=|Purging (?:social counterparties|age group|compatibility tokens) for player\\s+)(?<id>[0-9]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
	private static readonly Regex LogPattern1 = new Regex("! Joining game '([0-9a-f\\-]{36})' place ([0-9]+) at ([0-9\\.]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex LogPattern2 = new Regex("referral_page:([^,]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex LogPattern3 = new Regex("universeid:([0-9]+).*userid:([0-9]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex LogPattern4 = new Regex("UDMUX Address = ([0-9\\.]+), Port = [0-9]+ \\| RCC Server Address = ([0-9\\.]+), Port = [0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex LogPattern5 = new Regex("JoinTypeId(?:\"|%22)?(?::|%3a)(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex LogPattern6 = new Regex("\\[VoidstrapRPC\\] (.*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex LogPattern7 = new Regex("(added|removed): (.*) ([0-9]+)\\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex LogPattern8 = new Regex("Success Text: (.*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly string? LaunchStatusFile = Environment.GetEnvironmentVariable("VOIDSTRAP_STATUS_FILE");

	private bool _teleportMarker;

	private bool _reservedTeleportMarker;

	private static AppSettings.ResolutionSetting? _originalResolution;

	private static bool _resolutionApplied = false;

	private DateTime _lastRejoinAttempt = DateTime.MinValue;

	private DateTime LastRPCRequest;

	public string LogLocation;

	public bool InGame;

	public bool IsTeleporting => _teleportMarker;

	public List<ActivityData> History = new List<ActivityData>();

	public bool IsDisposed;

	private const int MaxHistoryEntries = 64;

	internal const int MaxPlayerLogEntries = 2048;

	internal const int MaxMessageLogEntries = 1000;

	private int _nextPlayerLogId;

	private int _nextMessageLogId;
	private int _playerSessionGeneration;
	private readonly object _playerStateLock = new object();
	private readonly HashSet<long> _activePlayerIds = new HashSet<long>();
	private readonly Dictionary<long, string> _playerNameCache = new Dictionary<long, string>();
	private readonly SemaphoreSlim _playerNameSemaphore = new SemaphoreSlim(4, 4);
	private readonly CancellationTokenSource _playerLifetimeCts = new CancellationTokenSource();
	private int _pendingPlayerLookups;
	private readonly SemaphoreSlim _logSignal = new SemaphoreSlim(0, 1);
	private FileSystemWatcher? _logWatcher;

	public static bool PlayerLoggingEnabled => App.FastFlags.GetPreset("Players.EventLog") == "7" || App.FastFlags.GetPreset("Players.LogLevel") == "trace" && App.FastFlags.GetPreset("Players.LogPattern") == "ExpChat/mountClientApp";

	public ActivityData Data { get; private set; } = new ActivityData();

	public Dictionary<int, ActivityData.UserLog> PlayerLogs => Data.PlayerLogs;

	public Dictionary<int, ActivityData.UserMessage> MessageLogs => Data.MessageLogs;

	public IReadOnlyList<ActivityData.UserLog> GetPlayerLogSnapshot()
	{
		Dictionary<int, ActivityData.UserLog> logs = Data.PlayerLogs;
		lock (logs)
			return logs.OrderBy(item => item.Key).Select(item => item.Value).ToArray();
	}

	public event EventHandler? OnGameJoin;

	public event EventHandler? OnGameLeave;

	public event EventHandler? OnLogOpen;

	public event EventHandler? OnAppClose;

	public event EventHandler<ActivityData.UserLog>? OnNewPlayerRequest;
	public event EventHandler<ActivityData.UserLog>? OnPlayerLogUpdated;

	public event EventHandler<ActivityData.UserMessage>? OnNewMessageRequest;

	public event EventHandler<Message>? OnRPCMessage;

	public ActivityWatcher(string? logFile = null)
	{
		if (!string.IsNullOrEmpty(logFile))
		{
			LogLocation = logFile;
		}
	}

	private void RaiseEvent(EventHandler? handlers, string name)
	{
		if (handlers == null)
			return;
		foreach (EventHandler handler in handlers.GetInvocationList())
		{
			try
			{
				handler(this, EventArgs.Empty);
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("ActivityWatcher::" + name, ex);
			}
		}
	}

	private void RaiseEvent<T>(EventHandler<T>? handlers, T args, string name)
	{
		if (handlers == null)
			return;
		foreach (EventHandler<T> handler in handlers.GetInvocationList())
		{
			try
			{
				handler(this, args);
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("ActivityWatcher::" + name, ex);
			}
		}
	}

	private static void PublishLaunchStatus(string message)
	{
		if (string.IsNullOrEmpty(LaunchStatusFile))
		{
			return;
		}
		try
		{
			File.WriteAllText(LaunchStatusFile, message);
		}
		catch
		{
		}
	}

	public async Task<string?> GetGameIconAsync()
	{
		try
		{
			if (Data.UniverseId == 0L)
			{
				return null;
			}
			string requestUri = $"https://thumbnails.roblox.com/v1/games/icons?universeIds={Data.UniverseId}&size=150x150&format=Png&isCircular=true";
			using JsonDocument jsonDocument = JsonDocument.Parse(await Voidstrap.Utility.Http.GetString(requestUri).ConfigureAwait(false));
			return jsonDocument.RootElement.GetProperty("data")[0].GetProperty("imageUrl").GetString();
		}
		catch
		{
			return null;
		}
	}

	public async Task<int> GetPlayerCount()
	{
		return (await GetServerPlayerCountAsync().ConfigureAwait(continueOnCapturedContext: false)).Item1;
	}

	public int GetPlayerCountFromLogs()
	{
		return CountPlayersFromLogs();
	}

	public async Task<int> GetMaxPlayers()
	{
		UniverseDetails details = Data.UniverseDetails;
		if ((details?.Data?.MaxPlayers).GetValueOrDefault() <= 0 && Data.UniverseId > 0)
		{
			try
			{
				await UniverseDetails.FetchSingle(Data.UniverseId).ConfigureAwait(continueOnCapturedContext: false);
				details = UniverseDetails.LoadFromCache(Data.UniverseId);
				if (details != null)
				{
					Data.UniverseDetails = details;
				}
			}
			catch
			{
			}
		}
		return (details?.Data?.MaxPlayers).GetValueOrDefault();
	}

	public async Task<(int Current, int Max)> GetServerPlayerCountAsync()
	{
		(int, int, int, bool) tuple = await GetServerPlayerStatsAsync().ConfigureAwait(continueOnCapturedContext: false);
		return (Current: tuple.Item1, Max: tuple.Item2);
	}

	public async Task<(int Current, int Max, int GameTotal, bool ServerFound)> GetServerPlayerStatsAsync()
	{
		int max = await GetMaxPlayers().ConfigureAwait(continueOnCapturedContext: false);
		int logCount = CountPlayersFromLogs();
		int gameTotal = await GetGameTotalPlayingAsync().ConfigureAwait(continueOnCapturedContext: false);
		int apiCurrent = 0;
		bool serverFound = false;
		try
		{
			ServerInfo serverInfo = await GetCurrentServerInfoAsync().ConfigureAwait(continueOnCapturedContext: false);
			if (serverInfo != null)
			{
				apiCurrent = serverInfo.Playing;
				serverFound = true;
				if (serverInfo.MaxPlayers > 0)
				{
					max = serverInfo.MaxPlayers;
				}
			}
		}
		catch
		{
		}
		int num = (serverFound ? apiCurrent : logCount);
		if (InGame && num < 1)
		{
			num = 1;
		}
		if (max > 0 && num > max)
		{
			num = max;
		}
		return (Current: num, Max: max, GameTotal: gameTotal, ServerFound: serverFound);
	}

	public async Task<int> GetGameTotalPlayingAsync()
	{
		long universeId = Data.UniverseId;
		if (universeId <= 0)
		{
			return 0;
		}
		try
		{
			using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(6L));
			string requestUri = $"https://games.roblox.com/v1/games?universeIds={universeId}";
			using JsonDocument jsonDocument = JsonDocument.Parse(await Voidstrap.Utility.Http.GetString(requestUri, cts.Token).ConfigureAwait(continueOnCapturedContext: false));
			if (jsonDocument.RootElement.TryGetProperty("data", out var value) && value.GetArrayLength() > 0 && value[0].TryGetProperty("playing", out var value2) && value2.TryGetInt64(out var value3))
			{
				return (int)Math.Max(0L, value3);
			}
		}
		catch
		{
		}
		return 0;
	}

	public async Task<ServerInfo?> GetCurrentServerInfoAsync()
	{
		long placeId = Data.PlaceId;
		string jobId = Data.JobId;
		string machineAddress = Data.MachineAddress;
		bool machineValid = Data.MachineAddressValid;
		if (placeId == 0L || string.IsNullOrEmpty(jobId) || Data.ServerType != ServerType.Public)
		{
			return null;
		}
		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8L));
		string text = null;
		for (int pages = 0; pages < 6; pages++)
		{
			if (cts.IsCancellationRequested)
			{
				break;
			}
			string text2 = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?limit=100&sortOrder=Asc";
			if (!string.IsNullOrEmpty(text))
			{
				text2 = text2 + "&cursor=" + Uri.EscapeDataString(text);
			}
			ServerListResponse serverListResponse;
			try
			{
				serverListResponse = JsonSerializer.Deserialize<ServerListResponse>(await Voidstrap.Utility.Http.GetString(text2, cts.Token).ConfigureAwait(continueOnCapturedContext: false), JsonOptions.CaseInsensitive);
			}
			catch
			{
				break;
			}
			if (serverListResponse?.Data == null || serverListResponse.Data.Count == 0)
			{
				break;
			}
			ServerInfo serverInfo = serverListResponse.Data.FirstOrDefault((ServerInfo s) => s.Id == jobId);
			if (serverInfo != null)
			{
				if (serverInfo.Ping > 0 && machineValid)
				{
					ServerFetchStore.RecordPing(machineAddress, serverInfo.Ping);
				}
				return serverInfo;
			}
			if (string.IsNullOrEmpty(serverListResponse.NextPageCursor))
			{
				break;
			}
			text = serverListResponse.NextPageCursor;
		}
		return null;
	}

	private int CountPlayersFromLogs()
	{
		lock (_playerStateLock)
			return _activePlayerIds.Count;
	}

	public void Start()
	{
		if (!PlayerLoggingEnabled)
			App.Logger.WriteLine("ActivityWatcher", "Player join/leave tracking is off. Roblox will not write those lines to its log. Enable it under Integrations to turn on the required FastFlag preset.");

		_ = StartAsync();
	}

	private async Task StartAsync()
	{
		try
		{
			await RunAsync();
		}
		catch (OperationCanceledException) when (_playerLifetimeCts.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ActivityWatcher::Start", ex);
		}
	}

	private static string[] GetClientLogDirectories()
	{
		List<string> candidates = [];
		if (OperatingSystem.IsLinux())
		{
			string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string soberData = Path.Combine(home, ".var", "app", "org.vinegarhq.Sober", "data", "sober");
			candidates.Add(Path.Combine(soberData, "appData", "logs"));
			candidates.Add(Path.Combine(soberData, "sober_logs"));
		}
		else
		{
			candidates.Add(Path.Combine(Paths.LocalAppData, "Roblox", "logs"));
		}

		return [.. candidates.Where(Directory.Exists)];
	}

	private static bool IsClientLogFile(FileInfo file)
	{
		return OperatingSystem.IsLinux()
			? file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) || file.Name.Contains("Player", StringComparison.OrdinalIgnoreCase)
			: file.Name.Contains("Player", StringComparison.OrdinalIgnoreCase);
	}

	private async Task RunAsync()
	{
		CancellationToken token = _playerLifetimeCts.Token;
		FileInfo logFileInfo = null;
		if (string.IsNullOrEmpty(LogLocation))
		{
			string[] logDirectories = GetClientLogDirectories();
			if (logDirectories.Length == 0)
			{
				return;
			}
			App.Logger.WriteLine("ActivityWatcher::Start", "Opening Roblox log file...");
			while (!IsDisposed && !token.IsCancellationRequested)
			{
				FileInfo fileInfo = (from x in logDirectories.SelectMany(static directory => new DirectoryInfo(directory).GetFiles())
					where IsClientLogFile(x) && x.CreationTime <= DateTime.Now
					orderby x.CreationTime descending
					select x).FirstOrDefault();
				if (fileInfo == null)
				{
					if (!await WaitAsync(1500, token))
						return;
					continue;
				}
				logFileInfo = fileInfo;
				if (logFileInfo.CreationTime.AddSeconds(15.0) > DateTime.Now)
				{
					LogLocation = logFileInfo.FullName;
					break;
				}
				App.Logger.WriteLine("ActivityWatcher::Start", "Could not find recent enough log file, waiting... (newest is " + logFileInfo.Name + ")");
				if (!await WaitAsync(1500, token))
					return;
			}
			if (IsDisposed || logFileInfo == null)
			{
				return;
			}
		}
		else
		{
			logFileInfo = new FileInfo(LogLocation);
		}
		FileStream fileStream;
		try
		{
			fileStream = logFileInfo.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ActivityWatcher::Start", "Failed to open log: " + ex.Message);
			return;
		}
		App.Logger.WriteLine("ActivityWatcher::Start", "Opened " + LogLocation);
		StartLogWatcher(logFileInfo);
		RaiseEvent(OnLogOpen, "OnLogOpen");
		using (fileStream)
		{
			using StreamReader streamReader = new StreamReader(fileStream);
			while (!IsDisposed && !token.IsCancellationRequested)
			{
				string text;
				try
				{
					text = await streamReader.ReadLineAsync(token);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex2)
				{
					App.Logger.WriteLine("ActivityWatcher::Start", "Read failed: " + ex2.Message);
					break;
				}
				if (text == null)
				{
					try
					{
						await _logSignal.WaitAsync(TimeSpan.FromSeconds(5), token);
					}
					catch (OperationCanceledException) when (token.IsCancellationRequested)
					{
						return;
					}
				}
				else
				{
					try
					{
						ReadLogEntry(text);
					}
					catch (Exception ex)
					{
						App.Logger.WriteException("ActivityWatcher::ReadLogEntry", ex);
					}
				}
			}
		}
	}

	private void StartLogWatcher(FileInfo logFileInfo)
	{
		try
		{
			_logWatcher = new FileSystemWatcher(logFileInfo.DirectoryName!, logFileInfo.Name)
			{
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
			};
			_logWatcher.Changed += OnLogChanged;
			_logWatcher.EnableRaisingEvents = true;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ActivityWatcher::Start", "Log change notifications unavailable: " + ex.Message);
		}
	}

	private void OnLogChanged(object sender, FileSystemEventArgs e)
	{
		if (IsDisposed || _logSignal.CurrentCount != 0)
			return;
		try
		{
			_logSignal.Release();
		}
		catch (SemaphoreFullException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private static async Task<bool> WaitAsync(int milliseconds, CancellationToken token)
	{
		try
		{
			await Task.Delay(milliseconds, token);
			return true;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			return false;
		}
	}

	private void ReadLogEntry(string entry)
	{
		if (entry.Contains("[FLog::SingleSurfaceApp] leaveUGCGameInternal"))
		{
			App.Logger.WriteLine("ActivityWatcher::ReadLogEntry", "User is back into the desktop app");
			RestoreOriginalResolution();
			RaiseEvent(OnAppClose, "OnAppClose");
			if (Data.PlaceId != 0L && !InGame)
			{
				App.Logger.WriteLine("ActivityWatcher::ReadLogEntry", "User appears to be leaving from a cancelled/errored join");
				ResetData();
				FrameGeneration.FrameGenManager.OnGameLeave();
			}
		}
		if (!InGame && Data.PlaceId == 0L)
		{
			if (!entry.Contains("[FLog::Output] ! Joining game"))
			{
				return;
			}
			Match match = LogPattern1.Match(entry);
			if (match.Groups.Count == 4)
			{
				InGame = false;
				Data.PlaceId = long.Parse(match.Groups[2].Value);
				AssetProxy.AssetPreloadCache.SwitchSession(Data.PlaceId);
				Data.JobId = match.Groups[1].Value;
				string joinAddress = match.Groups[3].Value;
				Data.MachineAddress = VoidstrapMatchmaker.IsPrivateIp(joinAddress) ? string.Empty : joinAddress;
				if (_teleportMarker)
				{
					Data.IsTeleport = true;
					_teleportMarker = false;
				}
				if (_reservedTeleportMarker)
				{
					Data.ServerType = ServerType.Reserved;
					_reservedTeleportMarker = false;
				}
				string message = "Joining Game (" + Data.JobId + ")";
				App.Logger.WriteLine("ActivityWatcher::ReadLogEntry", message);
				PublishLaunchStatus(message);
				FrameGeneration.FrameGenManager.OnGameJoinStarting();
			}
		}
		else if (!InGame && Data.PlaceId != 0L)
		{
			if (entry.Contains("[FLog::GameJoinLoadTime] Report game_join_loadtime:"))
			{
				Match match2 = LogPattern2.Match(entry);
				if (match2.Groups.Count == 2)
				{
					string value = match2.Groups[1].Value;
					if (Data.ServerType != ServerType.Reserved && (value.Contains("RequestPrivateGame", StringComparison.OrdinalIgnoreCase) || value.Contains("GameDetailPageJSHybridEvent", StringComparison.OrdinalIgnoreCase)))
					{
						Data.ServerType = ServerType.Private;
					}
				}
				Match match3 = LogPattern3.Match(entry);
				if (match3.Groups.Count != 3)
				{
					return;
				}
				Data.UniverseId = long.Parse(match3.Groups[1].Value);
				Data.UserId = long.Parse(match3.Groups[2].Value);
				lock (History)
				{
					if (History.Count > 0)
					{
						ActivityData activityData = History[0];
						if (Data.UniverseId == activityData.UniverseId && Data.IsTeleport)
						{
							Data.RootActivity = activityData.RootActivity ?? activityData;
						}
					}
				}
			}
			else if (entry.Contains("[FLog::Network] UDMUX Address = "))
			{
				Match match4 = LogPattern4.Match(entry);
				if (match4.Groups.Count == 3)
				{
					string udmuxAddress = match4.Groups[1].Value;
					string rccAddress = match4.Groups[2].Value;
					string routable = !VoidstrapMatchmaker.IsPrivateIp(udmuxAddress)
						? udmuxAddress
						: (!VoidstrapMatchmaker.IsPrivateIp(rccAddress) ? rccAddress : string.Empty);
					if (routable.Length != 0)
						Data.MachineAddress = routable;
					else if (!Data.MachineAddressValid)
						Data.MachineAddress = string.Empty;
					App.Logger.WriteLine("ActivityWatcher::ReadLogEntry", $"Server uses UDMUX, public address {udmuxAddress}, RCC address {rccAddress}");
					PublishLaunchStatus("Joining Server (" + (Data.MachineAddressValid ? Data.MachineAddress : udmuxAddress) + ")");
				}
			}
			else if (entry.Contains("[FLog::Network] serverId:"))
			{
				string message2 = "Confirmed game join (JobId = " + Data.JobId + ")";
				App.Logger.WriteLine("ActivityWatcher::ReadLogEntry", message2);
				PublishLaunchStatus(message2);
				InGame = true;
				Data.TimeJoined = DateTime.Now;
				ApplyInGameResolutionIfNeeded();
				FrameGeneration.FrameGenManager.OnGameJoinConfirmed();
				RecordPlayerEvent("added", Data.UserId, null);
				RaiseEvent(OnGameJoin, "OnGameJoin");
			}
		}
		else
		{
			if (!InGame || Data.PlaceId == 0L)
			{
				return;
			}
			if (entry.Contains("[FLog::Network] Time to disconnect replication data:"))
			{
				App.Logger.WriteLine("ActivityWatcher::ReadLogEntry", "Disconnected from Game (" + Data.JobId + ")");
				RestoreOriginalResolution();
				Data.TimeLeft = DateTime.Now;
				lock (History)
				{
					History.Insert(0, Data);
					while (History.Count > MaxHistoryEntries)
					{
						History.RemoveAt(History.Count - 1);
					}
				}
				InGame = false;
				_ = Data;
				ResetData();
				if (!_teleportMarker)
					FrameGeneration.FrameGenManager.OnGameLeave();
				RaiseEvent(OnGameLeave, "OnGameLeave");
			}
			else if (entry.Contains("[FLog::UgcExperienceController] UgcExperienceController: doTeleport: joinScriptUrl"))
			{
				_teleportMarker = true;
				App.Logger.WriteLine("ActivityWatcher::ReadLogEntry", "Initiating teleport (" + Data.JobId + ")");
				FrameGeneration.FrameGenManager.OnGameJoinStarting();
				Match match5 = LogPattern5.Match(entry);
				if (match5.Success && int.TryParse(match5.Groups[1].Value, out var result))
				{
					ServerSessionJoinType serverSessionJoinType = (ServerSessionJoinType)result;
					App.Logger.WriteLine("ActivityWatcher::ReadLogEntry", $"Teleport JoinTypeId = {result} ({serverSessionJoinType})");
					if ((serverSessionJoinType == ServerSessionJoinType.NewGamePrivateGame || serverSessionJoinType == ServerSessionJoinType.SpecificPrivateGame) ? true : false)
					{
						_reservedTeleportMarker = true;
					}
				}
			}
			else if (entry.Contains("[FLog::Output] [VoidstrapRPC]"))
			{
				Match match6 = LogPattern6.Match(entry);
				if (match6.Groups.Count != 2)
				{
					return;
				}
				string value2 = match6.Groups[1].Value;
				if ((DateTime.Now - LastRPCRequest).TotalSeconds <= 1.0)
				{
					return;
				}
				Message message3;
				try
				{
					message3 = JsonSerializer.Deserialize<Message>(value2);
				}
				catch
				{
					return;
				}
				if (message3 == null)
				{
					return;
				}
				if (message3.Command == "SetLaunchData")
				{
					string text = message3.Data.Deserialize<string>();
					if (text != null && text.Length <= 200)
					{
						Data.RPCLaunchData = text;
					}
				}
				RaiseEvent(OnRPCMessage, message3, "OnRPCMessage");
				LastRPCRequest = DateTime.Now;
			}
			else if (entry.Contains(GamePlayerDiscoveryEntry, StringComparison.Ordinal))
			{
				Match added = PlayerAddedRegex.Match(entry);
				if (added.Success && long.TryParse(added.Groups["id"].Value, out long addedId))
				{
					RecordPlayerEvent("added", addedId, null);
					return;
				}
				Match removed = PlayerRemovedRegex.Match(entry);
				if (removed.Success && long.TryParse(removed.Groups["id"].Value, out long removedId))
					RecordPlayerEvent("removed", removedId, null);
			}
			else if (entry.Contains("[ExpChat/mountClientApp (Trace)] - Player "))
			{
				Match match7 = LogPattern7.Match(entry);
				if (match7.Groups.Count == 4)
				{
					if (long.TryParse(match7.Groups[3].Value, out long userId))
						RecordPlayerEvent(match7.Groups[1].Value, userId, match7.Groups[2].Value);
				}
			}
			else if (entry.Contains("[ExpChat/mountClientApp (Debug)] - Incoming MessageReceived Status: "))
			{
				Match match8 = LogPattern8.Match(entry);
				if (match8.Groups.Count == 2)
				{
					ActivityData.UserMessage userMessage = new ActivityData.UserMessage
					{
						Message = match8.Groups[1].Value,
						Time = DateTime.Now
					};
					AddBounded(Data.MessageLogs, _nextMessageLogId++, userMessage, MaxMessageLogEntries);
					RaiseEvent(OnNewMessageRequest, userMessage, "OnNewMessageRequest");
				}
			}
		}
	}

	private void RecordPlayerEvent(string type, long userId, string? username)
	{
		if (userId <= 0 || !InGame)
			return;
		bool added = string.Equals(type, "added", StringComparison.OrdinalIgnoreCase);
		lock (_playerStateLock)
		{
			if (added)
			{
				if (!_activePlayerIds.Add(userId))
					return;
			}
			else if (!_activePlayerIds.Remove(userId))
			{
				return;
			}
			if (string.IsNullOrWhiteSpace(username) && _playerNameCache.TryGetValue(userId, out string? cached))
				username = cached;
			else if (!string.IsNullOrWhiteSpace(username))
				CachePlayerName(userId, username);
		}
		ActivityData.UserLog userLog = new ActivityData.UserLog
		{
			Type = added ? "Joined" : "Left",
			Username = string.IsNullOrWhiteSpace(username) ? "User " + userId : username,
			UserId = userId.ToString(),
			Time = DateTime.Now
		};
		AddBounded(Data.PlayerLogs, _nextPlayerLogId++, userLog, MaxPlayerLogEntries);
		RaiseEvent(OnNewPlayerRequest, userLog, "OnNewPlayerRequest");
		if (string.IsNullOrWhiteSpace(username) && Interlocked.Increment(ref _pendingPlayerLookups) <= 128)
			_ = ResolvePlayerNameAsync(userId, userLog, _playerSessionGeneration);
		else if (string.IsNullOrWhiteSpace(username))
			Interlocked.Decrement(ref _pendingPlayerLookups);
	}

	private void CachePlayerName(long userId, string username)
	{
		_playerNameCache[userId] = username;
		while (_playerNameCache.Count > 512)
		{
			long oldest = _playerNameCache.Keys.First();
			_playerNameCache.Remove(oldest);
		}
	}

	private async Task ResolvePlayerNameAsync(long userId, ActivityData.UserLog userLog, int generation)
	{
		try
		{
			await _playerNameSemaphore.WaitAsync(_playerLifetimeCts.Token).ConfigureAwait(false);
			try
			{
				string? cached;
				lock (_playerStateLock)
					_playerNameCache.TryGetValue(userId, out cached);
				if (!string.IsNullOrWhiteSpace(cached))
				{
					userLog.Username = cached;
					RaiseEvent(OnPlayerLogUpdated, userLog, "OnPlayerLogUpdated");
					return;
				}
				using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_playerLifetimeCts.Token);
				timeout.CancelAfter(TimeSpan.FromSeconds(8));
				using HttpResponseMessage response = await App.HttpClient.GetAsync("https://users.roblox.com/v1/users/" + userId, timeout.Token).ConfigureAwait(false);
				if (!response.IsSuccessStatusCode)
					return;
				using JsonDocument document = JsonDocument.Parse(await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 1048576, timeout.Token).ConfigureAwait(false));
				if (!document.RootElement.TryGetProperty("name", out JsonElement nameElement))
					return;
				string? name = nameElement.GetString();
				if (string.IsNullOrWhiteSpace(name))
					return;
				lock (_playerStateLock)
					CachePlayerName(userId, name);
				if (generation != _playerSessionGeneration || IsDisposed)
					return;
				userLog.Username = name;
				RaiseEvent(OnPlayerLogUpdated, userLog, "OnPlayerLogUpdated");
			}
			finally
			{
				_playerNameSemaphore.Release();
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ActivityWatcher::ResolvePlayerName", ex);
		}
		finally
		{
			Interlocked.Decrement(ref _pendingPlayerLookups);
		}
	}

	private void RestoreOriginalResolution()
	{
		if (_resolutionApplied && _originalResolution != null)
		{
			App.Logger.WriteLine("ActivityWatcher", "Restoring original desktop resolution");
			InGameResolutionApplier.Apply(_originalResolution);
			_resolutionApplied = false;
			_originalResolution = null;
		}
	}

	private static AppSettings.ResolutionSetting? GetCurrentResolution(string? monitor)
	{
		DisplayMode? mode = DisplaySystem.GetCurrentMode(monitor);
		if (mode == null)
		{
			return null;
		}
		return new AppSettings.ResolutionSetting
		{
			Width = mode.Width,
			Height = mode.Height,
			RefreshRate = mode.RefreshRate,
			Monitor = monitor
		};
	}

	private void ApplyInGameResolutionIfNeeded()
	{
		if (_resolutionApplied)
		{
			return;
		}
		AppSettings prop = App.Settings.Prop;
		if (prop.InGameResolution == null)
		{
			return;
		}
		if (prop.UsePlaceId)
		{
			long result;
			if (prop.MatchUniverseId)
			{
				if (Data.UniverseId == 0L || prop.TargetUniverseId != Data.UniverseId)
				{
					return;
				}
			}
			else if (!long.TryParse(prop.PlaceId, out result) || Data.PlaceId != result)
			{
				return;
			}
		}
		App.Logger.WriteLine("ActivityWatcher", $"Applying in game resolution (Universe={Data.UniverseId}, Place={Data.PlaceId})");
		if (_originalResolution == null)
		{
			_originalResolution = GetCurrentResolution(prop.InGameResolution.Monitor);
		}
		if (_originalResolution == null)
		{
			return;
		}
		_resolutionApplied = true;
		InGameResolutionApplier.Apply(prop.InGameResolution);
	}

	private void ResetData()
	{
		Interlocked.Increment(ref _playerSessionGeneration);
		lock (_playerStateLock)
			_activePlayerIds.Clear();
		Data = new ActivityData();
		_nextPlayerLogId = 0;
		_nextMessageLogId = 0;
	}

	private static void AddBounded<T>(Dictionary<int, T> entries, int key, T value, int limit)
	{
		lock (entries)
		{
			entries[key] = value;
			if (entries.Count > limit)
				entries.Remove(key - limit);
		}
	}

	public void Dispose()
	{
		if (IsDisposed)
		{
			return;
		}
		IsDisposed = true;
		_playerLifetimeCts.Cancel();
		if (_logWatcher != null)
		{
			_logWatcher.EnableRaisingEvents = false;
			_logWatcher.Changed -= OnLogChanged;
			_logWatcher.Dispose();
			_logWatcher = null;
		}
		RestoreOriginalResolution();
		OnGameJoin = null;
		OnGameLeave = null;
		OnLogOpen = null;
		OnAppClose = null;
		OnNewPlayerRequest = null;
		OnPlayerLogUpdated = null;
		OnNewMessageRequest = null;
		OnRPCMessage = null;
		lock (History)
		{
			History.Clear();
		}
		ResetData();
		_playerLifetimeCts.Dispose();
		_logSignal.Dispose();
		GC.SuppressFinalize(this);
	}
}
