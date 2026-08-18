using System.Collections.Generic;
using Voidstrap.Models.Persistable;

namespace Voidstrap.AppData;

public class RobloxPlayerData : CommonAppData, IAppData
{
	public string ProductName => "Roblox";

	public override string BinaryType => "WindowsPlayer";

	public string RegistryName => "RobloxPlayer";

	public override string ExecutableName
	{
		get
		{
			if (!App.Settings.Prop.RenameClientToEuroTrucks2)
			{
				return "RobloxPlayerBeta.exe";
			}
			return "eurotrucks2.exe";
		}
	}

	public override string VersionsRoot => string.IsNullOrWhiteSpace(App.Settings.Prop.PlayerInstallLocation) ? Paths.Versions : App.Settings.Prop.PlayerInstallLocation;

	public override AppState State => App.State.Prop.Player;

	public override IReadOnlyDictionary<string, string> PackageDirectoryMap { get; set; } = new Dictionary<string, string> { { "RobloxApp.zip", "" } };

	public override IReadOnlyList<string> CandidateCriticalFiles => ["RobloxPlayerBeta.dll"];
}
