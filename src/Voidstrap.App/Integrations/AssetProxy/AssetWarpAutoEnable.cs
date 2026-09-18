using System;
using System.Collections.Generic;
using Voidstrap.UI.ViewModels.Settings;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.AssetProxy;

internal static class AssetWarpAutoEnable
{
	private const string LogIdent = "AssetWarpAutoEnable";

	private const string Reason = "An enabled mod replaces Roblox assets that are downloaded instead of read from the client folder, so it only works through AssetWarp.";

	public static bool ModsNeedAssetWarp()
	{
		if (!Voidstrap.Utility.Platform.IsWindows && !Voidstrap.Utility.Platform.IsLinux)
		{
			return false;
		}
		try
		{
			foreach (ManagedModRecord record in ManagedModStore.Load())
			{
				if (record.Enabled && ExternalModConfigs.HasActiveAssetWarpConfig(record.Id))
				{
					return true;
				}
			}
			if (Voidstrap.Utility.Platform.IsWindows)
			{
				IReadOnlyList<string> folders = [.. ManagedModStore.EnabledFoldersByPriority(), Paths.Mods];
				bool playerMods = App.Settings.Prop.ModApplyTarget != Voidstrap.Enums.ModApplyTarget.Studio;
				if (playerMods && LegacyMaterialTextures.HasSources(folders))
				{
					return true;
				}
				LegacyMaterialTextures.RemoveGenerated();
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The enabled mods could not be checked for AssetWarp content: " + ex.Message);
		}
		return false;
	}

	public static bool IsActiveForMods()
	{
		return App.Settings.Prop.AssetWarpEnabled && App.Settings.Prop.AssetWarpCertificateApproved && ModsNeedAssetWarp();
	}

	public static bool EnsureEnabled(bool allowPrompt, bool userAction)
	{
		if (App.Settings.Prop.AssetWarpEnabled)
		{
			return true;
		}
		if (!userAction && App.Settings.Prop.AssetWarpAutoEnableDeclined)
		{
			return false;
		}
		if (!ModsNeedAssetWarp())
		{
			return false;
		}
		if (!allowPrompt)
		{
			App.Logger?.WriteLine(LogIdent, "An enabled mod needs AssetWarp, open Voidstrap to turn it on");
			return false;
		}
		if (!FastFlagsViewModel.TryEnableAssetWarp(Reason))
		{
			App.Logger?.WriteLine(LogIdent, "AssetWarp stayed off, mods that replace downloaded Roblox assets will not show");
			return false;
		}
		App.Logger?.WriteLine(LogIdent, "Turned on AssetWarp because an enabled mod needs it");
		return true;
	}
}
