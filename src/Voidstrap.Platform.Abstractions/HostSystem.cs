using System;

namespace Voidstrap.Platform;

public static class HostSystem
{
	public static bool IsFreeBSD { get; } = OperatingSystem.IsFreeBSD();

	public static bool UsesLinuxHost { get; } = OperatingSystem.IsLinux() || IsFreeBSD;
}
