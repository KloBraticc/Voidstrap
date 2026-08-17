using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.Utility
{
    public sealed class LaunchProfileData
    {
        public string Name = "";
        public string AvatarUrl = "";
        public Brush? Border;
        public BorderRender? ImageBorder;
    }

    public static class LaunchProfile
    {
        private const int MaxProfileBytes = 1048576;

        private const int MaxAvatarBytes = 4000000;

        public static bool TryGetCached(out string name, out string avatarUrl)
        {
            name = "";
            avatarUrl = "";
            try
            {
                string? activeId = WebsiteAuth.GetActiveId();
                foreach (var acc in WebsiteAuth.GetAccounts())
                {
                    if (acc.Id == activeId || string.IsNullOrEmpty(activeId))
                    {
                        name = acc.Label;
                        avatarUrl = acc.Avatar;
                        break;
                    }
                }
            }
            catch
            {
            }
            return !string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(avatarUrl);
        }

        public static async Task<LaunchProfileData?> FetchAsync(double avatarSize = 34.0, double containerSize = 56.0)
        {
            try
            {
                string? token = WebsiteAuth.GetToken();
                if (string.IsNullOrEmpty(token))
                    return null;

                using var request = new HttpRequestMessage(HttpMethod.Get, App.WebsiteBaseUrl + "/api/me");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;

                byte[]? payload = await ReadBoundedAsync(response.Content, MaxProfileBytes).ConfigureAwait(false);
                if (payload == null)
                    return null;
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("user", out var user) || user.ValueKind == JsonValueKind.Null)
                    return null;

                var data = new LaunchProfileData();
                if (user.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String)
                    data.Name = dn.GetString() ?? "";
                if (string.IsNullOrEmpty(data.Name) && user.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String)
                    data.Name = un.GetString() ?? "";
                if (user.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String)
                    data.AvatarUrl = WebsiteUrl.Absolute(av.GetString());
                if (user.TryGetProperty("avatarBorder", out var ab) && ab.ValueKind == JsonValueKind.String)
                    data.Border = GradientProfileBorder.ParseBorder(ab.GetString());
                if (user.TryGetProperty("equippedBorder", out var eqb) && eqb.ValueKind == JsonValueKind.Object)
                    data.ImageBorder = WebsiteBorderRenderer.Build(eqb, avatarSize, containerSize);
                return data;
            }
            catch
            {
                return null;
            }
        }

        public static async Task<ImageSource?> LoadAvatarAsync(string url)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            try
            {
                byte[] bytes;
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = url.IndexOf(',');
                    if (comma < 0 || url.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) < 0)
                        return null;
                    string encoded = url.Substring(comma + 1);
                    if (encoded.Length == 0 || encoded.Length > 5500000)
                        return null;
                    bytes = Convert.FromBase64String(encoded);
                }
                else
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                        return null;
                    byte[]? downloaded = null;
                    foreach (string candidate in Voidstrap.Utility.AppImage.GetCandidates(url, 96))
                    {
                        downloaded = await Voidstrap.Utility.AppImage.DownloadBytesAsync(candidate).ConfigureAwait(false);
                        if (downloaded != null)
                            break;
                    }
                    if (downloaded == null)
                        return null;
                    bytes = downloaded;
                }

                if (bytes.Length == 0 || bytes.Length > MaxAvatarBytes)
                    return null;
                var bmp = Voidstrap.Utility.SafeImaging.FromBytes(bytes, 96);
                if (bmp == null)
                    return null;
                if (bmp.CanFreeze)
                    bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maxBytes)
        {
            if (content.Headers.ContentLength is long contentLength && (contentLength <= 0 || contentLength > maxBytes))
                return null;
            await using Stream input = await content.ReadAsStreamAsync().ConfigureAwait(false);
            using MemoryStream output = new MemoryStream(content.Headers.ContentLength is long length ? (int)length : 81920);
            byte[] buffer = new byte[81920];
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (read == 0)
                    return output.Length == 0 ? null : output.ToArray();
                if (output.Length + read > maxBytes)
                    return null;
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
        }
    }
}
