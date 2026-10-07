using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiscordRPC;
using DiscordRPC.Message;
using Voidstrap.Enums;
using Voidstrap.Models.Entities;
using Voidstrap.Models.VoidstrapRPC;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

public partial class DiscordRichPresence : IDisposable
{
	private sealed class OriginalSnapshot
	{
		public string? Details;

		public string? State;

		public string? SmallImageKey;

		public string? SmallImageText;

		public string? LargeImageKey;

		public string? LargeImageText;
	}

	private const string LOG_IDENT = "DiscordRichPresence";

	private const int MaxQueuedMessages = 64;

	private const string IdleDetails = "Inside Voidstrap";

	private static string IdleDetailsText => Voidstrap.Utility.RpcText.Mark("Inside") + " " + Voidstrap.Utility.Branding.Name;

	private static string IdleStateText => Voidstrap.Utility.RpcText.Mark("Browsing Roblox");

	private const string IdleState = "Browsing Roblox";
	private static readonly string LoadingState = Voidstrap.Utility.RpcText.Mark("Loading game");

	private static readonly string? LaunchStatusFile = Environment.GetEnvironmentVariable("VOIDSTRAP_STATUS_FILE");

	private static readonly ConcurrentDictionary<long, (string Name, DateTime Expires)> PlaceNames = new();

	public static readonly Dictionary<string, (string Name, string Url)> IdleIconPresets = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
	{
		["blue"] = ("Blue Logo", "https://upload.wikimedia.org/wikipedia/commons/thumb/f/f2/Roblox_%282025%29_%28App_Icon%29.svg/250px-Roblox_%282025%29_%28App_Icon%29.svg.png"),
		["black"] = ("Black Logo", "https://devforum-uploads.s3.dualstack.us-east-2.amazonaws.com/uploads/original/5X/c/4/c/2/c4c28132e4907f73ea0430d18d769c06276e39cc.png"),
		["old"] = ("2009-2011 Logo", "https://static.wikia.nocookie.net/logopedia/images/b/b7/ROBLOX_2006-2009.svg/revision/latest/scale-to-width-down/1000?cb=20250403121056")
	};

	private readonly object _clientGate = new object();

	private DiscordRpcClient? _rpcClient;

	private DateTime _connectRetryAtUtc;

	private readonly ActivityWatcher _activityWatcher;

	private readonly ConcurrentQueue<Message> _messageQueue = new ConcurrentQueue<Message>();

	private readonly SemaphoreSlim _updateLock = new SemaphoreSlim(1, 1);

	private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();

	private readonly CancellationToken _lifetimeToken;

	private readonly DateTime _sessionStart = DateTime.UtcNow;

	private DiscordRPC.RichPresence? _currentPresence;

	private OriginalSnapshot? _originalSnapshot;

	private bool _visible = true;

	private bool _userVisible = true;

	private bool _studioSuppressed;

	private Timer? _studioWatchTimer;

	private bool _disposed;

	private DateTime _lastPresenceUpdate = DateTime.MinValue;

	private DiscordRPC.RichPresence? _pendingPresence;

	private string? _lastPresenceSignature;
	private string? _lastSentPresenceSignature;

	private readonly TimeSpan _updateCooldown = TimeSpan.FromSeconds(5L);

	private Timer? _refreshTimer;

	private readonly EventHandler _onGameJoinHandler;

	private readonly EventHandler _onGameLeaveHandler;

	private readonly EventHandler<Message> _onRpcMessageHandler;

	private int _joinPresenceUpdatePending;

	private int _updatePending;

	private int _cachedFlagCount = -1;

	private DateTime _cachedFlagsAtUtc = DateTime.MinValue;

	private long _cachedFlagsFileSize = -1L;

	private int _flushScheduled;

	private readonly object _activityGate = new object();
	private CancellationTokenSource? _gameCancellation;
	private int _activityRevision;

	private const int MaxGameMessages = 64;

	private const ulong MaxUnixSeconds = 32503680000UL;

	private readonly List<Message> _gameMessages = new List<Message>();

	private bool _gamePresenceActive;

	private ActivityData? _enrichedActivity;

	private string? _enrichedLocation;

	private string _enrichedLargeImage = string.Empty;

	private string? _enrichedSmallKey;

	private string? _enrichedSmallText;

	private volatile bool _linuxStudioRunning;

	private int _linuxStudioProbeActive;

	private void ClearGameMessages()
	{
		lock (_activityGate)
		{
			_gameMessages.Clear();
			_messageQueue.Clear();
		}
	}

	private void OnGameJoining(object? sender, EventArgs e)
	{
		ClearGameMessages();
		ResetGameRequests();
		PublishLoadingPresence();
		_ = Task.Run(SetCurrentGameAsync, _lifetimeToken);
	}

	private void OnGameJoined(object? sender, EventArgs e)
	{
		ResetGameRequests();
		Interlocked.Exchange(ref _joinPresenceUpdatePending, 1);
		_ = Task.Run(SetCurrentGameAsync, _lifetimeToken);
	}

	private void OnGameLeft(object? sender, EventArgs e)
	{
		ClearGameMessages();
		ResetGameRequests();
		if (_activityWatcher.IsTeleporting)
		{
			PublishLoadingPresence();
			return;
		}
		Interlocked.Exchange(ref _joinPresenceUpdatePending, 0);
		_ = Task.Run(SetCurrentGameAsync, _lifetimeToken);
	}

	private void OnGameRpcMessage(object? sender, Message message) => ProcessRPCMessage(message);

	private void ResetGameRequests()
	{
		lock (_activityGate)
		{
			_activityRevision++;
			_gameCancellation?.Cancel();
			_gameCancellation?.Dispose();
			_gameCancellation = _disposed ? null : CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
		}
	}

	private bool IsCurrentActivity(ActivityData activity, int revision)
	{
		return !_disposed && revision == _activityRevision && _activityWatcher.InGame
			&& ReferenceEquals(activity, _activityWatcher.Data);
	}

	private void PublishLoadingPresence()
	{
		lock (_activityGate)
		{
			if (_disposed || _activityWatcher.InGame)
				return;
			_gamePresenceActive = false;
			_currentPresence = new DiscordRPC.RichPresence
			{
				Details = "Roblox",
				State = LoadingState,
				StatusDisplay = StatusDisplayType.Details,
				Assets = new Assets { LargeImageKey = App.RpcLoadingImageUrl, LargeImageText = "Roblox" },
				Buttons = Array.Empty<Button>()
			};
			_originalSnapshot = null;
			string signature = BuildPresenceSignature(_currentPresence);
			if (_lastPresenceSignature == signature)
				return;
			_lastPresenceSignature = signature;
			UpdatePresence(force: true);
		}
	}

	public static string GetIdleIconUrl()
	{
		string key = App.Settings.Prop.RpcIdleIcon ?? "blue";
		if (!IdleIconPresets.TryGetValue(key, out (string, string) value))
		{
			return IdleIconPresets["blue"].Url;
		}
		return value.Item2;
	}

	public DiscordRichPresence(ActivityWatcher activityWatcher)
	{
		_lifetimeToken = _lifetimeCancellation.Token;
		Voidstrap.Utility.RpcText.Prewarm();
		_gameCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
		_activityWatcher = activityWatcher ?? throw new ArgumentNullException(nameof(activityWatcher));
		_onGameJoinHandler = OnGameJoined;
		_onGameLeaveHandler = OnGameLeft;
		_onRpcMessageHandler = OnGameRpcMessage;
		_activityWatcher.OnGameJoining += OnGameJoining;
		_activityWatcher.OnGameJoin += _onGameJoinHandler;
		_activityWatcher.OnGameLeave += _onGameLeaveHandler;
		_activityWatcher.OnRPCMessage += _onRpcMessageHandler;
		EnsureConnected();
		_refreshTimer = new Timer(OnRefreshTimer, null, TimeSpan.FromMinutes(5L), TimeSpan.FromMinutes(5L));
		_studioWatchTimer = new Timer(OnStudioWatch, null, TimeSpan.FromSeconds(4L), TimeSpan.FromSeconds(4L));
	}

	private void OnClientReady(object sender, ReadyMessage e)
	{
		App.Logger.WriteLine("DiscordRichPresence", $"Ready: {e.User} ({e.User.ID})");
		if (_disposed || !ReferenceEquals(sender, _rpcClient))
			return;
		lock (_activityGate)
		{
			_lastPresenceSignature = null;
			_lastSentPresenceSignature = null;
			UpdatePresence(force: true);
		}
		_ = Task.Run(SetCurrentGameAsync, _lifetimeToken);
	}

	private static string BuildPresenceSignature(DiscordRPC.RichPresence presence)
	{
		Assets? assets = presence.Assets;
		string buttons = string.Empty;
		if (presence.Buttons != null)
		{
			foreach (Button button in presence.Buttons)
				buttons += (button?.Label ?? string.Empty) + "\n" + (button?.Url ?? string.Empty) + "\n";
		}
		return string.Join("\n",
			presence.Details ?? string.Empty,
			presence.State ?? string.Empty,
			presence.StatusDisplay.ToString(),
			assets?.LargeImageKey ?? string.Empty,
			assets?.LargeImageText ?? string.Empty,
			assets?.SmallImageKey ?? string.Empty,
			assets?.SmallImageText ?? string.Empty,
			presence.Timestamps?.Start?.Ticks.ToString() ?? string.Empty,
			presence.Timestamps?.End?.Ticks.ToString() ?? string.Empty,
			buttons);
	}

	private void OnClientPresenceUpdate(object sender, PresenceMessage e)
	{
		App.Logger.WriteLine("DiscordRichPresence", "Presence updated");
	}

	private void OnClientError(object sender, ErrorMessage e)
	{
		App.Logger.WriteLine("DiscordRichPresence", "RPC Error: " + e.Message);
	}

	private void OnClientConnectionEstablished(object sender, ConnectionEstablishedMessage e)
	{
		if (_disposed || !ReferenceEquals(sender, _rpcClient))
			return;
		_lastPresenceSignature = null;
		App.Logger.WriteLine("DiscordRichPresence", "Connected to Discord RPC");
	}

	private void OnClientClose(object sender, CloseMessage e)
	{
		App.Logger.WriteLine("DiscordRichPresence", $"Connection closed: {e.Reason} ({e.Code})");
		RetryLater(sender);
	}

	private void OnClientConnectionFailed(object sender, ConnectionFailedMessage e)
	{
		RetryLater(sender);
	}

	private void RetryLater(object sender)
	{
		if (!_disposed && sender is DiscordRpcClient client && ReferenceEquals(client, _rpcClient))
		{
			_connectRetryAtUtc = DateTime.UtcNow + DiscordIpc.RetryDelay;
			_ = Task.Run(() => ReleaseClient(client));
		}
	}

	private void EnsureConnected()
	{
		lock (_clientGate)
		{
			if (_disposed || _rpcClient != null || DateTime.UtcNow < _connectRetryAtUtc || !DiscordIpc.TryFindPipe(out int pipe))
			{
				return;
			}
			DiscordRpcClient client = DiscordIpc.CreateClient("1005469189907173486", pipe);
			client.OnReady += OnClientReady;
			client.OnPresenceUpdate += OnClientPresenceUpdate;
			client.OnError += OnClientError;
			client.OnConnectionEstablished += OnClientConnectionEstablished;
			client.OnConnectionFailed += OnClientConnectionFailed;
			client.OnClose += OnClientClose;
			_rpcClient = client;
			try
			{
				client.Initialize();
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("DiscordRichPresence", "Initial connection failed: " + ex.Message);
				_connectRetryAtUtc = DateTime.UtcNow + DiscordIpc.RetryDelay;
				ReleaseClient(client);
			}
		}
	}

	private void ReleaseClient(DiscordRpcClient client)
	{
		lock (_clientGate)
		{
			if (!ReferenceEquals(_rpcClient, client))
			{
				return;
			}
			_rpcClient = null;
		}
		client.OnReady -= OnClientReady;
		client.OnPresenceUpdate -= OnClientPresenceUpdate;
		client.OnError -= OnClientError;
		client.OnConnectionEstablished -= OnClientConnectionEstablished;
		client.OnConnectionFailed -= OnClientConnectionFailed;
		client.OnClose -= OnClientClose;
		DiscordIpc.Close(client);
	}

	private void OnRefreshTimer(object? state)
	{
		try
		{
			if (!_disposed)
			{
				_ = Task.Run(SetCurrentGameAsync, _lifetimeToken);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("DiscordRichPresence::OnRefreshTimer", ex);
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

	public void ProcessRPCMessage(Message message, bool implicitUpdate = true)
	{
		lock (_activityGate)
		{
			if (_disposed || (message.Command != "SetRichPresence" && message.Command != "SetLaunchData"))
				return;
			while (_gameMessages.Count >= MaxGameMessages)
				_gameMessages.RemoveAt(0);
			_gameMessages.Add(message);
			if (!_gamePresenceActive || _currentPresence == null || _originalSnapshot == null)
				return;
			if (ApplyRPCMessage(message) && implicitUpdate)
				UpdatePresence();
		}
	}

	private void ReplayGameMessages()
	{
		foreach (Message message in _gameMessages)
			ApplyRPCMessage(message);
	}

	private bool ApplyRPCMessage(Message message)
	{
		if (_currentPresence == null || _originalSnapshot == null)
			return false;
		if (message.Command == "SetLaunchData")
		{
			_currentPresence.Buttons = GetButtons();
			return true;
		}
		if (message.Command != "SetRichPresence"
			|| !TryDeserializePresence(message.Data, out Voidstrap.Models.VoidstrapRPC.RichPresence? presence) || presence == null)
			return false;
		_currentPresence.Details = UpdateField(_currentPresence.Details, presence.Details, _originalSnapshot.Details, 128);
		_currentPresence.State = UpdateField(_currentPresence.State, presence.State, _originalSnapshot.State, 128);
		_currentPresence.Assets ??= new Assets();
		UpdateAssets(_currentPresence.Assets, _originalSnapshot, presence.SmallImage, small: true);
		UpdateAssets(_currentPresence.Assets, _originalSnapshot, presence.LargeImage, small: false);
		ApplyTimestamps(_currentPresence, presence);
		return true;
	}

	private static void ApplyTimestamps(DiscordRPC.RichPresence target, Voidstrap.Models.VoidstrapRPC.RichPresence presence)
	{
		if (presence.TimestampStart is null && presence.TimestampEnd is null)
			return;
		target.Timestamps ??= new Timestamps();
		if (presence.TimestampStart is ulong start)
		{
			if (start == 0)
				target.Timestamps.Start = null;
			else if (start <= MaxUnixSeconds)
				target.Timestamps.Start = DateTimeOffset.FromUnixTimeSeconds((long)start).UtcDateTime;
		}
		if (presence.TimestampEnd is ulong end)
		{
			if (end == 0)
				target.Timestamps.End = null;
			else if (end <= MaxUnixSeconds)
				target.Timestamps.End = DateTimeOffset.FromUnixTimeSeconds((long)end).UtcDateTime;
		}
	}

	private static bool TryDeserializePresence(JsonElement data, out Voidstrap.Models.VoidstrapRPC.RichPresence? presence)
	{
		try
		{
			presence = data.Deserialize<Voidstrap.Models.VoidstrapRPC.RichPresence>();
			return presence != null;
		}
		catch
		{
			presence = null;
			return false;
		}
	}

	private static string? UpdateField(string? current, string? newValue, string? original, int maxLength)
	{
		if (string.IsNullOrEmpty(newValue))
		{
			return current;
		}
		if (newValue == "<reset>")
		{
			return original;
		}
		string fitted = Voidstrap.Utility.DiscordPresenceGuard.Text(newValue, maxLength);
		return fitted.Length == 0 ? current : fitted;
	}

	private static void UpdateAssets(Assets current, OriginalSnapshot original, RichPresenceImage? data, bool small)
	{
		if (data == null)
		{
			return;
		}
		if (data.Clear)
		{
			if (small)
			{
				current.SmallImageKey = "";
			}
			else
			{
				current.LargeImageKey = "";
			}
			return;
		}
		if (data.Reset)
		{
			if (small)
			{
				current.SmallImageKey = original.SmallImageKey ?? "";
				current.SmallImageText = original.SmallImageText ?? "";
			}
			else
			{
				current.LargeImageKey = original.LargeImageKey ?? "";
				current.LargeImageText = original.LargeImageText ?? "";
			}
			return;
		}
		string customKey = Voidstrap.Utility.DiscordPresenceGuard.Key(data.CustomKey);
		if (customKey.Length > 0)
		{
			if (small)
			{
				current.SmallImageKey = customKey;
			}
			else
			{
				current.LargeImageKey = customKey;
			}
			return;
		}
		if (data.AssetId.HasValue)
		{
			string text = $"https://assetdelivery.roblox.com/v1/asset/?id={data.AssetId.Value}";
			if (small)
			{
				current.SmallImageKey = text;
			}
			else
			{
				current.LargeImageKey = text;
			}
		}
		string hoverText = Voidstrap.Utility.DiscordPresenceGuard.Text(data.HoverText);
		if (hoverText.Length > 0)
		{
			if (small)
			{
				current.SmallImageText = hoverText;
			}
			else
			{
				current.LargeImageText = hoverText;
			}
		}
	}

	public bool IsUserVisible => _userVisible;

	public void SetVisibility(bool visible)
	{
		_userVisible = visible;
		ApplyVisibility();
	}

	private void ApplyVisibility()
	{
		bool visible = _userVisible && !_studioSuppressed;
		if (_visible == visible)
		{
			return;
		}
		_visible = visible;
		_lastSentPresenceSignature = null;
		if (visible)
		{
			UpdatePresence(force: true);
			return;
		}
		try
		{
			_rpcClient?.ClearPresence();
		}
		catch
		{
		}
	}

	private void OnStudioWatch(object? state)
	{
		if (_disposed)
		{
			return;
		}
		if (_currentPresence?.State == LoadingState && !_activityWatcher.InGame
			&& _activityWatcher.Data.PlaceId == 0 && !_activityWatcher.IsTeleporting)
			_ = Task.Run(SetCurrentGameAsync, _lifetimeToken);
		EnsureConnected();
		lock (_activityGate)
		{
			if (_pendingPresence != null && _rpcClient?.IsInitialized == true)
				UpdatePresence();
		}
		bool studioRunning;
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			QueueLinuxStudioProbe();
			studioRunning = _linuxStudioRunning;
		}
		else
		{
			try
			{
				Process[] processes = Process.GetProcessesByName(Voidstrap.Utility.Platform.RobloxStudioProcessName);
				studioRunning = processes.Length != 0;
				foreach (Process process in processes)
				{
					try
					{
						process.Dispose();
					}
					catch
					{
					}
				}
			}
			catch
			{
				return;
			}
		}
		if (_studioSuppressed == studioRunning)
		{
			return;
		}
		_studioSuppressed = studioRunning;
		try
		{
			App.Logger.WriteLine("DiscordRichPresence", studioRunning ? "Roblox Studio opened, stopping rich presence" : "Roblox Studio closed, resuming rich presence");
			ApplyVisibility();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("DiscordRichPresence::OnStudioWatch", ex);
		}
	}

	private void QueueLinuxStudioProbe()
	{
		if (Interlocked.Exchange(ref _linuxStudioProbeActive, 1) == 1)
			return;
		_ = Task.Run(async () =>
		{
			try
			{
				Voidstrap.Platform.Linux.LinuxVinegarProcessProbe probe = new(new Voidstrap.Core.SystemProcessService());
				_linuxStudioRunning = await probe.IsRunningAsync(_lifetimeToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LOG_IDENT, "Roblox Studio check failed: " + ex.Message);
			}
			finally
			{
				Interlocked.Exchange(ref _linuxStudioProbeActive, 0);
			}
		});
	}

	private async Task SetCurrentGameAsync()
	{
		if (_disposed)
		{
			return;
		}
		Interlocked.Exchange(ref _updatePending, 1);
		bool acquired;
		try
		{
			acquired = await _updateLock.WaitAsync(0, _lifetimeToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		if (!acquired)
		{
			return;
		}
		try
		{
			while (!_disposed && Interlocked.Exchange(ref _updatePending, 0) == 1)
			{
				try
				{
					await SetCurrentGame().ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("DiscordRichPresence", "SetCurrentGame failed: " + ex.Message);
				}
			}
		}
		finally
		{
			try
			{
				_updateLock.Release();
			}
			catch
			{
			}
		}
		if (!_disposed && Volatile.Read(in _updatePending) == 1)
		{
			_ = Task.Run(SetCurrentGameAsync, _lifetimeToken);
		}
	}

	private int LoadFlags()
	{
		try
		{
			string text = Path.Combine(Paths.Mods, "ClientSettings", "ClientAppSettings.json");
			if (!File.Exists(text))
			{
				_cachedFlagCount = 0;
				_cachedFlagsFileSize = 0L;
				_cachedFlagsAtUtc = DateTime.UtcNow;
				return 0;
			}
			FileInfo fileInfo = new FileInfo(text);
			if (_cachedFlagCount >= 0 && fileInfo.Length == _cachedFlagsFileSize && DateTime.UtcNow - _cachedFlagsAtUtc < TimeSpan.FromSeconds(30L))
			{
				return _cachedFlagCount;
			}
			string text2 = File.ReadAllText(text);
			_cachedFlagCount = ParseFlagCount(text2);
			_cachedFlagsFileSize = fileInfo.Length;
			_cachedFlagsAtUtc = DateTime.UtcNow;
			return _cachedFlagCount;
		}
		catch
		{
			return 0;
		}
	}

	public async Task<bool> SetCurrentGame()
	{
		if (_disposed)
			return false;
		if (!_activityWatcher.InGame)
		{
			if (_activityWatcher.Data.PlaceId > 0 || _activityWatcher.IsTeleporting)
			{
				PublishLoadingPresence();
				return true;
			}
			await SetIdlePresenceAsync().ConfigureAwait(false);
			return true;
		}

		ActivityData activity = _activityWatcher.Data;
		int revision;
		CancellationToken gameToken;
		lock (_activityGate)
		{
			revision = _activityRevision;
			gameToken = _gameCancellation?.Token ?? _lifetimeToken;
		}
		DateTime started = activity.RootActivity?.TimeJoined ?? activity.TimeJoined;
		if (started == default)
			started = DateTime.UtcNow;
		int totalFlags = App.Settings.Prop.FFlagRPCDisplayer ? LoadFlags() : 0;
		Task<string> placeNameTask = GetPlaceNameAsync(activity.PlaceId, gameToken);
		if (activity.UniverseDetails == null && activity.UniverseId > 0)
		{
			try
			{
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(gameToken);
				timeout.CancelAfter(TimeSpan.FromSeconds(5));
				await UniverseDetails.FetchSingle(activity.UniverseId, timeout.Token).ConfigureAwait(false);
				activity.UniverseDetails = UniverseDetails.LoadFromCache(activity.UniverseId);
			}
			catch (OperationCanceledException) when (gameToken.IsCancellationRequested)
			{
				return false;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LOG_IDENT, "Universe details request failed: " + ex.Message);
			}
		}

		UniverseDetails? universe = activity.UniverseDetails;
		string rootName = universe?.Data?.Name ?? string.Empty;
		if (!IsCurrentActivity(activity, revision))
			return false;
		string placeName = await placeNameTask.ConfigureAwait(false);
		string shownName = string.IsNullOrWhiteSpace(rootName) ? placeName : rootName;
		long rootPlaceId = universe?.Data?.RootPlaceId ?? 0;
		string subplaceName = !string.IsNullOrWhiteSpace(rootName)
			&& !string.IsNullOrWhiteSpace(placeName)
			&& (rootPlaceId <= 0 || rootPlaceId != activity.PlaceId)
			&& !string.Equals(placeName.Trim(), rootName.Trim(), StringComparison.OrdinalIgnoreCase)
				? placeName.Trim()
				: string.Empty;
		bool unlistedExperience = string.IsNullOrWhiteSpace(shownName);
		if (unlistedExperience)
		{
			shownName = "Roblox";
			App.Logger.WriteLine(LOG_IDENT, "Game name unavailable for universe " + activity.UniverseId);
		}
		if (!string.IsNullOrWhiteSpace(App.Settings.Prop.CustomGameName))
		{
			shownName = App.Settings.Prop.CustomGameName;
			subplaceName = string.Empty;
		}
		(string cleanName, string? betaTag) = ExtractBetaTag(shownName, universe?.Data?.Description);
		string reservedName = ExtractReservedServerName(activity.RPCLaunchData);
		if (!IsCurrentActivity(activity, revision))
			return false;
		string creator = universe?.Data?.Creator?.Name ?? string.Empty;
		bool verified = universe?.Data?.Creator?.HasVerifiedBadge ?? false;
		string detailText = BuildDetailText(cleanName, betaTag, creator, verified);
		bool sameActivity;
		string? knownLocation;
		string knownLargeImage;
		string? knownSmallKey;
		string? knownSmallText;
		lock (_activityGate)
		{
			sameActivity = ReferenceEquals(_enrichedActivity, activity);
			knownLocation = sameActivity && App.Settings.Prop.ServerLocationGame ? _enrichedLocation : null;
			knownLargeImage = sameActivity ? _enrichedLargeImage : string.Empty;
			knownSmallKey = sameActivity ? _enrichedSmallKey : null;
			knownSmallText = sameActivity ? _enrichedSmallText : null;
		}
		string stateText = BuildStateText(activity.ServerType, reservedName, knownLocation, totalFlags);
		string largeImage = !string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon) ? App.Settings.Prop.UseCustomIcon : App.Settings.Prop.GameIconChecked ? universe?.Thumbnail?.ImageUrl ?? string.Empty : string.Empty;
		largeImage = Voidstrap.Utility.DiscordPresenceGuard.Key(largeImage);
		if (largeImage.Length == 0 && App.Settings.Prop.GameIconChecked && string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon))
			largeImage = knownLargeImage;
		string largeText = BuildLargeImageText(subplaceName.Length > 0 ? shownName + " · " + subplaceName : shownName, creator);
		bool showAccount = App.Settings.Prop.ShowAccountOnRichPresence && activity.UserId > 0;
		string smallImage = showAccount && !string.IsNullOrEmpty(knownSmallKey) ? knownSmallKey : "voidstrap";
		string smallText = showAccount && !string.IsNullOrEmpty(knownSmallKey) ? knownSmallText ?? "Voidstrap" : "Voidstrap";
		DiscordRPC.RichPresence gamePresence;
		OriginalSnapshot original;
		lock (_activityGate)
		{
			if (!IsCurrentActivity(activity, revision))
				return false;
			_currentPresence = new DiscordRPC.RichPresence
			{
				Details = detailText,
				State = stateText,
				StatusDisplay = string.IsNullOrWhiteSpace(detailText) ? DiscordRPC.StatusDisplayType.Name : DiscordRPC.StatusDisplayType.Details,
				Timestamps = new Timestamps { Start = started.ToUniversalTime() },
				Buttons = GetButtons(),
				Assets = new Assets
				{
					LargeImageKey = largeImage ?? string.Empty,
					LargeImageText = largeText ?? string.Empty,
					SmallImageKey = smallImage ?? string.Empty,
					SmallImageText = smallText ?? string.Empty
				}
			};
			_originalSnapshot = new OriginalSnapshot
			{
				Details = detailText,
				State = stateText,
				LargeImageKey = largeImage ?? string.Empty,
				LargeImageText = largeText ?? string.Empty,
				SmallImageKey = smallImage ?? string.Empty,
				SmallImageText = smallText ?? string.Empty
			};
			_gamePresenceActive = true;
			ReplayGameMessages();
			gamePresence = _currentPresence;
			original = _originalSnapshot;
			bool joined = Interlocked.Exchange(ref _joinPresenceUpdatePending, 0) == 1;
			string signature = BuildPresenceSignature(_currentPresence);
			if (!joined && signature == _lastPresenceSignature)
				return true;
			_lastPresenceSignature = signature;
			UpdatePresence(force: joined);
			string status = "Updated presence for " + Voidstrap.Utility.RpcText.Render(detailText);
			App.Logger.WriteLine(LOG_IDENT, status);
			if (joined)
				PublishLaunchStatus(status);
		}
		using var enrichmentTimeout = CancellationTokenSource.CreateLinkedTokenSource(gameToken);
		enrichmentTimeout.CancelAfter(TimeSpan.FromSeconds(5));
		CancellationToken enrichmentToken = enrichmentTimeout.Token;
		Task<string?> locationTask = App.Settings.Prop.ServerLocationGame
			? (knownLocation != null ? Task.FromResult<string?>(knownLocation) : activity.QueryServerLocation(enrichmentToken))
			: Task.FromResult<string?>(null);
		Task<(string key, string text)> smallImageTask = GetSmallImageAsync(activity, enrichmentToken);
		Task<string> iconTask = string.IsNullOrWhiteSpace(largeImage) && App.Settings.Prop.GameIconChecked && string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon)
			? FetchPlaceIconAsync(activity.PlaceId, enrichmentToken)
			: Task.FromResult(largeImage ?? string.Empty);
		try
		{
			await Task.WhenAll(locationTask, smallImageTask, iconTask).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			if (gameToken.IsCancellationRequested)
				return false;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Optional game details unavailable: " + ex.Message);
		}
		lock (_activityGate)
		{
			if (!IsCurrentActivity(activity, revision) || !ReferenceEquals(_currentPresence, gamePresence))
				return false;
			if (locationTask.IsCompletedSuccessfully && gamePresence.State == original.State)
				gamePresence.State = original.State = BuildStateText(activity.ServerType, reservedName, locationTask.Result, totalFlags);
			if (iconTask.IsCompletedSuccessfully && gamePresence.Assets.LargeImageKey == original.LargeImageKey)
				gamePresence.Assets.LargeImageKey = original.LargeImageKey = Voidstrap.Utility.DiscordPresenceGuard.Key(iconTask.Result);
			if (smallImageTask.IsCompletedSuccessfully && gamePresence.Assets.SmallImageKey == original.SmallImageKey)
			{
				gamePresence.Assets.SmallImageKey = original.SmallImageKey = smallImageTask.Result.key;
				if (gamePresence.Assets.SmallImageText == original.SmallImageText)
					gamePresence.Assets.SmallImageText = original.SmallImageText = smallImageTask.Result.text;
			}
			if (!sameActivity)
			{
				_enrichedLocation = null;
				_enrichedLargeImage = string.Empty;
				_enrichedSmallKey = null;
				_enrichedSmallText = null;
			}
			_enrichedActivity = activity;
			if (locationTask.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(locationTask.Result))
				_enrichedLocation = locationTask.Result;
			if (iconTask.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(iconTask.Result))
				_enrichedLargeImage = Voidstrap.Utility.DiscordPresenceGuard.Key(iconTask.Result);
			if (smallImageTask.IsCompletedSuccessfully && smallImageTask.Result.key != "voidstrap")
			{
				_enrichedSmallKey = smallImageTask.Result.key;
				_enrichedSmallText = smallImageTask.Result.text;
			}
			_lastPresenceSignature = BuildPresenceSignature(gamePresence);
			UpdatePresence();
			return true;
		}
	}

	internal static async Task<string> GetPlaceNameAsync(long placeId, CancellationToken token)
	{
		if (placeId <= 0)
			return string.Empty;
		if (PlaceNames.TryGetValue(placeId, out var cached) && cached.Expires > DateTime.UtcNow)
			return cached.Name;
		try
		{
			string url = "https://games.roblox.com/v1/games/multiget-place-details?placeIds=" + placeId;
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
			timeout.CancelAfter(TimeSpan.FromSeconds(5));
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			string? cookie = RobloxCookie.Get();
			if (!string.IsNullOrEmpty(cookie))
				request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			request.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
			using HttpResponseMessage response = await App.HttpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				StorePlaceName(placeId, string.Empty, DateTime.UtcNow.AddMinutes(2));
				return string.Empty;
			}
			using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
			using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
			string name = ReadPlaceName(json.RootElement);
			StorePlaceName(placeId, name, DateTime.UtcNow.AddHours(name.Length > 0 ? 6 : 1));
			return name;
		}
		catch (OperationCanceledException)
		{
			return string.Empty;
		}
		catch
		{
			StorePlaceName(placeId, string.Empty, DateTime.UtcNow.AddMinutes(2));
			return string.Empty;
		}
	}

	internal static string ReadPlaceName(JsonElement root)
	{
		JsonElement data = root;
		if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("data", out data))
			return string.Empty;
		if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
			return string.Empty;
		JsonElement place = data[0];
		return place.ValueKind == JsonValueKind.Object && place.TryGetProperty("name", out JsonElement name)
			&& name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty;
	}

	private static void StorePlaceName(long placeId, string name, DateTime expires)
	{
		PlaceNames[placeId] = (name, expires);
		if (PlaceNames.Count <= 256)
			return;
		foreach (KeyValuePair<long, (string Name, DateTime Expires)> entry in PlaceNames)
		{
			if (entry.Value.Expires <= DateTime.UtcNow || PlaceNames.Count > 256)
				PlaceNames.TryRemove(entry.Key, out _);
			if (PlaceNames.Count <= 256)
				break;
		}
	}

	private static string ExtractReservedServerName(string launchData)
	{
		if (string.IsNullOrWhiteSpace(launchData))
			return string.Empty;
		try
		{
			using JsonDocument json = JsonDocument.Parse(launchData);
			return FindServerName(json.RootElement);
		}
		catch
		{
			return string.Empty;
		}
	}

	private static string FindServerName(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in element.EnumerateObject())
			{
				if ((property.Name.Equals("serverName", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("reservedServerName", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("privateServerName", StringComparison.OrdinalIgnoreCase)) && property.Value.ValueKind == JsonValueKind.String)
					return property.Value.GetString() ?? string.Empty;
				string nested = FindServerName(property.Value);
				if (nested.Length > 0)
					return nested;
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in element.EnumerateArray())
			{
				string nested = FindServerName(item);
				if (nested.Length > 0)
					return nested;
			}
		}
		return string.Empty;
	}

	internal static string BuildDetailText(string name, string? betaTag, string creator, bool verified)
	{
		string detail = App.Settings.Prop.GameNameChecked ? name : string.Empty;
		if (!string.IsNullOrWhiteSpace(betaTag))
			detail = detail.Length == 0 ? betaTag : detail + " " + betaTag;
		if (App.Settings.Prop.GameCreatorChecked && !string.IsNullOrWhiteSpace(creator))
		{
			string byline = Voidstrap.Utility.RpcText.Mark("by") + " " + creator + (verified ? " \u2611\ufe0f" : string.Empty);
			detail = detail.Length == 0 ? byline : detail + " \u00b7 " + byline;
		}
		return Voidstrap.Utility.DiscordPresenceGuard.Text(detail);
	}

	internal static string BuildStateText(ServerType serverType, string reservedName, string? location, int totalFlags)
	{
		List<string> parts = new List<string>();
		if (App.Settings.Prop.GameStatusChecked)
		{
			string server = serverType switch
			{
				ServerType.Private => Voidstrap.Utility.RpcText.Mark("Private server"),
				ServerType.Reserved when reservedName.Length > 0 => Voidstrap.Utility.RpcText.Mark("Reserved server") + ": " + reservedName,
				ServerType.Reserved => Voidstrap.Utility.RpcText.Mark("Reserved server"),
				_ => Voidstrap.Utility.RpcText.Mark("Public server")
			};
			parts.Add(string.IsNullOrWhiteSpace(location) ? server : server + " " + Voidstrap.Utility.RpcText.Mark("in") + " " + location);
		}
		else if (!string.IsNullOrWhiteSpace(location))
		{
			parts.Add(Voidstrap.Utility.RpcText.Mark("Playing in") + " " + location);
		}
		if (App.Settings.Prop.FFlagRPCDisplayer)
		{
			parts.Add(totalFlags + " " + Voidstrap.Utility.RpcText.Mark(totalFlags == 1 ? "FFlag" : "FFlags"));
		}
		return Voidstrap.Utility.DiscordPresenceGuard.Text(string.Join(" \u00b7 ", parts));
	}

	private async Task SetIdlePresenceAsync()
	{
		try
		{
			CancellationToken idleToken;
			lock (_activityGate)
				idleToken = _gameCancellation?.Token ?? _lifetimeToken;
			ClearGameMessages();
			string smallImage = "voidstrap";
			string smallText = "Voidstrap";
			try
			{
				ActivityData data = _activityWatcher.Data;
				if (data != null && data.UserId > 0)
				{
					if (_currentPresence == null && App.Settings.Prop.ShowAccountOnRichPresence && !UserDetails.IsCached(data.UserId))
					{
						PublishIdlePresence(App.RpcLoadingImageUrl, string.Empty);
					}
					(smallImage, smallText) = await GetSmallImageAsync(data, idleToken).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			catch
			{
			}
			PublishIdlePresence(smallImage, smallText);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("DiscordRichPresence", "SetIdlePresenceAsync failed: " + ex.Message);
		}
	}

	private void PublishIdlePresence(string smallImage, string smallText)
	{
		lock (_activityGate)
		{
			if (_disposed || _activityWatcher.InGame || _activityWatcher.Data.PlaceId > 0 || _activityWatcher.IsTeleporting)
				return;
			_gamePresenceActive = false;
			_enrichedActivity = null;
			string idleIconUrl = GetIdleIconUrl();
			if (_currentPresence == null)
			{
				_currentPresence = new DiscordRPC.RichPresence
				{
					Details = IdleDetailsText,
					State = IdleStateText,
					Timestamps = new Timestamps
					{
						Start = _sessionStart
					},
					Buttons = Array.Empty<Button>(),
					Assets = new Assets
					{
						LargeImageKey = idleIconUrl ?? string.Empty,
						LargeImageText = "Roblox",
						SmallImageKey = smallImage ?? string.Empty,
						SmallImageText = smallText ?? string.Empty
					}
				};
			}
			else
			{
				_currentPresence.Details = IdleDetailsText;
				_currentPresence.State = IdleStateText;
				_currentPresence.Assets.LargeImageKey = idleIconUrl;
				_currentPresence.Assets.LargeImageText = "Roblox";
				_currentPresence.Assets.SmallImageKey = smallImage;
				_currentPresence.Assets.SmallImageText = smallText ?? string.Empty;
				_currentPresence.Buttons = Array.Empty<Button>();
				_currentPresence.Timestamps = new Timestamps
				{
					Start = _sessionStart
				};
			}
			_currentPresence.StatusDisplay = StatusDisplayType.Name;
			_originalSnapshot = new OriginalSnapshot
			{
				Details = IdleDetailsText,
				State = IdleStateText,
				LargeImageKey = idleIconUrl ?? string.Empty,
				LargeImageText = "Roblox",
				SmallImageKey = smallImage ?? string.Empty,
				SmallImageText = smallText ?? string.Empty
			};
			string signature = BuildPresenceSignature(_currentPresence);
			if (signature == _lastPresenceSignature)
				return;
			_lastPresenceSignature = signature;
			UpdatePresence(force: true);
			App.Logger.WriteLine("DiscordRichPresence", "Set idle presence (Inside Voidstrap).");
		}
	}

	private static readonly Dictionary<long, string> _placeIconCache = new Dictionary<long, string>();

	private async Task<string> FetchPlaceIconAsync(long placeId, CancellationToken token = default)
	{
		if (placeId <= 0)
		{
			return string.Empty;
		}
		lock (_placeIconCache)
		{
			if (_placeIconCache.TryGetValue(placeId, out string? cached))
			{
				return cached;
			}
		}
		string resolved = string.Empty;
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken, token);
			timeout.CancelAfter(TimeSpan.FromSeconds(10L));
			ApiArrayResponse<ThumbnailResponse> response = await Http.GetJson<ApiArrayResponse<ThumbnailResponse>>(
				"https://thumbnails.roblox.com/v1/places/gameicons?placeIds=" + placeId + "&returnPolicy=PlaceHolder&size=512x512&format=Png&isCircular=false",
				timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
			ThumbnailResponse? first = response?.Data?.FirstOrDefault();
			if (first != null && !string.IsNullOrWhiteSpace(first.ImageUrl))
			{
				resolved = first.ImageUrl;
			}
		}
		catch (OperationCanceledException)
		{
			return string.Empty;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("DiscordRichPresence", "Place icon fetch failed: " + ex.Message);
		}
		if (resolved.Length == 0)
			return string.Empty;
		lock (_placeIconCache)
		{
			while (_placeIconCache.Count >= 256)
				_placeIconCache.Remove(_placeIconCache.Keys.First());
			_placeIconCache[placeId] = resolved;
		}
		return resolved;
	}

	private async Task<bool> SetPrivateExperiencePresenceAsync(ActivityData activity, long placeId, int totalFlags)
	{
		if (_disposed)
		{
			return false;
		}
		string shownName = !string.IsNullOrWhiteSpace(App.Settings.Prop.CustomGameName)
			? App.Settings.Prop.CustomGameName
			: Voidstrap.Utility.RpcText.Mark("Private experience");
		shownName = Voidstrap.Utility.DiscordPresenceGuard.Text(shownName);
		string stateText = BuildStateText(activity.ServerType, string.Empty, null, totalFlags);

		string largeImage = string.Empty;
		if (!string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon))
		{
			largeImage = App.Settings.Prop.UseCustomIcon;
		}
		else if (App.Settings.Prop.GameIconChecked)
		{
			largeImage = await FetchPlaceIconAsync(placeId).ConfigureAwait(continueOnCapturedContext: false);
		}
		largeImage = Voidstrap.Utility.DiscordPresenceGuard.Key(largeImage);
		if (string.IsNullOrWhiteSpace(largeImage))
		{
			largeImage = "voidstrap";
		}

		(string smallImage, string smallText) = await GetSmallImageAsync(activity).ConfigureAwait(continueOnCapturedContext: false);
		DateTime started = activity.TimeJoined == default(DateTime) ? DateTime.UtcNow : activity.TimeJoined;

		_currentPresence = new DiscordRPC.RichPresence
		{
			Details = App.Settings.Prop.GameNameChecked ? shownName : string.Empty,
			State = stateText,
			StatusDisplay = App.Settings.Prop.GameNameChecked && !string.IsNullOrWhiteSpace(shownName) ? DiscordRPC.StatusDisplayType.Details : DiscordRPC.StatusDisplayType.Name,
			Timestamps = new Timestamps { Start = started.ToUniversalTime() },
			Buttons = GetButtons(),
			Assets = new Assets
			{
				LargeImageKey = largeImage ?? string.Empty,
				LargeImageText = App.Settings.Prop.GameIconChecked ? shownName : string.Empty,
				SmallImageKey = smallImage ?? string.Empty,
				SmallImageText = smallText ?? string.Empty,
			},
		};
		_originalSnapshot = new OriginalSnapshot
		{
			Details = _currentPresence.Details,
			State = _currentPresence.State,
			LargeImageKey = largeImage ?? string.Empty,
			LargeImageText = _currentPresence.Assets.LargeImageText,
			SmallImageKey = smallImage ?? string.Empty,
			SmallImageText = smallText ?? string.Empty,
		};
		lock (_activityGate)
		{
			_gamePresenceActive = true;
			ReplayGameMessages();
		}
		UpdatePresence(force: true);
		App.Logger.WriteLine(LOG_IDENT, "Updated presence for private experience " + placeId);
		return true;
	}

	private static readonly (string Pattern, string Tag)[] WipMarkers = BuildWipMarkers();



	[GeneratedRegex("\\s{2,}")]
	private static partial Regex WipWhitespace { get; }

	private static (string Pattern, string Tag)[] BuildWipMarkers()
	{
		(string Words, string Tag)[] source = new (string, string)[]
		{
			("PRE[\\s-]?ALPHA", "[PRE-ALPHA]"),
			("EARLY\\s+ACCESS", "[EARLY ACCESS]"),
			("OPEN\\s+BETA", "[OPEN BETA]"),
			("CLOSED\\s+BETA", "[CLOSED BETA]"),
			("IN\\s+WORKS", "[IN WORKS]"),
			("IN[\\s-]?DEV(?:ELOPMENT)?", "[IN DEV]"),
			("UNDER\\s+CONSTRUCTION", "[WIP]"),
			("WORK\\s+IN\\s+PROGRESS", "[WIP]"),
			("BETA", "[BETA]"),
			("ALPHA", "[ALPHA]"),
			("TESTING", "[TESTING]"),
			("PREVIEW", "[PREVIEW]"),
			("PROTOTYPE", "[PROTOTYPE]"),
			("EXPERIMENTAL", "[EXPERIMENTAL]"),
			("UNRELEASED", "[UNRELEASED]"),
			("SNAPSHOT", "[SNAPSHOT]"),
			("DEMO", "[DEMO]"),
			("WIP", "[WIP]"),
		};
		List<(string, string)> built = new List<(string, string)>(source.Length);
		foreach ((string words, string tag) in source)
		{
			built.Add(("(?:[\\[\\(\\{]\\s*" + words + "\\s*[\\]\\)\\}])|(?:\\b" + words + "\\b\\s*$)", tag));
		}
		return built.ToArray();
	}

	internal static (string CleanName, string? Tag) ExtractBetaTag(string gameName, string? description)
	{
		if (string.IsNullOrWhiteSpace(gameName))
		{
			return (CleanName: gameName ?? "", Tag: null);
		}
		if (!App.Settings.Prop.GameWIP)
		{
			return (CleanName: gameName, Tag: null);
		}
		string working = gameName;
		string? tag = null;
		foreach ((string pattern, string markerTag) in WipMarkers)
		{
			if (!Regex.IsMatch(working, pattern, RegexOptions.IgnoreCase))
			{
				continue;
			}
			if (tag == null)
			{
				tag = markerTag;
			}
			working = Regex.Replace(working, pattern, " ", RegexOptions.IgnoreCase);
		}
		if (tag == null)
		{
			return (CleanName: gameName, Tag: null);
		}
		working = WipEmptyBrackets.Replace(working, " ");
		working = WipWhitespace.Replace(working, " ").Trim();
		working = WipLeftoverSeparators.Replace(working, "").Trim();
		return (CleanName: string.IsNullOrWhiteSpace(working) ? gameName : working, Tag: tag);
	}

	private async Task<(string key, string text)> GetSmallImageAsync(ActivityData activity, CancellationToken token = default)
	{
		if (!App.Settings.Prop.ShowAccountOnRichPresence || activity.UserId <= 0 || token.IsCancellationRequested)
		{
			return (key: "voidstrap", text: "Voidstrap");
		}
		try
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken, token);
			timeout.CancelAfter(TimeSpan.FromSeconds(5));
			UserDetails userDetails = await UserDetails.Fetch(activity.UserId, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
			if (userDetails?.Data == null)
			{
				return (key: "voidstrap", text: "Voidstrap");
			}
			string avatar = Voidstrap.Utility.DiscordPresenceGuard.Key(userDetails.Thumbnail?.ImageUrl);
			return (key: avatar.Length > 0 ? avatar : "voidstrap", text: Voidstrap.Utility.DiscordPresenceGuard.Text(userDetails.Data.DisplayName + " (@" + userDetails.Data.Name + ")"));
		}
		catch
		{
			return (key: "voidstrap", text: "Voidstrap");
		}
	}

	internal static string BuildLargeImageText(string shownName, string creator)
	{
		return string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon) && App.Settings.Prop.GameIconChecked
			? Voidstrap.Utility.DiscordPresenceGuard.Text(creator.Length > 0 ? shownName + " " + Voidstrap.Utility.RpcText.Mark("by") + " " + creator : shownName)
			: string.Empty;
	}

	internal static int ParseFlagCount(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return 0;
		}
		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions.CaseInsensitive)?.Count ?? 0;
		}
		catch
		{
			return 0;
		}
	}

	public Button[] GetButtons()
	{
		return BuildButtons(_activityWatcher.Data);
	}

	internal static Button[] BuildButtons(ActivityData? data)
	{
		List<Button> list = new List<Button>();
		if (data == null)
		{
			return list.ToArray();
		}
		if (!App.Settings.Prop.HideRPCButtons)
		{
			string? text = null;
			if (data.ServerType == ServerType.Public || (data.ServerType == ServerType.Reserved && !string.IsNullOrEmpty(data.RPCLaunchData)))
			{
				try
				{
					text = data.GetInviteDeeplink();
				}
				catch
				{
					text = null;
				}
			}
			if (!string.IsNullOrEmpty(text) && text.Length <= 512 && Uri.TryCreate(text, UriKind.Absolute, out _))
			{
				list.Add(new Button
				{
					Label = Voidstrap.Utility.RpcText.Mark("Join server"),
					Url = text
				});
			}
		}
		list.Add(new Button
		{
			Label = Voidstrap.Utility.RpcText.Mark("View game"),
			Url = $"https://www.roblox.com/games/{data.PlaceId}"
		});
		return list.ToArray();
	}

	public Voidstrap.Models.PresenceSnapshot GetPresenceSnapshot()
	{
		DiscordRPC.RichPresence? presence = _currentPresence;
		if (_disposed || presence == null)
		{
			return new Voidstrap.Models.PresenceSnapshot { Connected = !_disposed && _rpcClient?.IsInitialized == true, Active = false };
		}
		Voidstrap.Models.PresenceSnapshot snapshot = new Voidstrap.Models.PresenceSnapshot
		{
			Connected = _rpcClient?.IsInitialized == true,
			Active = true,
			Details = Voidstrap.Utility.RpcText.Render(presence.Details),
			State = Voidstrap.Utility.RpcText.Render(presence.State),
			LargeImageKey = presence.Assets?.LargeImageKey ?? string.Empty,
			LargeImageText = Voidstrap.Utility.RpcText.Render(presence.Assets?.LargeImageText),
			SmallImageKey = presence.Assets?.SmallImageKey ?? string.Empty,
			SmallImageText = Voidstrap.Utility.RpcText.Render(presence.Assets?.SmallImageText),
			Start = presence.Timestamps?.Start,
			End = presence.Timestamps?.End,
			PartySize = presence.Party?.Size ?? 0,
			PartyMax = presence.Party?.Max ?? 0,
			PartyId = presence.Party?.ID ?? string.Empty,
		};
		Button[] buttons = presence.Buttons ?? Array.Empty<Button>();
		foreach (Button button in buttons)
		{
			if (button == null)
			{
				continue;
			}
			snapshot.Buttons.Add(new Voidstrap.Models.PresenceButton { Label = Voidstrap.Utility.RpcText.Render(button.Label), Url = button.Url ?? string.Empty });
		}
		return snapshot;
	}

	public void UpdatePresence(bool force = false)
	{
		lock (_activityGate)
		{
			if (_disposed)
				return;
			if (_currentPresence == null)
			{
				try
				{
					_rpcClient?.ClearPresence();
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine(LOG_IDENT, "Clear presence failed: " + ex.Message);
				}
				_pendingPresence = null;
				_lastSentPresenceSignature = null;
				return;
			}
			if (!_visible)
				return;
			string signature = BuildPresenceSignature(_currentPresence);
			if (signature == _lastSentPresenceSignature)
			{
				_pendingPresence = null;
				return;
			}
			_pendingPresence = _currentPresence.Clone();
			DiscordRpcClient? client = _rpcClient;
			if (client?.IsInitialized != true || client.IsDisposed)
				return;
			if (!force && DateTime.UtcNow - _lastPresenceUpdate < _updateCooldown)
			{
				ScheduleFlush();
				return;
			}
			_lastPresenceUpdate = DateTime.UtcNow;
			if (client.SetPresenceSafe(_pendingPresence))
			{
				_lastSentPresenceSignature = signature;
				_pendingPresence = null;
			}
			else
				ScheduleFlush();
		}
	}

	private void ScheduleFlush()
	{
		if (Interlocked.Exchange(ref _flushScheduled, 1) == 1)
		{
			return;
		}
		Task.Run(async delegate
		{
			try
			{
				TimeSpan timeSpan = _updateCooldown - (DateTime.UtcNow - _lastPresenceUpdate);
				if (timeSpan > TimeSpan.Zero)
				{
					await Task.Delay(timeSpan, _lifetimeToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				UpdatePresence(force: true);
			}
			catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
			{
			}
			finally
			{
				Interlocked.Exchange(ref _flushScheduled, 0);
				lock (_activityGate)
				{
					if (!_disposed && _visible && _pendingPresence != null && _rpcClient?.IsInitialized == true)
						ScheduleFlush();
				}
			}
		});
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_lifetimeCancellation.Cancel();
			try
			{
				_activityWatcher.OnGameJoin -= _onGameJoinHandler;
				_activityWatcher.OnGameJoining -= OnGameJoining;
				_activityWatcher.OnGameLeave -= _onGameLeaveHandler;
				_activityWatcher.OnRPCMessage -= _onRpcMessageHandler;
			}
			catch
			{
			}
			try
			{
				_refreshTimer?.Dispose();
				_refreshTimer = null;
				_studioWatchTimer?.Dispose();
				_studioWatchTimer = null;
			}
			catch
			{
			}
			DiscordRpcClient? client = _rpcClient;
			if (client != null)
			{
				ReleaseClient(client);
			}
			_messageQueue.Clear();
			lock (_activityGate)
				_gameMessages.Clear();
			_currentPresence = null;
			_pendingPresence = null;
			_originalSnapshot = null;
			lock (_activityGate)
			{
				_gameCancellation?.Cancel();
				_gameCancellation?.Dispose();
				_gameCancellation = null;
			}
			_lifetimeCancellation.Dispose();
			GC.SuppressFinalize(this);
		}
	}

    [GeneratedRegex("\\s*[-|:~/,]+\\s*$")]
    private static partial Regex WipLeftoverSeparators { get; }
    [GeneratedRegex("[\\[\\(\\{]\\s*[\\]\\)\\}]")]
    private static partial Regex WipEmptyBrackets { get; }
}
