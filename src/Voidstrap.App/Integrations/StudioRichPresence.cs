using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiscordRPC;

namespace Voidstrap.Integrations;

public sealed class StudioRichPresence : IDisposable
{
	private const string LogTag = "StudioRPC";
	private const string StudioIconUrl = "https://images.rbxcdn.com/905bd722ee0a6ceda3caacde54c0b081.png";
	private const int MaxIconCacheEntries = 64;
	private const int MaxApiResponseBytes = 1024 * 1024;
	private static readonly HttpClient Http = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(10));
	private static readonly Dictionary<long, string> IconCache = [];
	private static readonly Regex PlaceIdPattern = new(@"\bplaceId\b[^0-9]{0,12}(\d{1,19})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex UniverseIdPattern = new(@"\buniverseId\b[^0-9]{0,12}(\d{1,19})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex ScriptLinePattern = new(@"(?:line count|lines)\D{0,8}(\d{1,9})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private readonly CancellationTokenSource _lifetimeCancellation = new();
	private readonly CancellationToken _lifetimeToken;
	private DiscordRpcClient? _client;
	private System.Timers.Timer? _pollTimer;
	private bool _disposed;
	private bool _studioRunning;
	private DateTime _sessionStart = DateTime.UtcNow;
	private string _place = "";
	private long _placeId;
	private long _universeId;
	private string _script = "";
	private int _scriptLines;
	private string _mode = "";
	private string _iconUrl = "";
	private int _iconFetching;
	private int _polling;
	private string _lastSignature = "";
	private string _lastLogPath = "";
	private DateTime _lastLogWriteUtc;
	private LogState _lastLogState = new();

	private sealed record LogState(long PlaceId = 0, long UniverseId = 0, int ScriptLines = 0);

	public StudioRichPresence()
	{
		_lifetimeToken = _lifetimeCancellation.Token;
		try
		{
			Start();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException(LogTag, ex);
		}
	}

	private void Start()
	{
		string clientId = string.IsNullOrWhiteSpace(App.Settings.Prop.StudioRpcClientId)
			? "1005469189907173486"
			: App.Settings.Prop.StudioRpcClientId.Trim();
		_client = new DiscordRpcClient(clientId);
		_client.Initialize();
		App.Logger.WriteLine(LogTag, $"Studio RPC initialized, client {clientId}");
		_pollTimer = new System.Timers.Timer(4000) { AutoReset = true };
		_pollTimer.Elapsed += OnPollTimer;
		_pollTimer.Start();
		Poll();
	}

	private void OnPollTimer(object? sender, System.Timers.ElapsedEventArgs e)
	{
		Poll();
	}

	private void Poll()
	{
		if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
		{
			return;
		}
		try
		{
			PollCore();
		}
		finally
		{
			Interlocked.Exchange(ref _polling, 0);
		}
	}

	private void PollCore()
	{
		if (_disposed)
		{
			return;
		}
		using Process? studio = FindStudioProcess();
		if (studio == null)
		{
			HandleStudioClosed();
			return;
		}
		if (!_studioRunning)
		{
			_studioRunning = true;
			_sessionStart = DateTime.UtcNow;
			App.Logger.WriteLine(LogTag, "Roblox Studio detected");
		}
		ReadWindowState(studio);
		ReadLogState();
		ApplyPluginState();
		EnsureIcon();
		UpdatePresence();
	}

	private void HandleStudioClosed()
	{
		if (!_studioRunning)
		{
			return;
		}
		_studioRunning = false;
		_place = "";
		_script = "";
		_placeId = 0;
		_universeId = 0;
		_scriptLines = 0;
		_mode = "";
		_iconUrl = "";
		_lastSignature = "";
		App.Logger.WriteLine(LogTag, "Roblox Studio closed");
		try
		{
			_client?.ClearPresence();
		}
		catch
		{
		}
	}

	private static Process? FindStudioProcess()
	{
		Process[] processes;
		try
		{
			processes = Process.GetProcessesByName("RobloxStudioBeta");
		}
		catch
		{
			return null;
		}
		Process? selected = null;
		foreach (Process process in processes)
		{
			try
			{
				if (selected == null || (selected.MainWindowHandle == IntPtr.Zero && process.MainWindowHandle != IntPtr.Zero))
				{
					selected?.Dispose();
					selected = process;
					continue;
				}
			}
			catch
			{
			}
			process.Dispose();
		}
		return selected;
	}

	private void ReadWindowState(Process studio)
	{
		string title;
		try
		{
			title = studio.MainWindowTitle ?? "";
		}
		catch
		{
			return;
		}
		if (title.Length == 0)
		{
			return;
		}
		_mode = ResolveMode(title);
		string cleaned = Regex.Replace(title, @"\s*[-–—]\s*Roblox Studio\s*$", "", RegexOptions.IgnoreCase);
		string[] parts = cleaned.Split([" - ", " – ", " — "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		List<string> meaningful = parts.Where(part => !IsModeText(part)).ToList();
		if (meaningful.Count > 0)
		{
			_place = meaningful[^1];
		}
		_script = meaningful.Count > 1 ? meaningful[0] : "";
	}

	private void ApplyPluginState()
	{
		Voidstrap.Integrations.Studio.StudioState? state = Voidstrap.Integrations.Studio.StudioBridge.GetFreshState(TimeSpan.FromSeconds(12));
		if (state == null || !state.Sharing)
		{
			return;
		}
		if (!string.IsNullOrWhiteSpace(state.Place))
		{
			_place = state.Place;
		}
		if (state.PlaceId > 0)
		{
			_placeId = state.PlaceId;
		}
		if (state.UniverseId > 0)
		{
			_universeId = state.UniverseId;
		}
		if (state.ScriptLines > 0)
		{
			_scriptLines = state.ScriptLines;
		}
		if (!string.IsNullOrWhiteSpace(state.Mode))
		{
			_mode = state.Mode;
		}
		_script = state.Script ?? "";
	}

	private static string ResolveMode(string title)
	{
		if (title.Contains("Team Create", StringComparison.OrdinalIgnoreCase))
		{
			return "Team Create";
		}
		if (title.Contains("Playtest", StringComparison.OrdinalIgnoreCase) || title.Contains("Play Test", StringComparison.OrdinalIgnoreCase))
		{
			return "Playtesting";
		}
		if (title.Contains("Testing", StringComparison.OrdinalIgnoreCase) || title.Contains(" Test ", StringComparison.OrdinalIgnoreCase))
		{
			return "Testing";
		}
		return "";
	}

	private static bool IsModeText(string value)
	{
		return value.Contains("Team Create", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("Playtest", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("Testing", StringComparison.OrdinalIgnoreCase);
	}

	private void ReadLogState()
	{
		try
		{
			string logDirectory = Path.Combine(Paths.LocalAppData, "Roblox", "logs");
			FileInfo? latest = new DirectoryInfo(logDirectory)
				.EnumerateFiles("*.log")
				.Where(file => file.Name.Contains("Studio", StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(file => file.LastWriteTimeUtc)
				.FirstOrDefault();
			if (latest == null)
			{
				return;
			}
			if (string.Equals(_lastLogPath, latest.FullName, StringComparison.OrdinalIgnoreCase) && _lastLogWriteUtc == latest.LastWriteTimeUtc)
			{
				ApplyLogState(_lastLogState);
				return;
			}
			string tail = ReadTail(latest.FullName, 262144);
			LogState state = new(
				FindLastInt64(PlaceIdPattern, tail),
				FindLastInt64(UniverseIdPattern, tail),
				(int)Math.Min(int.MaxValue, FindLastInt64(ScriptLinePattern, tail)));
			_lastLogPath = latest.FullName;
			_lastLogWriteUtc = latest.LastWriteTimeUtc;
			_lastLogState = state;
			ApplyLogState(state);
		}
		catch
		{
		}
	}

	private void ApplyLogState(LogState state)
	{
		if (state.PlaceId > 0)
		{
			_placeId = state.PlaceId;
		}
		if (state.UniverseId > 0)
		{
			_universeId = state.UniverseId;
		}
		if (state.ScriptLines > 0)
		{
			_scriptLines = state.ScriptLines;
		}
	}

	private static string ReadTail(string path, int maxBytes)
	{
		using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		if (stream.Length > maxBytes)
		{
			stream.Seek(-maxBytes, SeekOrigin.End);
		}
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}

	private static long FindLastInt64(Regex pattern, string value)
	{
		MatchCollection matches = pattern.Matches(value);
		for (int index = matches.Count - 1; index >= 0; index--)
		{
			if (long.TryParse(matches[index].Groups[1].Value, out long parsed))
			{
				return parsed;
			}
		}
		return 0;
	}

	private void EnsureIcon()
	{
		long universeId = _universeId;
		long placeId = _placeId;
		long key = universeId > 0 ? universeId : placeId > 0 ? -placeId : 0;
		if (key == 0)
		{
			_iconUrl = "";
			return;
		}
		lock (IconCache)
		{
			if (IconCache.TryGetValue(key, out string? cached))
			{
				_iconUrl = cached;
				return;
			}
		}
		if (Interlocked.CompareExchange(ref _iconFetching, 1, 0) == 0)
		{
			_ = FetchIconAsync(key, universeId, placeId, _lifetimeToken);
		}
	}

	private async Task FetchIconAsync(long key, long universeId, long placeId, CancellationToken token)
	{
		try
		{
			if (universeId <= 0 && placeId > 0)
			{
				string universeJson = await GetJsonAsync($"https://apis.roblox.com/universes/v1/places/{placeId}/universe", token);
				using System.Text.Json.JsonDocument universeDoc = System.Text.Json.JsonDocument.Parse(universeJson);
				if (universeDoc.RootElement.TryGetProperty("universeId", out System.Text.Json.JsonElement universeValue) && universeValue.TryGetInt64(out long parsedUniverse))
				{
					universeId = parsedUniverse;
				}
			}
			string url = "";
			if (universeId > 0)
			{
				string json = await GetJsonAsync($"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&size=512x512&format=Png&isCircular=false", token);
				using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
				if (document.RootElement.TryGetProperty("data", out System.Text.Json.JsonElement data) && data.ValueKind == System.Text.Json.JsonValueKind.Array && data.GetArrayLength() > 0 && data[0].TryGetProperty("imageUrl", out System.Text.Json.JsonElement image))
				{
					url = image.GetString() ?? "";
				}
			}
			lock (IconCache)
			{
				IconCache[key] = url;
				while (IconCache.Count > MaxIconCacheEntries)
				{
					IconCache.Remove(IconCache.Keys.First());
				}
			}
			if (!_disposed)
			{
				_iconUrl = url;
				UpdatePresence();
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogTag, "Icon fetch failed: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _iconFetching, 0);
		}
	}

	private static async Task<string> GetJsonAsync(string url, CancellationToken token)
	{
		using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		return await Utility.Http.ReadStringBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false);
	}

	private void UpdatePresence()
	{
		if (_disposed || _client == null || !_studioRunning)
		{
			return;
		}
		var settings = App.Settings.Prop;
		bool showPlace = settings.StudioRpcShowPlace && !string.IsNullOrWhiteSpace(_place);
		string details = showPlace ? FormatActivity(_mode, _place) : "In Roblox Studio";
		List<string> parts = [];
		if (settings.StudioRpcShowState && _mode.Length > 0 && !details.Contains(_mode, StringComparison.OrdinalIgnoreCase))
		{
			parts.Add(_mode);
		}
		if (settings.StudioRpcShowScript && _script.Length > 0)
		{
			parts.Add(_scriptLines > 0 ? $"{_script}, {_scriptLines} lines" : _script);
		}
		if (parts.Count == 0)
		{
			parts.Add(_script.Length == 0 ? "Editing UI" : "In Roblox Studio");
		}
		string state = string.Join(", ", parts);
		string largeImage = showPlace && _iconUrl.Length > 0 ? _iconUrl : StudioIconUrl;
		string largeText = showPlace ? _place : "Roblox Studio";
		List<DiscordRPC.Button> buttons = [];
		if (showPlace && _placeId > 0)
		{
			buttons.Add(new DiscordRPC.Button { Label = "View Game", Url = $"https://www.roblox.com/games/{_placeId}" });
		}
		buttons.Add(new DiscordRPC.Button { Label = "Get Voidstrap", Url = App.WebsiteBaseUrl });
		string signature = string.Join("|", details, state, largeImage, largeText, _placeId);
		if (signature == _lastSignature)
		{
			return;
		}
		_lastSignature = signature;
		try
		{
			_client.SetPresence(new DiscordRPC.RichPresence
			{
				Details = Trim(details, 128),
				State = Trim(state, 128),
				StatusDisplay = StatusDisplayType.Details,
				Timestamps = new Timestamps { Start = _sessionStart },
				Assets = new Assets
				{
					LargeImageKey = largeImage,
					LargeImageText = Trim(largeText, 128),
					SmallImageKey = StudioIconUrl,
					SmallImageText = "Roblox Studio"
				},
				Buttons = buttons.ToArray()
			});
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogTag, "Presence update failed: " + ex.Message);
		}
	}

	private static string FormatActivity(string mode, string place)
	{
		return mode switch
		{
			"Playtesting" => "Playtesting " + place,
			"Testing" => "Testing " + place,
			"Team Create" => "Editing " + place + " in Team Create",
			_ => "Editing " + place
		};
	}

	private static string Trim(string value, int maximum)
	{
		return value.Length <= maximum ? value : value[..maximum];
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_pollTimer?.Stop();
		if (_pollTimer != null)
		{
			_pollTimer.Elapsed -= OnPollTimer;
			_pollTimer.Dispose();
		}
		_lifetimeCancellation.Cancel();
		try
		{
			_client?.ClearPresence();
		}
		catch
		{
		}
		_client?.Dispose();
		_lifetimeCancellation.Dispose();
		_pollTimer = null;
		_client = null;
		GC.SuppressFinalize(this);
	}
}
