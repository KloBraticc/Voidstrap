using System;
using System.Collections.Generic;
using System.Linq;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public enum PortFeatureKind
{
	Page,
	Setting,
	Action,
	BrowserBridge,
	Extension,
	Dialog,
	NativeIntegration,
	Package
}

public enum PortFeatureState
{
	Shared,
	NativeEquivalent,
	PermissionRequired,
	ExternalRuntimeRequired,
	Experimental,
	Unavailable
}

public sealed record PortFeatureDefinition(
	string Id,
	PortFeatureKind Kind,
	string Title,
	string Source,
	string TestCaseId,
	FeatureId? RequiredFeature = null);

public sealed record PortFeatureStatus(
	PortFeatureDefinition Definition,
	PortFeatureState State,
	string Reason,
	string? RequiredAction = null);

public static class PortFeatureInventory
{
	private static readonly PortFeatureDefinition[] StaticDefinitions =
	[
		Page("Home", "Home", "HomePage", null),
		Page("Integrations", "Integrations", "IntegrationsPage", FeatureId.Notifications),
		Page("Deployment", "Deployment", "BootstrapperPage", FeatureId.RobloxPlayer),
		Page("Appearance", "Appearance", "AppearancePage", FeatureId.DesktopShell),
		Page("FastFlagSettings", "FastFlag Settings", "FastFlagsPage", FeatureId.RobloxPlayer),
		Page("FastFlagEditor", "FastFlag Editor", "FastFlagEditorPage", FeatureId.RobloxPlayer),
		Page("Global", "Global", "GBSEditorPage", FeatureId.RobloxPlayer),
		Page("Mods", "Mods", "ModsPage", FeatureId.AssetInjection),
		Page("News", "News", "NewsPage", null),
		Page("Manager", "Manager", "DownloadsPage", FeatureId.RobloxPlayer),
		Page("Extensions", "Extensions", "ExtensionPage", FeatureId.ExtensionNativeAssets),
		Page("Shortcuts", "Shortcuts", "ShortcutsPage", FeatureId.ProtocolRegistration),
		Page("Settings", "Settings", "ChannelPage", FeatureId.DesktopShell),
		Page("About", "About", "AboutPage", null),
		Page("Friends", "Friends", "FriendsPage", FeatureId.EmbeddedBrowser),
		Page("History", "History", "HistoryPage", FeatureId.RobloxPlayer),
		Page("Library", "Library", "LibraryPage", FeatureId.RobloxPlayer),
		Page("MobileSupport", "Mobile Support", "MobilePage", null),
		Page("MobileExplanation", "Mobile Explanation", "MobilePageExplain", null),
		Page("Releases", "Releases", "ReleasesPage", null),
		Page("Help", "Help", "HelpPage", null),
		Page("Donation", "Support", "DonoPage", null),
		Page("NvidiaFastFlags", "NVIDIA Settings", "NvidiaFastFlagsPage", FeatureId.FrameGeneration),
		Page("NvidiaEditor", "NVIDIA Editor", "NvidiaFFlagEditorPage", FeatureId.FrameGeneration),
		Page("FastFlagWarning", "FastFlag Editor Warning", "FastFlagEditorWarningPage", FeatureId.RobloxPlayer),
		Page("Game", "Game", "GamePage", FeatureId.RobloxPlayer),
		Page("ServerBrowser", "Server browser", "ServerBrowserPage", FeatureId.EmbeddedBrowser),
		Page("AboutOverview", "About overview", "About/Pages/AboutPage", null),
		Page("AboutSupporters", "Supporters", "About/Pages/SupportersPage", null),
		Page("AboutTranslators", "Translators", "About/Pages/TranslatorsPage", null),
		Page("AboutLicenses", "Licenses", "About/Pages/LicensesPage", null),
		Page("InstallerWelcome", "Installer welcome", "Installer/Pages/WelcomePage", FeatureId.DesktopShell),
		Page("InstallerInstall", "Installer install", "Installer/Pages/InstallPage", FeatureId.DesktopShell),
		Page("InstallerCompletion", "Installer completion", "Installer/Pages/CompletionPage", FeatureId.DesktopShell),
		Action("LaunchPlayer", "Launch player", FeatureId.RobloxPlayer),
		Action("LaunchStudio", "Launch Studio", FeatureId.RobloxStudio),
		Action("RegisterProtocols", "Register Roblox protocols", FeatureId.ProtocolRegistration),
		Action("RefreshRuntime", "Refresh runtime discovery", FeatureId.RobloxPlayer),
		Action("SendTestNotification", "Send test notification", FeatureId.Notifications),
		Action("MigrateSettings", "Migrate settings", FeatureId.DesktopShell),
		Action("CheckForUpdates", "Check for updates", FeatureId.DesktopShell),
		Action("ApplyUpdate", "Apply update", FeatureId.DesktopShell),
		Action("Install", "Install application", FeatureId.DesktopShell),
		Action("Repair", "Repair installation", FeatureId.DesktopShell),
		Action("Uninstall", "Uninstall application", FeatureId.DesktopShell),
		Bridge(RobloxBrowserBridgeAction.OpenExternal, FeatureId.DesktopShell),
		Bridge(RobloxBrowserBridgeAction.ScriptRan, FeatureId.EmbeddedBrowser),
		Bridge(RobloxBrowserBridgeAction.Matchmake, FeatureId.RobloxPlayer),
		Bridge(RobloxBrowserBridgeAction.PrivateServer, FeatureId.RobloxPlayer),
		Bridge(RobloxBrowserBridgeAction.UpdateHeatmap, FeatureId.EmbeddedBrowser),
		Bridge(RobloxBrowserBridgeAction.Underrated, FeatureId.EmbeddedBrowser),
		Bridge(RobloxBrowserBridgeAction.Users, FeatureId.EmbeddedBrowser),
		Bridge(RobloxBrowserBridgeAction.ProfileBanner, FeatureId.EmbeddedBrowser),
		Bridge(RobloxBrowserBridgeAction.TranslateTexts, FeatureId.EmbeddedBrowser),
		Bridge(RobloxBrowserBridgeAction.PinGame, FeatureId.EmbeddedBrowser),
		Bridge(RobloxBrowserBridgeAction.UnpinGame, FeatureId.EmbeddedBrowser),
		Bridge(RobloxBrowserBridgeAction.Launch, FeatureId.RobloxPlayer),
		Bridge(RobloxBrowserBridgeAction.ApiError, FeatureId.EmbeddedBrowser),
		Dialog("BootstrapperByfron", "Byfron bootstrapper dialog", "Bootstrapper/ByfronDialog"),
		Dialog("BootstrapperClassicFluent", "Classic Fluent bootstrapper dialog", "Bootstrapper/ClassicFluentDialog"),
		Dialog("BootstrapperCustom", "Custom bootstrapper dialog", "Bootstrapper/CustomDialog"),
		Dialog("BootstrapperFluent", "Fluent bootstrapper dialog", "Bootstrapper/FluentDialog"),
		Dialog("BootstrapperLegacy2008", "Legacy 2008 bootstrapper dialog", "Bootstrapper/LegacyDialog2008"),
		Dialog("BootstrapperLegacy2011", "Legacy 2011 bootstrapper dialog", "Bootstrapper/LegacyDialog2011"),
		Dialog("BootstrapperProgress", "Progress bootstrapper dialog", "Bootstrapper/ProgressDialog"),
		Dialog("BootstrapperVista", "Vista bootstrapper dialog", "Bootstrapper/VistaDialog"),
		Dialog("AddCustomTheme", "Add custom theme dialog", "Dialogs/AddCustomThemeDialog"),
		Dialog("AddFastFlag", "Add FastFlag dialog", "Dialogs/AddFastFlagDialog"),
		Dialog("AddNvidiaFastFlag", "Add NVIDIA FastFlag dialog", "Dialogs/AddNvidiaFFlagWindow"),
		Dialog("Bloxshade", "Bloxshade dialog", "Dialogs/BloxshadeDialog"),
		Dialog("ChannelLists", "Channel lists dialog", "Dialogs/ChannelListsDialog"),
		Dialog("Connectivity", "Connectivity dialog", "Dialogs/ConnectivityDialog"),
		Dialog("CursorPreview", "Cursor preview dialog", "Dialogs/CursorPreviewDialog"),
		Dialog("Exception", "Exception dialog", "Dialogs/ExceptionDialog"),
		Dialog("FastFlagPresets", "FastFlag presets dialog", "Dialogs/FFlagPresetsDialog"),
		Dialog("FastFlagSearch", "FastFlag search dialog", "Dialogs/FFlagSearchDialog"),
		Dialog("FlagProfiles", "Flag profiles dialog", "Dialogs/FlagProfilesDialog"),
		Dialog("FluentMessageBox", "Fluent message dialog", "Dialogs/FluentMessageBox"),
		Dialog("LanguageSelector", "Language selector dialog", "Dialogs/LanguageSelectorDialog"),
		Dialog("LaunchMenu", "Launch menu dialog", "Dialogs/LaunchMenuDialog"),
		Dialog("NewsItem", "News item dialog", "Dialogs/NewsItemDialog"),
		Dialog("Uninstaller", "Uninstaller dialog", "Dialogs/UninstallerDialog"),
		Dialog("BootstrapperEditor", "Bootstrapper editor window", "Editor/BootstrapperEditorWindow"),
		Dialog("DownloadProgress", "Download progress window", "Settings/DownloadProgressWindow"),
		Dialog("AnimationPreview", "Animation preview window", "ContextMenu/AnimationPreviewWindow"),
		Dialog("Crosshair", "Crosshair window", "ContextMenu/CursorWindow"),
		Dialog("DoubleMovement", "Double movement window", "ContextMenu/DoubleMovementWindow"),
		Dialog("ImageAdjust", "Image adjust window", "ContextMenu/ImageAdjustWindow"),
		Dialog("ImageRecolor", "Image recolor window", "ContextMenu/ImageRecolorWindow"),
		Dialog("MeshViewer", "Mesh viewer window", "ContextMenu/MeshViewerWindow"),
		Dialog("ProfileEditor", "Profile editor window", "ContextMenu/ProfileEditorWindow"),
		Dialog("RichPresence", "Rich presence window", "ContextMenu/RPCWindow"),
		Dialog("Overlay", "Overlay window", "ContextMenu/UIWindow"),
		Native(FeatureId.DesktopShell),
		Native(FeatureId.EmbeddedBrowser),
		Native(FeatureId.RobloxPlayer),
		Native(FeatureId.RobloxStudio),
		Native(FeatureId.SecureStorage),
		Native(FeatureId.ProtocolRegistration),
		Native(FeatureId.Updater),
		Native(FeatureId.Notifications),
		Native(FeatureId.Tray),
		Native(FeatureId.Overlay),
		Native(FeatureId.GlobalInput),
		Native(FeatureId.AudioSession),
		Native(FeatureId.ResourceOptimization),
		Native(FeatureId.AssetInjection),
		Native(FeatureId.FrameGeneration),
		Native(FeatureId.VirtualController),
		Native(FeatureId.ExtensionNativeAssets),
		Package("Windows", "Windows release package"),
		Package("MacOS", "macOS app, DMG, and ZIP package"),
		Package("LinuxDeb", "Linux Debian package"),
		Package("LinuxRpm", "Linux RPM package"),
		Package("LinuxAppImage", "Linux AppImage package")
	];

	private static readonly Dictionary<string, FeatureId?> PageFeatures = StaticDefinitions
		.Where(static definition => definition.Kind == PortFeatureKind.Page)
		.GroupBy(static definition => definition.Source, StringComparer.Ordinal)
		.ToDictionary(
			static group => group.Key,
			static group => group.Select(static definition => definition.RequiredFeature).FirstOrDefault(static feature => feature is not null),
			StringComparer.Ordinal);

	private static readonly HashSet<string> SharedPageIds = new(StringComparer.Ordinal)
	{
		"Page.Home",
		"Page.Appearance",
		"Page.Deployment",
		"Page.Extensions",
		"Page.Settings",
		"Page.About"
	};

	private static readonly HashSet<string> CatalogPageIds = new(StringComparer.Ordinal)
	{
		"Page.Integrations",
		"Page.FastFlagSettings",
		"Page.FastFlagEditor",
		"Page.Global",
		"Page.Mods",
		"Page.News",
		"Page.Manager",
		"Page.Shortcuts"
	};

	private static readonly HashSet<string> SharedBridgeIds = new(StringComparer.Ordinal)
	{
		"BrowserBridge.OpenExternal",
		"BrowserBridge.ScriptRan",
		"BrowserBridge.PrivateServer",
		"BrowserBridge.UpdateHeatmap",
		"BrowserBridge.PinGame",
		"BrowserBridge.UnpinGame",
		"BrowserBridge.Launch",
		"BrowserBridge.ApiError"
	};

	public static IReadOnlyCollection<PortFeatureDefinition> CreateDefinitions(
		IEnumerable<SettingsCatalogEntry>? settings = null,
		IEnumerable<ExtensionManifest>? extensions = null)
	{
		IEnumerable<PortFeatureDefinition> settingDefinitions = settings is null
			? Array.Empty<PortFeatureDefinition>()
			: settings.Select(CreateSettingDefinition);
		IEnumerable<PortFeatureDefinition> extensionDefinitions = extensions is null
			? Array.Empty<PortFeatureDefinition>()
			: extensions.Select(CreateExtensionDefinition);
		return StaticDefinitions
			.Concat(settingDefinitions)
			.Concat(extensionDefinitions)
			.GroupBy(static definition => definition.Id, StringComparer.Ordinal)
			.Select(static group => group.First())
			.OrderBy(static definition => definition.Id, StringComparer.Ordinal)
			.ToArray();
	}

	public static IReadOnlyCollection<PortFeatureStatus> Evaluate(
		IPlatformHost host,
		IEnumerable<SettingsCatalogEntry>? settings = null,
		IEnumerable<ExtensionManifest>? extensions = null)
	{
		if (host is null)
		{
			throw new ArgumentNullException(nameof(host));
		}

		return Evaluate(host.Capabilities, host.Paths.Storage, settings, extensions);
	}

	public static IReadOnlyCollection<PortFeatureStatus> Evaluate(
		IPlatformCapabilities capabilities,
		IEnumerable<SettingsCatalogEntry>? settings = null,
		IEnumerable<ExtensionManifest>? extensions = null)
	{
		if (capabilities is null)
		{
			throw new ArgumentNullException(nameof(capabilities));
		}

		return Evaluate(capabilities, null, settings, extensions);
	}

	public static IReadOnlyCollection<PortFeatureStatus> Evaluate(
		IPlatformCapabilities capabilities,
		PlatformStoragePaths? storage,
		IEnumerable<SettingsCatalogEntry>? settings = null,
		IEnumerable<ExtensionManifest>? extensions = null)
	{
		if (capabilities is null)
		{
			throw new ArgumentNullException(nameof(capabilities));
		}

		ExtensionManifest[] extensionValues = extensions?.ToArray() ?? Array.Empty<ExtensionManifest>();
		IReadOnlyDictionary<string, ExtensionManifest> extensionsById = extensionValues
			.GroupBy(static extension => extension.Id, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
		return CreateDefinitions(settings, extensionValues)
			.Select(definition => Evaluate(capabilities, definition, extensionsById, storage))
			.ToArray();
	}

	public static PortFeatureStatus Evaluate(IPlatformHost host, PortFeatureDefinition definition)
	{
		if (host is null)
		{
			throw new ArgumentNullException(nameof(host));
		}

		return Evaluate(host.Capabilities, definition, null, host.Paths.Storage);
	}

	public static PortFeatureStatus Evaluate(IPlatformCapabilities capabilities, PortFeatureDefinition definition)
	{
		return Evaluate(capabilities, definition, null, null);
	}

	private static PortFeatureStatus Evaluate(
		IPlatformCapabilities capabilities,
		PortFeatureDefinition definition,
		IReadOnlyDictionary<string, ExtensionManifest>? extensionsById,
		PlatformStoragePaths? storage)
	{
		if (capabilities is null)
		{
			throw new ArgumentNullException(nameof(capabilities));
		}

		if (definition is null)
		{
			throw new ArgumentNullException(nameof(definition));
		}

		if (definition.Kind == PortFeatureKind.Setting)
		{
			if (capabilities.Platform == PlatformId.Windows)
			{
				return new PortFeatureStatus(definition, PortFeatureState.NativeEquivalent, "The setting remains available through the Windows baseline");
			}

			if (definition.RequiredFeature is not null)
			{
				CapabilityDescriptor settingCapability = capabilities.Get(definition.RequiredFeature.Value);
				if (!settingCapability.IsAvailable)
				{
					return FromCapability(definition, settingCapability);
				}
			}

			return new PortFeatureStatus(definition, PortFeatureState.Experimental, "The setting is preserved in shared configuration while its runtime behavior is migrated");
		}

		if (definition.Kind == PortFeatureKind.Extension)
		{
			return EvaluateExtension(capabilities, definition, extensionsById, storage);
		}

		if (definition.Kind == PortFeatureKind.Dialog)
		{
			return EvaluateDialog(capabilities, definition);
		}

		if (definition.Kind == PortFeatureKind.Package)
		{
			return EvaluatePackage(capabilities, definition);
		}

		if (definition.Kind == PortFeatureKind.Page)
		{
			return EvaluatePage(capabilities, definition);
		}

		if (definition.Kind == PortFeatureKind.Action)
		{
			return EvaluateAction(capabilities, definition);
		}

		if (definition.Kind == PortFeatureKind.BrowserBridge)
		{
			return EvaluateBridge(capabilities, definition);
		}

		if (definition.RequiredFeature is null)
		{
			return new PortFeatureStatus(definition, PortFeatureState.Shared, "The shared core implementation is available");
		}

		return FromCapability(definition, capabilities.Get(definition.RequiredFeature.Value));
	}

	private static PortFeatureStatus EvaluateExtension(
		IPlatformCapabilities capabilities,
		PortFeatureDefinition definition,
		IReadOnlyDictionary<string, ExtensionManifest>? extensionsById,
		PlatformStoragePaths? storage)
	{
		string id = definition.Id.StartsWith("Extension.", StringComparison.Ordinal)
			? definition.Id.Substring("Extension.".Length)
			: definition.Id;
		if (extensionsById is null || !extensionsById.TryGetValue(id, out ExtensionManifest? extension))
		{
			return FromCapability(definition, capabilities.Get(FeatureId.ExtensionNativeAssets));
		}

		return FromCapability(definition, ExtensionCapabilityEvaluator.Evaluate(extension, capabilities, storage).Capability);
	}

	private static PortFeatureStatus EvaluatePage(IPlatformCapabilities capabilities, PortFeatureDefinition definition)
	{
		if (capabilities.Platform == PlatformId.Windows)
		{
			return new PortFeatureStatus(definition, PortFeatureState.NativeEquivalent, "The existing Windows page remains available during shared shell migration");
		}

		if (SharedPageIds.Contains(definition.Id))
		{
			return new PortFeatureStatus(definition, PortFeatureState.Shared, "The shared desktop page is available");
		}

		if (CatalogPageIds.Contains(definition.Id))
		{
			return new PortFeatureStatus(definition, PortFeatureState.Experimental, "The shared settings catalog is available while page specific behavior is migrated");
		}

		if (definition.RequiredFeature is not null)
		{
			CapabilityDescriptor capability = capabilities.Get(definition.RequiredFeature.Value);
			if (!capability.IsAvailable)
			{
				return FromCapability(definition, capability);
			}
		}

		return new PortFeatureStatus(definition, PortFeatureState.Unavailable, "This page has not been ported to the shared navigation surface");
	}

	private static PortFeatureStatus EvaluateAction(IPlatformCapabilities capabilities, PortFeatureDefinition definition)
	{
		if (string.Equals(definition.Id, "Action.MigrateSettings", StringComparison.Ordinal))
		{
			return new PortFeatureStatus(definition, PortFeatureState.Shared, "The portable settings store preserves and migrates shared configuration");
		}

		if (string.Equals(definition.Id, "Action.RefreshRuntime", StringComparison.Ordinal))
		{
			return new PortFeatureStatus(definition, PortFeatureState.Shared, "Runtime discovery runs through the platform provider");
		}

		if (capabilities.Platform == PlatformId.Windows && definition.Id is "Action.CheckForUpdates" or "Action.ApplyUpdate" or "Action.Install" or "Action.Repair" or "Action.Uninstall")
		{
			return new PortFeatureStatus(definition, PortFeatureState.NativeEquivalent, "The existing Windows installer and updater remain available during migration");
		}

		if (definition.Id is "Action.CheckForUpdates" or "Action.ApplyUpdate" or "Action.Install" or "Action.Repair" or "Action.Uninstall")
		{
			return new PortFeatureStatus(definition, PortFeatureState.Unavailable, "The platform installer and updater have not been ported to the shared host");
		}

		return definition.RequiredFeature is null
			? new PortFeatureStatus(definition, PortFeatureState.Shared, "The shared action is available")
			: FromCapability(definition, capabilities.Get(definition.RequiredFeature.Value));
	}

	private static PortFeatureStatus EvaluateDialog(IPlatformCapabilities capabilities, PortFeatureDefinition definition)
	{
		return capabilities.Platform == PlatformId.Windows
			? new PortFeatureStatus(definition, PortFeatureState.NativeEquivalent, "The existing Windows dialog remains available during migration")
			: new PortFeatureStatus(definition, PortFeatureState.Unavailable, "This dialog has not been ported to the shared desktop host");
	}

	private static PortFeatureStatus EvaluateBridge(IPlatformCapabilities capabilities, PortFeatureDefinition definition)
	{
		if (capabilities.Platform == PlatformId.Windows)
		{
			return new PortFeatureStatus(definition, PortFeatureState.NativeEquivalent, "The Windows browser bridge remains available in the WPF baseline");
		}

		if (string.Equals(definition.Id, "BrowserBridge.Matchmake", StringComparison.Ordinal))
		{
			return new PortFeatureStatus(definition, PortFeatureState.Unavailable, "The shared matchmaker bridge is not yet available on this platform");
		}

		CapabilityDescriptor browser = capabilities.Get(FeatureId.EmbeddedBrowser);
		if (!browser.IsAvailable)
		{
			return FromCapability(definition, browser);
		}

		return SharedBridgeIds.Contains(definition.Id)
			? new PortFeatureStatus(definition, PortFeatureState.Experimental, "The shared browser bridge action is available with an experimental native web view")
			: new PortFeatureStatus(definition, PortFeatureState.Unavailable, "This browser bridge action has not been ported to the shared host");
	}

	private static PortFeatureStatus EvaluatePackage(IPlatformCapabilities capabilities, PortFeatureDefinition definition)
	{
		bool matches = definition.Id switch
		{
			"Package.Windows" => capabilities.Platform == PlatformId.Windows,
			"Package.MacOS" => capabilities.Platform == PlatformId.MacOS,
			_ => capabilities.Platform == PlatformId.Linux
		};
		if (!matches)
		{
			return new PortFeatureStatus(definition, PortFeatureState.Unavailable, "This package is produced on its matching operating system");
		}

		return capabilities.Platform == PlatformId.Windows
			? new PortFeatureStatus(definition, PortFeatureState.NativeEquivalent, "The Windows release package is available")
			: new PortFeatureStatus(definition, PortFeatureState.Experimental, "The native package is assembled in platform CI and requires native release signing validation");
	}

	private static PortFeatureStatus FromCapability(PortFeatureDefinition definition, CapabilityDescriptor capability)
	{
		return capability.State switch
		{
			CapabilityState.Available => new PortFeatureStatus(definition, PortFeatureState.NativeEquivalent, capability.Reason, capability.RequiredAction),
			CapabilityState.RequiresPermission => new PortFeatureStatus(definition, PortFeatureState.PermissionRequired, capability.Reason, capability.RequiredAction),
			CapabilityState.RequiresExternalRuntime => new PortFeatureStatus(definition, PortFeatureState.ExternalRuntimeRequired, capability.Reason, capability.RequiredAction),
			CapabilityState.Experimental => new PortFeatureStatus(definition, PortFeatureState.Experimental, capability.Reason, capability.RequiredAction),
			_ => new PortFeatureStatus(definition, PortFeatureState.Unavailable, capability.Reason, capability.RequiredAction)
		};
	}

	private static PortFeatureDefinition CreateSettingDefinition(SettingsCatalogEntry entry)
	{
		string title = SettingsCatalogImporter.GetDisplayText(entry.Title);
		if (string.IsNullOrWhiteSpace(title))
		{
			title = entry.Aliases.FirstOrDefault() ?? "Setting";
		}

		return new PortFeatureDefinition(
			entry.Id,
			PortFeatureKind.Setting,
			title,
			entry.SourcePage,
			"Port." + entry.Id,
			GetPageFeature(entry.SourcePage));
	}

	public static FeatureId? GetPageFeature(string? sourcePage)
	{
		if (string.IsNullOrWhiteSpace(sourcePage))
		{
			return null;
		}

		return PageFeatures.TryGetValue(sourcePage, out FeatureId? feature) ? feature : null;
	}

	private static PortFeatureDefinition CreateExtensionDefinition(ExtensionManifest extension)
	{
		return new PortFeatureDefinition(
			"Extension." + extension.Id,
			PortFeatureKind.Extension,
			extension.DisplayName,
			"ExtensionManifests",
			"Port.Extension." + extension.Id,
			FeatureId.ExtensionNativeAssets);
	}

	private static PortFeatureDefinition Page(string id, string title, string source, FeatureId? feature)
	{
		return new PortFeatureDefinition("Page." + id, PortFeatureKind.Page, title, source, "Port.Page." + id, feature);
	}

	private static PortFeatureDefinition Action(string id, string title, FeatureId feature)
	{
		return new PortFeatureDefinition("Action." + id, PortFeatureKind.Action, title, "Desktop", "Port.Action." + id, feature);
	}

	private static PortFeatureDefinition Bridge(RobloxBrowserBridgeAction action, FeatureId feature)
	{
		return new PortFeatureDefinition("BrowserBridge." + action, PortFeatureKind.BrowserBridge, action.ToString(), "RobloxBrowserBridge", "Port.BrowserBridge." + action, feature);
	}

	private static PortFeatureDefinition Dialog(string id, string title, string source)
	{
		return new PortFeatureDefinition("Dialog." + id, PortFeatureKind.Dialog, title, source, "Port.Dialog." + id);
	}

	private static PortFeatureDefinition Native(FeatureId feature)
	{
		return new PortFeatureDefinition("Native." + feature, PortFeatureKind.NativeIntegration, feature.ToString(), "PlatformHost", "Port.Native." + feature, feature);
	}

	private static PortFeatureDefinition Package(string id, string title)
	{
		return new PortFeatureDefinition("Package." + id, PortFeatureKind.Package, title, "Packaging", "Port.Package." + id);
	}
}
