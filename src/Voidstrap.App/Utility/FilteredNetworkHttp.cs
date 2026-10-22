using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

public static class FilteredNetworkHttp
{
	private const int HelloChunkBytes = 8;

	private static readonly string[] Resolvers =
	[
		"https://1.1.1.1/dns-query?type=A&name=",
		"https://8.8.8.8/resolve?type=A&name=",
		"https://1.0.0.1/dns-query?type=A&name="
	];

	private static readonly TimeSpan ResolveCacheAge = TimeSpan.FromMinutes(10);

	private static readonly ConcurrentDictionary<string, (IPAddress[] Addresses, DateTime Expires)> ResolveCache = new(StringComparer.OrdinalIgnoreCase);

	private static readonly HttpClient ResolverClient = CreateResolverClient();

	public static HttpClient Client { get; } = CreateClient();

	private static HttpClient CreateResolverClient()
	{
		HttpClient client = new(new SocketsHttpHandler
		{
			UseProxy = false,
			ConnectTimeout = TimeSpan.FromSeconds(5)
		})
		{
			Timeout = TimeSpan.FromSeconds(8)
		};
		client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/dns-json");
		return client;
	}

	private static HttpClient CreateClient()
	{
		SocketsHttpHandler handler = new()
		{
			UseProxy = false,
			ConnectTimeout = TimeSpan.FromSeconds(8),
			PooledConnectionLifetime = TimeSpan.FromMinutes(5),
			ConnectCallback = ConnectAsync
		};
		HttpClient client = new(handler)
		{
			Timeout = TimeSpan.FromSeconds(20),
			DefaultRequestVersion = HttpVersion.Version11,
			DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
		};
		client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VoidstrapUpdater/2.0");
		client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
		return client;
	}

	private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
	{
		IPAddress[] addresses = await ResolveAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
		Exception? last = null;
		foreach (IPAddress address in addresses)
		{
			Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
			try
			{
				await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
				return new SplitHelloStream(new NetworkStream(socket, ownsSocket: true));
			}
			catch (Exception ex) when (ex is SocketException or IOException)
			{
				last = ex;
				socket.Dispose();
			}
			catch
			{
				socket.Dispose();
				throw;
			}
		}
		throw new HttpRequestException("Could not connect to " + context.DnsEndPoint.Host, last);
	}

	private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
	{
		if (IPAddress.TryParse(host, out IPAddress? literal))
			return [literal];
		if (ResolveCache.TryGetValue(host, out var cached) && cached.Expires > DateTime.UtcNow)
			return cached.Addresses;
		Exception? last = null;
		foreach (string resolver in Resolvers)
		{
			try
			{
				using HttpResponseMessage response = await ResolverClient.GetAsync(resolver + Uri.EscapeDataString(host), cancellationToken).ConfigureAwait(false);
				response.EnsureSuccessStatusCode();
				using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
				List<IPAddress> found = [];
				if (document.RootElement.TryGetProperty("Answer", out JsonElement answers) && answers.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement answer in answers.EnumerateArray())
					{
						if (answer.TryGetProperty("type", out JsonElement type) && type.GetInt32() == 1
							&& answer.TryGetProperty("data", out JsonElement data)
							&& IPAddress.TryParse(data.GetString(), out IPAddress? address))
							found.Add(address);
					}
				}
				if (found.Count > 0)
				{
					IPAddress[] result = found.Distinct().ToArray();
					ResolveCache[host] = (result, DateTime.UtcNow + ResolveCacheAge);
					return result;
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				last = ex;
			}
		}
		throw new HttpRequestException("Could not resolve " + host + " over encrypted DNS", last);
	}

	private sealed class SplitHelloStream : Stream
	{
		private readonly Stream _inner;
		private bool _helloSent;

		public SplitHelloStream(Stream inner)
		{
			_inner = inner;
		}

		public override bool CanRead => _inner.CanRead;
		public override bool CanSeek => false;
		public override bool CanWrite => _inner.CanWrite;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override void Flush() => _inner.Flush();
		public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
		public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count)
		{
			WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
		}

		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
		}

		public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_helloSent)
			{
				await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
				return;
			}
			_helloSent = true;
			for (int offset = 0; offset < buffer.Length; offset += HelloChunkBytes)
			{
				await _inner.WriteAsync(buffer.Slice(offset, Math.Min(HelloChunkBytes, buffer.Length - offset)), cancellationToken).ConfigureAwait(false);
				await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				_inner.Dispose();
			base.Dispose(disposing);
		}

		public override ValueTask DisposeAsync() => _inner.DisposeAsync();
	}
}
