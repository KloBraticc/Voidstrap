using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Voidstrap.Integrations;

public static class DatacenterLatency
{
    private const string LOG_IDENT = "DatacenterLatency";

    private const int IcmpTimeoutMs = 900;

    private const int TcpTimeoutMs = 800;

    private const int MaxConcurrentProbes = 32;

    private static readonly TimeSpan ResultLifetime = TimeSpan.FromHours(24);

    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(3);

    private static readonly int[] DefaultPingHosts = [3, 65];

    private static readonly int[] TcpPorts = [443, 80];

    private static readonly ConcurrentDictionary<string, (int Ms, DateTime MeasuredUtc)> _results = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Lazy<Task<int>>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, string> _pingTargets = new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim _probeSlots = new(MaxConcurrentProbes);

    private static readonly object _cacheLock = new();

    private static bool _loaded;

    private static string _network = "";

    private static string _network6 = "";

    private static int _savePending;

    private static string CachePath => Path.Combine(ServerFetchStore.FolderPath, "DatacenterLatency.json");

    public static void UpdatePublicIp(string ip)
    {
        const string LOG_IDENT = "DatacenterLatency::UpdatePublicIp";
        if (!IPAddress.TryParse(ip.Trim(), out IPAddress? address))
            return;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        bool v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        byte[] bytes = address.GetAddressBytes();
        if (v6)
            Array.Clear(bytes, 8, 8);
        string network = Convert.ToHexString(SHA256.HashData(bytes));
        EnsureLoaded();
        bool changed;
        lock (_cacheLock)
        {
            string previous = v6 ? _network6 : _network;
            if (string.Equals(previous, network, StringComparison.Ordinal))
                return;
            changed = previous.Length > 0;
            if (v6)
                _network6 = network;
            else
                _network = network;
        }
        if (changed)
        {
            App.Logger.WriteLine(LOG_IDENT, "The public IP changed, datacenter latency will be measured again");
            _results.Clear();
        }
        QueueSave();
    }

    public static void ClearCache()
    {
        const string LOG_IDENT = "DatacenterLatency::ClearCache";
        EnsureLoaded();
        _results.Clear();
        App.Logger.WriteLine(LOG_IDENT, "Saved datacenter latency results were cleared");
        QueueSave();
    }

    private static void EnsureLoaded()
    {
        const string LOG_IDENT = "DatacenterLatency::EnsureLoaded";
        if (Volatile.Read(ref _loaded))
            return;
        lock (_cacheLock)
        {
            if (_loaded)
                return;
            try
            {
                if (!File.Exists(CachePath))
                    return;
                DatacenterLatencyCache? cache = JsonSerializer.Deserialize<DatacenterLatencyCache>(File.ReadAllText(CachePath));
                if (cache == null)
                    return;
                _network = cache.Network ?? "";
                _network6 = cache.Network6 ?? "";
                DateTime now = DateTime.UtcNow;
                int restored = 0;
                foreach (KeyValuePair<string, DatacenterLatencyResult> pair in cache.Results ?? new())
                {
                    if (pair.Value == null || !IsMeasurableCidr(pair.Key) || pair.Value.Ms < 0 || pair.Value.MeasuredUtc > now || now - pair.Value.MeasuredUtc > ResultLifetime)
                        continue;
                    if (_results.TryAdd(pair.Key, (pair.Value.Ms, pair.Value.MeasuredUtc)))
                        restored++;
                }
                App.Logger.WriteLine(LOG_IDENT, $"Restored {restored} saved datacenter latency results");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                App.Logger.WriteLine(LOG_IDENT, "Saved datacenter latency results could not be read: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _loaded, true);
            }
        }
    }

    private static void QueueSave()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 1)
            return;
        _ = SaveSoonAsync();
    }

    private static async Task SaveSoonAsync()
    {
        const string LOG_IDENT = "DatacenterLatency::SaveSoonAsync";
        await Task.Delay(SaveDelay).ConfigureAwait(false);
        Interlocked.Exchange(ref _savePending, 0);
        try
        {
            DatacenterLatencyCache cache = new DatacenterLatencyCache();
            lock (_cacheLock)
            {
                cache.Network = _network;
                cache.Network6 = _network6;
            }
            DateTime now = DateTime.UtcNow;
            foreach (var pair in _results)
            {
                if (pair.Value.Ms >= 0 && now - pair.Value.MeasuredUtc <= ResultLifetime)
                    cache.Results[pair.Key] = new DatacenterLatencyResult { Ms = pair.Value.Ms, MeasuredUtc = pair.Value.MeasuredUtc };
            }
            Directory.CreateDirectory(ServerFetchStore.FolderPath);
            string temp = CachePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(cache)).ConfigureAwait(false);
            File.Move(temp, CachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Logger.WriteLine(LOG_IDENT, "Datacenter latency results could not be saved: " + ex.Message);
        }
    }

    public static void RememberPingTarget(string cidr, string ip)
    {
        if (string.IsNullOrWhiteSpace(cidr) || !IPAddress.TryParse(ip, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork)
            return;
        _pingTargets[cidr] = address.ToString();
    }

    public static IReadOnlyList<string> KnownCidrs()
    {
        return RobloxDatacenterMap
            .AllCidrs()
            .Concat(ServerFetchStore.AllEntries().Select(entry => entry.Cidr))
            .Where(IsMeasurableCidr)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool TryGetCached(string? cidr, out int ms)
    {
        EnsureLoaded();
        ms = -1;
        if (string.IsNullOrWhiteSpace(cidr) || !_results.TryGetValue(cidr, out var result) || result.Ms < 0)
            return false;
        if (DateTime.UtcNow - result.MeasuredUtc > ResultLifetime)
            return false;
        ms = result.Ms;
        return true;
    }

    public static int BestKnownMs()
    {
        EnsureLoaded();
        DateTime now = DateTime.UtcNow;
        int best = -1;
        foreach (var result in _results.Values)
        {
            if (result.Ms >= 0 && now - result.MeasuredUtc <= ResultLifetime && (best < 0 || result.Ms < best))
                best = result.Ms;
        }
        return best;
    }

    public static Task<int> GetMsForIpAsync(string? ip, CancellationToken token)
    {
        string? cidr = string.IsNullOrWhiteSpace(ip) ? null : ServerFetchStore.GetSlash24Cidr(ip);
        return cidr == null ? Task.FromResult(-1) : GetMsAsync(cidr, token);
    }

    public static async Task<int> GetMsAsync(string cidr, CancellationToken token)
    {
        if (!IsMeasurableCidr(cidr))
            return -1;
        EnsureLoaded();
        if (_results.TryGetValue(cidr, out var cached))
        {
            TimeSpan age = DateTime.UtcNow - cached.MeasuredUtc;
            if (age <= (cached.Ms >= 0 ? ResultLifetime : FailureLifetime))
                return cached.Ms;
        }
        Lazy<Task<int>> lazy = _inflight.GetOrAdd(cidr, key => new Lazy<Task<int>>(() => MeasureCidrAsync(key)));
        try
        {
            return await lazy.Value.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            if (lazy.Value.IsCompleted)
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<int>>>(cidr, lazy));
        }
    }

    public static async Task MeasureAllAsync(IEnumerable<string> cidrs, CancellationToken token)
    {
        try
        {
            await Task.WhenAll(cidrs.Select(cidr => GetMsAsync(cidr, token))).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Datacenter latency measurement failed: " + ex.Message);
        }
    }

    public static double CalibrationRatio(double userLat, double userLon)
    {
        EnsureLoaded();
        DateTime now = DateTime.UtcNow;
        List<double> ratios = new();
        foreach (var pair in _results)
        {
            if (pair.Value.Ms < 0 || now - pair.Value.MeasuredUtc > ResultLifetime)
                continue;
            RobloxDatacenter? dc = RobloxDatacenterMap.Map(pair.Key[..^3]);
            if (dc == null || (dc.Lat == 0.0 && dc.Lon == 0.0))
                continue;
            double estimate = 5.0 + VoidstrapMatchmaker.HaversineKm(userLat, userLon, dc.Lat, dc.Lon) / 75.0;
            ratios.Add(pair.Value.Ms / estimate);
        }
        if (ratios.Count == 0)
            return 1.0;
        ratios.Sort();
        return Math.Clamp(ratios[ratios.Count / 2], 0.5, 4.0);
    }

    private static bool IsMeasurableCidr(string? cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr) || !cidr.EndsWith("/24", StringComparison.Ordinal))
            return false;
        return IPAddress.TryParse(cidr[..^3], out IPAddress? address)
            && address.AddressFamily == AddressFamily.InterNetwork
            && !VoidstrapMatchmaker.IsPrivateIp(address.ToString());
    }

    private static List<IPAddress> PingTargets(string cidr)
    {
        string network = cidr[..^3];
        LearnedServerEntry? entry = ServerFetchStore.Lookup(network);
        HashSet<string> gameServers = new(entry?.IPs ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        byte[] bytes = IPAddress.Parse(network).GetAddressBytes();
        List<string> candidates = new();
        if (_pingTargets.TryGetValue(cidr, out string? remembered))
            candidates.Add(remembered);
        if (!string.IsNullOrWhiteSpace(entry?.PingIP))
            candidates.Add(entry.PingIP);
        foreach (int host in DefaultPingHosts)
            candidates.Add($"{bytes[0]}.{bytes[1]}.{bytes[2]}.{host}");
        List<IPAddress> targets = new();
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (gameServers.Contains(candidate) || !IPAddress.TryParse(candidate, out IPAddress? address))
                continue;
            if (address.AddressFamily == AddressFamily.InterNetwork)
                targets.Add(address);
        }
        return targets;
    }

    private static async Task<int> MeasureCidrAsync(string cidr)
    {
        List<IPAddress> targets = PingTargets(cidr);
        Task<int>[] probes = targets.Select(MeasureIpAsync).ToArray();
        int ms = -1;
        string pingIp = "";
        for (int i = 0; i < probes.Length; i++)
        {
            int result = await probes[i].ConfigureAwait(false);
            if (result >= 0)
            {
                ms = result;
                pingIp = targets[i].ToString();
                break;
            }
        }
        _results[cidr] = (ms, DateTime.UtcNow);
        if (ms >= 0)
        {
            _pingTargets[cidr] = pingIp;
            ServerFetchStore.RecordPing(cidr, pingIp, ms);
            QueueSave();
        }
        return ms;
    }

    private static async Task<int> MeasureIpAsync(IPAddress address)
    {
        await _probeSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            int icmp = await IcmpRttAsync(address).ConfigureAwait(false);
            if (icmp >= 0)
            {
                int again = await IcmpRttAsync(address).ConfigureAwait(false);
                return again >= 0 ? Math.Min(icmp, again) : icmp;
            }
            int[] tcp = await Task.WhenAll(TcpPorts.Select(port => TcpRttAsync(address, port))).ConfigureAwait(false);
            int best = -1;
            foreach (int value in tcp)
            {
                if (value >= 0 && (best < 0 || value < best))
                    best = value;
            }
            return best;
        }
        finally
        {
            _probeSlots.Release();
        }
    }

    private static async Task<int> IcmpRttAsync(IPAddress address)
    {
        try
        {
            using Ping ping = new Ping();
            PingReply reply = await ping.SendPingAsync(address, IcmpTimeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException or PlatformNotSupportedException)
        {
            return -1;
        }
    }

    private static async Task<int> TcpRttAsync(IPAddress address, int port)
    {
        using Socket socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using CancellationTokenSource cts = new CancellationTokenSource(TcpTimeoutMs);
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(address, port, cts.Token).ConfigureAwait(false);
            return (int)clock.ElapsedMilliseconds;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return (int)clock.ElapsedMilliseconds;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return -1;
        }
    }
}
