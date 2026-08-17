using System;
using System.Diagnostics;

namespace Voidstrap;

public static class CpuCoreLimiter
{
	private static readonly object Sync = new object();

	private static IntPtr? _originalAffinity;

	public static void ApplyConfiguredLimit()
	{
		SetCpuCoreLimit(App.Settings.Prop.CpuCoreLimit);
	}

	public static void SetCpuCoreLimit(int coreCount)
	{
		int processorCount = Environment.ProcessorCount;
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
				ulong mask = ((1UL << coreCount) - 1UL) << (processorCount - coreCount);
				process.ProcessorAffinity = (IntPtr)unchecked((long)mask);
				App.Logger.WriteLine("CpuCoreLimiter", "Voidstrap CPU limit set to the top " + coreCount + " logical processors");
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("CpuCoreLimiter", "CPU limit change failed: " + ex.Message);
			}
		}
	}
}
