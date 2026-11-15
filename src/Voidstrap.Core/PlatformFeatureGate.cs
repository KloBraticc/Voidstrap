using System;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public static class PlatformFeatureGate
{
	public static bool IsHidden(IPlatformCapabilities? capabilities, FeatureId feature)
	{
		if (capabilities is null)
		{
			return !OperatingSystem.IsWindows();
		}

		if (capabilities.Platform == PlatformId.Windows)
		{
			return false;
		}

		return capabilities.Get(feature).State == CapabilityState.Unavailable;
	}
}
