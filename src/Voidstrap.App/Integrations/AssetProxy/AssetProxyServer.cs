using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Core.AssetProxy;
using Voidstrap.Core.Networking;
using ZstdSharp;

namespace Voidstrap.Integrations.AssetProxy;

public static class AssetProxyServer
{
	public static bool IsRequired =>
		Voidstrap.Utility.Platform.IsWindows &&
		App.Settings.Prop.AssetWarpEnabled &&
		App.Settings.Prop.AssetWarpCertificateApproved &&
		(App.Settings.Prop.AssetWarpDisableAllTextures ||
		App.Settings.Prop.AssetWarpDisableAllDecals ||
		App.Settings.Prop.AssetWarpDisableAllImages ||
		App.Settings.Prop.AssetWarpDisableAllAnimations ||
		App.Settings.Prop.AssetWarpDisableAllMeshes ||
		TextureStripper.HasConfiguredRules ||
		App.Settings.Prop.AssetWarpPreloadEnabled ||
		PresenceSpoofer.IsEnabled() ||
		UsernameSpoofer.IsEnabled());

	private sealed class HttpHead
	{
		public required string FirstLine { get; init; }

		public required List<string> Lines { get; init; }

		public required byte[] Raw { get; init; }

		public string Get(string name)
		{
			foreach (string line in Lines)
			{
				int separator = line.IndexOf(':');
				if (separator > 0 && line.AsSpan(0, separator).Equals(name, StringComparison.OrdinalIgnoreCase))
				{
					return line[(separator + 1)..].Trim();
				}
			}
			return "";
		}
	}

	private sealed record MessageBody(byte[] Raw, byte[] Decoded, bool Chunked);

	private sealed class BufferedConnection(Stream stream)
	{
		private readonly Stream _stream = stream;

		private readonly byte[] _buffer = new byte[65536];

		private int _offset;

		private int _count;

		public async Task<byte[]> ReadHeaderAsync(CancellationToken ct)
		{
			using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
			deadline.CancelAfter(TimeSpan.FromSeconds(90));
			CancellationToken readToken = deadline.Token;
			using MemoryStream output = new();
			int state = 0;
			while (output.Length < 65536)
			{
				int value = await ReadByteAsync(readToken).ConfigureAwait(false);
				if (value < 0)
				{
					return output.ToArray();
				}
				output.WriteByte((byte)value);
				state = state switch
				{
					0 when value == 13 => 1,
					1 when value == 10 => 2,
					2 when value == 13 => 3,
					3 when value == 10 => 4,
					_ when value == 13 => 1,
					_ => 0
				};
				if (state == 4)
				{
					return output.ToArray();
				}
			}
			throw new InvalidDataException("HTTP header exceeds the AssetWarp limit");
		}

		public async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
		{
			byte[] result = new byte[count];
			int written = 0;
			while (written < count)
			{
				if (_offset < _count)
				{
					int available = Math.Min(count - written, _count - _offset);
					Buffer.BlockCopy(_buffer, _offset, result, written, available);
					_offset += available;
					written += available;
					continue;
				}
				_count = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
				_offset = 0;
				if (_count == 0)
				{
					throw new EndOfStreamException();
				}
			}
			return result;
		}

		public async Task<byte[]?> CopyExactToAsync(long count, Stream destination, int captureLimit, CancellationToken ct)
		{
			MemoryStream? capture = count > 0 && count <= captureLimit ? new MemoryStream((int)count) : null;
			try
			{
				long remaining = count;
				while (remaining > 0)
				{
					if (_offset >= _count)
					{
						_count = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
						_offset = 0;
						if (_count == 0)
						{
							throw new EndOfStreamException();
						}
					}
					int available = (int)Math.Min(remaining, _count - _offset);
					ReadOnlyMemory<byte> chunk = _buffer.AsMemory(_offset, available);
					await destination.WriteAsync(chunk, ct).ConfigureAwait(false);
					if (capture != null)
					{
						await capture.WriteAsync(chunk, ct).ConfigureAwait(false);
					}
					_offset += available;
					remaining -= available;
				}
				return capture?.ToArray();
			}
			finally
			{
				capture?.Dispose();
			}
		}

		public async Task<string> ReadLineAsync(CancellationToken ct)
		{
			using MemoryStream output = new();
			while (output.Length < 8192)
			{
				int value = await ReadByteAsync(ct).ConfigureAwait(false);
				if (value < 0)
					throw new EndOfStreamException();
				if (value == 10)
					return Encoding.ASCII.GetString(output.ToArray());
				if (value != 13)
					output.WriteByte((byte)value);
			}
			throw new InvalidDataException("HTTP line exceeds the AssetWarp limit");
		}

		public async Task CopyChunkedToAsync(Stream destination, CancellationToken ct)
		{
			long total = 0;
			int trailerBytesTotal = 0;
			while (true)
			{
				string line = await ReadLineAsync(ct).ConfigureAwait(false);
				byte[] lineBytes = Encoding.ASCII.GetBytes(line + "\r\n");
				await destination.WriteAsync(lineBytes, ct).ConfigureAwait(false);
				string sizeText = line.Split(';', 2)[0].Trim();
				if (!long.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long size) || size < 0 || size > MaxStreamedBodyBytes - total)
				{
					throw new InvalidDataException("Invalid chunk size");
				}
				if (size == 0)
				{
					while (true)
					{
						string trailer = await ReadLineAsync(ct).ConfigureAwait(false);
						trailerBytesTotal += trailer.Length + 2;
						if (trailerBytesTotal > 65536)
							throw new InvalidDataException("HTTP trailers exceed the AssetWarp limit");
						byte[] trailerBytes = Encoding.ASCII.GetBytes(trailer + "\r\n");
						await destination.WriteAsync(trailerBytes, ct).ConfigureAwait(false);
						if (trailer.Length == 0)
						{
							return;
						}
					}
				}
				total += size;
				await CopyExactToAsync(size, destination, 0, ct).ConfigureAwait(false);
				byte[] ending = await ReadExactAsync(2, ct).ConfigureAwait(false);
				if (ending[0] != 13 || ending[1] != 10)
					throw new InvalidDataException("Invalid chunk terminator");
				await destination.WriteAsync(ending, ct).ConfigureAwait(false);
			}
		}

		public async Task<byte[]> ReadToEndAsync(int maximum, CancellationToken ct)
		{
			using MemoryStream output = new();
			if (_offset < _count)
			{
				output.Write(_buffer, _offset, _count - _offset);
				_offset = _count;
			}
			while (output.Length <= maximum)
			{
				int read = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
				if (read == 0)
				{
					return output.ToArray();
				}
				output.Write(_buffer, 0, read);
			}
			throw new InvalidDataException("HTTP body exceeds the AssetWarp limit");
		}

		private async Task<int> ReadByteAsync(CancellationToken ct)
		{
			if (_offset >= _count)
			{
				_count = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
				_offset = 0;
				if (_count == 0)
				{
					return -1;
				}
			}
			return _buffer[_offset++];
		}
	}

	private const string LogIdent = "AssetProxy";

	private const int ListenPort = 443;

	private const int MaxBodyBytes = 33554432;

	private const int MaxCapturedAssetBytes = 33554432;

	private const long MaxStreamedBodyBytes = 4294967296;

	private static readonly string[] BaseHosts =
	[
		"assetdelivery.roblox.com",
		"fts.rbxcdn.com",
		"contentdelivery.roblox.com"
	];

	private static readonly System.Threading.Lock Gate = new();

	static AssetProxyServer()
	{
		try
		{
			AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
		}
		catch
		{
		}
	}

	private static void OnProcessExit(object? sender, EventArgs e)
	{
		AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
		Stop();
	}

	private static readonly SemaphoreSlim Lifecycle = new(1, 1);

	private static readonly SemaphoreSlim ClientSlots = new(64, 64);

	private static readonly ConcurrentDictionary<int, Task> ClientTasks = new();

	private static readonly ConcurrentDictionary<int, TcpClient> ClientConnections = new();

	private static List<TcpListener> _listeners = [];

	private static List<Task> _acceptLoops = [];

	private static CancellationTokenSource? _cts;
	private static CancellationTokenSource? _startupCts;
	private static FileStream? _ownershipLock;
	private static CancellationTokenSource? _reconcileCts;

	private static ConcurrentDictionary<string, string> _upstreamIps = new(StringComparer.OrdinalIgnoreCase);
	private static int _upstreamNetworkVersion = -1;

	private static string[] _hosts = [];

	private static int _nextClientId;

	private static string _lastTlsFailure = "";

	private static volatile bool _running;

	private static int _explicitPort;

	public static bool IsRunning => _running;

	public static int Port => ListenPort;

	public static bool UsesExplicitProxy => Voidstrap.Utility.Platform.IsLinux;

	public static int ExplicitPort => Volatile.Read(ref _explicitPort);

	public static Uri? ExplicitProxyAddress
	{
		get
		{
			int port = ExplicitPort;
			return port > 0 ? new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)) : null;
		}
	}

	public static async Task StartAsync(CancellationToken ct)
	{
		await Lifecycle.WaitAsync(ct).ConfigureAwait(false);
		using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
		try
		{
			lock (Gate)
			{
				_startupCts = startup;
			}
			try
			{
				await StartCoreAsync(startup.Token).ConfigureAwait(false);
			}
			catch
			{
				ReleaseOwnership();
				throw;
			}
		}
		finally
		{
			lock (Gate)
			{
				if (ReferenceEquals(_startupCts, startup))
				{
					_startupCts = null;
				}
			}
			Lifecycle.Release();
		}
	}

	private static async Task StartCoreAsync(CancellationToken ct)
	{
		if (App.LaunchSettings?.RobloxLaunchMode != Voidstrap.Enums.LaunchMode.Player)
			return;
		if (!IsRequired)
		{
			AssetProxyRouting.Cleanup();
			return;
		}
		lock (Gate)
		{
			if (_running)
			{
				return;
			}
		}
		if (!TryAcquireOwnership(out FileStream? ownership))
			throw new IOException("Another AssetWarp proxy session is already active");
		lock (Gate)
			_ownershipLock = ownership;

		List<string> hostList = [];
		if (TextureStripper.IsEnabled || TextureStripper.HasConfiguredRules || App.Settings.Prop.AssetWarpPreloadEnabled)
		{
			hostList.AddRange(BaseHosts);
		}
		UsernameSpoofState usernameState = UsernameSpoofer.CurrentState;
		if (usernameState.ProfileEnabled || PresenceSpoofer.IsEnabled())
		{
			hostList.Add("apis.roblox.com");
		}
		if (usernameState.SelfGameCreator)
		{
			hostList.Add("gamejoin.roblox.com");
		}
		string[] hosts = [.. hostList.Distinct(StringComparer.OrdinalIgnoreCase)];
		if (hosts.Length == 0)
		{
			App.Logger?.WriteLine(LogIdent, "No AssetWarp feature needs a routed host, proxy not started");
			ReleaseOwnership();
			AssetProxyRouting.Cleanup();
			return;
		}
		UpstreamConnector.ConnectorType = Enum.IsDefined(typeof(UpstreamConnectorType), App.Settings.Prop.ProxyConnectorType) ? (UpstreamConnectorType)App.Settings.Prop.ProxyConnectorType : UpstreamConnectorType.DirectIp;
		UpstreamConnector.HttpConnectProxyHost = App.Settings.Prop.ProxyHttpConnectHost ?? "";
		UpstreamConnector.HttpConnectProxyPort = App.Settings.Prop.ProxyHttpConnectPort > 0 ? App.Settings.Prop.ProxyHttpConnectPort : 3128;
		UpstreamConnector.Socks5ProxyHost = App.Settings.Prop.ProxySocks5Host ?? "";
		UpstreamConnector.Socks5ProxyPort = App.Settings.Prop.ProxySocks5Port > 0 ? App.Settings.Prop.ProxySocks5Port : 1080;
		if (TextureStripper.RequiresCacheReset)
		{
			try
			{
				AssetProxyRouting.ClearRobloxCache();
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Roblox asset cache could not be cleared: " + ex.Message);
			}
		}
		AssetProxyCA.Initialize();
		bool resolveEndpoints = UpstreamConnector.ConnectorType is not UpstreamConnectorType.HttpConnect and not UpstreamConnectorType.Socks5;
		IReadOnlyDictionary<string, string> endpoints = await AssetProxyRouting.PrepareAsync(hosts, ct, resolveEndpoints).ConfigureAwait(false);
		CancellationTokenSource linked = new();
		using CancellationTokenRegistration startupRegistration = ct.Register(linked.Cancel);
		List<TcpListener> listeners = [];
		List<Task> loops = [];

		try
		{
			if (UsesExplicitProxy)
			{
				TcpListener forward = new(IPAddress.Loopback, 0);
				forward.Server.ExclusiveAddressUse = true;
				forward.Server.LingerState = new LingerOption(true, 0);
				forward.Start(256);
				listeners.Add(forward);
				Volatile.Write(ref _explicitPort, ((IPEndPoint)forward.LocalEndpoint).Port);
			}
			else
			{
				TcpListener ipv4 = new(IPAddress.Loopback, ListenPort);
				ipv4.Server.ExclusiveAddressUse = true;
				ipv4.Server.LingerState = new LingerOption(true, 0);
				try
				{
					ipv4.Start(256);
				}
				catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
				{
					ipv4.Dispose();
					throw new IOException("Local port 443 is already in use, AssetWarp cannot route Roblox assets until the other program releases it", ex);
				}
				listeners.Add(ipv4);
				try
				{
					TcpListener ipv6 = new(IPAddress.IPv6Loopback, ListenPort);
					ipv6.Server.DualMode = false;
					ipv6.Server.ExclusiveAddressUse = true;
					ipv6.Server.LingerState = new LingerOption(true, 0);
					ipv6.Start(256);
					listeners.Add(ipv6);
				}
				catch (SocketException ex)
				{
					App.Logger?.WriteLine(LogIdent, "IPv6 listener unavailable: " + ex.Message);
				}
			}

			lock (Gate)
			{
				_listeners = listeners;
				_cts = linked;
				_upstreamIps = new ConcurrentDictionary<string, string>(endpoints, StringComparer.OrdinalIgnoreCase);
				_hosts = hosts;
				foreach (TcpListener listener in listeners)
				{
					loops.Add(AcceptLoopAsync(listener, UsesExplicitProxy, linked.Token));
				}
				_acceptLoops = loops;
			}

			Volatile.Write(ref _lastTlsFailure, "");
			if (UsesExplicitProxy)
			{
				Uri? address = ExplicitProxyAddress;
				if (address is null)
				{
					throw new InvalidOperationException("The AssetWarp proxy port was not assigned");
				}
				Voidstrap.Platform.OperationResult enabled = await LinuxAssetWarpBridge.EnableAsync(address, linked.Token).ConfigureAwait(false);
				if (!enabled.Succeeded)
				{
					throw new InvalidOperationException(enabled.Failure?.Message ?? "AssetWarp could not be enabled on this system");
				}
			}
			else
			{
				await SelfTestAsync(hosts[0], linked.Token).ConfigureAwait(false);
			}
			try
			{
				AssetProxyRouting.InstallEntries(hosts, listeners.Count > 1);
			}
			catch (IOException ex)
			{
				App.Logger?.WriteLine(LogIdent, "AssetWarp routing could not be verified, retrying: " + ex.Message);
				AssetProxyRouting.InstallEntries(hosts, listeners.Count > 1);
			}
			lock (Gate)
			{
				_running = true;
			}
			App.Logger?.WriteLine(LogIdent, UsesExplicitProxy
				? "AssetWarp forward proxy active on local port " + ExplicitPort.ToString(CultureInfo.InvariantCulture)
				: "AssetWarp TLS proxy active on local port 443");
		}
		catch
		{
			linked.Cancel();
			foreach (TcpListener listener in listeners)
			{
				listener.Stop();
			}
			AssetProxyRouting.Cleanup();
			if (UsesExplicitProxy)
			{
				LinuxAssetWarpBridge.DisableBlocking();
			}
			lock (Gate)
			{
				_running = false;
				Volatile.Write(ref _explicitPort, 0);
				_listeners = [];
				_acceptLoops = [];
				_cts = null;
			}
			linked.Dispose();
			AssetProxyCA.Cleanup();
			throw;
		}
	}

	public static void Stop()
	{
		CancellationTokenSource? reconcile = Interlocked.Exchange(ref _reconcileCts, null);
		SafeCancel(reconcile);
		StopInternal();
	}

	private static void SafeCancel(CancellationTokenSource? source)
	{
		if (source == null)
			return;
		try
		{
			source.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		catch (AggregateException)
		{
		}
	}

	public static bool DisableForUnsupportedClient()
	{
		Stop();
		CleanupStaleState();
		AssetProxyRouting.Cleanup();
		return true;
	}

	private static void StopInternal()
	{
		lock (Gate)
		{
			SafeCancel(_startupCts);
		}
		bool acquired = Lifecycle.Wait(TimeSpan.FromSeconds(15));
		if (!acquired)
		{
			App.Logger?.WriteLine(LogIdent, "AssetWarp startup did not release in time, stopping anyway");
		}
		try
		{
			StopCore();
		}
		finally
		{
			if (acquired)
			{
				Lifecycle.Release();
			}
		}
	}

	public static void ReconcileRuntimeState()
	{
		TextureStripper.InvalidateRuntimeState();
		if (!App.Settings.Prop.AssetWarpEnabled)
		{
			App.FastFlags.ApplyPreloadFlags();
			QueueRuntimeReconcile(false);
			return;
		}
		if (!App.Settings.Prop.AssetWarpPreloadEnabled)
		{
			AssetPreloadCache.StopBackgroundWarm();
		}
		if (!IsRequired)
		{
			QueueRuntimeReconcile(false);
			return;
		}
		if (IsRunning)
			QueueRuntimeReconcile(true);
	}

	public static void PrepareCertificate()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return;
		AssetProxyCA.Initialize();
		if (!_running)
		{
			AssetProxyCA.Cleanup();
		}
	}

	public static void RemoveCertificates()
	{
		Stop();
		if (!TryAcquireOwnership(out FileStream? ownership))
			throw new IOException("Another AssetWarp proxy session is active");
		using (ownership)
			AssetProxyCA.RemoveOutdatedCertificates(false);
	}

	private static void QueueRuntimeReconcile(bool restart)
	{
		CancellationTokenSource next = new();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _reconcileCts, next);
		SafeCancel(previous);
		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(250, next.Token).ConfigureAwait(false);
				StopInternal();
				if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpCertificateApproved)
					CleanupStaleState();
				if (restart && IsRequired && ProcessElevation.IsAdministrator())
					await StartAsync(next.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (next.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Runtime reconcile failed: " + ex.Message);
			}
			finally
			{
				Interlocked.CompareExchange(ref _reconcileCts, null, next);
				next.Dispose();
			}
		});
	}

	private static void StopCore()
	{
		TextureStripper.InvalidateRuntimeState();
		CancellationTokenSource? cancellation;
		Task[] loops;
		bool wasActive;
		bool ownsProxy;
		lock (Gate)
		{
			wasActive = _running || _cts != null;
			ownsProxy = _ownershipLock != null;
			_running = false;
			Volatile.Write(ref _explicitPort, 0);
			cancellation = _cts;
			_cts = null;
			loops = [.. _acceptLoops];
			_acceptLoops = [];
			foreach (TcpListener listener in _listeners)
			{
				try { listener.Server?.Shutdown(SocketShutdown.Both); } catch { }
				try { listener.Server?.Close(); } catch { }
				try { listener.Stop(); } catch { }
			}
			_listeners = [];
		}

		cancellation?.Cancel();
		foreach (TcpClient client in ClientConnections.Values)
		{
			try { client.Client?.Shutdown(SocketShutdown.Both); } catch { }
			try { client.Client?.Close(); } catch { }
			try { client.Dispose(); } catch { }
		}
		bool routingCleaned = !ownsProxy || AssetProxyRouting.Cleanup();
		if (ownsProxy && !routingCleaned)
		{
			App.Logger?.WriteLine(LogIdent, "AssetWarp routing cleanup will be retried after shutdown");
		}
		LinuxAssetWarpBridge.DisableBlocking();
		AssetPreloadCache.StopBackgroundWarm();
		if (!wasActive)
		{
			_upstreamIps.Clear();
			_hosts = [];
			if (ownsProxy)
				AssetProxyCA.Cleanup();
			AssetCaptureStore.Shutdown();
			ReleaseOwnership();
			return;
		}
		try
		{
			Task.WaitAll(loops, TimeSpan.FromSeconds(3));
		}
		catch
		{
		}
		try
		{
			Task.WaitAll([.. ClientTasks.Values], TimeSpan.FromSeconds(3));
		}
		catch
		{
		}
		cancellation?.Dispose();
		ClientConnections.Clear();
		ClientTasks.Clear();
		_upstreamIps.Clear();
		_hosts = [];
		if (ownsProxy)
			AssetProxyCA.Cleanup();
		AssetCaptureStore.Shutdown();
		AssetPreloadCache.Flush();
		ReleaseOwnership();
		App.Logger?.WriteLine(LogIdent, "AssetWarp proxy stopped");
	}

	public static void CleanupStaleState()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return;
		if (!TryAcquireOwnership(out FileStream? ownership))
		{
			App.Logger?.WriteLine(LogIdent, "Active AssetWarp owner detected, stale cleanup skipped");
			return;
		}
		using (ownership)
		{
			AssetProxyRouting.Cleanup();
			if (App.Settings.Prop.AssetWarpEnabled && !App.Settings.Prop.AssetWarpCertificateApproved)
			{
				App.Settings.Prop.AssetWarpEnabled = false;
				App.Settings.Save();
			}
			AssetProxyCA.RemoveOutdatedCertificates(App.Settings.Prop.AssetWarpEnabled && App.Settings.Prop.AssetWarpCertificateApproved);
		}
	}

	private static bool TryAcquireOwnership(out FileStream? ownership)
	{
		ownership = null;
		try
		{
			Directory.CreateDirectory(Paths.AssetProxy);
			ownership = new FileStream(Path.Combine(Paths.AssetProxy, "assetwarp.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			return true;
		}
		catch (IOException)
		{
			ownership?.Dispose();
			ownership = null;
			return false;
		}
	}

	private static void ReleaseOwnership()
	{
		FileStream? ownership;
		lock (Gate)
		{
			ownership = _ownershipLock;
			_ownershipLock = null;
		}
		ownership?.Dispose();
	}

	private static async Task AcceptLoopAsync(TcpListener listener, bool explicitProxy, CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
				if (!ClientSlots.Wait(0))
				{
					client.Dispose();
					continue;
				}
				int id = Interlocked.Increment(ref _nextClientId);
				ClientConnections[id] = client;
				Task task = explicitProxy ? HandleExplicitClientAsync(client, ct) : HandleClientAsync(client, ct);
				ClientTasks[id] = task;
				_ = task.ContinueWith(completedTask =>
				{
					if (completedTask.Exception != null)
					{
						App.Logger?.WriteLine(LogIdent, "Client session faulted: " + completedTask.Exception.GetBaseException().Message);
					}
					ClientTasks.TryRemove(id, out _);
					ClientConnections.TryRemove(id, out _);
					ClientSlots.Release();
				}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				break;
			}
			catch (ObjectDisposedException) when (ct.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Accept failed: " + ex.Message);
				try
				{
					await Task.Delay(250, ct).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					break;
				}
			}
		}
	}

	private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
	{
		using (client)
		{
			client.NoDelay = true;
			await HandleInterceptedStreamAsync(client.GetStream(), ct).ConfigureAwait(false);
		}
	}

	private static async Task HandleInterceptedStreamAsync(Stream transport, CancellationToken ct)
	{
		{
			using SslStream localTls = new(transport, false);
			try
			{
				using CancellationTokenSource handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
				handshakeDeadline.CancelAfter(TimeSpan.FromSeconds(15));
				await localTls.AuthenticateAsServerAsync(AssetProxyTlsOptions.Create(SelectCertificate), handshakeDeadline.Token).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Volatile.Write(ref _lastTlsFailure, ex.ToString());
				App.Logger?.WriteLine(LogIdent, "Client TLS failed: " + ex.Message);
				return;
			}

			BufferedConnection local = new(localTls);
			UpstreamConnection? upstream = null;
			BufferedConnection? remote = null;
			string upstreamHost = "";
			try
			{
				while (!ct.IsCancellationRequested)
				{
					byte[] requestHeaderBytes = await local.ReadHeaderAsync(ct).ConfigureAwait(false);
					if (!IsCompleteHead(requestHeaderBytes))
					{
						break;
					}
					HttpHead request = ParseHead(requestHeaderBytes);
					string[] requestParts = request.FirstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
					if (requestParts.Length < 3)
					{
						break;
					}
					string method = requestParts[0];
					string path = requestParts[1];
					string host = request.Get("Host").Split(':')[0].Trim().ToLowerInvariant();
					if (!_hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
					{
						break;
					}
					AssetPreloadCache.ObservePlaceHeader(request.Get("Roblox-Place-Id"));
					if (request.Get("Expect").Contains("100-continue", StringComparison.OrdinalIgnoreCase))
					{
						await localTls.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
					}

					MessageBody requestBody = await ReadMessageBodyAsync(local, request, false, method, ct).ConfigureAwait(false);
					if (PresenceSpoofer.TryCreateLocalResponse(host, path, method, out byte[] localResponse))
					{
						await WriteLocalJsonResponseAsync(localTls, localResponse, ct).ConfigureAwait(false);
						if (ShouldClose(request))
						{
							break;
						}
						continue;
					}

					AssetBatchContext? batchContext = null;
					bool batch = TextureStripper.IsBatchRequest(host, path) && method.Equals("POST", StringComparison.OrdinalIgnoreCase);
					bool transformPresence = PresenceSpoofer.CanTransformRequest(host, path, method);
					byte[] decodedRequest = batch || transformPresence
						? DecodeContent(requestBody.Decoded, request.Get("Content-Encoding"))
						: requestBody.Decoded;
					byte[] outboundBody = decodedRequest;
					if (batch)
					{
						outboundBody = TextureStripper.PrepareBatchRequest(decodedRequest, AssetPreloadCache.ActivePlaceId, out batchContext);
					}
					if (transformPresence)
					{
						byte[]? presenceBody = PresenceSpoofer.ProcessRequest(host, path, method, outboundBody);
						if (presenceBody != null)
						{
							outboundBody = presenceBody;
						}
					}
					bool bodyModified = !ReferenceEquals(outboundBody, decodedRequest) && !outboundBody.AsSpan().SequenceEqual(decodedRequest);
					bool requestModified = request.Get("Expect").Length > 0 || bodyModified;

					if (IsCdnHost(host) && TextureStripper.TryGetRoute(host, path, out AssetWarpRoute? route) && route != null)
					{
						await ServeRouteAsync(localTls, method, request.Get("Range"), route, ct).ConfigureAwait(false);
						if (ShouldClose(request))
						{
							break;
						}
						continue;
					}

					if (IsCdnHost(host))
					{
						string? assetId = TextureStripper.ResolveAssetId(host, path);
						string? hash = ExtractHash(path);
						if (request.Get("Range").Length == 0 && AssetPreloadCache.TryResolveCachedRoute(assetId, hash, out AssetWarpRoute? cachedRoute) && cachedRoute != null)
						{
							await ServeRouteAsync(localTls, method, request.Get("Range"), cachedRoute, ct).ConfigureAwait(false);
							if (ShouldClose(request))
							{
								break;
							}
							continue;
						}
					}

					if (upstream != null && Volatile.Read(ref _upstreamNetworkVersion) != Voidstrap.Utility.VpnHttpClient.NetworkVersion)
					{
						upstream.Dispose();
						upstream = null;
						remote = null;
					}
					if (upstream == null || !host.Equals(upstreamHost, StringComparison.OrdinalIgnoreCase))
					{
						upstream?.Dispose();
						upstream = await ConnectUpstreamAsync(host, ct).ConfigureAwait(false);
						if (upstream == null)
						{
							await WriteGatewayFailureAsync(localTls, ct).ConfigureAwait(false);
							break;
						}
						upstreamHost = host;
						remote = new BufferedConnection(upstream.Ssl);
					}

					await WriteUpstreamRequestAsync(upstream.Ssl, request, requestBody, outboundBody, requestModified, bodyModified, ct).ConfigureAwait(false);

					byte[] responseHeaderBytes = await remote!.ReadHeaderAsync(ct).ConfigureAwait(false);
					if (!IsCompleteHead(responseHeaderBytes))
					{
						upstream.Dispose();
						upstream = null;
						remote = null;
						bool safeRetry = requestBody.Raw.Length == 0 && (method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase));
						if (safeRetry)
						{
							upstream = await ConnectUpstreamAsync(host, ct).ConfigureAwait(false);
							if (upstream != null)
							{
								upstreamHost = host;
								remote = new BufferedConnection(upstream.Ssl);
								await WriteUpstreamRequestAsync(upstream.Ssl, request, requestBody, outboundBody, requestModified, bodyModified, ct).ConfigureAwait(false);
								responseHeaderBytes = await remote.ReadHeaderAsync(ct).ConfigureAwait(false);
							}
						}
						if (!IsCompleteHead(responseHeaderBytes))
						{
							upstream?.Dispose();
							upstream = null;
							remote = null;
							await WriteGatewayFailureAsync(localTls, ct).ConfigureAwait(false);
							break;
						}
					}
					HttpHead response = ParseHead(responseHeaderBytes);
					while (IsInformational(response))
					{
						responseHeaderBytes = await remote.ReadHeaderAsync(ct).ConfigureAwait(false);
						if (!IsCompleteHead(responseHeaderBytes))
						{
							await WriteGatewayFailureAsync(localTls, ct).ConfigureAwait(false);
							return;
						}
						response = ParseHead(responseHeaderBytes);
					}
					bool cdnResponse = IsCdnHost(host);
					bool passthroughChunkedCdn = cdnResponse &&
						response.Get("Transfer-Encoding").Contains("chunked", StringComparison.OrdinalIgnoreCase) &&
						ResponseHasBody(response, method);
					if (passthroughChunkedCdn)
					{
						await localTls.WriteAsync(response.Raw, ct).ConfigureAwait(false);
						await remote.CopyChunkedToAsync(localTls, ct).ConfigureAwait(false);
						bool chunkedClose = ShouldClose(response);
						if (chunkedClose)
						{
							upstream?.Dispose();
							upstream = null;
							remote = null;
						}
						if (ShouldClose(request) || chunkedClose)
						{
							break;
						}
						continue;
					}
					if (cdnResponse && CanStreamCdnResponse(response, method, out long streamingLength))
					{
						await localTls.WriteAsync(response.Raw, ct).ConfigureAwait(false);
						string responseEncoding = response.Get("Content-Encoding");
						bool identityEncoded = responseEncoding.Length == 0 || responseEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase);
						int captureLimit = App.Settings.Prop.AssetWarpEnabled && App.Settings.Prop.AssetWarpPreloadEnabled && identityEncoded ? MaxCapturedAssetBytes : 0;
						byte[]? captured = streamingLength > 0
							? await remote.CopyExactToAsync(streamingLength, localTls, captureLimit, ct).ConfigureAwait(false)
							: null;
						if (captured is { Length: > 0 })
						{
							string? assetId = TextureStripper.ResolveAssetId(host, path);
							AssetCaptureStore.QueueAdd(assetId, ExtractHash(path), captured, "https://" + host + path);
						}
						bool streamedClose = ShouldClose(response);
						if (streamedClose)
						{
							upstream?.Dispose();
							upstream = null;
							remote = null;
						}
						if (ShouldClose(request) || streamedClose)
						{
							break;
						}
						continue;
					}
					MessageBody responseBody = await ReadMessageBodyAsync(remote, response, true, method, ct).ConfigureAwait(false);
					bool spoofResponse = UsernameSpoofer.CanProcessResponse(host, path);
					bool captureCdnResponse = cdnResponse && App.Settings.Prop.AssetWarpEnabled && App.Settings.Prop.AssetWarpPreloadEnabled;
					byte[] decodedResponse = batch || captureCdnResponse || spoofResponse
						? DecodeContent(responseBody.Decoded, response.Get("Content-Encoding"))
						: responseBody.Decoded;
					if (batch)
					{
						TextureStripper.ObserveBatchResponse(batchContext, decodedResponse);
					}

					byte[]? changed = spoofResponse
						? await UsernameSpoofer.ProcessResponseAsync(host, path, decodedResponse, ct).ConfigureAwait(false)
						: null;
					if (changed != null)
					{
						byte[] rebuilt = RebuildHead(response, changed.Length, true);
						await localTls.WriteAsync(rebuilt, ct).ConfigureAwait(false);
						await localTls.WriteAsync(changed, ct).ConfigureAwait(false);
					}
					else
					{
						await localTls.WriteAsync(response.Raw, ct).ConfigureAwait(false);
						if (responseBody.Raw.Length > 0)
						{
							await localTls.WriteAsync(responseBody.Raw, ct).ConfigureAwait(false);
						}
					}

					if (captureCdnResponse && decodedResponse.Length > 0)
					{
						string? assetId = TextureStripper.ResolveAssetId(host, path);
						AssetCaptureStore.QueueAdd(assetId, ExtractHash(path), decodedResponse, "https://" + host + path);
					}

					bool remoteClose = ShouldClose(response) || !HasDelimitedBody(response, method);
					if (remoteClose)
					{
						upstream?.Dispose();
						upstream = null;
						remote = null;
					}
					if (ShouldClose(request) || ShouldClose(response))
					{
						break;
					}
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
			}
			catch (OperationCanceledException)
			{
			}
			catch (IOException)
			{
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Session failed: " + ex.Message);
			}
			finally
			{
				upstream?.Dispose();
			}
		}
	}

	private static async Task HandleExplicitClientAsync(TcpClient client, CancellationToken ct)
	{
		using (client)
		{
			client.NoDelay = true;
			NetworkStream transport = client.GetStream();
			byte[] headerBytes;
			try
			{
				headerBytes = await ReadProxyRequestHeaderAsync(transport, ct).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Proxy request read failed: " + ex.Message);
				return;
			}

			if (headerBytes.Length == 0)
			{
				return;
			}

			HttpHead request = ParseHead(headerBytes);
			string[] parts = request.FirstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2)
			{
				return;
			}

			string method = parts[0];
			bool isConnect = string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase);
			if (!Voidstrap.Core.AssetProxy.ProxyRequestTarget.TryResolve(parts[1], request.Get("Host"), isConnect, out string host, out int port))
			{
				await WriteProxyStatusAsync(transport, "400 Bad Request", ct).ConfigureAwait(false);
				return;
			}

			if (isConnect && port == 443 && _hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
			{
				await WriteProxyStatusAsync(transport, "200 Connection Established", ct).ConfigureAwait(false);
				await HandleInterceptedStreamAsync(transport, ct).ConfigureAwait(false);
				return;
			}

			await TunnelAsync(transport, host, port, isConnect ? null : headerBytes, ct).ConfigureAwait(false);
		}
	}

	private static async Task<byte[]> ReadProxyRequestHeaderAsync(Stream stream, CancellationToken ct)
	{
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(TimeSpan.FromSeconds(15));
		using MemoryStream output = new();
		byte[] single = new byte[1];
		int state = 0;
		while (output.Length < 16384)
		{
			int read = await stream.ReadAsync(single.AsMemory(0, 1), deadline.Token).ConfigureAwait(false);
			if (read <= 0)
			{
				return [];
			}
			byte value = single[0];
			output.WriteByte(value);
			state = state switch
			{
				0 when value == 13 => 1,
				1 when value == 10 => 2,
				2 when value == 13 => 3,
				3 when value == 10 => 4,
				_ when value == 13 => 1,
				_ => 0
			};
			if (state == 4)
			{
				return output.ToArray();
			}
		}

		throw new InvalidDataException("Proxy request header exceeds the AssetWarp limit");
	}

	private static async Task WriteProxyStatusAsync(Stream stream, string status, CancellationToken ct)
	{
		byte[] payload = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nProxy-Connection: close\r\n\r\n");
		await stream.WriteAsync(payload, ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	private static async Task TunnelAsync(Stream client, string host, int port, byte[]? preface, CancellationToken ct)
	{
		using TcpClient remote = new();
		try
		{
			using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
			deadline.CancelAfter(TimeSpan.FromSeconds(15));
			await remote.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Tunnel to " + host + " failed: " + ex.Message);
			if (preface is null)
			{
				await WriteProxyStatusAsync(client, "502 Bad Gateway", ct).ConfigureAwait(false);
			}
			return;
		}

		remote.NoDelay = true;
		NetworkStream remoteStream = remote.GetStream();
		if (preface is null)
		{
			await WriteProxyStatusAsync(client, "200 Connection Established", ct).ConfigureAwait(false);
		}
		else
		{
			await remoteStream.WriteAsync(preface, ct).ConfigureAwait(false);
			await remoteStream.FlushAsync(ct).ConfigureAwait(false);
		}

		using CancellationTokenSource pump = CancellationTokenSource.CreateLinkedTokenSource(ct);
		Task outbound = CopyUntilClosedAsync(client, remoteStream, pump.Token);
		Task inbound = CopyUntilClosedAsync(remoteStream, client, pump.Token);
		await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
		pump.Cancel();
		try
		{
			await Task.WhenAll(outbound, inbound).ConfigureAwait(false);
		}
		catch
		{
		}
	}

	private static async Task CopyUntilClosedAsync(Stream source, Stream destination, CancellationToken ct)
	{
		byte[] buffer = new byte[32768];
		try
		{
			while (!ct.IsCancellationRequested)
			{
				int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
				if (read <= 0)
				{
					return;
				}
				await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
				await destination.FlushAsync(ct).ConfigureAwait(false);
			}
		}
		catch
		{
		}
	}

	private static X509Certificate SelectCertificate(object sender, string? host)
	{
		string selected = host?.Trim().ToLowerInvariant() ?? "";
		if (!_hosts.Contains(selected, StringComparer.OrdinalIgnoreCase))
		{
			selected = _hosts.FirstOrDefault() ?? "assetdelivery.roblox.com";
		}
		return AssetProxyCA.GenerateHostCert(selected);
	}

	private static async Task<UpstreamConnection?> ConnectUpstreamAsync(string host, CancellationToken ct)
	{
		int networkVersion = Voidstrap.Utility.VpnHttpClient.NetworkVersion;
		if (Volatile.Read(ref _upstreamNetworkVersion) != networkVersion)
		{
			DnsResolver.Clear();
			AssetCaptureStore.ResetResolveState();
			Volatile.Write(ref _upstreamNetworkVersion, networkVersion);
		}
		if (UpstreamConnector.ConnectorType is UpstreamConnectorType.HttpConnect or UpstreamConnectorType.Socks5)
		{
			return await UpstreamConnector.ConnectAsync(host, 443, null, ct).ConfigureAwait(false);
		}
		if (!_upstreamIps.TryGetValue(host, out string? address))
		{
			address = await DnsResolver.ResolveDirectAsync(host, ct).ConfigureAwait(false);
			if (string.IsNullOrEmpty(address))
			{
				return null;
			}
			_upstreamIps[host] = address;
		}
		UpstreamConnection? connection = await UpstreamConnector.ConnectAsync(host, 443, address, ct).ConfigureAwait(false);
		if (connection != null || UpstreamConnector.ConnectorType is UpstreamConnectorType.HttpConnect or UpstreamConnectorType.Socks5)
		{
			return connection;
		}
		DnsResolver.Invalidate(host);
		string? refreshed = await DnsResolver.ResolveDirectAsync(host, ct).ConfigureAwait(false);
		if (string.IsNullOrEmpty(refreshed))
		{
			return null;
		}
		_upstreamIps[host] = refreshed;
		return await UpstreamConnector.ConnectAsync(host, 443, refreshed, ct).ConfigureAwait(false);
	}

	private static async Task WriteUpstreamRequestAsync(Stream stream, HttpHead request, MessageBody requestBody, byte[] outboundBody, bool requestModified, bool bodyModified, CancellationToken ct)
	{
		if (requestModified)
		{
			byte[] rebuilt = RebuildHead(request, outboundBody.Length, true, bodyModified);
			await stream.WriteAsync(rebuilt, ct).ConfigureAwait(false);
			if (outboundBody.Length > 0)
			{
				await stream.WriteAsync(outboundBody, ct).ConfigureAwait(false);
			}
		}
		else
		{
			await stream.WriteAsync(request.Raw, ct).ConfigureAwait(false);
			if (requestBody.Raw.Length > 0)
			{
				await stream.WriteAsync(requestBody.Raw, ct).ConfigureAwait(false);
			}
		}
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	private static async Task SelfTestAsync(string host, CancellationToken ct)
	{
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(TimeSpan.FromSeconds(20));
		using TcpClient client = new();
		await client.ConnectAsync(IPAddress.Loopback, ListenPort, deadline.Token).ConfigureAwait(false);
		using SslStream tls = new(client.GetStream(), false);
		try
		{
			await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
			{
				TargetHost = host,
				EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
				ApplicationProtocols = [SslApplicationProtocol.Http11]
			}, deadline.Token).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			string serverFailure = Volatile.Read(ref _lastTlsFailure);
			string detail = serverFailure.Length == 0 ? "Server failure was not reported" : serverFailure;
			App.Logger?.WriteLine(LogIdent, "TLS self test failed, client: " + ex + Environment.NewLine + "Server: " + detail);
			throw;
		}
	}

	private static bool IsCompleteHead(byte[] raw)
	{
		return raw.Length >= 4 && raw.AsSpan(raw.Length - 4).SequenceEqual("\r\n\r\n"u8);
	}

	private static HttpHead ParseHead(byte[] raw)
	{
		if (!IsCompleteHead(raw))
			throw new InvalidDataException("HTTP header is incomplete");
		string text = Encoding.ASCII.GetString(raw);
		string[] split = text.Split(["\r\n"], StringSplitOptions.None);
		if (split.Length == 0 || string.IsNullOrWhiteSpace(split[0]))
		{
			throw new InvalidDataException("HTTP start line is missing");
		}
		List<string> lines = [.. split.Skip(1).Where(line => line.Length > 0)];
		foreach (string line in lines)
		{
			int separator = line.IndexOf(':');
			if (separator <= 0 || char.IsWhiteSpace(line[0]) || line.AsSpan(0, separator).ContainsAny(" ()<>@,;:\\\"/[]?={}\t"))
				throw new InvalidDataException("HTTP header contains an invalid field");
		}
		string[] contentLengths = [.. lines
			.Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
			.Select(line => line[(line.IndexOf(':') + 1)..].Trim())
			.Distinct(StringComparer.Ordinal)];
		if (contentLengths.Length > 1)
			throw new InvalidDataException("HTTP header contains conflicting content lengths");
		bool hasTransferEncoding = lines.Any(line => line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase));
		if (hasTransferEncoding && contentLengths.Length > 0)
			throw new InvalidDataException("HTTP header contains both transfer encoding and content length");
		return new HttpHead
		{
			FirstLine = split[0],
			Lines = lines,
			Raw = raw
		};
	}

	private static async Task<MessageBody> ReadMessageBodyAsync(BufferedConnection reader, HttpHead head, bool response, string method, CancellationToken ct)
	{
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(TimeSpan.FromSeconds(response ? 120 : 60));
		ct = deadline.Token;
		if (response && !ResponseHasBody(head, method))
		{
			return new MessageBody([], [], false);
		}

		string transfer = head.Get("Transfer-Encoding");
		if (transfer.Contains("chunked", StringComparison.OrdinalIgnoreCase))
		{
			return await ReadChunkedAsync(reader, ct).ConfigureAwait(false);
		}

		if (long.TryParse(head.Get("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out long length))
		{
			if (length < 0 || length > MaxBodyBytes)
			{
				throw new InvalidDataException("HTTP body exceeds the AssetWarp limit");
			}
			byte[] body = length == 0 ? [] : await reader.ReadExactAsync((int)length, ct).ConfigureAwait(false);
			return new MessageBody(body, body, false);
		}

		if (response)
		{
			byte[] body = await reader.ReadToEndAsync(MaxBodyBytes, ct).ConfigureAwait(false);
			return new MessageBody(body, body, false);
		}

		return new MessageBody([], [], false);
	}

	private static async Task<MessageBody> ReadChunkedAsync(BufferedConnection reader, CancellationToken ct)
	{
		using MemoryStream raw = new();
		using MemoryStream decoded = new();
		int trailerBytesTotal = 0;
		while (true)
		{
			string line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
			byte[] lineBytes = Encoding.ASCII.GetBytes(line + "\r\n");
			raw.Write(lineBytes);
			string sizeText = line.Split(';', 2)[0].Trim();
			if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size < 0)
			{
				throw new InvalidDataException("Invalid chunk size");
			}
			if (size == 0)
			{
				while (true)
				{
					string trailer = await reader.ReadLineAsync(ct).ConfigureAwait(false);
					trailerBytesTotal += trailer.Length + 2;
					if (trailerBytesTotal > 65536)
						throw new InvalidDataException("HTTP trailers exceed the AssetWarp limit");
					byte[] trailerBytes = Encoding.ASCII.GetBytes(trailer + "\r\n");
					raw.Write(trailerBytes);
					if (trailer.Length == 0)
					{
						break;
					}
				}
				break;
			}
			if (decoded.Length + size > MaxBodyBytes)
			{
				throw new InvalidDataException("HTTP body exceeds the AssetWarp limit");
			}
			byte[] data = await reader.ReadExactAsync(size, ct).ConfigureAwait(false);
			byte[] ending = await reader.ReadExactAsync(2, ct).ConfigureAwait(false);
			if (ending[0] != 13 || ending[1] != 10)
				throw new InvalidDataException("Invalid chunk terminator");
			raw.Write(data);
			raw.Write(ending);
			decoded.Write(data);
		}
		return new MessageBody(raw.ToArray(), decoded.ToArray(), true);
	}

	private static byte[] DecodeContent(byte[] body, string encoding)
	{
		if (body.Length == 0)
		{
			return body;
		}
		bool gzipEncoded = encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase) || body.Length > 2 && body[0] == 0x1F && body[1] == 0x8B;
		bool brotliEncoded = encoding.Contains("br", StringComparison.OrdinalIgnoreCase);
		bool zstdEncoded = encoding.Contains("zstd", StringComparison.OrdinalIgnoreCase) || body.Length > 4 && body[0] == 0x28 && body[1] == 0xB5 && body[2] == 0x2F && body[3] == 0xFD;
		try
		{
			if (gzipEncoded)
			{
				using MemoryStream input = new(body);
				using GZipStream gzip = new(input, CompressionMode.Decompress);
				return ReadDecodedBounded(gzip);
			}
			if (brotliEncoded)
			{
				using MemoryStream input = new(body);
				using BrotliStream brotli = new(input, CompressionMode.Decompress);
				return ReadDecodedBounded(brotli);
			}
			if (zstdEncoded)
			{
				using Decompressor decompressor = new();
				ulong size = Decompressor.GetDecompressedSize(body, 0, body.Length);
				if (size == 0 || size > MaxBodyBytes)
					return [];
				byte[] decoded = new byte[(int)size];
				int written = decompressor.Unwrap(body, decoded, 0);
				return written == decoded.Length ? decoded : [];
			}
		}
		catch
		{
			return gzipEncoded || brotliEncoded || zstdEncoded ? [] : body;
		}
		return body;
	}

	private static byte[] ReadDecodedBounded(Stream input)
	{
		using MemoryStream output = new();
		byte[] buffer = new byte[65536];
		while (true)
		{
			int read = input.Read(buffer, 0, buffer.Length);
			if (read == 0)
			{
				return output.ToArray();
			}
			if (output.Length + read > MaxBodyBytes)
			{
				return [];
			}
			output.Write(buffer, 0, read);
		}
	}

	private static byte[] RebuildHead(HttpHead head, int contentLength, bool stripIntegrity, bool stripEncoding = true)
	{
		StringBuilder builder = new();
		builder.Append(head.FirstLine).Append("\r\n");
		foreach (string line in head.Lines)
		{
			int separator = line.IndexOf(':');
			string name = separator > 0 ? line[..separator] : line;
			if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
				name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
				stripEncoding && name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
				name.Equals("Expect", StringComparison.OrdinalIgnoreCase) ||
				stripIntegrity && (name.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase) || name.Equals("Digest", StringComparison.OrdinalIgnoreCase) || name.Equals("ETag", StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}
			builder.Append(line).Append("\r\n");
		}
		builder.Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n\r\n");
		return Encoding.ASCII.GetBytes(builder.ToString());
	}

	private static async Task ServeRouteAsync(Stream stream, string method, string range, AssetWarpRoute route, CancellationToken ct)
	{
		if (route.Kind == AssetWarpRouteKind.Cdn)
		{
			string response = "HTTP/1.1 302 Found\r\nLocation: " + route.Value + "\r\nContent-Length: 0\r\nCache-Control: no-store\r\n\r\n";
			await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
			return;
		}

		FileInfo file = new(route.Value);
		if (!file.Exists || file.Length > MaxBodyBytes)
		{
			await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"), ct).ConfigureAwait(false);
			return;
		}
		if (file.Extension.Equals(".obj", StringComparison.OrdinalIgnoreCase))
		{
			byte[] data = MeshParser.ObjToMesh(await File.ReadAllTextAsync(file.FullName, ct).ConfigureAwait(false));
			await WriteLocalBytesAsync(stream, method, range, data, "application/octet-stream", "no-cache", ct).ConfigureAwait(false);
			return;
		}
		long offset = 0;
		long length = file.Length;
		await using FileStream input = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] prefix = new byte[37];
		int prefixRead = await input.ReadAsync(prefix, ct).ConfigureAwait(false);
		if (prefixRead == prefix.Length && prefix.AsSpan(0, 4).SequenceEqual("RBXH"u8))
		{
			int payloadLength = BitConverter.ToInt32(prefix, 25);
			if (payloadLength > 0 && payloadLength + prefix.Length == file.Length)
			{
				offset = prefix.Length;
				length = payloadLength;
			}
		}
		string type = GetContentType(file.Extension);
		string cacheControl = route.Immutable ? "private, max-age=31536000, immutable" : "no-cache";
		long rangeStart = 0;
		long responseLength = length;
		bool partial = range.Length > 0;
		if (partial && !TryParseRange(range, length, out rangeStart, out responseLength))
		{
			string invalid = "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" + length.ToString(CultureInfo.InvariantCulture) + "\r\nContent-Length: 0\r\n\r\n";
			await stream.WriteAsync(Encoding.ASCII.GetBytes(invalid), ct).ConfigureAwait(false);
			return;
		}
		string status = partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n";
		string contentRange = partial ? "Content-Range: bytes " + rangeStart.ToString(CultureInfo.InvariantCulture) + "-" + (rangeStart + responseLength - 1).ToString(CultureInfo.InvariantCulture) + "/" + length.ToString(CultureInfo.InvariantCulture) + "\r\n" : "";
		string header = status + "Content-Type: " + type + "\r\nContent-Length: " + responseLength.ToString(CultureInfo.InvariantCulture) + "\r\nAccept-Ranges: bytes\r\n" + contentRange + "Cache-Control: " + cacheControl + "\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
		if (!method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
		{
			input.Position = offset + rangeStart;
			await CopyBoundedAsync(input, stream, responseLength, ct).ConfigureAwait(false);
		}
	}

	private static async Task WriteLocalBytesAsync(Stream stream, string method, string range, byte[] data, string contentType, string cacheControl, CancellationToken ct)
	{
		long start = 0;
		long length = data.Length;
		bool partial = range.Length > 0;
		if (partial && !TryParseRange(range, data.Length, out start, out length))
		{
			string invalid = "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" + data.Length.ToString(CultureInfo.InvariantCulture) + "\r\nContent-Length: 0\r\n\r\n";
			await stream.WriteAsync(Encoding.ASCII.GetBytes(invalid), ct).ConfigureAwait(false);
			return;
		}
		string status = partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n";
		string contentRange = partial ? "Content-Range: bytes " + start.ToString(CultureInfo.InvariantCulture) + "-" + (start + length - 1).ToString(CultureInfo.InvariantCulture) + "/" + data.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" : "";
		string header = status + "Content-Type: " + contentType + "\r\nContent-Length: " + length.ToString(CultureInfo.InvariantCulture) + "\r\nAccept-Ranges: bytes\r\n" + contentRange + "Cache-Control: " + cacheControl + "\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
		if (!method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) && length > 0)
			await stream.WriteAsync(data.AsMemory((int)start, (int)length), ct).ConfigureAwait(false);
	}

	private static bool TryParseRange(string value, long totalLength, out long start, out long length)
	{
		start = 0;
		length = 0;
		if (totalLength <= 0 || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(','))
			return false;
		string[] parts = value[6..].Split('-', 2);
		if (parts.Length != 2)
			return false;
		if (parts[0].Length == 0)
		{
			if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix <= 0)
				return false;
			length = Math.Min(suffix, totalLength);
			start = totalLength - length;
			return true;
		}
		if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0 || start >= totalLength)
			return false;
		long end = totalLength - 1;
		if (parts[1].Length > 0 && (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start))
			return false;
		end = Math.Min(end, totalLength - 1);
		length = end - start + 1;
		return true;
	}

	private static bool CanStreamCdnResponse(HttpHead response, string method, out long contentLength)
	{
		contentLength = 0;
		if (!ResponseHasBody(response, method))
		{
			return true;
		}
		if (response.Get("Transfer-Encoding").Length > 0)
		{
			return false;
		}
		return long.TryParse(response.Get("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) && contentLength >= 0 && contentLength <= MaxStreamedBodyBytes;
	}

	private static async Task CopyBoundedAsync(Stream input, Stream output, long length, CancellationToken ct)
	{
		byte[] buffer = new byte[131072];
		long remaining = length;
		while (remaining > 0)
		{
			int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
			if (read == 0)
			{
				throw new EndOfStreamException();
			}
			await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
			remaining -= read;
		}
	}

	private static async Task WriteGatewayFailureAsync(Stream stream, CancellationToken ct)
	{
		byte[] body = Encoding.UTF8.GetBytes("AssetWarp could not reach Roblox asset delivery.");
		string header = "HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/plain\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
		await stream.WriteAsync(body, ct).ConfigureAwait(false);
	}

	private static async Task WriteLocalJsonResponseAsync(Stream stream, byte[] body, CancellationToken ct)
	{
		string header = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\nCache-Control: no-store\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
		if (body.Length > 0)
		{
			await stream.WriteAsync(body, ct).ConfigureAwait(false);
		}
	}

	private static bool ResponseHasBody(HttpHead head, string method)
	{
		if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string[] parts = head.FirstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return parts.Length < 2 || !int.TryParse(parts[1], out int status) || status is not (>= 100 and < 200) and not 204 and not 304;
	}

	private static bool IsInformational(HttpHead head)
	{
		string[] parts = head.FirstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return parts.Length >= 2 && int.TryParse(parts[1], out int status) && status >= 100 && status < 200 && status != 101;
	}

	private static bool HasDelimitedBody(HttpHead head, string method)
	{
		return !ResponseHasBody(head, method) || head.Get("Transfer-Encoding").Contains("chunked", StringComparison.OrdinalIgnoreCase) || long.TryParse(head.Get("Content-Length"), out _);
	}

	private static bool ShouldClose(HttpHead head)
	{
		foreach (string token in head.Get("Connection").Split(','))
		{
			if (token.Trim().Equals("close", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return head.FirstLine.EndsWith("HTTP/1.0", StringComparison.OrdinalIgnoreCase) || head.FirstLine.StartsWith("HTTP/1.0", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsCdnHost(string host)
	{
		return host.Equals("fts.rbxcdn.com", StringComparison.OrdinalIgnoreCase) || host.Equals("contentdelivery.roblox.com", StringComparison.OrdinalIgnoreCase);
	}

	private static string? ExtractHash(string path)
	{
		string value = path.Split('?', 2)[0];
		string candidate = value[(value.LastIndexOf('/') + 1)..];
		return candidate.Length == 32 && candidate.All(Uri.IsHexDigit) ? candidate.ToLowerInvariant() : null;
	}

	private static string GetContentType(string extension)
	{
		return extension.ToLowerInvariant() switch
		{
			".png" => "image/png",
			".jpg" or ".jpeg" => "image/jpeg",
			".webp" => "image/webp",
			".json" => "application/json",
			".xml" => "application/xml",
			".ogg" => "audio/ogg",
			".mp3" => "audio/mpeg",
			_ => "application/octet-stream"
		};
	}

	private static byte[] StripRobloxMetadata(byte[] data)
	{
		ReadOnlySpan<byte> prefix = [82, 66, 88, 72, 2, 0, 0, 0, 0, 0, 0, 0, 0, 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
		if (data.Length < prefix.Length + 12 || !data.AsSpan(0, prefix.Length).SequenceEqual(prefix))
		{
			return data;
		}
		int length = BitConverter.ToInt32(data, prefix.Length);
		int offset = prefix.Length + 12;
		if (length < 0 || length > data.Length - offset)
		{
			return data;
		}
		return data.AsSpan(offset, length).ToArray();
	}
}
