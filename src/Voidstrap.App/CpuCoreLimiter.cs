using System;
using System.Diagnostics;

namespace Voidstrap;

public static class CpuCoreLimiter
{
	private static readonly object Sync = new object();

	private static readonly int ProcessorCount = Environment.ProcessorCount;

	private static IntPtr? _originalAffinity;

	public static void ApplyConfiguredLimit()
	{
		SetCpuCoreLimit(App.Settings.Prop.CpuCoreLimit);
	}

	public static void SetCpuCoreLimit(int coreCount)
	{
		if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
		{
			return;
		}
		if (OperatingSystem.IsLinux())
		{
			Voidstrap.Platform.Linux.LinuxSoberResources.CaptureStartupAffinity();
		}
		int processorCount = ProcessorCount;
		if (processorCount > IntPtr.Size * 8)
		{
			return;
		}
		if (coreCount < 1 || coreCount > processorCount)
		{
			coreCount = processorCount;
		}
		lock (Sync)
		{
			try
			{
				using Process process = Process.GetCurrentProcess();
				_originalAffinity ??= process.ProcessorAffinity;
				if (coreCount >= processorCount)
				{
					process.ProcessorAffinity = _originalAffinity.Value;
					return;
				}
				ulong original = unchecked((ulong)(long)_originalAffinity.Value);
				ulong mask = 0;
				for (int bit = 63, taken = 0; bit >= 0 && taken < coreCount; bit--)
				{
					if ((original & (1UL << bit)) != 0)
					{
						mask |= 1UL << bit;
						taken++;
					}
				}
				process.ProcessorAffinity = new IntPtr(unchecked((long)mask));
				App.Logger.WriteLine("CpuCoreLimiter", "Voidstrap CPU limit set to the top " + coreCount + " logical processors");
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("CpuCoreLimiter", "CPU limit change failed: " + ex.Message);
			}
		}
	}
}
