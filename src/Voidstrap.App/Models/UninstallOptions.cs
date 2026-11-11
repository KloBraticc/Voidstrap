namespace Voidstrap.Models;

public sealed record UninstallOptions(bool KeepData, bool KeepVersions, bool KeepLogs)
{
    public static UninstallOptions Default { get; } = new(true, false, false);
}
