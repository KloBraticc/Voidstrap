using System;
using System.Diagnostics;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3D11 = Vortice.Direct3D11.D3D11;

namespace Voidstrap.Integrations.Overlays;

internal static class GraphicsPrewarm
{
	private const string LOG_IDENT = "GraphicsPrewarm";

	private static readonly FeatureLevel[] FeatureLevels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

	private static int _started;

	public static void StartIfUseful()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return;
		if (App.LaunchSettings.RobloxLaunchMode != LaunchMode.Player && !App.LaunchSettings.WatcherFlag.Active)
			return;
		if (!OverlaySettings.RiShadeClientActive && !OverlaySettings.GameEffectsEnabled && !OverlaySettings.HomepageBackgroundEnabled)
			return;
		if (Interlocked.Exchange(ref _started, 1) != 0)
			return;
		Thread thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "GraphicsPrewarm",
			Priority = ThreadPriority.Normal
		};
		thread.Start();
	}

	private static void Run()
	{
		long started = Stopwatch.GetTimestamp();
		int warmed = 0;
		try
		{
			using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
			for (uint i = 0; ; i++)
			{
				if (factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Failure || adapter == null)
					break;
				using (adapter)
				{
					if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
						continue;
					if (D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, FeatureLevels, out ID3D11Device? device, out ID3D11DeviceContext? context).Success)
						warmed++;
					context?.Dispose();
					device?.Dispose();
				}
			}
			App.Logger.WriteLine(LOG_IDENT, $"Graphics driver loaded ahead of the compositor on {warmed} adapter(s) in {(int)Stopwatch.GetElapsedTime(started).TotalMilliseconds} ms");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Graphics driver prewarm skipped: " + ex.Message);
		}
	}
}
