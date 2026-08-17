using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility
{
    public sealed class WebsiteFriend
    {
        public string Id { get; set; } = "";
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Avatar { get; set; } = "";
        public string AvatarBorder { get; set; } = "";
        public string EquippedBorderJson { get; set; } = "";
        public string BadgesJson { get; set; } = "";
    }

    public static class WebsiteFriends
    {
        private const int MaxResponseBytes = 4 * 1024 * 1024;

        private const string CacheName = "friends";

        public static bool IsSignedIn() => WebsiteAuth.IsSignedIn();

        private static void SaveCache(List<WebsiteFriend> friends)
        {
            WebsiteCache.Save(CacheName, friends);
        }

        private static List<WebsiteFriend> LoadCache()
        {
            return WebsiteCache.Load<List<WebsiteFriend>>(CacheName) ?? new List<WebsiteFriend>();
        }

        public static async Task<(bool Ok, List<WebsiteFriend> Friends, string? Error)> GetFriendsAsync()
        {
            List<WebsiteFriend> list = new List<WebsiteFriend>();
            try
            {
                string? token = WebsiteAuth.GetToken();
                if (string.IsNullOrEmpty(token))
                    return (false, list, "You are not signed in.");

                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, App.WebsiteBaseUrl + "/api/me/social");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using HttpResponseMessage resp = await App.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return (false, list, "Your session expired. Sign in again.");
                if (!resp.IsSuccessStatusCode)
                {
                    List<WebsiteFriend> offline = LoadCache();
                    if (offline.Count > 0)
                        return (true, offline, "Showing your last saved friends list. The site is unreachable.");
                    return (false, list, "Could not load friends (HTTP " + (int)resp.StatusCode + ").");
                }

                string json = await Http.ReadStringBoundedAsync(resp.Content, MaxResponseBytes, timeout.Token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("friends", out JsonElement friends) && friends.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement f in friends.EnumerateArray())
                    {
                        if (f.ValueKind != JsonValueKind.Object)
                            continue;
                        WebsiteFriend wf = new WebsiteFriend
                        {
                            Id = Str(f, "id"),
                            Username = Str(f, "username"),
                            DisplayName = Str(f, "displayName"),
                            Avatar = Str(f, "avatar"),
                            AvatarBorder = Str(f, "avatarBorder"),
                        };
                        if (f.TryGetProperty("equippedBorder", out JsonElement eqb) && eqb.ValueKind == JsonValueKind.Object)
                            wf.EquippedBorderJson = eqb.GetRawText();
                        if (f.TryGetProperty("badges", out JsonElement badgesEl) && badgesEl.ValueKind == JsonValueKind.Array)
                            wf.BadgesJson = badgesEl.GetRawText();
                        if (!string.IsNullOrEmpty(wf.Id))
                            list.Add(wf);
                    }
                }
                SaveCache(list);
                return (true, list, null);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WebsiteFriends::GetFriends", ex);
                List<WebsiteFriend> offline = LoadCache();
                if (offline.Count > 0)
                    return (true, offline, "Showing your last saved friends list. The site is unreachable.");
                return (false, list, "Network error while loading friends.");
            }
        }

        public static async Task<(bool Ok, string? Error)> UnfriendAsync(string userId)
        {
            try
            {
                if (!IsNumericId(userId))
                    return (false, "Invalid user.");
                string? token = WebsiteAuth.GetToken();
                if (string.IsNullOrEmpty(token))
                    return (false, "You are not signed in.");

                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, App.WebsiteBaseUrl + "/api/users/" + userId + "/unfriend");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using HttpResponseMessage resp = await App.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                    return (true, null);
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return (false, "Your session expired. Sign in again.");
                if ((int)resp.StatusCode == 429)
                    return (false, "Too many requests, slow down.");
                return (false, "Could not unfriend (HTTP " + (int)resp.StatusCode + ").");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WebsiteFriends::Unfriend", ex);
                return (false, "Network error.");
            }
        }

        private static bool IsNumericId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 20)
                return false;
            foreach (char c in s)
            {
                if (c < '0' || c > '9')
                    return false;
            }
            return true;
        }

        private static string Str(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
        }
    }
}
