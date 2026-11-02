using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Voidstrap.Utility;

internal sealed record SkyboxPreviewFaces(BitmapSource[] Faces);

internal sealed record SkyboxPreviewResult(SkyboxPreviewFaces? Faces, string? Message);

internal static class SkyboxPreviewLoader
{
    private const string LogIdent = "SkyboxPreviewLoader";

    private const int PreviewTextureLimit = 4096;

    private static readonly HttpClient RequestClient = new() { Timeout = TimeSpan.FromSeconds(30.0) };

    public static async Task<SkyboxPreviewResult> LoadAsync(string packName, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(packName) || packName.Length > 128 || packName == "." || packName == ".." || packName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return new SkyboxPreviewResult(null, Strings.Ui_Mods_SkyboxPreviewFailed);
        bool custom = packName.Equals(SkyboxImageConverter.CustomPackName, StringComparison.OrdinalIgnoreCase);
        string folder = custom ? SkyboxImageConverter.CustomPackDirectory : Path.Combine(Paths.SkyboxPack, packName);
        if (SkyboxImageConverter.IsValidPackDirectory(folder))
            return await ReadFacesAsync(folder, token).ConfigureAwait(false);
        if (custom)
            return new SkyboxPreviewResult(null, Strings.Ui_Mods_SkyboxPreviewFailed);
        if (!await DownloadPackAsync(packName, folder, token).ConfigureAwait(false))
            return new SkyboxPreviewResult(null, Strings.Ui_Mods_SkyboxPreviewFailed);
        if (!SkyboxImageConverter.IsValidPackDirectory(folder))
            return new SkyboxPreviewResult(null, Strings.Ui_Mods_SkyboxPreviewFailed);
        return await ReadFacesAsync(folder, token).ConfigureAwait(false);
    }

    private static async Task<SkyboxPreviewResult> ReadFacesAsync(string folder, CancellationToken token)
    {
        BitmapSource[] faces = new BitmapSource[App.SkyboxFileNames.Length];
        for (int i = 0; i < App.SkyboxFileNames.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(folder, App.SkyboxFileNames[i]), token).ConfigureAwait(false);
            BitmapSource? face = TryDecodeFace(bytes, App.SkyboxFileNames[i]);
            if (face == null)
                return new SkyboxPreviewResult(null, Strings.Ui_Mods_SkyboxPreviewFailed);
            faces[i] = face;
        }
        return new SkyboxPreviewResult(new SkyboxPreviewFaces(faces), null);
    }

    private static BitmapSource? TryDecodeFace(byte[] bytes, string name)
    {
        try
        {
            int width;
            int height;
            byte[] bgra;
            if (
                bytes.Length > 4
                && bytes[0] == (byte)'D'
                && bytes[1] == (byte)'D'
                && bytes[2] == (byte)'S'
                && bytes[3] == (byte)' '
            )
            {
                if (!DdsDecoder.TryDecode(bytes, out width, out height, out bgra))
                {
                    App.Logger?.WriteLine(LogIdent, "The " + name + " skybox face uses a DDS format the preview cannot decode");
                    return null;
                }
            }
            else
            {
                Raster raster = Raster.Decode(bytes, 805306368);
                width = raster.Width;
                height = raster.Height;
                bgra = raster.Pixels;
            }
            if (width > PreviewTextureLimit || height > PreviewTextureLimit)
            {
                Raster resized = new Raster(width, height, bgra).ResizeToCover(PreviewTextureLimit, PreviewTextureLimit, RasterFilter.Bilinear);
                width = resized.Width;
                height = resized.Height;
                bgra = resized.Pixels;
            }
            BitmapSource source = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, width * 4);
            source.Freeze();
            return source;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The " + name + " skybox face could not be decoded for the preview: " + ex.Message);
            return null;
        }
    }

    private static async Task<bool> DownloadPackAsync(string packName, string folder, CancellationToken token)
    {
        Directory.CreateDirectory(Paths.SkyboxPack);
        string prefix = App.SkyboxRawBaseUrl + "main/" + Uri.EscapeDataString(packName) + "/";
        Directory.CreateDirectory(folder);
        try
        {
            foreach (string name in App.SkyboxFileNames)
            {
                token.ThrowIfCancellationRequested();
                byte[]? bytes = await TryReadBoundedAsync(prefix + Uri.EscapeDataString(name), name, App.SkyboxMaxFaceBytes, token).ConfigureAwait(false);
                if (bytes == null)
                {
                    TryDelete(folder);
                    return false;
                }
                await File.WriteAllBytesAsync(Path.Combine(folder, name), bytes, token).ConfigureAwait(false);
            }
            App.Logger?.WriteLine(LogIdent, "Downloaded the " + packName + " skybox pack for the preview");
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(folder);
            throw;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The " + packName + " skybox pack could not be downloaded for the preview: " + ex.Message);
            TryDelete(folder);
            return false;
        }
    }

    private static async Task<byte[]?> TryReadBoundedAsync(string url, string name, long maximum, CancellationToken token)
    {
        using HttpResponseMessage response = await RequestClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            App.Logger?.WriteLine(LogIdent, "The " + name + " skybox face could not be downloaded, status " + (int)response.StatusCode);
            return null;
        }
        if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > maximum))
        {
            App.Logger?.WriteLine(LogIdent, "The " + name + " skybox face download has an invalid size");
            return null;
        }
        return await Voidstrap.Utility.Http.ReadBytesBoundedAsync(response.Content, (int)maximum, token).ConfigureAwait(false);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, true);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "A partial skybox preview download could not be cleaned up: " + ex.Message);
        }
    }
}
