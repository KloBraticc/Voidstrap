using System;
using System.Collections.Generic;
using Voidstrap.Core;
using Voidstrap.Platform;

namespace Voidstrap.Desktop;

public static class DesktopRuntime
{
	private static IPlatformHost? _host;
	private static string? _initialDeeplink;

	public static IPlatformHost Host => _host ?? throw new InvalidOperationException("The desktop runtime has not been configured");

	public static string? InitialDeeplink => _initialDeeplink;

	public static void Configure(IPlatformHost host, IReadOnlyList<string>? arguments = null)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
		_initialDeeplink = FindDeeplink(arguments);
	}

	private static string? FindDeeplink(IReadOnlyList<string>? arguments)
	{
		if (arguments is null)
		{
			return null;
		}

		foreach (string argument in arguments)
		{
			if (RobloxDeeplink.TryExtract(argument, out Uri? deeplink) && deeplink is not null)
			{
				return deeplink.AbsoluteUri;
			}
		}

		return null;
	}
}
