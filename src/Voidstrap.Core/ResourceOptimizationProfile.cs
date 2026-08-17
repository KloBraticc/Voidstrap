using System;
using System.Globalization;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed record ResourceOptimizationProfile(ResourcePriority Priority, int? CpuLimit)
{
	public bool IsEnabled => Priority != ResourcePriority.Automatic && Priority != ResourcePriority.Normal || CpuLimit is not null;
}

public static class ResourceOptimizationProfileResolver
{
	private static readonly int ProcessorCount = Environment.ProcessorCount;

	public static ResourceOptimizationProfile Resolve(SettingsDocument settings)
	{
		if (settings is null)
		{
			throw new ArgumentNullException(nameof(settings));
		}

		bool optimize = settings.Get("OptimizeRoblox", false);
		ResourcePriority priority = ParsePriority(settings.Get("PriorityLimit", "Normal"), optimize);
		int? cpuLimit = ParseCpuLimit(settings.Get("SelectedCpuPriority", "Automatic"));
		return new ResourceOptimizationProfile(priority, cpuLimit);
	}

	private static ResourcePriority ParsePriority(string? value, bool optimize)
	{
		if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "Normal", StringComparison.OrdinalIgnoreCase))
		{
			return optimize ? ResourcePriority.AboveNormal : ResourcePriority.Normal;
		}

		return value.Trim() switch
		{
			"Idle" => ResourcePriority.Idle,
			"Low" => ResourcePriority.Idle,
			"Below Normal" => ResourcePriority.BelowNormal,
			"BelowNormal" => ResourcePriority.BelowNormal,
			"Above Normal" => ResourcePriority.AboveNormal,
			"AboveNormal" => ResourcePriority.AboveNormal,
			"High" => ResourcePriority.High,
			"Realtime" => ResourcePriority.High,
			_ => optimize ? ResourcePriority.AboveNormal : ResourcePriority.Normal
		};
	}

	private static int? ParseCpuLimit(string? value)
	{
		if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "Automatic", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		string candidate = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
		if (!int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < 1)
		{
			return null;
		}

		return Math.Min(parsed, ProcessorCount);
	}
}
