using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Linq;

namespace VoidstrapClient.ServerAuthInterface;

internal static class Utils
{
	public static string GetLocalIP()
	{
		try
		{
			using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.IP);
			socket.Connect("8.8.8.8", 65530);
			if (socket.LocalEndPoint is IPEndPoint routeEndpoint && !IPAddress.IsLoopback(routeEndpoint.Address))
				return routeEndpoint.Address.ToString();
		}
		catch
		{
		}

		var candidates = NetworkInterface.GetAllNetworkInterfaces()
			.Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback && network.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
			.SelectMany(network =>
			{
				IPInterfaceProperties properties = network.GetIPProperties();
				bool hasGateway = properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
				return properties.UnicastAddresses
					.Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
					.Select(address => new { address.Address, HasGateway = hasGateway, IsLinkLocal = address.Address.GetAddressBytes() is [169, 254, _, _] });
			})
			.OrderByDescending(candidate => candidate.HasGateway)
			.ThenBy(candidate => candidate.IsLinkLocal)
			.ToList();
		if (candidates.Count > 0)
			return candidates[0].Address.ToString();
		return IPAddress.Loopback.ToString();
	}
}
