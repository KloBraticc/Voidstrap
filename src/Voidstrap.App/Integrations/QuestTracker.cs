using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

public class QuestTracker : IDisposable
{
    private const string LOG_IDENT = "QuestTracker";

    private const int LocalTickMs = 60000;

    private const int MaxResponseBytes = 8192;
    private const int MaxProgressResponseBytes = 262144;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12.0);

    private readonly ActivityWatcher _activityWatcher;

    private readonly EventHandler _onGameJoinHandler;

    private readonly EventHandler _onGameLeaveHandler;

    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    private Timer? _beatTimer;

    private string _sessionId = string.Empty;

    private bool _disposed;



    private DateTime _sessionStartedUtc;

    private static string PendingPath => Path.Combine(Paths.Config, "QuestSession.json");

    private sealed class PendingSession
    {
        public string sessionId { get; set; } = "";

        public string startedUtc { get; set; } = "";

        public string lastAliveUtc { get; set; } = "";
    }

    public Func<Voidstrap.UI.NotifyIconWrapper?>? NotifyIconResolver { get; set; }

    public static Voidstrap.Models.QuestProgressSnapshot? Progress { get; private set; }

    private static string QuestsUrl => App.WebsiteBaseUrl + "/api/quests";

    private static string StartUrl => App.WebsiteBaseUrl + "/api/quests/session/start";


    private static string EndUrl => App.WebsiteBaseUrl + "/api/quests/session/end";

    private static string SubmitUrl => App.WebsiteBaseUrl + "/api/quests/session/submit";

    public QuestTracker(ActivityWatcher activityWatcher)
    {
        _activityWatcher = activityWatcher ?? throw new ArgumentNullException(nameof(activityWatcher));
        _onGameJoinHandler = OnGameJoin;
        _onGameLeaveHandler = OnGameLeave;
        _activityWatcher.OnGameJoin += _onGameJoinHandler;
        _activityWatcher.OnGameLeave += _onGameLeaveHandler;
        if (_activityWatcher.InGame)
        {
            _ = StartAsync();
        }
    }

    private void OnGameJoin(object? sender, EventArgs e)
    {
        _ = StartAsync();
    }

    private void OnGameLeave(object? sender, EventArgs e)
    {
        if (_activityWatcher.IsTeleporting)
            return;
        _ = StopAsync();
    }

    private async Task StartAsync()
    {
        if (_disposed || !App.Settings.Prop.WebsiteQuestTracking || !WebsiteAuth.IsSignedIn())
            return;
        await RecoverPendingAsync().ConfigureAwait(false);
        long universeId = _activityWatcher.Data?.UniverseId ?? 0L;
        bool entered = false;
        try
        {
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            entered = true;
            if (_disposed)
                return;
            if (_sessionId.Length != 0)
                return;
            string payload = "{\"universeId\":" + (universeId > 0 ? universeId.ToString() : "0") + ",\"batch\":true}";
            string? body = await PostAsync(StartUrl, payload).ConfigureAwait(false);
            string? sessionId = ReadString(body, "sessionId");
            if (string.IsNullOrEmpty(sessionId))
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not open a quest session.");
                return;
            }
            _sessionId = sessionId;
            _sessionStartedUtc = DateTime.UtcNow;
            PersistPending();
            _beatTimer?.Dispose();
            _beatTimer = new Timer(OnLocalTick, null, LocalTickMs, LocalTickMs);
            App.Logger.WriteLine(LOG_IDENT, "Quest session open for universe " + universeId);
            _ = RefreshProgressAsync(universeId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Session start failed: " + ex.Message);
        }
        finally
        {
            if (entered)
                _gate.Release();
        }
    }

    private void OnLocalTick(object? state)
    {
        try
        {
            if (_disposed || _sessionId.Length == 0)
                return;
            PersistPending();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("QuestTracker::OnLocalTick", ex);
        }
    }



    private async Task StopAsync()
    {
        if (_disposed)
            return;
        bool entered = false;
        try
        {
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            entered = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Session end failed: " + ex.Message);
        }
        finally
        {
            if (entered)
                _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        string sessionId = _sessionId;
        _sessionId = string.Empty;
        Timer? timer = _beatTimer;
        _beatTimer = null;
        timer?.Dispose();
        DateTime startedUtc = _sessionStartedUtc;
        if (sessionId.Length == 0 || !WebsiteAuth.IsSignedIn())
        {
            ClearPending();
            return;
        }
        try
        {
            int minutes = MinutesBetween(startedUtc.ToString("O"), DateTime.UtcNow.ToString("O"));
            if (minutes <= 0)
            {
                ClearPending();
                await PostAsync(EndUrl, "{\"sessionId\":\"" + sessionId + "\"}").ConfigureAwait(false);
                return;
            }
            if (await SubmitAsync(sessionId, minutes).ConfigureAwait(false))
            {
                ClearPending();
                App.Logger.WriteLine(LOG_IDENT, "Quest session closed.");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<string?> PostAsync(string url, string payload)
    {
        string? token = WebsiteAuth.GetToken();
        if (string.IsNullOrEmpty(token))
            return null;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(RequestTimeout);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            App.Logger.WriteLine(LOG_IDENT, "Request to " + url + " returned " + (int)response.StatusCode);
            return null;
        }
        byte[] raw = await Utility.Http.ReadBytesBoundedAsync(response.Content, MaxResponseBytes, timeout.Token).ConfigureAwait(false);
        if (raw.Length == 0)
            return null;
        return Encoding.UTF8.GetString(raw);
    }

    private async Task RefreshProgressAsync(long universeId)
    {
        if (_disposed || !App.Settings.Prop.WebsiteQuestTracking)
            return;
        string? token = WebsiteAuth.GetToken();
        if (string.IsNullOrEmpty(token))
            return;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(RequestTimeout);
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, QuestsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return;
            byte[] raw = await Utility.Http.ReadBytesBoundedAsync(response.Content, MaxProgressResponseBytes, timeout.Token).ConfigureAwait(false);
            if (raw.Length == 0)
                return;
            Progress = BuildSnapshot(Encoding.UTF8.GetString(raw), universeId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Progress refresh failed: " + ex.Message);
        }
    }

    private static Voidstrap.Models.QuestProgressSnapshot? BuildSnapshot(string body, long universeId)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("quests", out JsonElement quests) || quests.ValueKind != JsonValueKind.Array)
                return null;
            Voidstrap.Models.QuestLiveSession? live = Voidstrap.Models.QuestLiveSession.FromResponse(root);
            Voidstrap.Models.QuestProgressSnapshot snapshot = new Voidstrap.Models.QuestProgressSnapshot { UniverseId = universeId, Session = live };
            AppendQuestLines(snapshot, quests, universeId, live);

            return snapshot;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Progress parse failed: " + ex.Message);
            return null;
        }
    }

    private static void AppendQuestLines(
        Voidstrap.Models.QuestProgressSnapshot snapshot,
        JsonElement quests,
        long universeId,
        Voidstrap.Models.QuestLiveSession? live)
    {
        foreach (JsonElement quest in quests.EnumerateArray())
        {
            if (quest.ValueKind != JsonValueKind.Object)
                continue;
            string kind = ReadJsonText(quest, "kind");
            long target = ReadJsonLong(quest, "universeId");
            bool relevant = kind == "variety" || (kind == "playtime" && (target == 0L || target == universeId));
            if (!relevant)
                continue;
            snapshot.Lines.Add(new Voidstrap.Models.QuestProgressLine
            {
                Title = ReadJsonText(quest, "title"),
                Kind = kind,
                UniverseId = target,
                Goal = ReadJsonInt(quest, "goal"),
                Progress = ReadJsonInt(quest, "progress"),
                Xp = ReadJsonInt(quest, "xp"),
                Complete = quest.TryGetProperty("complete", out JsonElement done) && done.ValueKind == JsonValueKind.True,
                Session = live,
            });
        }
    }

    private static string ReadJsonText(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int ReadJsonInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
    }

    private static long ReadJsonLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0L;
    }

	private bool AnnounceCompleted(string body)
    {
		bool found = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
				return false;
            if (!document.RootElement.TryGetProperty("completed", out JsonElement completed) || completed.ValueKind != JsonValueKind.Array)
				return false;
            foreach (JsonElement entry in completed.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                string title = entry.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? string.Empty
                    : string.Empty;
                int xp = entry.TryGetProperty("xp", out JsonElement x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0;
                if (title.Length == 0)
                    continue;
				found = true;
				if (!App.Settings.Prop.QuestCompleteNotifications)
					continue;
                App.Logger.WriteLine(LOG_IDENT, "Quest complete: " + title);
                NotifyIconResolver?.Invoke()?.ShowAlert("Quest complete", title + ". Claim " + xp + " XP on the website.", 10, null);
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not read completed quests: " + ex.Message);
        }
		return found;
    }

    private static string? ReadString(string? body, string property)
    {
        if (string.IsNullOrEmpty(body))
            return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!document.RootElement.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
                return null;
            return value.GetString();
        }
        catch
        {
            return null;
        }
    }

    private void PersistPending()
    {
        if (_sessionId.Length == 0)
            return;
        try
        {
            PendingSession pending = new PendingSession
            {
                sessionId = _sessionId,
                startedUtc = _sessionStartedUtc.ToString("O"),
                lastAliveUtc = DateTime.UtcNow.ToString("O"),
            };
            File.WriteAllText(PendingPath, JsonSerializer.Serialize(pending));
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not persist quest session: " + ex.Message);
        }
    }

    private static void ClearPending()
    {
        try
        {
            if (File.Exists(PendingPath))
                File.Delete(PendingPath);
        }
        catch
        {
        }
    }

    private static bool PendingIsStale(string lastAliveUtc)
    {
        if (!DateTime.TryParse(lastAliveUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime alive))
            return true;
        return (DateTime.UtcNow - alive).TotalHours > 6.0;
    }

    private static int MinutesBetween(string startedUtc, string lastAliveUtc)
    {
        if (!DateTime.TryParse(startedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime started))
            return 0;
        if (!DateTime.TryParse(lastAliveUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime alive))
            return 0;
        double minutes = (alive - started).TotalMinutes;
        if (minutes <= 0 || minutes > 1440)
            return 0;
        return (int)Math.Floor(minutes);
    }

    private async Task<bool> SubmitAsync(string sessionId, int minutes)
    {
        if (sessionId.Length == 0 || minutes <= 0 || !WebsiteAuth.IsSignedIn())
            return false;
        string? body = await PostAsync(SubmitUrl, "{\"sessionId\":\"" + sessionId + "\",\"minutes\":" + minutes + "}").ConfigureAwait(false);
        if (body == null)
        {
            App.Logger.WriteLine(LOG_IDENT, "Quest submit unavailable, keeping " + minutes + " minutes for retry.");
            return false;
        }
        AnnounceCompleted(body);
        App.Logger.WriteLine(LOG_IDENT, "Submitted " + minutes + " quest minutes.");
        return true;
    }

    public async Task RecoverPendingAsync()
    {
        PendingSession? pending = null;
        try
        {
            if (!File.Exists(PendingPath))
                return;
            pending = JsonSerializer.Deserialize<PendingSession>(File.ReadAllText(PendingPath));
        }
        catch
        {
            ClearPending();
            return;
        }
        if (pending == null || pending.sessionId.Length == 0)
        {
            ClearPending();
            return;
        }
        int minutes = MinutesBetween(pending.startedUtc, pending.lastAliveUtc);
        if (minutes <= 0 || PendingIsStale(pending.lastAliveUtc))
        {
            ClearPending();
            return;
        }
        try
        {
            if (await SubmitAsync(pending.sessionId, minutes).ConfigureAwait(false))
                ClearPending();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Pending quest recovery failed: " + ex.Message);
        }
    }


    private static bool ReadBool(string? body, string property)
    {
        if (string.IsNullOrEmpty(body))
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            return document.RootElement.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _activityWatcher.OnGameJoin -= _onGameJoinHandler;
        _activityWatcher.OnGameLeave -= _onGameLeaveHandler;
        Timer? timer = _beatTimer;
        _beatTimer = null;
        timer?.Dispose();
        _sessionId = string.Empty;
        Progress = null;
        try
        {
            _lifetime.Cancel();
        }
        catch
        {
        }
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
