using System;
using System.Collections.Generic;
using System.Linq;

namespace Voidstrap.Core;

public static class SettingsKeyResolver
{
	private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["ActivityTrackingEnabled"] = "EnableActivityTracking",
		["BlockTelemetry"] = "BlockRobloxTelemetry",
		["Dialog"] = "BootstrapperStyle",
		["DisableAppPatchEnabled"] = "UseDisableAppPatch",
		["disablecrashhandleryayyysocool"] = "DisableCrash",
		["DuckRobloxAudio"] = "DuckRobloxAudioOnUnfocus",
		["SelectedAutoTranslateLanguage"] = "AutoTranslateLanguage",
		["SelectedBackdrop"] = "WindowBackdrop",
		["SelectedCpuLimit"] = "CpuCoreLimit",
		["SelectedLanguage"] = "Locale",
		["SelectedPriority"] = "PriorityLimit",
		["Snowww"] = "SnowWOWSOCOOLWpfSnowbtw",
		["Theme"] = "Theme2",
		["UpdateCheckingEnabled"] = "CheckForUpdates"
	};

	public static string? Resolve(SettingsDocument document, IEnumerable<string> aliases)
	{
		if (document is null)
		{
			throw new ArgumentNullException(nameof(document));
		}

		foreach (string alias in aliases.Where(static alias => !string.IsNullOrWhiteSpace(alias)))
		{
			string candidate = Aliases.TryGetValue(alias, out string? mapped) ? mapped : alias;
			string? key = document.Root.Select(static pair => pair.Key).FirstOrDefault(key => string.Equals(key, candidate, StringComparison.OrdinalIgnoreCase));
			if (key is not null)
			{
				return key;
			}
		}

		return null;
	}
}
