using System.Collections;

namespace Voidstrap.Platform.Linux;

public enum LinuxSteamDeckModel
{
	None,
	Lcd,
	Oled
}

public sealed record LinuxSteamOSInfo(
	bool IsSteamOS,
	bool IsSteamOSLike,
	string DistributionName,
	LinuxSteamDeckModel DeckModel,
	bool IsGamescopeSession,
	bool IsGameMode)
{
	public bool IsSteamDeck => DeckModel != LinuxSteamDeckModel.None;

	public string Describe()
	{
		List<string> parts = [];
		parts.Add(string.IsNullOrWhiteSpace(DistributionName) ? "Linux" : DistributionName);
		if (DeckModel == LinuxSteamDeckModel.Lcd)
			parts.Add("on a Steam Deck LCD");
		else if (DeckModel == LinuxSteamDeckModel.Oled)
			parts.Add("on a Steam Deck OLED");
		if (IsGameMode)
			parts.Add("in Game Mode");
		else if (IsGamescopeSession)
			parts.Add("inside gamescope");
		else if (IsSteamOSLike)
			parts.Add("in Desktop Mode");
		return string.Join(" ", parts);
	}
}

public static class LinuxSteamOS
{
	private static readonly string[] SteamOSLikeIds = ["steamos", "holoiso", "chimeraos", "bazzite"];

	private static readonly Lazy<LinuxSteamOSInfo> Detected = new(DetectCurrent);

	public static LinuxSteamOSInfo Current => Detected.Value;

	public static string WithPrivilegeHint(string message)
	{
		LinuxSteamOSInfo current = Current;
		if (current.IsGameMode)
			return message + ". Switch to Desktop Mode and try again, Game Mode cannot show the password prompt";
		if (current.IsSteamOSLike)
			return message + ". On SteamOS the password prompt only works after you set a password once by running passwd in Konsole";
		return message;
	}

	private static LinuxSteamOSInfo DetectCurrent()
	{
		return Detect(
			ReadText("/etc/os-release") ?? ReadText("/usr/lib/os-release") ?? string.Empty,
			ReadText("/sys/class/dmi/id/sys_vendor"),
			ReadText("/sys/class/dmi/id/board_vendor"),
			ReadText("/sys/class/dmi/id/product_name"),
			Environment.GetEnvironmentVariables());
	}

	public static LinuxSteamOSInfo Detect(string osRelease, string? systemVendor, string? boardVendor, string? productName, IDictionary environment)
	{
		Dictionary<string, string> release = ParseOsRelease(osRelease);
		string id = release.GetValueOrDefault("ID", string.Empty).ToLowerInvariant();
		string idLike = release.GetValueOrDefault("ID_LIKE", string.Empty).ToLowerInvariant();
		string variant = release.GetValueOrDefault("VARIANT_ID", string.Empty).ToLowerInvariant();
		string name = release.GetValueOrDefault("PRETTY_NAME", release.GetValueOrDefault("NAME", string.Empty));

		bool steamOS = id == "steamos";
		bool steamOSLike = steamOS
			|| SteamOSLikeIds.Contains(id)
			|| idLike.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("steamos")
			|| variant.Contains("deck", StringComparison.Ordinal);

		bool valve = IsValve(systemVendor) || IsValve(boardVendor);
		string product = (productName ?? string.Empty).Trim();
		LinuxSteamDeckModel deck = !valve
			? LinuxSteamDeckModel.None
			: string.Equals(product, "Galileo", StringComparison.OrdinalIgnoreCase)
				? LinuxSteamDeckModel.Oled
				: string.Equals(product, "Jupiter", StringComparison.OrdinalIgnoreCase)
					? LinuxSteamDeckModel.Lcd
					: LinuxSteamDeckModel.None;

		string currentDesktop = Read(environment, "XDG_CURRENT_DESKTOP");
		bool desktopIsGamescope = currentDesktop
			.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Any(part => string.Equals(part, "gamescope", StringComparison.OrdinalIgnoreCase));
		bool gamescopeSession = desktopIsGamescope || Read(environment, "GAMESCOPE_WAYLAND_DISPLAY").Length > 0;
		bool gameMode = desktopIsGamescope
			|| (gamescopeSession && (Read(environment, "SteamGamepadUI") == "1" || Read(environment, "STEAM_GAMEPADUI") == "1"));

		return new LinuxSteamOSInfo(steamOS, steamOSLike || deck != LinuxSteamDeckModel.None, name, deck, gamescopeSession, gameMode);
	}

	public static Dictionary<string, string> ParseOsRelease(string contents)
	{
		Dictionary<string, string> values = new(StringComparer.Ordinal);
		foreach (string rawLine in contents.Split('\n'))
		{
			string line = rawLine.Trim();
			if (line.Length == 0 || line[0] == '#')
				continue;
			int separator = line.IndexOf('=');
			if (separator <= 0)
				continue;
			string value = line[(separator + 1)..].Trim();
			if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
				value = value[1..^1];
			values[line[..separator].Trim()] = value;
		}
		return values;
	}

	private static bool IsValve(string? vendor)
	{
		return (vendor ?? string.Empty).Trim().StartsWith("Valve", StringComparison.OrdinalIgnoreCase);
	}

	private static string Read(IDictionary environment, string name)
	{
		return environment[name] is string value ? value.Trim() : string.Empty;
	}

	private static string? ReadText(string path)
	{
		try
		{
			return File.Exists(path) ? File.ReadAllText(path) : null;
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}
}
