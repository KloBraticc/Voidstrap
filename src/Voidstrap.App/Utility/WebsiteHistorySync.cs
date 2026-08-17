using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Enums;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;

namespace Voidstrap.Utility
{
    public static class WebsiteHistorySync
    {
        private const int MaxHistoryEntries = 100;

        private const int MaxUniverses = 2000;

        private const int MaxResponseBytes = 4194304;

        private const int MaxLocalHistoryBytes = 16777216;

        private const int MaxStateBytes = 65536;

        private static readonly object StateSync = new object();

        private static readonly object LifecycleSync = new object();

        private static int _running;

        private static int _pending;

        private static int _pushScheduled;

        private static bool _installed;

        private static CancellationTokenSource? _lifetimeCancellation;

        private static DateTime _lastPushUtc = DateTime.MinValue;

        private static readonly TimeSpan PushInterval = TimeSpan.FromMinutes(60);

        private static string StatePath => Path.Combine(Paths.Config, "WebsiteHistorySync.json");

        private static string HistoryUrl => App.WebsiteBaseUrl + "/api/me/apphistory";

        private sealed class SyncState
        {
            public string account { get; set; } = "";

            public string lastPush { get; set; } = "";
        }

        private sealed class HistoryEntryDto
        {
            public long placeId { get; set; }

            public string jobId { get; set; } = "";

            public long universeId { get; set; }

            public int serverType { get; set; }

            public bool isTeleport { get; set; }

            public string timeJoined { get; set; } = "";

            public string timeLeft { get; set; } = "";
        }

        private sealed class PlayTimeUniverseDto
        {
            public double minutes { get; set; }

            public string lastPlayed { get; set; } = "";
        }

        private sealed class PlayTimeDto
        {
            public Dictionary<string, PlayTimeUniverseDto> universes { get; set; } = new Dictionary<string, PlayTimeUniverseDto>();
        }

        private sealed class HistoryPayload
        {
            public List<HistoryEntryDto> serverHistory { get; set; } = new List<HistoryEntryDto>();

            public PlayTimeDto playTime { get; set; } = new PlayTimeDto();
        }

        private sealed class RemoteEnvelope
        {
            public HistoryPayload? data { get; set; }
        }

        public static void Install()
        {
            lock (LifecycleSync)
            {
                if (_installed)
                    return;
                _lifetimeCancellation = new CancellationTokenSource();
                _installed = true;
                WebsiteAuth.Changed += OnAuthChanged;
            }
            TriggerAsync();
        }

        public static void Shutdown()
        {
            CancellationTokenSource? cancellation;
            lock (LifecycleSync)
            {
                if (!_installed)
                    return;
                WebsiteAuth.Changed -= OnAuthChanged;
                _installed = false;
                cancellation = _lifetimeCancellation;
                _lifetimeCancellation = null;
            }
            cancellation?.Cancel();
            cancellation?.Dispose();
        }

        private static void OnAuthChanged()
        {
            TriggerAsync();
        }

        public static void TriggerAsync()
        {
            if (!WebsiteAuth.IsSignedIn())
                return;
            if (!TryGetLifetimeToken(out CancellationToken cancellationToken))
                return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                Interlocked.Exchange(ref _pending, 1);
                return;
            }
            _ = Task.Run(async delegate
            {
                try
                {
                    await SyncAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("WebsiteHistorySync::Trigger", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _running, 0);
                    if (Interlocked.Exchange(ref _pending, 0) != 0)
                        TriggerAsync();
                }
            });
        }

        public static async Task FetchAndApplyAsync()
        {
            if (!TryGetLifetimeToken(out CancellationToken cancellationToken))
                return;
            string? token = WebsiteAuth.GetToken();
            if (string.IsNullOrEmpty(token))
                return;
            string active = WebsiteAuth.GetActiveId() ?? "";
            SyncState state = LoadState();
            bool switched = !string.IsNullOrEmpty(state.account) && state.account != active;
            (bool ok, HistoryPayload? remote) = await FetchRemoteAsync(token, cancellationToken).ConfigureAwait(false);
            if (ok && active == (WebsiteAuth.GetActiveId() ?? ""))
            {
                ApplyRemote(remote ?? new HistoryPayload(), switched);
                if (state.account != active)
                {
                    state.account = active;
                    state.lastPush = "";
                    SaveState(state);
                }
            }
        }

        public static void PushSoon()
        {
            if (!WebsiteAuth.IsSignedIn())
                return;
            if (DateTime.UtcNow - _lastPushUtc < PushInterval)
                return;
            if (Interlocked.CompareExchange(ref _pushScheduled, 1, 0) != 0)
                return;
            if (!TryGetLifetimeToken(out CancellationToken cancellationToken))
            {
                Interlocked.Exchange(ref _pushScheduled, 0);
                return;
            }
            _ = Task.Run(async delegate
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
                    string? token = WebsiteAuth.GetToken();
                    if (!string.IsNullOrEmpty(token))
                        await PushAsync(token, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("WebsiteHistorySync::PushSoon", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _pushScheduled, 0);
                }
            });
        }

        private static async Task SyncAsync(CancellationToken cancellationToken)
        {
            string? token = WebsiteAuth.GetToken();
            if (string.IsNullOrEmpty(token))
                return;
            string active = WebsiteAuth.GetActiveId() ?? "";
            SyncState state = LoadState();
            bool switched = !string.IsNullOrEmpty(state.account) && state.account != active;
            (bool ok, HistoryPayload? remote) = await FetchRemoteAsync(token, cancellationToken).ConfigureAwait(false);
            if (!ok || active != (WebsiteAuth.GetActiveId() ?? ""))
                return;
            ApplyRemote(remote ?? new HistoryPayload(), switched);
            if (state.account != active)
            {
                state.account = active;
                state.lastPush = "";
                SaveState(state);
                if (switched)
                    App.Logger.WriteLine("WebsiteHistorySync", "Account changed, history loaded for the active account");
            }
            await PushAsync(token, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<(bool Ok, HistoryPayload? Data)> FetchRemoteAsync(string token, CancellationToken cancellationToken)
        {
            try
            {
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, HistoryUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage resp = await App.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return (false, null);
                string text = await ReadBoundedTextAsync(resp, cancellationToken).ConfigureAwait(false);
                RemoteEnvelope? env = JsonSerializer.Deserialize<RemoteEnvelope>(text);
                return (true, env?.data);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WebsiteHistorySync::Fetch", ex);
                return (false, null);
            }
        }

        private static async Task<string> ReadBoundedTextAsync(HttpResponseMessage response, CancellationToken token)
        {
            if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > MaxResponseBytes))
                throw new InvalidDataException("History response is too large");
            await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using MemoryStream output = new MemoryStream(response.Content.Headers.ContentLength is long contentLength ? (int)contentLength : 0);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (true)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (output.Length + read > MaxResponseBytes)
                        throw new InvalidDataException("History response is too large");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static void ApplyRemote(HistoryPayload remote, bool replace)
        {
            List<ActivityData> remoteEntries = new List<ActivityData>();
            foreach (HistoryEntryDto dto in (remote.serverHistory ?? new List<HistoryEntryDto>()).Take(MaxHistoryEntries))
            {
                ActivityData? entry = FromDto(dto);
                if (entry != null)
                    remoteEntries.Add(entry);
            }
            List<ActivityData> merged = replace
                ? remoteEntries.OrderByDescending(x => x.TimeJoined).Take(MaxHistoryEntries).ToList()
                : MergeHistory(LoadLocalHistory(), remoteEntries);
            try
            {
				JsonFile.SerializeAtomic(Paths.ServerHistory, merged, JsonOptions.Indented);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("WebsiteHistorySync", "History write failed: " + ex.Message);
            }
            Dictionary<long, PlayTimeStore.UniverseTime> universes = new Dictionary<long, PlayTimeStore.UniverseTime>();
            if (remote.playTime?.universes != null)
            {
                foreach (KeyValuePair<string, PlayTimeUniverseDto> kv in remote.playTime.universes.Take(MaxUniverses))
                {
                    if (!long.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0 || kv.Value == null)
                        continue;
                    PlayTimeStore.UniverseTime time = new PlayTimeStore.UniverseTime
                    {
                        Minutes = kv.Value.minutes > 0.0 ? kv.Value.minutes : 0.0
                    };
                    if (TryParseTime(kv.Value.lastPlayed, out DateTime lastPlayed))
                        time.LastPlayed = lastPlayed;
                    universes[id] = time;
                }
            }
            PlayTimeStore.MergeRemote(universes, replace);
        }

        private static List<ActivityData> MergeHistory(List<ActivityData> local, List<ActivityData> remote)
        {
            Dictionary<string, ActivityData> map = new Dictionary<string, ActivityData>();
            foreach (ActivityData item in local)
            {
                if (item != null && item.PlaceId != 0)
                    map[$"{item.PlaceId}_{item.JobId}"] = item;
            }
            foreach (ActivityData item in remote)
            {
                string key = $"{item.PlaceId}_{item.JobId}";
                if (map.TryGetValue(key, out ActivityData? existing))
                {
                    if (item.TimeJoined != default(DateTime) && (existing.TimeJoined == default(DateTime) || item.TimeJoined < existing.TimeJoined))
                        existing.TimeJoined = item.TimeJoined;
                    if (item.TimeLeft.HasValue && (!existing.TimeLeft.HasValue || existing.TimeLeft.Value < item.TimeLeft.Value))
                        existing.TimeLeft = item.TimeLeft;
                    if (existing.UniverseId == 0 && item.UniverseId != 0)
                        existing.UniverseId = item.UniverseId;
                }
                else
                {
                    map[key] = item;
                }
            }
            return map.Values.OrderByDescending(x => x.TimeJoined).Take(MaxHistoryEntries).ToList();
        }

        private static async Task PushAsync(string token, CancellationToken cancellationToken)
        {
            HistoryPayload payload = BuildPayload();
            if (payload.serverHistory.Count == 0 && payload.playTime.universes.Count == 0)
                return;
            string body = JsonSerializer.Serialize(payload);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            SyncState state = LoadState();
            if (state.lastPush == hash)
                return;
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Put, HistoryUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await App.HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                _lastPushUtc = DateTime.UtcNow;
                state.lastPush = hash;
                SaveState(state);
                App.Logger.WriteLine("WebsiteHistorySync", $"Synced {payload.serverHistory.Count} history entries and {payload.playTime.universes.Count} play time records to the account");
            }
            else
            {
                App.Logger.WriteLine("WebsiteHistorySync", $"Push rejected: {(int)resp.StatusCode}");
            }
        }

        private static HistoryPayload BuildPayload()
        {
            HistoryPayload payload = new HistoryPayload();
            foreach (ActivityData item in LoadLocalHistory())
            {
                if (item != null && item.PlaceId != 0 && item.TimeJoined != default(DateTime))
                    payload.serverHistory.Add(ToDto(item));
            }
            if (payload.serverHistory.Count > MaxHistoryEntries)
                payload.serverHistory = payload.serverHistory.OrderByDescending(x => x.timeJoined, StringComparer.Ordinal).Take(MaxHistoryEntries).ToList();
            PlayTimeStore.StoreData store = PlayTimeStore.Read();
            foreach (KeyValuePair<long, PlayTimeStore.UniverseTime> kv in store.Universes.OrderByDescending(k => k.Value.LastPlayed ?? DateTime.MinValue).Take(MaxUniverses))
            {
                if (kv.Key <= 0 || kv.Value == null)
                    continue;
                payload.playTime.universes[kv.Key.ToString(CultureInfo.InvariantCulture)] = new PlayTimeUniverseDto
                {
                    minutes = Math.Round(kv.Value.Minutes, 2),
                    lastPlayed = kv.Value.LastPlayed.HasValue ? kv.Value.LastPlayed.Value.ToString("o", CultureInfo.InvariantCulture) : ""
                };
            }
            return payload;
        }

        private static List<ActivityData> LoadLocalHistory()
        {
            try
            {
                if (!File.Exists(Paths.ServerHistory))
                    return new List<ActivityData>();
				return JsonFile.Deserialize<List<ActivityData>>(Paths.ServerHistory, JsonOptions.Tolerant, MaxLocalHistoryBytes).OrderByDescending(x => x.TimeJoined).Take(MaxHistoryEntries).ToList();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("WebsiteHistorySync", "History read failed: " + ex.Message);
                return new List<ActivityData>();
            }
        }

        private static HistoryEntryDto ToDto(ActivityData a)
        {
            return new HistoryEntryDto
            {
                placeId = a.PlaceId,
                jobId = a.JobId ?? "",
                universeId = a.UniverseId,
                serverType = (int)a.ServerType,
                isTeleport = a.IsTeleport,
                timeJoined = a.TimeJoined.ToString("o", CultureInfo.InvariantCulture),
                timeLeft = a.TimeLeft.HasValue ? a.TimeLeft.Value.ToString("o", CultureInfo.InvariantCulture) : ""
            };
        }

        private static ActivityData? FromDto(HistoryEntryDto? dto)
        {
            if (dto == null || dto.placeId <= 0)
                return null;
            if (!TryParseTime(dto.timeJoined, out DateTime joined))
                return null;
            ActivityData data = new ActivityData
            {
                PlaceId = dto.placeId,
                JobId = dto.jobId ?? "",
                IsTeleport = dto.isTeleport,
                ServerType = (ServerType)dto.serverType,
                TimeJoined = joined
            };
            if (dto.universeId > 0)
                data.UniverseId = dto.universeId;
            if (TryParseTime(dto.timeLeft, out DateTime left))
                data.TimeLeft = left;
            return data;
        }

        private static bool TryParseTime(string? s, out DateTime value)
        {
            value = default(DateTime);
            if (string.IsNullOrEmpty(s))
                return false;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
        }

        private static SyncState LoadState()
        {
            lock (StateSync)
            {
                try
                {
                    if (File.Exists(StatePath))
                    {
						return JsonFile.Deserialize<SyncState>(StatePath, JsonOptions.Tolerant, MaxStateBytes);
                    }
                }
                catch
                {
                }
                return new SyncState();
            }
        }

        private static void SaveState(SyncState state)
        {
            lock (StateSync)
            {
                try
                {
					JsonFile.SerializeAtomic(StatePath, state);
                }
                catch
                {
                }
            }
        }

        private static bool TryGetLifetimeToken(out CancellationToken token)
        {
            lock (LifecycleSync)
            {
                if (!_installed || _lifetimeCancellation == null)
                {
                    token = default;
                    return false;
                }
                token = _lifetimeCancellation.Token;
                return true;
            }
        }
    }
}
