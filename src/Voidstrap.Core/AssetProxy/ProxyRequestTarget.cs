using System;
using System.Globalization;

namespace Voidstrap.Core.AssetProxy;

public static class ProxyRequestTarget
{
	public static bool TryResolve(string? target, string? hostHeader, bool isConnect, out string host, out int port)
	{
		host = "";
		port = isConnect ? 443 : 80;
		string? authority = target;
		if (!isConnect)
		{
			if (Uri.TryCreate(target, UriKind.Absolute, out Uri? absolute)
				&& (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
			{
				host = absolute.Host.ToLowerInvariant();
				port = absolute.Port;
				return host.Length > 0;
			}

			authority = hostHeader;
		}

		if (string.IsNullOrWhiteSpace(authority))
		{
			return false;
		}

		authority = authority.Trim();
		if (authority.StartsWith('['))
		{
			int close = authority.IndexOf(']');
			if (close < 0)
			{
				return false;
			}

			host = authority[1..close].ToLowerInvariant();
			if (close + 1 < authority.Length)
			{
				if (authority[close + 1] != ':' || !TryParsePort(authority[(close + 2)..], out port))
				{
					return false;
				}
			}

			return host.Length > 0;
		}

		int separator = authority.LastIndexOf(':');
		if (separator > 0 && separator == authority.IndexOf(':'))
		{
			if (!TryParsePort(authority[(separator + 1)..], out port))
			{
				return false;
			}

			host = authority[..separator].ToLowerInvariant();
		}
		else if (separator < 0)
		{
			host = authority.ToLowerInvariant();
		}
		else
		{
			return false;
		}

		return host.Length > 0 && !host.Contains('/');
	}

	private static bool TryParsePort(string value, out int port)
	{
		return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
	}
}
