using System;
using System.Collections.Generic;
using System.Reflection;
using Voidstrap.Core;
using Voidstrap.Enums;
using Voidstrap.Models;

namespace Voidstrap;

public class LaunchSettings
{
	private readonly Dictionary<string, LaunchFlag> _flagMap = new Dictionary<string, LaunchFlag>();

	public LaunchFlag MenuFlag { get; } = new LaunchFlag("preferences,menu,settings");

	public LaunchFlag WatcherFlag { get; } = new LaunchFlag("watcher");

	public LaunchFlag BackgroundUpdaterFlag { get; } = new LaunchFlag("backgroundupdater");

	public LaunchFlag QuietFlag { get; } = new LaunchFlag("quiet");

	public LaunchFlag UninstallFlag { get; } = new LaunchFlag("uninstall");

	public LaunchFlag NoLaunchFlag { get; } = new LaunchFlag("nolaunch");

	public LaunchFlag TestModeFlag { get; } = new LaunchFlag("testmode");

	public LaunchFlag WindowAuditFlag { get; } = new LaunchFlag("windowaudit");

	public LaunchFlag NvApplyFlag { get; } = new LaunchFlag("nvapply");

	public LaunchFlag NoGPUFlag { get; } = new LaunchFlag("nogpu");

	public LaunchFlag UpgradeFlag { get; } = new LaunchFlag("upgrade");

	public LaunchFlag ThemeFlag { get; } = new LaunchFlag("theme");

	public LaunchFlag PlayerFlag { get; } = new LaunchFlag("player");

	public LaunchFlag StudioFlag { get; } = new LaunchFlag("studio");

	public LaunchFlag VersionFlag { get; } = new LaunchFlag("version");

	public LaunchFlag ChannelFlag { get; } = new LaunchFlag("channel");

	public LaunchFlag ForceFlag { get; } = new LaunchFlag("force");

	public LaunchFlag BloxshadeFlag { get; } = new LaunchFlag("bloxshade");

	public LaunchFlag MatchmakerRejoinFlag { get; } = new LaunchFlag("matchmakerrejoin");

	public LaunchFlag MatchmakerAttemptFlag { get; } = new LaunchFlag("matchmakerattempt");

	public LaunchFlag MatchmakerTargetFlag { get; } = new LaunchFlag("matchmakertarget");

	public LaunchFlag TelemetryBlockFlag { get; } = new LaunchFlag("telemetryblock");

	public LaunchFlag OrcRedirectFlag { get; } = new LaunchFlag("orcredirect");

	public bool IsHelperInvocation =>
		NvApplyFlag.Active
		|| WindowAuditFlag.Active
		|| TelemetryBlockFlag.Active
		|| OrcRedirectFlag.Active
		|| UninstallFlag.Active
		|| FactoryResetFlag.Active;

	public LaunchFlag AdminRetriedFlag { get; } = new LaunchFlag("adminretried");

	public LaunchFlag ResumeLaunchFlag { get; } = new LaunchFlag("resumelaunch");

	public LaunchFlag FactoryResetFlag { get; } = new LaunchFlag("factoryreset");

	public LaunchFlag DeferredCleanupFlag { get; } = new LaunchFlag("deferredcleanup");

	public LaunchFlag AssetWarpGuardFlag { get; } = new LaunchFlag("assetwarpguard");

	public LaunchFlag AssetWarpCleanupFlag { get; } = new LaunchFlag("assetwarpcleanup");

	public bool BypassUpdateCheck
	{
		get
		{
			if (!UninstallFlag.Active)
			{
				return WatcherFlag.Active;
			}
			return true;
		}
	}

	public LaunchMode RobloxLaunchMode { get; set; }

	public string RobloxLaunchArgs { get; set; } = "";

	public string[] Args { get; private set; }

	public LaunchSettings(string[] args)
	{
		Args = args;
		PropertyInfo[] properties = GetType().GetProperties();
		foreach (PropertyInfo propertyInfo in properties)
		{
			if (!(propertyInfo.PropertyType != typeof(LaunchFlag)) && propertyInfo.GetValue(this) is LaunchFlag launchFlag)
			{
				string[] array = launchFlag.Identifiers.Split(',');
				foreach (string key in array)
				{
					_flagMap.Add(key, launchFlag);
				}
			}
		}
		int num = 0;
		if (Args.Length >= 1)
		{
			string text = Args[0];
			if (RobloxDeeplink.TryExtract(text, out Uri? directLink) && directLink != null)
			{
				bool studio = directLink.Scheme.StartsWith("roblox-studio", StringComparison.OrdinalIgnoreCase);
				App.Logger.WriteLine("LaunchSettings", studio ? "Got Roblox Studio argument" : "Got Roblox player argument");
				RobloxLaunchMode = directLink.Scheme.Equals("roblox-studio-auth", StringComparison.OrdinalIgnoreCase) ? LaunchMode.StudioAuth : studio ? LaunchMode.Studio : LaunchMode.Player;
				RobloxLaunchArgs = directLink.AbsoluteUri;
				num = 1;
			}
		}
		for (int k = num; k < Args.Length; k++)
		{
			string text2 = Args[k];
			if (!text2.StartsWith('-'))
			{
				App.Logger.WriteLine("LaunchSettings", "Invalid argument: " + text2);
				continue;
			}
			string text3 = text2.Substring(1);
			if (!_flagMap.TryGetValue(text3, out LaunchFlag value) || value == null)
			{
				App.Logger.WriteLine("LaunchSettings", "Unknown argument: " + text3);
				continue;
			}
			value.Active = true;
			if (k < Args.Length - 1)
			{
				string text4 = Args[k + 1];
				if (text4 != null && !text4.StartsWith('-'))
				{
					value.Data = text4;
					k++;
					App.Logger.WriteLine("LaunchSettings", "Identifier '" + text3 + "' is active with data");
					continue;
				}
			}
			App.Logger.WriteLine("LaunchSettings", "Identifier '" + text3 + "' is active");
		}
		if (PlayerFlag.Active)
		{
			ParsePlayer(PlayerFlag.Data);
		}
		else if (StudioFlag.Active)
		{
			ParseStudio(StudioFlag.Data);
		}
	}

	private void ParsePlayer(string? data)
	{
		RobloxLaunchMode = LaunchMode.Player;
		if (RobloxDeeplink.TryExtract(data, out Uri? deeplink) && deeplink != null && !deeplink.Scheme.StartsWith("roblox-studio", StringComparison.OrdinalIgnoreCase))
		{
			App.Logger.WriteLine("LaunchSettings::ParsePlayer", "Got Roblox launch arguments");
			RobloxLaunchArgs = deeplink.AbsoluteUri;
		}
		else
		{
			App.Logger.WriteLine("LaunchSettings::ParsePlayer", "No Roblox launch arguments were provided");
		}
	}

	private void ParseStudio(string? data)
	{
		RobloxLaunchMode = LaunchMode.Studio;
		if (string.IsNullOrEmpty(data))
		{
			App.Logger.WriteLine("LaunchSettings::ParseStudio", "No Roblox launch arguments were provided");
		}
		else if (RobloxDeeplink.TryExtract(data, out Uri? deeplink) && deeplink != null && deeplink.Scheme.Equals("roblox-studio", StringComparison.OrdinalIgnoreCase))
		{
			App.Logger.WriteLine("LaunchSettings::ParseStudio", "Got Roblox Studio launch arguments");
			RobloxLaunchArgs = deeplink.AbsoluteUri;
		}
		else if (RobloxDeeplink.TryExtract(data, out deeplink) && deeplink != null && deeplink.Scheme.Equals("roblox-studio-auth", StringComparison.OrdinalIgnoreCase))
		{
			App.Logger.WriteLine("LaunchSettings::ParseStudio", "Got Roblox Studio Auth launch arguments");
			RobloxLaunchMode = LaunchMode.StudioAuth;
			RobloxLaunchArgs = deeplink.AbsoluteUri;
		}
		else
		{
			App.Logger.WriteLine("LaunchSettings::ParseStudio", "Got Roblox Studio local place file");
			RobloxLaunchArgs = "-task EditFile -localPlaceFile \"" + data + "\"";
		}
	}
}
