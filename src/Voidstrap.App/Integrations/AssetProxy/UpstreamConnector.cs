using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.AssetProxy
{
    public enum UpstreamConnectorType
    {
        DirectIp = 0,
        HttpConnect = 1,
        Socks5 = 2,
        Auto = 3
    }

    public sealed class UpstreamConnection(TcpClient tcp, SslStream ssl) : IDisposable
    {
        private bool _disposed;

        public TcpClient Tcp { get; } = tcp;
        public SslStream Ssl { get; } = ssl;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { Ssl.Dispose(); } catch { }
            try { Tcp.Dispose(); } catch { }
            GC.SuppressFinalize(this);
        }
    }

    public static class UpstreamConnector
    {
        private const string LOG_IDENT = "UpstreamConnector";

        private static readonly RemoteCertificateValidationCallback _validateCertificate =
            (sender, certificate, chain, errors) => errors == SslPolicyErrors.None;

        public static UpstreamConnectorType ConnectorType { get; set; } = UpstreamConnectorType.DirectIp;

        public static string HttpConnectProxyHost { get; set; } = "";
        public static int HttpConnectProxyPort { get; set; } = 3128;

        public static string Socks5ProxyHost { get; set; } = "";
        public static int Socks5ProxyPort { get; set; } = 1080;

        public static async Task<UpstreamConnection?> ConnectAsync(string host, int port, CancellationToken ct = default)
		{
			return await ConnectAsync(host, port, null, ct).ConfigureAwait(false);
		}

		public static async Task<UpstreamConnection?> ConnectAsync(string host, int port, string? forcedIp, CancellationToken ct = default)
        {
			try
			{
				switch (ConnectorType)
				{
					case UpstreamConnectorType.HttpConnect:
						return await WithDeadlineAsync(token => HttpConnectAsync(host, port, token), ct).ConfigureAwait(false);
					case UpstreamConnectorType.Socks5:
						return await WithDeadlineAsync(token => Socks5ConnectAsync(host, port, token), ct).ConfigureAwait(false);
					case UpstreamConnectorType.Auto:
						UpstreamConnection? result = await WithDeadlineAsync(token => DirectConnectAsync(host, port, forcedIp, token), ct).ConfigureAwait(false);
						if (result is not null)
							return result;
						result = await WithDeadlineAsync(token => HttpConnectAsync(host, port, token), ct).ConfigureAwait(false);
						if (result is not null)
							return result;
						return await WithDeadlineAsync(token => Socks5ConnectAsync(host, port, token), ct).ConfigureAwait(false);
					default:
						return await WithDeadlineAsync(token => DirectConnectAsync(host, port, forcedIp, token), ct).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Upstream connection timed out for " + host);
				return null;
			}
        }

		private static async Task<UpstreamConnection?> WithDeadlineAsync(Func<CancellationToken, Task<UpstreamConnection?>> attempt, CancellationToken ct)
		{
			using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
			deadline.CancelAfter(TimeSpan.FromSeconds(12));
			try
			{
				return await attempt(deadline.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Upstream connection attempt timed out");
				return null;
			}
		}

        public static async Task<UpstreamConnection?> DirectConnectAsync(string host, int port, string? forcedIp, CancellationToken ct = default)
        {
			TcpClient? tcp = null;
			SslStream? ssl = null;
            try
            {
                string ip = forcedIp ?? await DnsResolver.ResolveAsync(host, ct).ConfigureAwait(false) ?? host;

				tcp = new TcpClient();
                await tcp.ConnectAsync(ip, port, ct).ConfigureAwait(false);
                tcp.NoDelay = true;

                var raw = tcp.GetStream();
				ssl = new SslStream(raw, false, _validateCertificate);

                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, ct).ConfigureAwait(false);

				UpstreamConnection result = new(tcp, ssl);
				tcp = null;
				ssl = null;
				return result;
            }
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, $"Direct connect to {host} failed: {ex.Message}");
                return null;
            }
			finally
			{
				ssl?.Dispose();
				tcp?.Dispose();
			}
        }

        private static async Task<UpstreamConnection?> HttpConnectAsync(string targetHost, int targetPort, CancellationToken ct)
        {
            string proxyHost = HttpConnectProxyHost;
            int proxyPort = HttpConnectProxyPort;

            if (string.IsNullOrWhiteSpace(proxyHost))
                return null;

			TcpClient? tcp = null;
			SslStream? ssl = null;
			try
            {
				tcp = new TcpClient();
                await tcp.ConnectAsync(proxyHost, proxyPort, ct).ConfigureAwait(false);
                tcp.NoDelay = true;

                var raw = tcp.GetStream();

                string connectReq = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n\r\n";
                byte[] reqBytes = Encoding.ASCII.GetBytes(connectReq);
                await raw.WriteAsync(reqBytes, ct).ConfigureAwait(false);
                await raw.FlushAsync(ct).ConfigureAwait(false);

                var buffer = new byte[4096];
                int total = 0;
                int end = -1;

                while (total < buffer.Length)
                {
                    int read = await raw.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    total += read;

                    for (int i = 3; i < total; i++)
                    {
                        if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n'
                            && buffer[i - 1] == '\r' && buffer[i] == '\n')
                        {
                            end = i - 3;
                            break;
                        }
                    }
                    if (end >= 0)
                        break;
                }

                if (end < 0)
                {
                    tcp.Dispose();
                    return null;
                }

                string response = Encoding.ASCII.GetString(buffer, 0, end);
                if (!response.StartsWith("HTTP/1.1 200") && !response.StartsWith("HTTP/1.0 200"))
                {
                    App.Logger?.WriteLine(LOG_IDENT, $"HTTP CONNECT to {targetHost} failed: {response.Split('\n')[0]}");
                    tcp.Dispose();
                    return null;
                }

				ssl = new SslStream(raw, false, _validateCertificate);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, ct).ConfigureAwait(false);

				UpstreamConnection result = new(tcp, ssl);
				tcp = null;
				ssl = null;
				return result;
            }
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, $"HTTP CONNECT to {targetHost} via {proxyHost}:{proxyPort} failed: {ex.Message}");
                return null;
            }
			finally
			{
				ssl?.Dispose();
				tcp?.Dispose();
			}
        }

        private static async Task<UpstreamConnection?> Socks5ConnectAsync(string targetHost, int targetPort, CancellationToken ct)
        {
            string proxyHost = Socks5ProxyHost;
            int proxyPort = Socks5ProxyPort;

            if (string.IsNullOrWhiteSpace(proxyHost))
                return null;

			TcpClient? tcp = null;
			SslStream? ssl = null;
			try
            {
				tcp = new TcpClient();
                await tcp.ConnectAsync(proxyHost, proxyPort, ct).ConfigureAwait(false);
                tcp.NoDelay = true;

                var raw = tcp.GetStream();

                byte[] greeting = [5, 1, 0];
                await raw.WriteAsync(greeting, ct).ConfigureAwait(false);
                await raw.FlushAsync(ct).ConfigureAwait(false);

                var resp = new byte[2];
                int got = 0;
                while (got < 2)
                {
                    int r = await raw.ReadAsync(resp.AsMemory(got, 2 - got), ct).ConfigureAwait(false);
                    if (r <= 0)
                    {
                        tcp.Dispose();
                        return null;
                    }
                    got += r;
                }

                if (resp[0] != 5 || resp[1] != 0)
                {
                    tcp.Dispose();
                    return null;
                }

                byte[] addrBytes = Encoding.ASCII.GetBytes(targetHost);
				if (addrBytes.Length == 0 || addrBytes.Length > byte.MaxValue)
				{
					tcp.Dispose();
					return null;
				}
				var connectReq = new byte[7 + addrBytes.Length];
                connectReq[0] = 5;
                connectReq[1] = 1;
                connectReq[2] = 0;
                connectReq[3] = 3;
                connectReq[4] = (byte)addrBytes.Length;
                Array.Copy(addrBytes, 0, connectReq, 5, addrBytes.Length);
                connectReq[5 + addrBytes.Length] = (byte)(targetPort >> 8);
                connectReq[6 + addrBytes.Length] = (byte)targetPort;

                await raw.WriteAsync(connectReq, ct).ConfigureAwait(false);
                await raw.FlushAsync(ct).ConfigureAwait(false);

				byte[] reply = new byte[4];
				if (!await ReadExactAsync(raw, reply, ct).ConfigureAwait(false) || reply[0] != 5 || reply[1] != 0 || reply[2] != 0)
				{
					tcp.Dispose();
					return null;
				}
				int addressLength;
				switch (reply[3])
				{
					case 1:
						addressLength = 4;
						break;
					case 4:
						addressLength = 16;
						break;
					case 3:
						byte[] domainLength = new byte[1];
						if (!await ReadExactAsync(raw, domainLength, ct).ConfigureAwait(false))
						{
							tcp.Dispose();
							return null;
						}
						addressLength = domainLength[0];
						break;
					default:
						tcp.Dispose();
						return null;
				}
				byte[] endpoint = new byte[addressLength + 2];
				if (!await ReadExactAsync(raw, endpoint, ct).ConfigureAwait(false))
				{
					tcp.Dispose();
					return null;
				}

				ssl = new SslStream(raw, false, _validateCertificate);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, ct).ConfigureAwait(false);

				UpstreamConnection result = new(tcp, ssl);
				tcp = null;
				ssl = null;
				return result;
            }
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, $"SOCKS5 to {targetHost} via {proxyHost}:{proxyPort} failed: {ex.Message}");
                return null;
            }
			finally
			{
				ssl?.Dispose();
				tcp?.Dispose();
			}
        }

		private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
		{
			int offset = 0;
			while (offset < buffer.Length)
			{
				int read = await stream.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
				if (read == 0)
				{
					return false;
				}
				offset += read;
			}
			return true;
		}
    }
}
