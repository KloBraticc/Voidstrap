using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class GameShortcut
{
    private const int IconSize = 256;

    private static readonly HttpClient HttpClient = VpnHttpClient.Create(TimeSpan.FromSeconds(20));

    public static async Task<bool> CreateOnDesktopAsync(string name, long placeId, long universeId)
    {
        const string LOG_IDENT = "GameShortcut::CreateOnDesktopAsync";
        string iconPath = await PrepareIconAsync(universeId).ConfigureAwait(false) ?? Paths.LaunchExecutable;
        string fileName = SafeFileName(name, placeId) + Shortcut.Suffix;
        string arguments = $"-player \"roblox://experiences/start?placeId={placeId}\"";
        bool created = Shortcut.Create(Paths.LaunchExecutable, arguments, Path.Combine(Paths.Desktop, fileName), iconPath, overwrite: true);
        App.Logger.WriteLine(LOG_IDENT, created ? "Created a shortcut for place " + placeId : "Could not create a shortcut for place " + placeId);
        return created;
    }

    private static string SafeFileName(string name, long placeId)
    {
        string cleaned = new string(name.Where(static c => !Path.GetInvalidFileNameChars().Contains(c) && !"<>:\"/\\|?*".Contains(c)).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(cleaned) ? "Roblox " + placeId : cleaned;
    }

    private static async Task<string?> PrepareIconAsync(long universeId)
    {
        const string LOG_IDENT = "GameShortcut::PrepareIconAsync";
        if (universeId <= 0)
            return null;
        try
        {
            string url = $"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&size={IconSize}x{IconSize}&format=Png&isCircular=false";
            using JsonDocument document = JsonDocument.Parse(await Http.GetStringBoundedAsync(HttpClient, url).ConfigureAwait(false));
            string? imageUrl = document.RootElement.GetProperty("data")[0].GetProperty("imageUrl").GetString();
            if (string.IsNullOrEmpty(imageUrl))
                return null;
            byte[] png = await HttpClient.GetByteArrayAsync(imageUrl).ConfigureAwait(false);
            string folder = Path.Combine(Paths.Library, "ShortcutIcons");
            Directory.CreateDirectory(folder);
            string pngPath = Path.Combine(folder, universeId + ".png");
            await File.WriteAllBytesAsync(pngPath, png).ConfigureAwait(false);
            if (!Platform.IsWindows)
                return pngPath;
            string icoPath = Path.Combine(folder, universeId + ".ico");
            WriteIco(icoPath, png);
            return icoPath;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
            return null;
        }
    }

    private static void WriteIco(string path, byte[] png)
    {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(png.Length);
        writer.Write(22);
        writer.Write(png);
    }
}
