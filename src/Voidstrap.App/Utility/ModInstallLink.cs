using System.Text.RegularExpressions;

namespace Voidstrap.Utility;

internal static partial class ModInstallLink
{
    public const string Scheme = "voidstrap";

    private static string PendingPath => Path.Combine(Path.GetTempPath(), "VoidstrapPendingModInstall");

    [GeneratedRegex(@"^\d{1,20}/[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ModIdPattern();

    public static bool TryParse(string? argument, out string modId)
    {
        modId = "";
        if (string.IsNullOrWhiteSpace(argument) || !Uri.TryCreate(argument.Trim(), UriKind.Absolute, out Uri? uri))
            return false;
        if (!uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase) || !uri.Host.Equals("mods", StringComparison.OrdinalIgnoreCase))
            return false;
        string path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        const string prefix = "install/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        string id = path.Substring(prefix.Length);
        if (!ModIdPattern().IsMatch(id))
            return false;
        modId = id;
        return true;
    }

    public static void SetPending(string modId)
    {
        const string LOG_IDENT = "ModInstallLink::SetPending";
        try
        {
            File.WriteAllText(PendingPath, modId);
            App.Logger.WriteLine(LOG_IDENT, "Queued the website install of " + modId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Logger.WriteLine(LOG_IDENT, "The website install could not be queued: " + ex.Message);
        }
    }

    public static string? TakePending()
    {
        try
        {
            if (!File.Exists(PendingPath))
                return null;
            string id = File.ReadAllText(PendingPath).Trim();
            File.Delete(PendingPath);
            return ModIdPattern().IsMatch(id) ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool HasPending => File.Exists(PendingPath);
}
