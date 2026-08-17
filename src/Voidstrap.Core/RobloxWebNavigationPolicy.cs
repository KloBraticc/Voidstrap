using System;

namespace Voidstrap.Core;

public static class RobloxWebNavigationPolicy
{
	public static bool IsInAppRobloxUri(Uri uri)
	{
		if (uri is null || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return string.Equals(uri.Host, "roblox.com", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(uri.Host, "www.roblox.com", StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsSafeExternalUri(Uri uri)
	{
		return uri is not null && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
	}
}
