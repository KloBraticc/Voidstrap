using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VoidstrapClient.Common;

namespace VoidstrapClient.ServerAuthInterface.Proxy;

internal class Server
{
	private const int MaxConnections = 256;
	private const int MaxConnectionsPerAddress = 16;

	public int ConnectionTimeout { get; set; } = 240000;

	public async Task Start(string remoteServerHostNameOrAddress, ushort remoteServerPort, ushort localPort, string? localIp = null, CancellationToken token = default)
	{
		ConcurrentDictionary<IPEndPoint, Connection> connections = new ConcurrentDictionary<IPEndPoint, Connection>();
		IPEndPoint remoteServerEndPoint = new IPEndPoint((await Dns.GetHostAddressesAsync(remoteServerHostNameOrAddress, token).ConfigureAwait(continueOnCapturedContext: false))[0], remoteServerPort);
		using UdpClient localServer = new UdpClient(AddressFamily.InterNetworkV6);
		localServer.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, optionValue: false);
		IPAddress address = (string.IsNullOrEmpty(localIp) ? IPAddress.IPv6Any : IPAddress.Parse(localIp));
		localServer.Client.Bind(new IPEndPoint(address, localPort));
		Task cleanupTask = CleanupConnectionsAsync(connections, token);
		try
		{
			while (!token.IsCancellationRequested)
			{
				UdpReceiveResult udpReceiveResult = await localServer.ReceiveAsync(token).ConfigureAwait(continueOnCapturedContext: false);
				IPEndPoint sourceEndPoint = udpReceiveResult.RemoteEndPoint;
				if (!connections.TryGetValue(sourceEndPoint, out Connection? orAdd))
				{
					if (connections.Count >= MaxConnections || connections.Keys.Count(endpoint => endpoint.Address.Equals(sourceEndPoint.Address)) >= MaxConnectionsPerAddress)
					{
						continue;
					}
					if (!AuthService.Instance.TryConsumeAuthorisation(sourceEndPoint.Address))
					{
						continue;
					}
					orAdd = new Connection(localServer, sourceEndPoint, remoteServerEndPoint);
					orAdd.Run();
					connections[sourceEndPoint] = orAdd;
				}
				await orAdd.SendToServerAsync(udpReceiveResult.Buffer).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception value)
		{
			Logger.Instance.Warn($"An exception occurred on receiving a client datagram: {value}");
		}
		finally
		{
			foreach (Connection connection in connections.Values)
			{
				connection.Stop();
			}
			try
			{
				await cleanupTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
			}
		}
	}

	private async Task CleanupConnectionsAsync(ConcurrentDictionary<IPEndPoint, Connection> connections, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
			foreach (KeyValuePair<IPEndPoint, Connection> pair in connections.ToArray())
			{
				if (pair.Value.LastActivity + ConnectionTimeout < Environment.TickCount64 || !pair.Value.IsRunning)
				{
					connections.TryRemove(pair.Key, out _);
					pair.Value.Stop();
				}
			}
		}
	}
}
