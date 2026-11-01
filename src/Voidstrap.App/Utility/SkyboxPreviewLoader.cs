using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Voidstrap.Utility;

internal sealed record SkyboxPreviewFaces(BitmapSource[] Faces);

internal static class SkyboxPreviewLoader
{
    private const string LogIdent = "SkyboxPreviewLoader";

    private const int PreviewTextureLimit = 4096;

    private static readonly HttpClient RequestClient = new() { Timeout = TimeSpan.FromSeconds(30.0) };

    public static async Task<SkyboxPreviewFaces> LoadAsync(string packName, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(packName))
            throw new InvalidOperationException("No skybox is selected yet.");
        if (packName.Length > 128 || packName == "." || packName == ".." || packName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new InvalidOperationException("The selected skybox name is invalid.");
        string folder =
            packName.Equals(SkyboxImageConverter.CustomPackName, StringComparison.OrdinalIgnoreCase)
                ? SkyboxImageConverter.CustomPackDirectory
                : Path.Combine(Paths.SkyboxPack, packName);
        if (SkyboxImageConverter.IsValidPackDirectory(folder))
            return await ReadFacesAsync(folder, token).ConfigureAwait(false);
        if (packName.Equals(SkyboxImageConverter.CustomPackName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The custom skybox is missing some of its faces.");
        await DownloadPackAsync(packName, folder, token).ConfigureAwait(false);
        if (!SkyboxImageConverter.IsValidPackDirectory(folder))
            throw new InvalidOperationException("The downloaded skybox could not be completed.");
        return await ReadFacesAsync(folder, token).ConfigureAwait(false);
    }

    private static async Task<SkyboxPreviewFaces> ReadFacesAsync(string folder, CancellationToken token)
    {
        BitmapSource[] faces = new BitmapSource[App.SkyboxFileNames.Length];
        for (int i = 0; i < App.SkyboxFileNames.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            faces[i] = DecodeFace(await File.ReadAllBytesAsync(Path.Combine(folder, App.SkyboxFileNames[i]), token).ConfigureAwait(false));
        }
        return new SkyboxPreviewFaces(faces);
    }

    private static BitmapSource DecodeFace(byte[] bytes)
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
                throw new InvalidOperationException("The skybox face uses an image format the preview cannot decode.");
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

    private static async Task DownloadPackAsync(string packName, string folder, CancellationToken token)
    {
        string commit = await ReadLatestCommitAsync(token).ConfigureAwait(false);
        Directory.CreateDirectory(Paths.SkyboxPack);
        string prefix = App.SkyboxRawBaseUrl + commit + "/" + Uri.EscapeDataString(packName) + "/";
        Directory.CreateDirectory(folder);
        try
        {
            foreach (string name in App.SkyboxFileNames)
            {
                token.ThrowIfCancellationRequested();
                byte[] bytes = await ReadBoundedAsync(prefix + Uri.EscapeDataString(name), App.SkyboxMaxFaceBytes, token).ConfigureAwait(false);
                if (bytes.Length <= 0)
                    throw new InvalidOperationException("The skybox download returned an empty face.");
                await File.WriteAllBytesAsync(Path.Combine(folder, name), bytes, token).ConfigureAwait(false);
            }
            await File.WriteAllTextAsync(Path.Combine(folder, App.SkyboxPackCommitFile), commit, token).ConfigureAwait(false);
            App.Logger.WriteLine(LogIdent, "Downloaded the " + packName + " skybox pack for the preview");
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    private static async Task<string> ReadLatestCommitAsync(CancellationToken token)
    {
        using HttpResponseMessage response = await RequestClient.GetAsync(App.SkyboxCommitApiUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[] data = await Voidstrap.Utility.Http.ReadBytesBoundedAsync(response.Content, 262144, token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(data);
        return document.RootElement.TryGetProperty("sha", out JsonElement sha) ? sha.GetString() ?? "" : "";
    }

    private static async Task<byte[]> ReadBoundedAsync(string url, long maximum, CancellationToken token)
    {
        using HttpResponseMessage response = await RequestClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > maximum))
            throw new InvalidOperationException("The skybox face download has an invalid size.");
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
