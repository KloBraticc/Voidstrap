namespace Voidstrap.Enums;

[Flags]
public enum ModInstallTargets
{
    None = 0,
    Roblox = 1,
    Studio = 2,
    Classic = 4,
    All = Roblox | Studio | Classic,
}
