using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class LinuxSteamIntegration
{
    private const string LOG_IDENT = "LinuxSteamIntegration";
    public const string RobloxShortcutName = "Roblox (Voidstrap)";
    public const string VoidstrapShortcutName = "Voidstrap";
    private static readonly TimeSpan SteamExitTimeout = TimeSpan.FromSeconds(30);

    public static bool IsSteamInstalled => Platform.IsLinux && LinuxSteamLibrary.FindSteamRoots().Count > 0;

    public static async Task<string> AddToSteamAsync(byte[]? iconPng, bool closeSteam, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roots = LinuxSteamLibrary.FindSteamRoots();
        if (roots.Count == 0)
            return "Steam was not found on this computer";

        bool steamWasRunning = LinuxSteamLibrary.IsSteamRunning();
        if (steamWasRunning)
        {
            if (!closeSteam)
                return "Steam has to be closed while Voidstrap updates your library";
            App.Logger?.WriteLine(LOG_IDENT, "Closing Steam so its library file can be updated");
            if (!await CloseSteamAsync(roots, cancellationToken).ConfigureAwait(false))
                return "Steam did not close in time, close it yourself and try again";
        }

        List<string> summaries = [];
        try
        {
            foreach (string root in roots)
            {
                IReadOnlyList<SteamShortcutRequest> requests = BuildRequests(root, iconPng);
                SteamShortcutResult result = LinuxSteamLibrary.AddOrUpdate(root, requests);
                App.Logger?.WriteLine(LOG_IDENT, root + ": " + result.Message);
                if (result.Success)
                    summaries.Add(result.Message);
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteException(LOG_IDENT, ex);
            summaries.Clear();
            summaries.Add("Voidstrap could not update the Steam library: " + ex.Message);
        }
        finally
        {
            if (steamWasRunning)
                StartSteam(roots[0]);
        }

        if (summaries.Count == 0)
            return "No signed in Steam account was found, open Steam and sign in once first";
        return RobloxShortcutName + " and " + VoidstrapShortcutName + " are in your Steam library. " + string.Join(". ", summaries);
    }

    private static IReadOnlyList<SteamShortcutRequest> BuildRequests(string steamRoot, byte[]? iconPng)
    {
        string executable;
        string startDirectory;
        string prefix;
        string flatpakAppId = string.Empty;
        if (LinuxFlatpakHost.IsSandboxed)
        {
            executable = Quote("/usr/bin/flatpak");
            startDirectory = Quote("/usr/bin");
            flatpakAppId = LinuxFlatpakHost.CurrentApplicationId;
            prefix = "run " + flatpakAppId;
        }
        else
        {
            string path = LinuxDesktopEntry.ResolveLaunchPath(Paths.Application);
            if (LinuxSteamLibrary.IsFlatpakSteam(steamRoot))
            {
                executable = Quote("/usr/bin/flatpak-spawn");
                startDirectory = Quote(Path.GetDirectoryName(path) ?? "/");
                prefix = "--host --directory=/ " + Quote(path);
            }
            else
            {
                executable = Quote(path);
                startDirectory = Quote(Path.GetDirectoryName(path) ?? "/");
                prefix = string.Empty;
            }
        }

        return
        [
            new SteamShortcutRequest(RobloxShortcutName, executable, startDirectory, Join(prefix, "-player"), flatpakAppId, BuildArtwork(iconPng, true)),
            new SteamShortcutRequest(VoidstrapShortcutName, executable, startDirectory, Join(prefix, string.Empty), flatpakAppId, BuildArtwork(iconPng, false))
        ];
    }

    private static IReadOnlyDictionary<string, byte[]> BuildArtwork(byte[]? iconPng, bool playBadge)
    {
        Dictionary<string, byte[]> art = [];
        if (iconPng is null || iconPng.Length == 0)
            return art;
        try
        {
            Raster icon = Raster.Decode(iconPng);
            art["p.png"] = Render(icon, 600, 900, 360, 0.42, playBadge, true);
            art[".png"] = Render(icon, 920, 430, 280, 0.5, playBadge, true);
            art["_hero.png"] = Render(icon, 1920, 620, 0, 0.5, false, true);
            art["_logo.png"] = Render(icon, 640, 360, 300, 0.5, playBadge, false);
            art["_icon.png"] = Render(icon, 256, 256, 256, 0.5, playBadge, false);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The Steam artwork could not be drawn: " + ex.Message);
        }
        return art;
    }

    private static byte[] Render(Raster icon, int width, int height, int iconSize, double verticalCenter, bool playBadge, bool background)
    {
        Raster canvas = Raster.Blank(width, height);
        if (background)
        {
            byte[] pixels = canvas.Pixels;
            for (int y = 0; y < height; y++)
            {
                double fade = (double)y / Math.Max(1, height - 1);
                for (int x = 0; x < width; x++)
                {
                    double dx = (x - width * 0.5) / width;
                    double dy = (y - height * verticalCenter) / height;
                    double glow = Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy) * 1.8);
                    int offset = (y * width + x) * 4;
                    pixels[offset] = (byte)Math.Clamp(32 - fade * 12 + glow * 70, 0, 255);
                    pixels[offset + 1] = (byte)Math.Clamp(24 - fade * 10 + glow * 30, 0, 255);
                    pixels[offset + 2] = (byte)Math.Clamp(22 - fade * 10 + glow * 40, 0, 255);
                    pixels[offset + 3] = 255;
                }
            }
        }

        if (iconSize > 0)
        {
            Raster scaled = icon.Resize(iconSize, iconSize);
            int left = (width - iconSize) / 2;
            int top = (int)Math.Round(height * verticalCenter - iconSize / 2.0);
            canvas.DrawOver(scaled, left, Math.Max(0, top));
            if (playBadge)
                DrawPlayBadge(canvas, left + iconSize - iconSize / 4, Math.Max(0, top) + iconSize - iconSize / 4, Math.Max(18, iconSize / 5));
        }

        return canvas.EncodePng();
    }

    private static void DrawPlayBadge(Raster canvas, int centerX, int centerY, int radius)
    {
        byte[] pixels = canvas.Pixels;
        for (int y = Math.Max(0, centerY - radius); y < Math.Min(canvas.Height, centerY + radius); y++)
        {
            for (int x = Math.Max(0, centerX - radius); x < Math.Min(canvas.Width, centerX + radius); x++)
            {
                double dx = x - centerX;
                double dy = y - centerY;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > radius)
                    continue;
                double edge = Math.Clamp(radius - distance, 0, 1);
                bool inTriangle = dx > -radius * 0.28 && dx < radius * 0.42 && Math.Abs(dy) < (radius * 0.42 - dx) * 0.72;
                int fillRed = inTriangle ? 255 : 0;
                int fillGreen = inTriangle ? 255 : 176;
                int fillBlue = inTriangle ? 255 : 111;
                int offset = (y * canvas.Width + x) * 4;
                pixels[offset] = (byte)(pixels[offset] + (fillBlue - pixels[offset]) * edge);
                pixels[offset + 1] = (byte)(pixels[offset + 1] + (fillGreen - pixels[offset + 1]) * edge);
                pixels[offset + 2] = (byte)(pixels[offset + 2] + (fillRed - pixels[offset + 2]) * edge);
                pixels[offset + 3] = (byte)Math.Max(pixels[offset + 3], 255 * edge);
            }
        }
    }

    private static async Task<bool> CloseSteamAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        bool flatpak = LinuxSteamLibrary.IsFlatpakSteam(roots[0]);
        RunDetached(flatpak ? "flatpak" : "steam", flatpak ? ["run", "com.valvesoftware.Steam", "-shutdown"] : ["-shutdown"]);
        DateTime deadline = DateTime.UtcNow + SteamExitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            if (!LinuxSteamLibrary.IsSteamRunning())
            {
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
        return false;
    }

    private static void StartSteam(string root)
    {
        bool flatpak = LinuxSteamLibrary.IsFlatpakSteam(root);
        App.Logger?.WriteLine(LOG_IDENT, "Opening Steam again");
        RunDetached(flatpak ? "flatpak" : "steam", flatpak ? ["run", "com.valvesoftware.Steam"] : []);
    }

    private static void RunDetached(string command, IReadOnlyList<string> arguments)
    {
        try
        {
            bool sandboxed = LinuxFlatpakHost.IsSandboxed;
            ProcessStartInfo info = new(sandboxed ? "flatpak-spawn" : "setsid")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (sandboxed)
            {
                info.ArgumentList.Add("--host");
                info.ArgumentList.Add("--directory=/");
            }
            else
                info.ArgumentList.Add("-f");
            info.ArgumentList.Add(command);
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);
            using Process? process = Process.Start(info);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Could not run " + command + ": " + ex.Message);
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value + "\"";
    }

    private static string Join(string prefix, string argument)
    {
        if (prefix.Length == 0)
            return argument;
        return argument.Length == 0 ? prefix : prefix + " " + argument;
    }
}
