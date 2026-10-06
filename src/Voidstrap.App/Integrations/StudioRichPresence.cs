using Voidstrap.Utility;
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

public sealed partial class StudioRichPresence : IDisposable
{
	[GeneratedRegex(@"\s*[-–—]\s*Roblox Studio\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex StudioTitleSuffixPattern { get; }

	private const string LogTag = "StudioRPC";
	private const string StudioIconUrl = "https://images.rbxcdn.com/905bd722ee0a6ceda3caacde54c0b081.png";
	private const int MaxIconCacheEntries = 64;
	private const int MaxApiResponseBytes = 1024 * 1024;
	private static readonly HttpClient Http = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(10));
	private static readonly Dictionary<long, string> IconCache = [];
	[GeneratedRegex(@"(?:line count|lines)\D{0,8}(\d{1,9})", RegexOptions.IgnoreCase)]
	private static partial Regex ScriptLinePattern { get; }
	private readonly CancellationTokenSource _lifetimeCancellation = new();
	private readonly CancellationToken _lifetimeToken;
	private DiscordRpcClient? _client;
	private string _clientId = "";
	private DateTime _retryAtUtc;
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
	private readonly StudioLogTracker _linuxLog = new();
	private string _creator = "";
	private long _namedUniverse;
	private int _nameFetching;
	private string _sessionKey = "";

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
		_clientId = string.IsNullOrWhiteSpace(App.Settings.Prop.StudioRpcClientId)
			? "1005469189907173486"
			: App.Settings.Prop.StudioRpcClientId.Trim();
		_pollTimer = new System.Timers.Timer(4000) { AutoReset = true };
		_pollTimer.Elapsed += OnPollTimer;
		_pollTimer.Start();
		Poll();
	}

	private void EnsureClient()
	{
		if (_disposed || _client != null || DateTime.UtcNow < _retryAtUtc || !DiscordIpc.TryFindPipe(out int pipe))
		{
			return;
		}
		DiscordRpcClient client = DiscordIpc.CreateClient(_clientId, pipe);
		client.OnConnectionFailed += OnConnectionFailed;
		_client = client;
		_lastSignature = "";
		try
		{
			client.Initialize();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogTag, "Discord connection failed: " + ex.Message);
			_retryAtUtc = DateTime.UtcNow + DiscordIpc.RetryDelay;
			ReleaseClient();
			return;
		}
		App.Logger.WriteLine(LogTag, $"Studio RPC initialized, client {_clientId}");
	}

	private void OnConnectionFailed(object sender, DiscordRPC.Message.ConnectionFailedMessage e)
	{
		_retryAtUtc = DateTime.UtcNow + DiscordIpc.RetryDelay;
		ReleaseClient();
	}

	private void ReleaseClient()
	{
		DiscordRpcClient? client = Interlocked.Exchange(ref _client, null);
		if (client == null)
		{
			return;
		}
		client.OnConnectionFailed -= OnConnectionFailed;
		DiscordIpc.Close(client);
	}

	private void OnPollTimer(object? sender, System.Timers.ElapsedEventArgs e)
	{
		try
		{
			Poll();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException(LogTag + "::OnPollTimer", ex);
		}
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
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			ReadLinuxState();
		}
		else
		{
			ReadWindowState(studio);
			ReadLogState();
		}
		ApplyPluginState();
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			EnsurePlaceName();
			TrackPlaceSession();
		}
		EnsureIcon();
		EnsureClient();
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
		_creator = "";
		_namedUniverse = 0;
		_sessionKey = "";
		_lastSignature = "";
		_linuxLog.Reset();
		App.Logger.WriteLine(LogTag, "Roblox Studio closed");
		try
		{
			_client?.ClearPresence();
		}
		catch
		{
		}
	}

	private static string SafeProcessName(Process process)
	{
		try
		{
			return process.ProcessName;
		}
		catch
		{
			return string.Empty;
		}
	}

	private static Process? FindStudioProcess()
	{
		Process[] processes;
		try
		{
			processes = Voidstrap.Utility.Platform.IsLinux
				? Process.GetProcesses().Where(static candidate => Voidstrap.Platform.Linux.StudioProcessNames.IsStudio(SafeProcessName(candidate))).ToArray()
				: Process.GetProcessesByName("RobloxStudioBeta");
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
		string cleaned = StudioTitleSuffixPattern.Replace(title, "");
		string[] parts = cleaned.Split([" - ", " – ", " — "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		List<string> meaningful = parts.Where(part => !IsModeText(part)).ToList();
		if (meaningful.Count > 0)
		{
			_place = meaningful[^1];
		}
		_script = meaningful.Count > 1 ? meaningful[0] : "";
	}

	private void ReadLinuxState()
	{
		try
		{
			FileInfo? latest = Voidstrap.Utility.VinegarPaths.LogDirectories
				.Where(Directory.Exists)
				.SelectMany(directory => new DirectoryInfo(directory).EnumerateFiles("*.log"))
				.Where(file => file.Name.Contains("Studio", StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(file => file.LastWriteTimeUtc)
				.FirstOrDefault();
			if (latest != null)
			{
				_linuxLog.Update(latest.FullName);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogTag, "The Studio log could not be read: " + ex.Message);
		}

		string title = Voidstrap.Platform.Linux.LinuxWindowInterop.FindStudioWindowTitle();
		string cleaned = StudioTitleSuffixPattern.Replace(title, "").Trim();
		if (string.Equals(cleaned, "Roblox Studio", StringComparison.OrdinalIgnoreCase))
		{
			cleaned = "";
		}
		string[] parts = cleaned.Split([" - ", " – ", " — "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		List<string> meaningful = parts.Where(part => !IsModeText(part)).ToList();
		string titleMode = ResolveMode(title);

		if (!_linuxLog.DocumentOpen && _linuxLog.HasEvents)
		{
			_place = "";
			_script = "";
			_placeId = 0;
			_universeId = 0;
			_mode = "";
			return;
		}

		_placeId = _linuxLog.PlaceId;
		_universeId = _linuxLog.UniverseId;
		_place = meaningful.Count > 0 ? meaningful[^1] : _namedUniverse == _universeId && _universeId > 0 ? _place : "";
		_script = meaningful.Count > 1 ? meaningful[0] : "";
		_mode = titleMode.Length > 0 ? titleMode : _linuxLog.Mode;
	}

	private void EnsurePlaceName()
	{
		long universeId = _universeId;
		if (universeId <= 0 || _namedUniverse == universeId || Interlocked.CompareExchange(ref _nameFetching, 1, 0) != 0)
		{
			return;
		}
		_ = FetchPlaceNameAsync(universeId, _lifetimeToken);
	}

	private async Task FetchPlaceNameAsync(long universeId, CancellationToken token)
	{
		try
		{
			if (Voidstrap.Models.Entities.UniverseDetails.LoadFromCache(universeId) == null)
			{
				await Voidstrap.Models.Entities.UniverseDetails.FetchSingle(universeId, token).ConfigureAwait(false);
			}
			Voidstrap.Models.Entities.UniverseDetails? details = Voidstrap.Models.Entities.UniverseDetails.LoadFromCache(universeId);
			if (_disposed || universeId != _universeId)
			{
				return;
			}
			_namedUniverse = universeId;
			_creator = details?.Data?.Creator?.Name ?? "";
			if (string.IsNullOrWhiteSpace(_place) && !string.IsNullOrWhiteSpace(details?.Data?.Name))
			{
				_place = details.Data.Name;
			}
			UpdatePresence();
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogTag, "The place name could not be fetched: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _nameFetching, 0);
		}
	}

	private void TrackPlaceSession()
	{
		string key = _placeId + ":" + (_linuxLog.DocumentOpen ? _mode : "start");
		if (key == _sessionKey)
		{
			return;
		}
		if (_sessionKey.Length > 0)
		{
			_sessionStart = DateTime.UtcNow;
		}
		_sessionKey = key;
		App.Logger.WriteLine(LogTag, _linuxLog.DocumentOpen && _placeId > 0
			? "Studio is " + (_mode.Length > 0 ? _mode.ToLowerInvariant() : "editing") + " place " + _placeId + (_universeId > 0 ? " in universe " + _universeId : "")
			: "Studio is on the start page");
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
		bool linux = Voidstrap.Utility.Platform.IsLinux;
		bool startPage = linux && _linuxLog.HasEvents && !_linuxLog.DocumentOpen;
		if (linux && showPlace && _creator.Length > 0)
		{
			details += " \u00b7 by " + _creator;
		}
		List<string> parts = [];
		if (settings.StudioRpcShowState && _mode.Length > 0 && !details.Contains(_mode, StringComparison.OrdinalIgnoreCase))
		{
			parts.Add(Voidstrap.Utility.RpcText.Mark(_mode));
		}
		if (settings.StudioRpcShowScript && _script.Length > 0)
		{
			parts.Add(_scriptLines > 0 ? $"{_script}, {_scriptLines} lines" : _script);
		}
		if (parts.Count == 0)
		{
			parts.Add(Voidstrap.Utility.RpcText.Mark(startPage
				? "On the start page"
				: linux
					? _mode == "Playtesting" || _mode == "Testing" ? "Testing the experience" : "Building the experience"
					: _script.Length == 0 ? "Editing UI" : "In Roblox Studio"));
		}
		string state = string.Join(", ", parts);
		string largeImage = showPlace && _iconUrl.Length > 0 ? _iconUrl : StudioIconUrl;
		string largeText = showPlace ? _place : "Roblox Studio";
		List<DiscordRPC.Button> buttons = [];
		if (showPlace && _placeId > 0)
		{
			buttons.Add(new DiscordRPC.Button { Label = Voidstrap.Utility.RpcText.Mark("View game"), Url = $"https://www.roblox.com/games/{_placeId}" });
		}
		buttons.Add(new DiscordRPC.Button { Label = Voidstrap.Utility.RpcText.Mark("Get") + " " + Voidstrap.Utility.Branding.Name, Url = App.ProjectDownloadLink });
		string signature = string.Join("|", Voidstrap.Utility.RpcText.Render(details), Voidstrap.Utility.RpcText.Render(state), largeImage, largeText, _placeId);
		if (signature == _lastSignature)
		{
			return;
		}
		try
		{
			if (_client.SetPresenceSafe(new DiscordRPC.RichPresence
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
					SmallImageText = linux && _linuxLog.Version.Length > 0 ? "Roblox Studio " + _linuxLog.Version + " on Linux" : "Roblox Studio"
				},
				Buttons = buttons.ToArray()
			}))
			{
				_lastSignature = signature;
			}
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
			"Playtesting" => Voidstrap.Utility.RpcText.Mark("Playtesting") + " " + place,
			"Testing" => Voidstrap.Utility.RpcText.Mark("Testing") + " " + place,
			"Team Create" => Voidstrap.Utility.RpcText.Mark("Editing") + " " + place + " " + Voidstrap.Utility.RpcText.Mark("in Team Create"),
			_ => Voidstrap.Utility.RpcText.Mark("Editing") + " " + place
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
		ReleaseClient();
		_lifetimeCancellation.Dispose();
		_pollTimer = null;
		GC.SuppressFinalize(this);
	}

	private sealed partial class StudioLogTracker
	{
		private const long MaxReadBytes = 16L * 1024 * 1024;

		[GeneratedRegex(@"DmId: [0-9A-Fa-f-]+-(\d{3,19})-StudioGameStateType_(\w+)")]
		private static partial Regex DataModelIdPattern { get; }

		[GeneratedRegex(@"(?:Joining|continuing to open) universeId (\d{1,19})")]
		private static partial Regex OpenUniversePattern { get; }

		[GeneratedRegex(@"(Setting up|Tearing down) for DataModel (\w+)")]
		private static partial Regex DataModelLifecyclePattern { get; }

		[GeneratedRegex(@"^(\d+\.\d+)\.")]
		private static partial Regex VersionPattern { get; }

		private readonly HashSet<string> _dataModels = new(StringComparer.Ordinal);
		private string _path = "";
		private long _offset;
		private string _pending = "";

		public long PlaceId { get; private set; }

		public long UniverseId { get; private set; }

		public bool DocumentOpen { get; private set; }

		public bool HasEvents { get; private set; }

		public string Version { get; private set; } = "";

		public string Mode
		{
			get
			{
				if (_dataModels.Any(static model => !model.Equals("Edit", StringComparison.Ordinal) && !model.Equals("Standalone", StringComparison.Ordinal)))
				{
					return "Playtesting";
				}
				return _dataModels.Contains("Edit") ? "Editing" : "";
			}
		}

		public void Reset()
		{
			_path = "";
			_offset = 0;
			_pending = "";
			_dataModels.Clear();
			PlaceId = 0;
			UniverseId = 0;
			DocumentOpen = false;
			HasEvents = false;
			Version = "";
		}

		public void Update(string path)
		{
			if (!string.Equals(path, _path, StringComparison.Ordinal))
			{
				Reset();
				_path = path;
				Match version = VersionPattern.Match(Path.GetFileName(path));
				Version = version.Success ? version.Groups[1].Value : "";
			}

			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (stream.Length < _offset)
			{
				string current = _path;
				Reset();
				_path = current;
			}
			if (stream.Length == _offset)
			{
				return;
			}
			long start = Math.Max(_offset, stream.Length - MaxReadBytes);
			stream.Seek(start, SeekOrigin.Begin);
			byte[] buffer = new byte[stream.Length - start];
			int read = stream.ReadAtLeast(buffer, buffer.Length, false);
			_offset = start + read;
			string text = _pending + System.Text.Encoding.UTF8.GetString(buffer, 0, read);
			int end = text.LastIndexOf('\n');
			if (end < 0)
			{
				_pending = text;
				return;
			}
			_pending = text[(end + 1)..];
			foreach (string line in text[..end].Split('\n'))
			{
				ParseLine(line);
			}
		}

		private void ParseLine(string line)
		{
			if (line.Contains("createAndShowIDEDoc", StringComparison.Ordinal))
			{
				DocumentOpen = true;
				HasEvents = true;
				PlaceId = 0;
				UniverseId = 0;
				return;
			}
			if (line.Contains("RobloxIDEDoc::doClose", StringComparison.Ordinal) || line.Contains("close IDE doc", StringComparison.Ordinal))
			{
				DocumentOpen = false;
				HasEvents = true;
				PlaceId = 0;
				UniverseId = 0;
				_dataModels.RemoveWhere(static model => !model.Equals("Standalone", StringComparison.Ordinal));
				return;
			}
			Match lifecycle = DataModelLifecyclePattern.Match(line);
			if (lifecycle.Success)
			{
				HasEvents = true;
				if (lifecycle.Groups[1].Value == "Setting up")
				{
					_dataModels.Add(lifecycle.Groups[2].Value);
				}
				else
				{
					_dataModels.Remove(lifecycle.Groups[2].Value);
				}
				return;
			}
			if (!DocumentOpen)
			{
				return;
			}
			Match model = DataModelIdPattern.Match(line);
			if (model.Success && long.TryParse(model.Groups[1].Value, out long placeId) && placeId > 0)
			{
				PlaceId = placeId;
				return;
			}
			Match universe = OpenUniversePattern.Match(line);
			if (universe.Success && long.TryParse(universe.Groups[1].Value, out long universeId) && universeId > 0)
			{
				UniverseId = universeId;
			}
		}
	}

    [GeneratedRegex(@"\bplaceId\b[^0-9]{0,12}(\d{1,19})", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex PlaceIdPattern { get; }
    [GeneratedRegex(@"\buniverseId\b[^0-9]{0,12}(\d{1,19})", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex UniverseIdPattern { get; }
}
