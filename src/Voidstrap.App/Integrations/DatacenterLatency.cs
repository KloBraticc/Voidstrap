using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Voidstrap.Integrations;

public static class DatacenterLatency
{
    private const string LOG_IDENT = "DatacenterLatency";

    private const int IcmpTimeoutMs = 900;

    private const int TcpTimeoutMs = 800;

    private const int MaxConcurrentProbes = 32;

    private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(3);

    private static readonly int[] DefaultPingHosts = [3, 65];

    private static readonly int[] TcpPorts = [443, 80];

    private static readonly ConcurrentDictionary<string, (int Ms, DateTime MeasuredUtc)> _results = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Lazy<Task<int>>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, string> _pingTargets = new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim _probeSlots = new(MaxConcurrentProbes);

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
