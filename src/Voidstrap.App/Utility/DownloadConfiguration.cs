using System;
using System.Collections.Generic;
using System.Linq;
using Voidstrap.Models.Persistable;

namespace Voidstrap.Utility;

public static class DownloadConfiguration
{
	public static IReadOnlyList<int> BufferChoices { get; } = [64, 128, 256, 512, 1024, 2048, 4096, 8192];

	public static IReadOnlyList<int> ConcurrentChoices { get; } = Enumerable.Range(1, 32).ToArray();

	public static IReadOnlyList<int> SegmentChoices { get; } = Enumerable.Range(1, 16).ToArray();

	public static int NormalizeBuffer(int value)
	{
		return BufferChoices.OrderBy(choice => Math.Abs((long)choice - value)).ThenBy(choice => choice).First();
	}

	public static int NormalizeConcurrent(int value)
	{
		return Math.Clamp(value, ConcurrentChoices[0], ConcurrentChoices[^1]);
	}

	public static int NormalizeSegments(int value)
	{
		return Math.Clamp(value, SegmentChoices[0], SegmentChoices[^1]);
	}

	public static int ResolveSegmentRequestLimit(AppSettings settings)
	{
		int packages = NormalizeConcurrent(settings.MaxConcurrentDownloads);
		int segments = NormalizeSegments(settings.MaxDownloadSegments);
		return Math.Clamp(packages * segments, 1, 32);
	}

	public static bool Normalize(AppSettings settings)
	{
		int buffer = NormalizeBuffer(settings.DownloadBufferKb);
		int concurrent = NormalizeConcurrent(settings.MaxConcurrentDownloads);
		int segments = NormalizeSegments(settings.MaxDownloadSegments);
		bool changed = settings.DownloadBufferKb != buffer || settings.MaxConcurrentDownloads != concurrent || settings.MaxDownloadSegments != segments || settings.DownloadPipelineVersion != 3;
		settings.DownloadBufferKb = buffer;
		settings.MaxConcurrentDownloads = concurrent;
		settings.MaxDownloadSegments = segments;
		settings.DownloadPipelineVersion = 3;
		return changed;
	}
}
