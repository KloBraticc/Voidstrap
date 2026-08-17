using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.AssetProxy
{
    internal static class DnsResolver
    {
        private const string LOG_IDENT = "DnsResolver";

        private static readonly ConcurrentDictionary<string, DnsCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<string> _cacheOrder = new();
        private const int MaxCacheEntries = 128;
        private const int MaxDohResponseBytes = 65536;
        private static readonly HttpClient _dohClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(5));
        private static int _networkVersion = -1;

        private sealed class DnsCacheEntry(string ip, DateTime expiry)
        {
            public string Ip = ip;
            public DateTime Expiry = expiry;
        }

        public static async Task<string?> ResolveAsync(string host, CancellationToken ct = default)
        {
            RefreshForNetworkChange();
            if (_cache.TryGetValue(host, out var cached) && DateTime.UtcNow < cached.Expiry)
                return cached.Ip;

            string? ip = await TrySystemDnsAsync(host, ct).ConfigureAwait(false);
            if (ip is not null)
            {
                Store(host, ip, DateTime.UtcNow.AddMinutes(2));
                return ip;
            }

            ip = await TryDohAsync(host, ct).ConfigureAwait(false);
            if (ip is not null)
            {
                Store(host, ip, DateTime.UtcNow.AddMinutes(5));
                return ip;
            }

            ip = await TryUdpDnsAsync(host, ct).ConfigureAwait(false);
            if (ip is not null)
            {
                Store(host, ip, DateTime.UtcNow.AddMinutes(10));
                return ip;
            }

            return null;
        }

        public static async Task<Dictionary<string, string>> ResolveAllAsync(IEnumerable<string> hosts, CancellationToken ct = default)
        {
            var results = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tasks = hosts.Where(host => !string.IsNullOrWhiteSpace(host)).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).Select(async host =>
            {
                string? ip = await ResolveAsync(host, ct).ConfigureAwait(false);
                if (ip is not null)
                    results[host] = ip;
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return new Dictionary<string, string>(results, StringComparer.OrdinalIgnoreCase);
        }

        public static async Task<string?> ResolveDirectAsync(string host, CancellationToken ct = default)
        {
            RefreshForNetworkChange();
            string? ip = await TryDohAsync(host, ct).ConfigureAwait(false);
            if (ip is not null)
            {
                Store(host, ip, DateTime.UtcNow.AddMinutes(5));
                return ip;
            }

            ip = await TryUdpDnsAsync(host, ct).ConfigureAwait(false);
            if (ip is not null)
            {
                Store(host, ip, DateTime.UtcNow.AddMinutes(10));
                return ip;
            }

            return null;
        }

        public static void Invalidate(string host)
        {
            _cache.TryRemove(host, out _);
        }

        public static void Clear()
        {
            _cache.Clear();
            while (_cacheOrder.TryDequeue(out _))
            {
            }
        }

        private static void RefreshForNetworkChange()
        {
            int version = Voidstrap.Utility.VpnHttpClient.NetworkVersion;
            if (Volatile.Read(ref _networkVersion) == version)
                return;
            Clear();
            Volatile.Write(ref _networkVersion, version);
        }

        private static void Store(string host, string ip, DateTime expiry)
        {
            DnsCacheEntry entry = new(ip, expiry);
            if (_cache.TryAdd(host, entry))
                _cacheOrder.Enqueue(host);
            else
                _cache[host] = entry;
            while (_cache.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out string? oldest))
                _cache.TryRemove(oldest, out _);
        }

        private static async Task<string?> TrySystemDnsAsync(string host, CancellationToken ct)
        {
            try
            {
                var entries = await Dns.GetHostEntryAsync(host, ct).ConfigureAwait(false);
                var ipv4 = entries.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                return ipv4?.ToString();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> TryDohAsync(string host, CancellationToken ct)
        {
            try
            {
                string url = $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/dns-json");

                using var resp = await _dohClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                byte[]? payload = await ReadBoundedAsync(resp.Content, ct).ConfigureAwait(false);
                if (payload == null)
                    return null;
                using var doc = JsonDocument.Parse(payload);

                if (doc.RootElement.TryGetProperty("Answer", out var answers))
                {
                    foreach (var a in answers.EnumerateArray())
                    {
                        if (a.TryGetProperty("type", out var t) && t.GetInt32() == 1
                            && a.TryGetProperty("data", out var d) && d.GetString() is string ip && ip.Length > 0)
                        {
                            return ip;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, $"DoH resolve failed for {host}: {ex.Message}");
            }

            return null;
        }

        private static async Task<string?> TryUdpDnsAsync(string host, CancellationToken ct)
        {
            string[] resolvers = ["1.1.1.1", "8.8.8.8", "9.9.9.9"];

            foreach (string resolver in resolvers)
            {
                try
                {
                    using var udp = new UdpClient();
					using CancellationTokenSource resolverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
					resolverCts.CancelAfter(TimeSpan.FromSeconds(1));
                    udp.Client.SendTimeout = 3000;
                    udp.Client.ReceiveTimeout = 3000;
                    udp.Connect(resolver, 53);

                    byte[] query = BuildDnsQuery(host, out ushort queryId);
                    ct.ThrowIfCancellationRequested();
                    await udp.SendAsync(query, resolverCts.Token).ConfigureAwait(false);

					var result = await udp.ReceiveAsync(resolverCts.Token).ConfigureAwait(false);
                    string? ip = ParseDnsResponse(result.Buffer, queryId);
                    if (ip is not null)
                        return ip;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
				catch (OperationCanceledException)
				{
					App.Logger?.WriteLine(LOG_IDENT, $"UDP DNS via {resolver} timed out for {host}");
				}
                catch (Exception ex)
                {
                    App.Logger?.WriteLine(LOG_IDENT, $"UDP DNS via {resolver} failed for {host}: {ex.Message}");
                }
            }

            return null;
        }

        private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken token)
        {
            if (content.Headers.ContentLength is long contentLength && (contentLength <= 0 || contentLength > MaxDohResponseBytes))
                return null;
            await using Stream input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using MemoryStream output = new(content.Headers.ContentLength is long length ? (int)length : 8192);
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0)
                    return output.Length == 0 ? null : output.ToArray();
                if (output.Length + read > MaxDohResponseBytes)
                    return null;
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }

        private static byte[] BuildDnsQuery(string host, out ushort id)
        {
            id = (ushort)Random.Shared.Next(1, 65536);
            using var ms = new MemoryStream();
            var w = new BigEndianWriter(ms);

            w.WriteUInt16(id);
            w.WriteUInt16(0x0100);
            w.WriteUInt16(1);
            w.WriteUInt16(0);
            w.WriteUInt16(0);
            w.WriteUInt16(0);

            foreach (string label in host.Split('.'))
            {
                if (label.Length is 0 or > 63)
                {
                    continue;
                }
                w.WriteByte((byte)label.Length);
                w.WriteBytes(Encoding.ASCII.GetBytes(label));
            }
            w.WriteByte(0);
            w.WriteUInt16(1);
            w.WriteUInt16(1);

            return ms.ToArray();
        }

        private static string? ParseDnsResponse(byte[] data, ushort expectedId)
        {
            if (data.Length < 12)
                return null;

            if (((data[0] << 8) | data[1]) != expectedId)
                return null;

            int answerCount = (data[6] << 8) | data[7];
            if (answerCount == 0)
                return null;

            int pos = 12;
            while (pos < data.Length)
            {
                if (data[pos] == 0)
                {
                    pos++;
                    break;
                }
                if ((data[pos] & 0xC0) == 0xC0)
                {
                    pos += 2;
                    break;
                }
                pos += data[pos] + 1;
            }

            for (int i = 0; i < answerCount && pos + 12 <= data.Length; i++)
            {
                if ((data[pos] & 0xC0) == 0xC0)
                    pos += 2;
                else
                {
                    while (pos < data.Length && data[pos] != 0)
                    {
                        if ((data[pos] & 0xC0) == 0xC0) { pos += 2; break; }
                        pos += data[pos] + 1;
                    }
                    pos++;
                }

                if (pos + 10 > data.Length) break;

                ushort type = (ushort)((data[pos] << 8) | data[pos + 1]);
                ushort dataLen = (ushort)((data[pos + 8] << 8) | data[pos + 9]);
                pos += 10;

                if (type == 1 && dataLen == 4 && pos + 4 <= data.Length)
                    return $"{data[pos]}.{data[pos + 1]}.{data[pos + 2]}.{data[pos + 3]}";

                pos += dataLen;
            }

            return null;
        }

        private readonly struct BigEndianWriter(MemoryStream ms) : IDisposable
        {
            private readonly MemoryStream _ms = ms;

            public void WriteUInt16(ushort v)
            {
                _ms.WriteByte((byte)(v >> 8));
                _ms.WriteByte((byte)v);
            }

            public void WriteByte(byte v) => _ms.WriteByte(v);
            public void WriteBytes(byte[] v) => _ms.Write(v, 0, v.Length);

            public void Dispose() { }
        }
    }
}
