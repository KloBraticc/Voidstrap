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

public class DiscordRichPresence : IDisposable
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

	private const int MaxReconnectAttempts = 8;

	private const int MaxQueuedMessages = 64;

	private const string IdleDetails = "Inside Voidstrap";

	private const string IdleState = "Browsing Roblox";

	private static readonly string? LaunchStatusFile = Environment.GetEnvironmentVariable("VOIDSTRAP_STATUS_FILE");

	private static readonly ConcurrentDictionary<long, (string Name, DateTime Expires)> PlaceNames = new();

	public static readonly Dictionary<string, (string Name, string Url)> IdleIconPresets = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
	{
		["blue"] = ("Blue Logo", "https://upload.wikimedia.org/wikipedia/commons/thumb/f/f2/Roblox_%282025%29_%28App_Icon%29.svg/250px-Roblox_%282025%29_%28App_Icon%29.svg.png"),
		["black"] = ("Black Logo", "https://devforum-uploads.s3.dualstack.us-east-2.amazonaws.com/uploads/original/5X/c/4/c/2/c4c28132e4907f73ea0430d18d769c06276e39cc.png"),
		["old"] = ("2009-2011 Logo", "https://static.wikia.nocookie.net/logopedia/images/b/b7/ROBLOX_2006-2009.svg/revision/latest/scale-to-width-down/1000?cb=20250403121056")
	};

	private readonly DiscordRpcClient _rpcClient = new DiscordRpcClient("1005469189907173486");

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

	private long? _previousPlaceId;

	private DateTime _lastPresenceUpdate = DateTime.MinValue;

	private DiscordRPC.RichPresence? _pendingPresence;

	private string? _lastPresenceSignature;

	private int _reconnectAttempt;

	private int _reconnectPending;

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
		_activityWatcher = activityWatcher ?? throw new ArgumentNullException("activityWatcher");
		_onGameJoinHandler = delegate
		{
			Interlocked.Exchange(ref _joinPresenceUpdatePending, 1);
			SetCurrentGameAsync();
		};
		_onGameLeaveHandler = delegate
		{
			if (_activityWatcher.IsTeleporting)
				return;
			Interlocked.Exchange(ref _joinPresenceUpdatePending, 0);
			SetCurrentGameAsync();
		};
		_onRpcMessageHandler = delegate(object? _, Message message)
		{
			ProcessRPCMessage(message);
		};
		_activityWatcher.OnGameJoin += _onGameJoinHandler;
		_activityWatcher.OnGameLeave += _onGameLeaveHandler;
		_activityWatcher.OnRPCMessage += _onRpcMessageHandler;
		_rpcClient.OnReady += OnClientReady;
		_rpcClient.OnPresenceUpdate += OnClientPresenceUpdate;
		_rpcClient.OnError += OnClientError;
		_rpcClient.OnConnectionEstablished += OnClientConnectionEstablished;
		_rpcClient.OnClose += OnClientClose;
		try
		{
			_rpcClient.Initialize();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("DiscordRichPresence", "Initial connection failed: " + ex.Message);
		}
		_refreshTimer = new Timer(OnRefreshTimer, null, TimeSpan.FromMinutes(5L), TimeSpan.FromMinutes(5L));
		_studioWatchTimer = new Timer(OnStudioWatch, null, TimeSpan.FromSeconds(4L), TimeSpan.FromSeconds(4L));
	}

	private void OnClientReady(object sender, ReadyMessage e)
	{
		App.Logger.WriteLine("DiscordRichPresence", $"Ready: {e.User} ({e.User.ID})");
		_lastPresenceSignature = null;
		_ = SetCurrentGameAsync();
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
			assets?.LargeImageKey ?? string.Empty,
			assets?.LargeImageText ?? string.Empty,
			assets?.SmallImageKey ?? string.Empty,
			assets?.SmallImageText ?? string.Empty,
			presence.Timestamps?.Start?.Ticks.ToString() ?? string.Empty,
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
		Volatile.Write(ref _reconnectAttempt, 0);
		Volatile.Write(ref _reconnectPending, 0);
		_lastPresenceSignature = null;
		App.Logger.WriteLine("DiscordRichPresence", "Connected to Discord RPC");
	}

	private void OnClientClose(object sender, CloseMessage e)
	{
		App.Logger.WriteLine("DiscordRichPresence", $"Connection closed: {e.Reason} ({e.Code})");
		if (_disposed)
		{
			return;
		}
		if (Interlocked.CompareExchange(ref _reconnectPending, 1, 0) != 0)
			return;
		int attempt = Interlocked.Increment(ref _reconnectAttempt);
		if (attempt > MaxReconnectAttempts)
		{
			Interlocked.Exchange(ref _reconnectPending, 0);
			App.Logger.WriteLine("DiscordRichPresence", "Maximum reconnect attempts reached");
			return;
		}
		int delayMs = Math.Min(30000, 1000 * (int)Math.Pow(2.0, attempt - 1));
		_ = ReconnectAsync(attempt, delayMs, _lifetimeToken);
	}

	private async Task ReconnectAsync(int attempt, int delayMs, CancellationToken token)
	{
		try
		{
			await Task.Delay(delayMs, token).ConfigureAwait(continueOnCapturedContext: false);
			if (_disposed)
			{
				return;
			}
			_rpcClient.Initialize();
			App.Logger.WriteLine("DiscordRichPresence", $"Reinitialized RPC (attempt {attempt}).");
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("DiscordRichPresence", "Reconnect failed: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _reconnectPending, 0);
		}
	}

	private void OnRefreshTimer(object? state)
	{
		try
		{
			if (!_disposed)
			{
				_ = SetCurrentGameAsync();
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
		if (message.Command != "SetRichPresence" && message.Command != "SetLaunchData")
		{
			return;
		}
		if (_currentPresence == null || _originalSnapshot == null)
		{
			while (_messageQueue.Count >= MaxQueuedMessages)
			{
				_messageQueue.TryDequeue(out _);
			}
			_messageQueue.Enqueue(message);
			return;
		}
		if (message.Command == "SetLaunchData")
		{
			_currentPresence.Buttons = GetButtons();
		}
		else if (message.Command == "SetRichPresence")
		{
			if (!TryDeserializePresence(message.Data, out Voidstrap.Models.VoidstrapRPC.RichPresence presence) || presence == null)
			{
				return;
			}
			_currentPresence.Details = UpdateField(_currentPresence.Details, presence.Details, _originalSnapshot.Details, 128);
			_currentPresence.State = UpdateField(_currentPresence.State, presence.State, _originalSnapshot.State, 128);
			UpdateAssets(_currentPresence.Assets, _originalSnapshot, presence.SmallImage, small: true);
			UpdateAssets(_currentPresence.Assets, _originalSnapshot, presence.LargeImage, small: false);
		}
		if (implicitUpdate)
		{
			UpdatePresence();
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
		if (newValue.Length > maxLength)
		{
			return current;
		}
		return newValue;
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
		if (!string.IsNullOrEmpty(data.CustomKey))
		{
			if (small)
			{
				current.SmallImageKey = data.CustomKey;
			}
			else
			{
				current.LargeImageKey = data.CustomKey;
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
		if (!string.IsNullOrEmpty(data.HoverText))
		{
			if (small)
			{
				current.SmallImageText = data.HoverText;
			}
			else
			{
				current.LargeImageText = data.HoverText;
			}
		}
	}

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
		if (visible)
		{
			UpdatePresence(force: true);
			return;
		}
		try
		{
			_rpcClient.ClearPresence();
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
		bool studioRunning;
		try
		{
			Process[] processes = Process.GetProcessesByName("RobloxStudioBeta");
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
			_ = SetCurrentGameAsync();
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
			if (string.IsNullOrWhiteSpace(text2))
			{
				_cachedFlagCount = 0;
			}
			else
			{
				try
				{
					_cachedFlagCount = JsonSerializer.Deserialize<Dictionary<string, string>>(text2, JsonOptions.CaseInsensitive)?.Count ?? 0;
				}
				catch
				{
					_cachedFlagCount = 0;
				}
			}
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
			await SetIdlePresenceAsync().ConfigureAwait(false);
			return true;
		}

		ActivityData activity = _activityWatcher.Data;
		DateTime started = activity.RootActivity?.TimeJoined ?? activity.TimeJoined;
		int totalFlags = App.Settings.Prop.FFlagRPCDisplayer ? LoadFlags() : 0;
		if (activity.UniverseDetails == null && activity.UniverseId > 0)
		{
			try
			{
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
				timeout.CancelAfter(TimeSpan.FromSeconds(15));
				await UniverseDetails.FetchSingle(activity.UniverseId, timeout.Token).ConfigureAwait(false);
				activity.UniverseDetails = UniverseDetails.LoadFromCache(activity.UniverseId);
			}
			catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
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
		string placeName = await GetPlaceNameAsync(activity.PlaceId, _lifetimeToken).ConfigureAwait(false);
		string shownName = string.IsNullOrWhiteSpace(placeName) ? rootName : placeName;
		bool unlistedExperience = string.IsNullOrWhiteSpace(shownName);
		if (unlistedExperience)
		{
			shownName = "Private experience";
			App.Logger.WriteLine(LOG_IDENT, "No public name for universe " + activity.UniverseId + ", treating it as a private experience.");
		}
		if (!string.IsNullOrWhiteSpace(App.Settings.Prop.CustomGameName))
			shownName = App.Settings.Prop.CustomGameName;
		(string cleanName, string? betaTag) = ExtractBetaTag(shownName, universe?.Data?.Description);
		string reservedName = ExtractReservedServerName(activity.RPCLaunchData);
		string serverName = activity.ServerType switch
		{
			ServerType.Private => "Private Server",
			ServerType.Reserved when reservedName.Length > 0 => "Reserved Server: " + reservedName,
			ServerType.Reserved => "Reserved Server",
			_ => "Public Server"
		};

		var details = new List<string>();
		if (App.Settings.Prop.GameNameChecked)
			details.Add(cleanName);
		if (App.Settings.Prop.GameStatusChecked)
			details.Add(serverName);
		if (App.Settings.Prop.ServerLocationGame)
		{
			string? location = await activity.QueryServerLocation().ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(location))
				details.Add(location);
		}
		string detailText = string.Join(" | ", details);
		if (!string.IsNullOrWhiteSpace(betaTag))
			detailText = detailText.Length == 0 ? betaTag : detailText + " " + betaTag;

		string creator = universe?.Data?.Creator?.Name ?? string.Empty;
		bool verified = universe?.Data?.Creator?.HasVerifiedBadge ?? false;
		string stateText = App.Settings.Prop.GameCreatorChecked && creator.Length > 0 ? "by " + creator + (verified ? " ☑️" : string.Empty) : string.Empty;
		if (App.Settings.Prop.FFlagRPCDisplayer)
			stateText = stateText.Length == 0 ? $"FFlags: {totalFlags}" : $"{stateText} | FFlags: {totalFlags}";

		(string smallImage, string smallText) = await GetSmallImageAsync(activity).ConfigureAwait(false);
		string largeImage = !string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon) ? App.Settings.Prop.UseCustomIcon : App.Settings.Prop.GameIconChecked ? universe?.Thumbnail?.ImageUrl ?? string.Empty : string.Empty;
		if (string.IsNullOrWhiteSpace(largeImage) && App.Settings.Prop.GameIconChecked && string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon))
		{
			largeImage = await FetchPlaceIconAsync(activity.PlaceId).ConfigureAwait(false);
		}
		string largeText = string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon) && App.Settings.Prop.GameIconChecked ? shownName : string.Empty;
		_currentPresence = new DiscordRPC.RichPresence
		{
			Details = detailText,
			State = stateText,
			StatusDisplay = string.IsNullOrWhiteSpace(detailText) ? DiscordRPC.StatusDisplayType.Name : DiscordRPC.StatusDisplayType.Details,
			Timestamps = new Timestamps { Start = started.ToUniversalTime() },
			Buttons = GetButtons(),
			Assets = new Assets
			{
				LargeImageKey = largeImage,
				LargeImageText = largeText,
				SmallImageKey = smallImage,
				SmallImageText = smallText
			}
		};
		_originalSnapshot = new OriginalSnapshot
		{
			Details = detailText,
			State = stateText,
			LargeImageKey = largeImage,
			LargeImageText = largeText,
			SmallImageKey = smallImage,
			SmallImageText = smallText
		};
		while (_messageQueue.TryDequeue(out Message? queued))
			ProcessRPCMessage(queued, implicitUpdate: false);
		bool joined = Interlocked.Exchange(ref _joinPresenceUpdatePending, 0) == 1;
		string signature = BuildPresenceSignature(_currentPresence);
		if (!joined && signature == _lastPresenceSignature)
			return true;
		_lastPresenceSignature = signature;
		UpdatePresence(force: joined);
		string status = "Updated presence for " + detailText;
		App.Logger.WriteLine(LOG_IDENT, status);
		if (joined)
			PublishLaunchStatus(status);
		return true;
	}

	private static async Task<string> GetPlaceNameAsync(long placeId, CancellationToken token)
	{
		if (placeId <= 0)
			return string.Empty;
		if (PlaceNames.TryGetValue(placeId, out var cached) && cached.Expires > DateTime.UtcNow)
			return cached.Name;
		try
		{
			string url = "https://games.roblox.com/v1/games/multiget-place-details?placeIds=" + placeId;
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			string? cookie = RobloxCookie.Get();
			if (!string.IsNullOrEmpty(cookie))
				request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			request.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
			using HttpResponseMessage response = await App.HttpClient.SendAsync(request, token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				StorePlaceName(placeId, string.Empty, DateTime.UtcNow.AddMinutes(2));
				return string.Empty;
			}
			using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
			using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
			JsonElement data = json.RootElement.GetProperty("data");
			string name = data.GetArrayLength() == 0 ? string.Empty : data[0].GetProperty("name").GetString() ?? string.Empty;
			StorePlaceName(placeId, name, DateTime.UtcNow.AddHours(name.Length > 0 ? 6 : 1));
			return name;
		}
		catch
		{
			StorePlaceName(placeId, string.Empty, DateTime.UtcNow.AddMinutes(2));
			return string.Empty;
		}
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

	private async Task<bool> SetCurrentGameLegacy()
	{
		if (_disposed)
		{
			return false;
		}
		if (!_activityWatcher.InGame)
		{
			await SetIdlePresenceAsync().ConfigureAwait(continueOnCapturedContext: false);
			return true;
		}
		ActivityData activity = _activityWatcher.Data;
		if (activity == null)
		{
			App.Logger.WriteLine("DiscordRichPresence", "Activity data is null, skipping presence update.");
			return false;
		}
		DateTime timeStarted = activity.RootActivity?.TimeJoined ?? activity.TimeJoined;
		long placeId = activity.PlaceId;
		bool teleported = _previousPlaceId.HasValue && _previousPlaceId.Value != placeId;
		_previousPlaceId = placeId;
		int totalFlags = 0;
		if (App.Settings.Prop.FFlagRPCDisplayer)
		{
			totalFlags = LoadFlags();
		}
		if (activity.UniverseDetails == null)
		{
			try
			{
				using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
				timeout.CancelAfter(TimeSpan.FromSeconds(15L));
				await UniverseDetails.FetchSingle(activity.UniverseId, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
				activity.UniverseDetails = UniverseDetails.LoadFromCache(activity.UniverseId);
			}
			catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
			{
				return false;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("DiscordRichPresence", "Failed to fetch universe details: " + ex.Message);
			}
		}
		UniverseDetails universe = activity.UniverseDetails;
		if (universe?.Data == null)
		{
			App.Logger.WriteLine("DiscordRichPresence", "Universe details unavailable, using private experience fallback for place " + placeId);
			return await SetPrivateExperiencePresenceAsync(activity, placeId, totalFlags).ConfigureAwait(continueOnCapturedContext: false);
		}
		(string, string) tuple = await GetSmallImageAsync(activity).ConfigureAwait(continueOnCapturedContext: false);
		string smallImage = tuple.Item1;
		string smallText = tuple.Item2;
		string serverPrivacy = activity.ServerType switch
		{
			ServerType.Private => "Private Server", 
			ServerType.Reserved => "Reserved Server", 
			_ => "Public Server", 
		};
		(string, string) tuple2 = ExtractBetaTag(universe.Data.Name, universe.Data.Description);
		string item = tuple2.Item1;
		string betaTag = tuple2.Item2;
		string universeName = ((!string.IsNullOrWhiteSpace(App.Settings.Prop.CustomGameName)) ? App.Settings.Prop.CustomGameName : ((item.Length < 2) ? (item + "⠀⠀⠀") : item));
		if (teleported)
		{
			universeName = "Teleported to " + universeName;
		}
		string text = string.Empty;
		if (App.Settings.Prop.ServerLocationGame)
		{
			try
			{
				text = await activity.QueryServerLocation().ConfigureAwait(continueOnCapturedContext: false);
			}
			catch
			{
				text = "Unknown Location";
			}
		}
		List<string> list = new List<string>();
		if (App.Settings.Prop.GameNameChecked && !string.IsNullOrWhiteSpace(universeName))
		{
			list.Add(universeName);
		}
		if (App.Settings.Prop.GameStatusChecked)
		{
			list.Add(serverPrivacy);
		}
		if (App.Settings.Prop.ServerLocationGame && !string.IsNullOrWhiteSpace(text))
		{
			list.Add(text);
		}
		string text2 = string.Join(" • ", list);
		if (!string.IsNullOrEmpty(betaTag))
		{
			text2 = (string.IsNullOrEmpty(text2) ? betaTag : (text2 + " " + betaTag));
		}
		string text3 = universe.Data.Creator?.Name ?? "";
		bool flag = universe.Data.Creator?.HasVerifiedBadge ?? false;
		string text4 = ((App.Settings.Prop.GameCreatorChecked && !string.IsNullOrEmpty(text3)) ? ("by " + text3 + (flag ? " ☑\ufe0f" : "")) : "");
		if (App.Settings.Prop.FFlagRPCDisplayer)
		{
			text4 = (string.IsNullOrWhiteSpace(text4) ? $"FFlags: {totalFlags}" : $"{text4} • FFlags: {totalFlags}");
		}
		string largeImageKey = ((!string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon)) ? App.Settings.Prop.UseCustomIcon : ((!App.Settings.Prop.GameIconChecked) ? "" : (universe.Thumbnail?.ImageUrl ?? "")));
		string largeImageText = ((!string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon)) ? "" : ((App.Settings.Prop.GameIconChecked && App.Settings.Prop.GameNameChecked) ? (universe.Data.Name ?? "") : ""));
		if (_currentPresence != null)
		{
			_currentPresence.Details = text2;
			_currentPresence.State = text4;
			_currentPresence.Assets.LargeImageKey = largeImageKey;
			_currentPresence.Assets.LargeImageText = largeImageText;
			_currentPresence.Assets.SmallImageKey = smallImage;
			_currentPresence.Assets.SmallImageText = smallText;
			_currentPresence.Buttons = GetButtons();
			_currentPresence.Timestamps.Start = timeStarted.ToUniversalTime();
		}
		else
		{
			_currentPresence = new DiscordRPC.RichPresence
			{
				Details = text2,
				State = text4,
				StatusDisplay = string.IsNullOrWhiteSpace(text2) ? DiscordRPC.StatusDisplayType.Name : DiscordRPC.StatusDisplayType.Details,
				Timestamps = new Timestamps
				{
					Start = timeStarted.ToUniversalTime()
				},
				Buttons = GetButtons(),
				Assets = new Assets
				{
					LargeImageKey = largeImageKey,
					LargeImageText = largeImageText,
					SmallImageKey = smallImage,
					SmallImageText = smallText
				}
			};
		}
		_originalSnapshot = new OriginalSnapshot
		{
			Details = text2,
			State = text4,
			LargeImageKey = largeImageKey,
			LargeImageText = largeImageText,
			SmallImageKey = smallImage,
			SmallImageText = smallText
		};
		Message result;
		while (_messageQueue.TryDequeue(out result))
		{
			ProcessRPCMessage(result, implicitUpdate: false);
		}
		UpdatePresence(force: true);
		string message = $"Updated presence for {text2} with FFlags: {totalFlags}";
		App.Logger.WriteLine("DiscordRichPresence", message);
		if (Interlocked.Exchange(ref _joinPresenceUpdatePending, 0) == 1)
		{
			PublishLaunchStatus(message);
		}
		return true;
	}

	private async Task SetIdlePresenceAsync()
	{
		try
		{
			_messageQueue.Clear();
			_previousPlaceId = null;
			string smallImage = "voidstrap";
			string smallText = "Voidstrap";
			try
			{
				ActivityData data = _activityWatcher.Data;
				if (data != null && data.UserId > 0)
				{
					(smallImage, smallText) = await GetSmallImageAsync(data).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			catch
			{
			}
			string idleIconUrl = GetIdleIconUrl();
			if (_currentPresence == null)
			{
				_currentPresence = new DiscordRPC.RichPresence
				{
					Details = "Inside Voidstrap",
					State = "Browsing Roblox",
					Timestamps = new Timestamps
					{
						Start = _sessionStart
					},
					Buttons = Array.Empty<Button>(),
					Assets = new Assets
					{
						LargeImageKey = idleIconUrl,
						LargeImageText = "Roblox",
						SmallImageKey = smallImage,
						SmallImageText = smallText
					}
				};
			}
			else
			{
				_currentPresence.Details = "Inside Voidstrap";
				_currentPresence.State = "Browsing Roblox";
				_currentPresence.Assets.LargeImageKey = idleIconUrl;
				_currentPresence.Assets.LargeImageText = "Roblox";
				_currentPresence.Assets.SmallImageKey = smallImage;
				_currentPresence.Assets.SmallImageText = smallText;
				_currentPresence.Buttons = Array.Empty<Button>();
				_currentPresence.Timestamps = new Timestamps
				{
					Start = _sessionStart
				};
			}
			_originalSnapshot = new OriginalSnapshot
			{
				Details = "Inside Voidstrap",
				State = "Browsing Roblox",
				LargeImageKey = idleIconUrl,
				LargeImageText = "Roblox",
				SmallImageKey = smallImage,
				SmallImageText = smallText
			};
			string signature = BuildPresenceSignature(_currentPresence);
			if (signature == _lastPresenceSignature)
				return;
			_lastPresenceSignature = signature;
			UpdatePresence(force: true);
			App.Logger.WriteLine("DiscordRichPresence", "Set idle presence (Inside Voidstrap).");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("DiscordRichPresence", "SetIdlePresenceAsync failed: " + ex.Message);
		}
	}

	private static readonly Dictionary<long, string> _placeIconCache = new Dictionary<long, string>();

	private async Task<string> FetchPlaceIconAsync(long placeId)
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
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
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
		catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
		{
			return string.Empty;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("DiscordRichPresence", "Place icon fetch failed: " + ex.Message);
		}
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
			: "Private experience";
		string serverPrivacy = activity.ServerType switch
		{
			ServerType.Private => "Private Server",
			ServerType.Reserved => "Reserved Server",
			_ => "Public Server",
		};

		List<string> stateParts = new List<string>();
		if (App.Settings.Prop.GameStatusChecked)
		{
			stateParts.Add(serverPrivacy);
		}
		if (App.Settings.Prop.FFlagRPCDisplayer)
		{
			stateParts.Add("FFlags: " + totalFlags);
		}
		string stateText = string.Join(" | ", stateParts);

		string largeImage = string.Empty;
		if (!string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon))
		{
			largeImage = App.Settings.Prop.UseCustomIcon;
		}
		else if (App.Settings.Prop.GameIconChecked)
		{
			largeImage = await FetchPlaceIconAsync(placeId).ConfigureAwait(continueOnCapturedContext: false);
		}
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
				LargeImageKey = largeImage,
				LargeImageText = App.Settings.Prop.GameIconChecked ? shownName : string.Empty,
				SmallImageKey = smallImage,
				SmallImageText = smallText,
			},
		};
		_originalSnapshot = new OriginalSnapshot
		{
			Details = _currentPresence.Details,
			State = _currentPresence.State,
			LargeImageKey = largeImage,
			LargeImageText = _currentPresence.Assets.LargeImageText,
			SmallImageKey = smallImage,
			SmallImageText = smallText,
		};
		while (_messageQueue.TryDequeue(out Message? queued))
		{
			ProcessRPCMessage(queued, implicitUpdate: false);
		}
		UpdatePresence(force: true);
		App.Logger.WriteLine(LOG_IDENT, "Updated presence for private experience " + placeId);
		return true;
	}

	private static readonly (string Pattern, string Tag)[] WipMarkers = BuildWipMarkers();

	private static readonly Regex WipLeftoverSeparators = new Regex("\\s*[-|:~/,]+\\s*$", RegexOptions.Compiled);

	private static readonly Regex WipEmptyBrackets = new Regex("[\\[\\(\\{]\\s*[\\]\\)\\}]", RegexOptions.Compiled);

	private static readonly Regex WipWhitespace = new Regex("\\s{2,}", RegexOptions.Compiled);

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

	private static (string CleanName, string? Tag) ExtractBetaTag(string gameName, string? description)
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

	private async Task<(string key, string text)> GetSmallImageAsync(ActivityData activity)
	{
		if (!App.Settings.Prop.ShowAccountOnRichPresence)
		{
			return (key: "voidstrap", text: "Voidstrap");
		}
		try
		{
			UserDetails userDetails = await UserDetails.Fetch(activity.UserId, _lifetimeToken).ConfigureAwait(continueOnCapturedContext: false);
			if (userDetails?.Data == null)
			{
				return (key: "voidstrap", text: "Voidstrap");
			}
			return (key: userDetails.Thumbnail?.ImageUrl ?? "voidstrap", text: userDetails.Data.DisplayName + " (@" + userDetails.Data.Name + ")");
		}
		catch
		{
			return (key: "voidstrap", text: "Voidstrap");
		}
	}

	public Button[] GetButtons()
	{
		ActivityData data = _activityWatcher.Data;
		List<Button> list = new List<Button>();
		if (data == null)
		{
			return list.ToArray();
		}
		if (!App.Settings.Prop.HideRPCButtons)
		{
			string text = null;
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
			if (!string.IsNullOrEmpty(text))
			{
				list.Add(new Button
				{
					Label = "Join server",
					Url = text
				});
			}
		}
		list.Add(new Button
		{
			Label = "Game Page",
			Url = $"https://www.roblox.com/games/{data.PlaceId}"
		});
		return list.ToArray();
	}

	public Voidstrap.Models.PresenceSnapshot GetPresenceSnapshot()
	{
		DiscordRPC.RichPresence? presence = _currentPresence;
		if (_disposed || presence == null)
		{
			return new Voidstrap.Models.PresenceSnapshot { Connected = !_disposed && _rpcClient.IsInitialized, Active = false };
		}
		Voidstrap.Models.PresenceSnapshot snapshot = new Voidstrap.Models.PresenceSnapshot
		{
			Connected = _rpcClient.IsInitialized,
			Active = true,
			Details = presence.Details ?? string.Empty,
			State = presence.State ?? string.Empty,
			LargeImageKey = presence.Assets?.LargeImageKey ?? string.Empty,
			LargeImageText = presence.Assets?.LargeImageText ?? string.Empty,
			SmallImageKey = presence.Assets?.SmallImageKey ?? string.Empty,
			SmallImageText = presence.Assets?.SmallImageText ?? string.Empty,
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
			snapshot.Buttons.Add(new Voidstrap.Models.PresenceButton { Label = button.Label ?? string.Empty, Url = button.Url ?? string.Empty });
		}
		return snapshot;
	}

	public void UpdatePresence(bool force = false)
	{
		if (_disposed)
		{
			return;
		}
		if (_currentPresence == null)
		{
			try
			{
				_rpcClient.ClearPresence();
			}
			catch
			{
			}
			_pendingPresence = null;
		}
		else
		{
			if (!_visible)
			{
				return;
			}
			if (force || !(DateTime.UtcNow - _lastPresenceUpdate < _updateCooldown))
			{
				_lastPresenceUpdate = DateTime.UtcNow;
				_pendingPresence = null;
				try
				{
					_rpcClient.SetPresence(_currentPresence);
					return;
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("DiscordRichPresence", "SetPresence failed: " + ex.Message);
					return;
				}
			}
			_pendingPresence = _currentPresence;
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
				if (!_disposed)
				{
					DiscordRPC.RichPresence pendingPresence = _pendingPresence;
					if (pendingPresence != null && _visible)
					{
						_lastPresenceUpdate = DateTime.UtcNow;
						_pendingPresence = null;
						try
						{
							_rpcClient.SetPresence(pendingPresence);
							return;
						}
						catch (Exception ex)
						{
							App.Logger.WriteLine("DiscordRichPresence", "Flushed SetPresence failed: " + ex.Message);
							return;
						}
					}
				}
			}
			catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
			{
			}
			finally
			{
				Interlocked.Exchange(ref _flushScheduled, 0);
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
			try
			{
				_rpcClient.OnReady -= OnClientReady;
				_rpcClient.OnPresenceUpdate -= OnClientPresenceUpdate;
				_rpcClient.OnError -= OnClientError;
				_rpcClient.OnConnectionEstablished -= OnClientConnectionEstablished;
				_rpcClient.OnClose -= OnClientClose;
			}
			catch
			{
			}
			try
			{
				_rpcClient.ClearPresence();
			}
			catch
			{
			}
			try
			{
				_rpcClient.Dispose();
			}
			catch
			{
			}
			_messageQueue.Clear();
			_currentPresence = null;
			_pendingPresence = null;
			_originalSnapshot = null;
			_lifetimeCancellation.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}
