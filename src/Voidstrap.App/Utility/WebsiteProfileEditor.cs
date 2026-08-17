using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.Utility
{
    public sealed class WebsiteProfileLink
    {
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
    }

    public sealed class WebsiteProfileData
    {
        public string Id { get; set; } = "";
        public string RobloxId { get; set; } = "";
        public string Email { get; set; } = "";
        public bool HasRoblox { get; set; }
        public bool HasEmail { get; set; }
        public bool HasGoogle { get; set; }
        public bool HasDiscord { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string About { get; set; } = "";
        public string Status { get; set; } = "";
        public string GradientKey { get; set; } = "none";
        public string GradientCss { get; set; } = "";
        public string AvatarBorder { get; set; } = "";
        public string Banner { get; set; } = "";
        public string Avatar { get; set; } = "";
        public List<WebsiteProfileLink> Links { get; set; } = new();
    }

    internal enum PostOutcome
    {
        Ok,
        Transient,
        Permanent
    }

    public static class WebsiteProfileEditor
    {
        private static readonly string MeUrl = App.WebsiteBaseUrl + "/api/me";
        private static readonly string SaveUrl = App.WebsiteBaseUrl + "/api/me/profile";

        public static readonly IReadOnlyDictionary<string, string> Gradients = new Dictionary<string, string>
        {
            ["none"] = "",
            ["violet"] = "linear-gradient(135deg,#7c3aed,#4f46e5)",
            ["sunset"] = "linear-gradient(135deg,#f59e0b,#ef4444)",
            ["ocean"] = "linear-gradient(135deg,#0ea5e9,#22d3ee)",
            ["forest"] = "linear-gradient(135deg,#10b981,#059669)",
            ["rose"] = "linear-gradient(135deg,#ec4899,#f43f5e)",
            ["mono"] = "linear-gradient(135deg,#3f3f46,#18181b)",
        };

        private static readonly Regex GradientCssRe = new(@"^linear-gradient\(\d{1,3}deg,#[0-9a-fA-F]{6},#[0-9a-fA-F]{6}\)$", RegexOptions.Compiled);
        private static readonly Regex HexRe = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
        private static readonly Regex UrlRe = new(@"^https://[^\s]+$", RegexOptions.Compiled);

        private const int MaxAbout = 1000;
        private const int MaxStatus = 80;
        private const int MaxLabel = 40;
        private const int MaxLinks = 5;
        private const int MaxBannerBytes = 1_000_000;
        private const int MaxBannerSourceBytes = 50 * 1024 * 1024;
        private const long MaxBannerPixels = 40_000_000;
        private const int MaxProfileResponseBytes = 4 * 1024 * 1024;
        private const int MaxErrorResponseBytes = 256 * 1024;

        public static bool IsSignedIn() => WebsiteAuth.IsSignedIn();

        public static async Task<WebsiteProfileData?> LoadAsync()
        {
            try
            {
                string? token = WebsiteAuth.GetToken();
                if (string.IsNullOrEmpty(token)) return null;

                using var req = new HttpRequestMessage(HttpMethod.Get, MeUrl);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var resp = await App.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await Http.ReadStringBoundedAsync(resp.Content, MaxProfileResponseBytes, timeout.Token).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("user", out var u) || u.ValueKind != JsonValueKind.Object)
                    return null;

                var d = new WebsiteProfileData
                {
                    Id = Str(u, "id"),
                    RobloxId = Str(u, "robloxId"),
                    Email = Str(u, "email"),
                    Username = Str(u, "username"),
                    DisplayName = Str(u, "displayName"),
                    About = Str(u, "about"),
                    Status = Str(u, "status"),
                    GradientKey = string.IsNullOrEmpty(Str(u, "gradientKey")) ? "none" : Str(u, "gradientKey"),
                    GradientCss = Str(u, "gradient"),
                    AvatarBorder = Str(u, "avatarBorder"),
                    Banner = Str(u, "banner"),
                    Avatar = WebsiteUrl.Absolute(Str(u, "avatar")),
                };

                if (u.TryGetProperty("methods", out var methods) && methods.ValueKind == JsonValueKind.Object)
                {
                    d.HasRoblox = Flag(methods, "roblox");
                    d.HasEmail = Flag(methods, "email");
                    d.HasGoogle = Flag(methods, "google");
                    d.HasDiscord = Flag(methods, "discord");
                }
                if (string.IsNullOrEmpty(d.RobloxId) && d.HasRoblox)
                    d.RobloxId = d.Id;

                if (u.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
                {
                    foreach (var l in links.EnumerateArray())
                    {
                        if (l.ValueKind != JsonValueKind.Object) continue;
                        d.Links.Add(new WebsiteProfileLink { Label = Str(l, "label"), Url = Str(l, "url") });
                    }
                }
                return d;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WebsiteProfileEditor::Load", ex);
                return null;
            }
        }

        public static async Task<(bool Ok, bool Queued, string? Message)> SaveAsync(WebsiteProfileData d)
        {
            try
            {
                string? token = WebsiteAuth.GetToken();
                if (string.IsNullOrEmpty(token)) return (false, false, "You are not signed in.");
                if (d is null) return (false, false, "No data to save.");

                var payload = new Dictionary<string, object?>
                {
                    ["about"] = (d.About ?? "").Length > MaxAbout ? d.About!.Substring(0, MaxAbout) : (d.About ?? ""),
                    ["status"] = (d.Status ?? "").Length > MaxStatus ? d.Status!.Substring(0, MaxStatus) : (d.Status ?? ""),
                };

                string key = d.GradientKey ?? "none";
                if (key == "custom" && !string.IsNullOrEmpty(d.GradientCss) && GradientCssRe.IsMatch(d.GradientCss.Trim()))
                {
                    payload["gradient"] = "custom";
                    payload["gradientCss"] = d.GradientCss.Trim();
                }
                else
                {
                    payload["gradient"] = Gradients.ContainsKey(key) ? key : "none";
                }

                string ab = (d.AvatarBorder ?? "").Trim();
                if (ab == "" || HexRe.IsMatch(ab) || GradientCssRe.IsMatch(ab))
                    payload["avatarBorder"] = ab;

                var outLinks = new List<object>();
                foreach (var l in d.Links ?? new())
                {
                    if (outLinks.Count >= MaxLinks) break;
                    string url = NormalizeUrl(l?.Url ?? "");
                    string label = (l?.Label ?? "").Trim();
                    if (label.Length > MaxLabel) label = label.Substring(0, MaxLabel);
                    if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(url)) continue;
                    outLinks.Add(new { label, url });
                }
                payload["links"] = outLinks;

                if (d.Banner != null && d.Banner.Length <= 1_400_000)
                    payload["banner"] = d.Banner;

                string json = JsonSerializer.Serialize(payload);
                var (outcome, error) = await WebsiteSaveQueue.PostLatestAsync(json, CancellationToken.None).ConfigureAwait(false);

                if (outcome == PostOutcome.Ok)
                {
                    return (true, false, null);
                }
                if (outcome == PostOutcome.Transient)
                {
                    return (false, true, "Cloudflare (the service that hosts Voidstrap) is temporarily unavailable, so your profile could not be saved right now. This is not something you did. Your changes are queued and will be applied automatically once Cloudflare recovers, even if you close this window.");
                }
                return (false, false, error ?? "Could not save profile.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WebsiteProfileEditor::Save", ex);
                return (false, false, "Network error while saving.");
            }
        }

        internal static async Task<(PostOutcome Outcome, string? Error)> PostProfileAsync(string jsonPayload, CancellationToken ct)
        {
            try
            {
                string? token = WebsiteAuth.GetToken();
                if (string.IsNullOrEmpty(token)) return (PostOutcome.Permanent, "You are not signed in.");

                using var req = new HttpRequestMessage(HttpMethod.Post, SaveUrl);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using var resp = await App.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    return (PostOutcome.Ok, null);
                }

                int code = (int)resp.StatusCode;
                if (code == 408 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504)
                    return (PostOutcome.Transient, "HTTP " + code);

                string err = "Could not save profile (HTTP " + code + ").";
                try
                {
                    string body = await Http.ReadStringBoundedAsync(resp.Content, MaxErrorResponseBytes, ct).ConfigureAwait(false);
                    using var ed = JsonDocument.Parse(body);
                    if (ed.RootElement.TryGetProperty("error", out var ep) && ep.ValueKind == JsonValueKind.String)
                        err = (ep.GetString() ?? err) + " (HTTP " + code + ")";
                }
                catch { }
                return (PostOutcome.Permanent, err);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (PostOutcome.Transient, "Canceled.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("WebsiteProfileEditor::PostProfile", "Transient network error: " + ex.Message);
                return (PostOutcome.Transient, "Network error.");
            }
        }

        public static (bool Ok, string DataUrl, string? Error) BuildBannerDataUrl(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return (false, "", "File not found.");
                if (new FileInfo(filePath).Length > MaxBannerSourceBytes)
                    return (false, "", "Image is too large.");

                BitmapSource src;
                using (var fs = File.OpenRead(filePath))
                {
                    var headerDecoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (headerDecoder.Frames.Count == 0)
                        return (false, "", "Could not read that image.");
                    int width = headerDecoder.Frames[0].PixelWidth;
                    int height = headerDecoder.Frames[0].PixelHeight;
                    if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > MaxBannerPixels)
                        return (false, "", "Image dimensions are too large.");
                    fs.Position = 0;
                    var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0) return (false, "", "Could not read that image.");
                    src = decoder.Frames[0];
                }

                int maxW = 1200;
                double scale = src.PixelWidth > maxW ? (double)maxW / src.PixelWidth : 1.0;

                for (int quality = 85; quality >= 35; quality -= 10)
                {
                    BitmapSource frame = src;
                    if (scale < 1.0)
                        frame = new TransformedBitmap(src, new ScaleTransform(scale, scale));

                    var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
                    var enc = new JpegBitmapEncoder { QualityLevel = quality };
                    enc.Frames.Add(BitmapFrame.Create(converted));

                    using var ms = new MemoryStream();
                    enc.Save(ms);

                    byte[] bytes = ms.ToArray();
                    if (bytes.Length <= MaxBannerBytes)
                        return (true, "data:image/jpeg;base64," + Convert.ToBase64String(bytes), null);

                    scale = scale < 1.0 ? scale * 0.85 : 0.85;
                }

                return (false, "", "Image is too large even after compression.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WebsiteProfileEditor::Banner", ex);
                return (false, "", "Could not read that image.");
            }
        }

        private static string NormalizeUrl(string raw)
        {
            string url = (raw ?? "").Trim();
            if (string.IsNullOrEmpty(url)) return "";
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url.Substring("http://".Length);
            if (url.Length > 200 || !UrlRe.IsMatch(url)) return "";
            return url;
        }

        private static string Str(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
        }

        private static bool Flag(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        }
    }
}
