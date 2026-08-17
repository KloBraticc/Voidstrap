using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VoidstrapClient.Common;

namespace VoidstrapClient.ServerAuthInterface.Proxy;

internal class Connection
{
	private readonly UdpClient _localServer;

	private readonly UdpClient _forwardClient;

	private readonly IPEndPoint _sourceEndpoint;

	private readonly IPEndPoint _remoteEndpoint;

	private volatile bool _isRunning;

	private long _totalBytesForwarded;

	private long _totalBytesResponded;

	public bool IsRunning => _isRunning;

	public long LastActivity { get; private set; } = Environment.TickCount64;

	public Connection(UdpClient localServer, IPEndPoint sourceEndpoint, IPEndPoint remoteEndpoint)
	{
		_localServer = localServer;
		_isRunning = true;
		_remoteEndpoint = remoteEndpoint;
		_sourceEndpoint = sourceEndpoint;
		_forwardClient = new UdpClient(AddressFamily.InterNetworkV6);
		try
		{
			_forwardClient.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, optionValue: false);
			_forwardClient.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
			_forwardClient.Connect(_remoteEndpoint);
		}
		catch
		{
			_forwardClient.Dispose();
			throw;
		}
	}

	public async Task SendToServerAsync(byte[] message)
	{
		LastActivity = Environment.TickCount64;
		if (_isRunning)
		{
			int num = await _forwardClient.SendAsync(message, message.Length).ConfigureAwait(continueOnCapturedContext: false);
			Interlocked.Add(ref _totalBytesForwarded, num);
		}
	}

	public void Run()
	{
		_ = ReceiveLoopAsync();
	}

	private async Task ReceiveLoopAsync()
	{
		using (_forwardClient)
		{
			Logger.Instance.Info($"{_sourceEndpoint}: Connected");
			try
			{
				while (_isRunning)
				{
					UdpReceiveResult udpReceiveResult = await _forwardClient.ReceiveAsync().ConfigureAwait(continueOnCapturedContext: false);
					LastActivity = Environment.TickCount64;
					int num = await _localServer.SendAsync(udpReceiveResult.Buffer, udpReceiveResult.Buffer.Length, _sourceEndpoint).ConfigureAwait(continueOnCapturedContext: false);
					Interlocked.Add(ref _totalBytesResponded, num);
				}
			}
			catch (ObjectDisposedException)
			{
			}
			catch (SocketException value)
			{
				if (_isRunning)
					Logger.Instance.Warn("Server datagram receive stopped: " + value.SocketErrorCode);
			}
			catch (Exception value)
			{
				if (_isRunning)
					Logger.Instance.Warn("Server datagram receive stopped: " + value.Message);
			}
			finally
			{
				_isRunning = false;
			}
		}
	}

	public void Stop()
	{
		if (!_isRunning)
		{
			return;
		}
		try
		{
			Logger.Instance.Info($"{_sourceEndpoint}: Disconnected");
			_isRunning = false;
			_forwardClient.Close();
		}
		catch (Exception value)
		{
			Console.WriteLine($"An exception occurred while closing UdpConnection : {value}");
		}
	}
}
