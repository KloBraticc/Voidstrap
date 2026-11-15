#if CROSSPLAT
using System;
using System.Threading.Tasks;
using Tmds.DBus;

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

	public static Connection Open(string address)
	{
		return Voidstrap.Platform.HostSystem.IsFreeBSD ? new Connection(new NoFileDescriptorOptions(address)) : new Connection(address);
	}

	private sealed class NoFileDescriptorOptions : ClientConnectionOptions
	{
		public NoFileDescriptorOptions(string address) : base(address)
		{
		}

		protected override async Task<ClientSetupResult> SetupAsync()
		{
			ClientSetupResult result = await base.SetupAsync().ConfigureAwait(false);
			result.SupportsFdPassing = false;
			return result;
		}
	}
}
#endif
