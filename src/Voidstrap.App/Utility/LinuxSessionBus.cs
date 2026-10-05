#if CROSSPLAT
using System;

namespace Voidstrap.Utility;

internal static class LinuxSessionBus
{
	public static string? Address
	{
		get
		{
			try
			{
				string? value = Tmds.DBus.Address.Session;
				return string.IsNullOrWhiteSpace(value) ? null : value;
			}
			catch (Exception)
			{
				return null;
			}
		}
	}

	public static string RequireAddress()
	{
		return Address ?? throw new InvalidOperationException("No desktop session bus is available");
	}
}
#endif
