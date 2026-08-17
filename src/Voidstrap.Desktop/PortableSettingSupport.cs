using System;
using Voidstrap.Platform;

namespace Voidstrap.Desktop;

internal sealed record PortableSettingSupport(bool IsEditable, string Reason);

internal static class PortableSettingSupportResolver
{
	public static PortableSettingSupport Resolve(IPlatformHost host, string? key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return new PortableSettingSupport(false, "This action requires a platform implementation that is still being migrated.");
		}

		if (string.Equals(key, "Theme2", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "WindowBackdrop", StringComparison.OrdinalIgnoreCase))
		{
			return new PortableSettingSupport(true, "This setting applies immediately in the shared desktop shell.");
		}

		if (string.Equals(key, "OptimizeRoblox", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(key, "PriorityLimit", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(key, "SelectedCpuPriority", StringComparison.OrdinalIgnoreCase))
		{
			if (host.Id == PlatformId.Windows && host.ResourceOptimization.Capability.IsAvailable)
			{
				return new PortableSettingSupport(true, "This setting applies to a directly launched Roblox process.");
			}

			return new PortableSettingSupport(false, "This setting needs a directly managed Roblox process in the shared host.");
		}

		if ((string.Equals(key, "PlayerInstallLocation", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "StudioInstallLocation", StringComparison.OrdinalIgnoreCase)) && host.Id == PlatformId.Windows)
		{
			return new PortableSettingSupport(true, "This location is used by Windows runtime discovery.");
		}

		return new PortableSettingSupport(false, "This setting remains available in the WPF baseline while its shared implementation is migrated.");
	}
}
