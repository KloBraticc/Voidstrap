using System.Collections.Generic;
using System.IO;
using Voidstrap.Models.Persistable;

namespace Voidstrap.AppData;

public class RobloxPlayerData : CommonAppData, IAppData
{
    public bool UseVng { get; }

    public RobloxPlayerData(bool? useVng = null)
    {
        UseVng = Voidstrap.Utility.Platform.IsWindows && (useVng ?? App.Settings.Prop.UseVng);
    }

    public string ProductName => UseVng ? "Roblox VNG" : "Roblox";

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

    public override string VersionsRoot => UseVng
        ? (string.IsNullOrWhiteSpace(App.Settings.Prop.VngInstallLocation) ? Path.Combine(Paths.Versions, "VNG") : App.Settings.Prop.VngInstallLocation)
        : (string.IsNullOrWhiteSpace(App.Settings.Prop.PlayerInstallLocation) ? Paths.Versions : App.Settings.Prop.PlayerInstallLocation);

    public override AppState State => UseVng ? App.State.Prop.VngPlayer : App.State.Prop.Player;

    public override IReadOnlyDictionary<string, string> PackageDirectoryMap { get; set; } = new Dictionary<string, string> { { "RobloxApp.zip", "" } };

    public override IReadOnlyList<string> CandidateCriticalFiles => ["RobloxPlayerBeta.dll"];
}
