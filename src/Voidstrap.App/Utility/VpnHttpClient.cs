using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

public static class VpnHttpClient
{
	private static int _networkVersion;
	private static int _initialized;

	public static int NetworkVersion => Volatile.Read(ref _networkVersion);

	public static void Initialize()
	{
		if (Interlocked.Exchange(ref _initialized, 1) != 0)
			return;
		NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
		NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
	}

	public static void Shutdown()
	{
		if (Interlocked.Exchange(ref _initialized, 0) == 0)
			return;
		NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
		NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
		Interlocked.Increment(ref _networkVersion);
	}

	public static HttpClient Create(TimeSpan? timeout = null, Action<SocketsHttpHandler>? configure = null, bool log = false)
	{
		HttpMessageHandler handler = new VpnResilientHandler(configure);
		if (log)
			handler = new HttpClientLoggingHandler(handler);
		return new HttpClient(handler)
		{
			Timeout = timeout ?? TimeSpan.FromSeconds(30)
		};
	}

	private static void OnNetworkAddressChanged(object? sender, EventArgs e)
	{
		Interlocked.Increment(ref _networkVersion);
	}

	private static void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
	{
		Interlocked.Increment(ref _networkVersion);
	}

	private sealed class VpnResilientHandler : HttpMessageHandler
	{
		private readonly Action<SocketsHttpHandler>? _configure;
		private readonly object _sync = new();
		private readonly Queue<Transport> _retired = new();
		private Transport? _transport;
		private bool _disposed;

		public VpnResilientHandler(Action<SocketsHttpHandler>? configure)
		{
			_configure = configure;
		}

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			bool retryable = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
			RequestSnapshot? snapshot = retryable ? RequestSnapshot.Capture(request) : null;
			for (int attempt = 0; ; attempt++)
			{
				HttpRequestMessage current = attempt == 0 ? request : snapshot!.Create();
				Transport transport = GetTransport();
				try
				{
					HttpResponseMessage response = await transport.Invoker.SendAsync(current, cancellationToken).ConfigureAwait(false);
					if (!retryable || attempt >= 2 || !IsTransient(response.StatusCode))
						return response;
					response.Dispose();
					RefreshTransport(transport);
				}
				catch (Exception ex) when (retryable && attempt < 2 && IsTransient(ex, cancellationToken))
				{
					RefreshTransport(transport);
				}
				finally
				{
					if (attempt != 0)
						current.Dispose();
				}
				await Task.Delay(attempt == 0 ? 150 : 450, cancellationToken).ConfigureAwait(false);
			}
		}

		private Transport GetTransport()
		{
			int version = NetworkVersion;
			Transport? current = Volatile.Read(ref _transport);
			if (current is not null && current.Version == version)
				return current;
			lock (_sync)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				current = _transport;
				if (current is not null && current.Version == version)
					return current;
				Transport replacement = CreateTransport(version);
				_transport = replacement;
				if (current is not null)
					Retire(current);
				return replacement;
			}
		}

		private void RefreshTransport(Transport failed)
		{
			lock (_sync)
			{
				if (_disposed || !ReferenceEquals(_transport, failed))
					return;
				_transport = CreateTransport(NetworkVersion);
				Retire(failed);
			}
		}

		private void Retire(Transport transport)
		{
			_retired.Enqueue(transport);
			while (_retired.Count > 16)
				_retired.Dequeue().Dispose();
		}

		private Transport CreateTransport(int version)
		{
			SocketsHttpHandler handler = new()
			{
				AutomaticDecompression = DecompressionMethods.All,
				UseProxy = true,
				Proxy = null,
				DefaultProxyCredentials = CredentialCache.DefaultCredentials,
				ConnectTimeout = TimeSpan.FromSeconds(10),
				PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
				PooledConnectionLifetime = TimeSpan.FromMinutes(2),
				KeepAlivePingDelay = TimeSpan.FromSeconds(30),
				KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
				KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
				EnableMultipleHttp2Connections = true,
				MaxConnectionsPerServer = 512
			};
			_configure?.Invoke(handler);
			return new Transport(version, new HttpMessageInvoker(handler, true));
		}

		private static bool IsTransient(HttpStatusCode status)
		{
			return status is HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
		}

		private static bool IsTransient(Exception exception, CancellationToken token)
		{
			if (token.IsCancellationRequested)
				return false;
			return exception is HttpRequestException or IOException or SocketException or TimeoutException or TaskCanceledException;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				lock (_sync)
				{
					if (!_disposed)
					{
						_disposed = true;
						_transport?.Dispose();
						_transport = null;
						while (_retired.TryDequeue(out Transport? retired))
							retired.Dispose();
					}
				}
			}
			base.Dispose(disposing);
		}
	}

	private sealed class Transport(int version, HttpMessageInvoker invoker) : IDisposable
	{
		public int Version { get; } = version;
		public HttpMessageInvoker Invoker { get; } = invoker;
		public void Dispose() => Invoker.Dispose();
	}

	private sealed class RequestSnapshot
	{
		private readonly HttpMethod _method;
		private readonly Uri? _uri;
		private readonly Version _version;
		private readonly HttpVersionPolicy _versionPolicy;
		private readonly KeyValuePair<string, string[]>[] _headers;
		private readonly KeyValuePair<string, object?>[] _options;

		private RequestSnapshot(HttpRequestMessage request)
		{
			_method = request.Method;
			_uri = request.RequestUri;
			_version = request.Version;
			_versionPolicy = request.VersionPolicy;
			_headers = request.Headers.Select(entry => new KeyValuePair<string, string[]>(entry.Key, entry.Value.ToArray())).ToArray();
			_options = request.Options.Select(entry => new KeyValuePair<string, object?>(entry.Key, entry.Value)).ToArray();
		}

		public static RequestSnapshot Capture(HttpRequestMessage request) => new(request);

		public HttpRequestMessage Create()
		{
			HttpRequestMessage request = new(_method, _uri)
			{
				Version = _version,
				VersionPolicy = _versionPolicy
			};
			foreach (KeyValuePair<string, string[]> header in _headers)
				request.Headers.TryAddWithoutValidation(header.Key, header.Value);
			foreach (KeyValuePair<string, object?> option in _options)
				request.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
			return request;
		}
	}
}
