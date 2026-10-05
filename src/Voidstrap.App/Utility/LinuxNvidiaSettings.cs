using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Voidstrap.Integrations;
using Voidstrap.Models;

namespace Voidstrap.Utility;

internal static class LinuxNvidiaSettings
{
	public const string OverlayId = "2945366";

	public const string FrameRateLimitId = "277041154";

	public const string LowLatencyId = "390467";

	public const string FxaaId = "276089202";

	public const string FxaaModeId = "276757595";

	public const int OverlayOff = 0;

	public const int OverlayFps = 1;

	public const int OverlayGraph = 4;

	public const int OverlayFull = 511;

	public sealed record Choices(int FrameRateLimit, int Overlay, bool LowLatency, bool Fxaa)
	{
		public static Choices None { get; } = new(0, OverlayOff, false, false);
	}

	public static string NipPath => Path.Combine(Paths.NipProfiles, "Voidstrap.nip");

	public static bool Applies => OperatingSystem.IsLinux() && GpuInventory.HasNvidia;

	public static Choices Read()
	{
		if (!Applies || !File.Exists(NipPath))
			return Choices.None;

		try
		{
			Dictionary<string, string> values = new(StringComparer.Ordinal);
			foreach (NvidiaEditorEntry entry in NvidiaProfileManager.LoadFromNip(NipPath))
			{
				string id = NormalizeId(entry.SettingId);
				if (id.Length > 0 && !values.ContainsKey(id))
					values[id] = (entry.Value ?? string.Empty).Trim();
			}

			return new Choices(
				Math.Clamp(ReadInt(values, FrameRateLimitId), 0, 1000),
				NormalizeOverlay(ReadInt(values, OverlayId)),
				ReadInt(values, LowLatencyId) > 0,
				ReadInt(values, FxaaId) == 1 && ReadInt(values, FxaaModeId) == 1);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
		{
			App.Logger.WriteLine("LinuxNvidiaSettings", "The NVIDIA settings could not be read: " + ex.Message);
			return Choices.None;
		}
	}

	public static int NormalizeOverlay(int value)
	{
		return value switch
		{
			OverlayOff => OverlayOff,
			OverlayFull => OverlayFull,
			1 or 2 => OverlayFps,
			< 0 => OverlayOff,
			_ => OverlayGraph
		};
	}

	public static string? OverlayConfiguration(int overlay)
	{
		return NormalizeOverlay(overlay) switch
		{
			OverlayFps => "fps_only",
			OverlayGraph => "gpu_stats=0,cpu_stats=0,fps,frametime,frame_timing",
			OverlayFull => "full",
			_ => null
		};
	}

	public static IReadOnlyList<string> BuildLaunchArguments(Choices choices, bool overlayLayerInstalled)
	{
		List<string> arguments = [];
		string? overlay = OverlayConfiguration(choices.Overlay);
		if (overlay is not null && overlayLayerInstalled)
		{
			arguments.Add("--env=MANGOHUD=1");
			arguments.Add("--env=MANGOHUD_CONFIG=" + overlay);
		}

		if (choices.LowLatency)
			arguments.Add("--env=__GL_MaxFramesAllowed=1");

		return arguments;
	}

	public static void ApplyFrameRateLimit(Choices choices)
	{
		if (choices.FrameRateLimit <= 0)
			return;

		try
		{
			App.GlobalSettings.Load();
			if (App.GlobalSettings.GetInt("FramerateCap", 0) == choices.FrameRateLimit)
				return;
			if (App.GlobalSettings.SetInt("FramerateCap", choices.FrameRateLimit) && App.GlobalSettings.Save())
				App.Logger.WriteLine("LinuxNvidiaSettings", "Roblox frame rate limited to " + choices.FrameRateLimit.ToString(CultureInfo.InvariantCulture));
			else
				App.Logger.WriteLine("LinuxNvidiaSettings", "The Roblox frame rate limit could not be saved");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			App.Logger.WriteLine("LinuxNvidiaSettings", "The Roblox frame rate limit could not be saved: " + ex.Message);
		}
	}

	private static int ReadInt(Dictionary<string, string> values, string id)
	{
		if (!values.TryGetValue(id, out string? raw))
			return 0;
		if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int signed))
			return signed;
		return uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint unsigned) ? unchecked((int)unsigned) : 0;
	}

	private static string NormalizeId(string? id)
	{
		string text = (id ?? string.Empty).Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
			&& uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
			return parsed.ToString(CultureInfo.InvariantCulture);
		return text;
	}
}
