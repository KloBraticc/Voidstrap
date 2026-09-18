using Voidstrap.Models.Persistable;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class VinegarConfigurationMapper
{
	public static LinuxStudioPreparationOptions CreateStudioOptions(AppSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		bool modsAllowed = settings.ModApplyTarget is Voidstrap.Enums.ModApplyTarget.Both or Voidstrap.Enums.ModApplyTarget.Studio;
		return new LinuxStudioPreparationOptions(
			settings.VinegarApplyFastFlags && settings.UseFastFlagManager,
			ApplyVirtualMachineProfile(CreateNativeOptions(settings)),
			modsAllowed,
			modsAllowed ? CollectManagedModSources() : null);
	}

	private static List<LinuxModSource> CollectManagedModSources()
	{
		List<LinuxModSource> sources = [];
		try
		{
			ManagedModScanResult scan = ManagedModStore.ScanEnabledFiles();
			foreach (ManagedModFile file in scan.Files)
			{
				string relative = file.Relative.Replace('\\', '/');
				if (relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
					continue;
				sources.Add(new LinuxModSource(relative, file.Source));
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("VinegarConfigurationMapper::CollectManagedModSources", "Managed mods could not be indexed: " + ex.Message);
		}

		return sources;
	}

	private static VinegarNativeConfigurationOptions? ApplyVirtualMachineProfile(VinegarNativeConfigurationOptions? options)
	{
		if (!Voidstrap.Utility.VirtualMachineProfile.ShouldForceSafeGraphics)
		{
			return options;
		}

		VinegarNativeConfigurationOptions current = options ?? new VinegarNativeConfigurationOptions();
		return current with
		{
			Renderer = Voidstrap.Platform.Linux.VinegarRenderer.D3D11
		};
	}

	public static VinegarNativeConfigurationOptions? CreateNativeOptions(AppSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		if (!HasNativeOverrides(settings))
		{
			return null;
		}

		return new VinegarNativeConfigurationOptions(
			Renderer: settings.VinegarRenderer,
			EnableGameMode: settings.VinegarEnableGameMode,
			DiscordRpcEnabled: settings.VinegarDiscordRpcEnabled,
			Gpu: settings.VinegarGpu,
			VirtualDesktop: settings.VinegarVirtualDesktop,
			Launcher: settings.VinegarLauncher,
			ForcedVersion: settings.VinegarForcedVersion,
			Channel: settings.VinegarChannel,
			WineRoot: settings.VinegarWineRoot);
	}

	public static bool HasNativeOverrides(AppSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		return settings.VinegarRenderer is not null
			|| settings.VinegarEnableGameMode is not null
			|| settings.VinegarDiscordRpcEnabled is not null
			|| !string.IsNullOrWhiteSpace(settings.VinegarGpu)
			|| !string.IsNullOrWhiteSpace(settings.VinegarVirtualDesktop)
			|| !string.IsNullOrWhiteSpace(settings.VinegarLauncher)
			|| !string.IsNullOrWhiteSpace(settings.VinegarForcedVersion)
			|| !string.IsNullOrWhiteSpace(settings.VinegarChannel)
			|| !string.IsNullOrWhiteSpace(settings.VinegarWineRoot);
	}
}
