namespace Voidstrap.Integrations.Plugins;

internal static class ModCrashGuard
{
    private const string LogIdent = "ModCrashGuard";

    public static bool TryContain(Exception ex)
    {
        string? mod = FindMod(ex);
        if (mod == null)
            return false;
        App.Logger.WriteLine(LogIdent, mod + ": contained an error that would have crashed Voidstrap");
        App.Logger.WriteException(LogIdent, ex);
        PluginHost.Problem(mod, "caused an error that would have crashed Voidstrap: " + ex.Message);
        return true;
    }

    private static string? FindMod(Exception ex)
    {
        string? running = PluginHost.ModRunningOnThisThread();
        if (running != null)
            return running;
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is Jint.Runtime.JavaScriptException js)
            {
                string? mod = ModFromPath(js.Location.SourceFile);
                if (mod != null)
                    return mod;
            }
            if (current.StackTrace?.Contains("Voidstrap.Integrations.Plugins.", StringComparison.Ordinal) == true)
                return "Voidstrap mods";
        }
        return null;
    }

    private static string? ModFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(Paths.Plugins))
            return null;
        try
        {
            string relative = Path.GetRelativePath(Paths.Plugins, Path.GetFullPath(path));
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return null;
            string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return first.Length > 0 ? first : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
