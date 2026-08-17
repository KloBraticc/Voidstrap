using System;
using System.Collections.Generic;
using System.Linq;
using Voidstrap.Platform;

namespace Voidstrap.Desktop;

public sealed record DesktopPageDefinition(
	string Id,
	string Title,
	string Section,
	string Description,
	FeatureId? CapabilityFeature,
	bool IsSidebar)
{
	public override string ToString()
	{
		return Title;
	}
}

public static class DesktopPageCatalog
{
	private static readonly DesktopPageDefinition[] AllPages =
	[
		new("Home", "Home", "Core", "Your launch, activity, and platform overview.", null, true),
		new("Integrations", "Integrations", "Core", "Connected services and activity integrations.", FeatureId.Notifications, true),
		new("Deployment", "Deployment", "Core", "Roblox runtime discovery, launching, downloads, and deployment behavior.", FeatureId.RobloxPlayer, true),
		new("Appearance", "Appearance", "Core", "Theme, accessibility, language, and interface appearance.", FeatureId.DesktopShell, true),
		new("FastFlagSettings", "FastFlag Settings", "Configuration", "Managed Roblox client flag presets.", FeatureId.RobloxPlayer, true),
		new("FastFlagEditor", "FastFlag Editor", "Configuration", "Advanced Roblox client flag editing.", FeatureId.RobloxPlayer, true),
		new("Global", "Global", "Configuration", "Global Roblox preferences and game settings.", FeatureId.RobloxPlayer, true),
		new("Mods", "Mods", "Configuration", "Overlays, visual settings, and platform dependent enhancements.", FeatureId.Overlay, true),
		new("News", "News", "Updates", "Release news and announcements.", null, true),
		new("Manager", "Manager", "Updates", "Installed runtime and download management.", FeatureId.RobloxPlayer, true),
		new("Extensions", "Extensions", "Footer", "Extension discovery and platform compatibility.", FeatureId.ExtensionNativeAssets, true),
		new("Shortcuts", "Shortcuts", "Footer", "Application and Roblox protocol shortcuts.", FeatureId.ProtocolRegistration, true),
		new("Settings", "Settings", "Footer", "Application behavior, updates, and diagnostics.", FeatureId.DesktopShell, true),
		new("About", "About", "Footer", "Application information, licenses, translators, and support.", null, true),
		new("Friends", "Friends", "Secondary", "Roblox friend activity.", FeatureId.EmbeddedBrowser, false),
		new("History", "History", "Secondary", "Recently played Roblox experiences.", FeatureId.RobloxPlayer, false),
		new("Library", "Library", "Secondary", "Saved and discovered Roblox experiences.", FeatureId.EmbeddedBrowser, false),
		new("MobileSupport", "Mobile Support", "Secondary", "Roblox mobile support information.", null, false),
		new("MobileExplanation", "Mobile Explanation", "Secondary", "Roblox mobile support guidance.", null, false),
		new("Releases", "Releases", "Secondary", "Application release history.", null, false),
		new("Help", "Help", "Secondary", "Help and support resources.", null, false),
		new("Donation", "Support", "Secondary", "Community support information.", null, false),
		new("NvidiaFastFlags", "NVIDIA Settings", "Secondary", "Windows graphics driver and FastFlag settings.", FeatureId.FrameGeneration, false),
		new("NvidiaEditor", "NVIDIA Editor", "Secondary", "Windows graphics profile editing.", FeatureId.FrameGeneration, false),
		new("FastFlagWarning", "FastFlag Editor Warning", "Secondary", "Advanced FastFlag safety information.", FeatureId.RobloxPlayer, false),
		new("Game", "Game", "Secondary", "A specific Roblox experience.", FeatureId.RobloxPlayer, false)
	];

	public static IReadOnlyList<DesktopPageDefinition> Pages => AllPages;

	public static IReadOnlyList<DesktopPageDefinition> SidebarPages => AllPages.Where(static page => page.IsSidebar).ToArray();

	public static DesktopPageDefinition GetRequired(string id)
	{
		return AllPages.First(page => string.Equals(page.Id, id, StringComparison.Ordinal));
	}
}
