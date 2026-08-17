using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.GameChat
{
    public static class GameChatEmotes
    {
        private const string Tag = "GameChatEmotes";
        private const int MaxResponseBytes = 256 * 1024;
        private static string BaseUrl => App.WebsiteBaseUrl + "/api/chat";

        public static bool IsVerified => App.Settings.Prop.GameChatVerified && App.Settings.Prop.GameChatRobloxUserId > 0;

        private static StringContent JsonBody(object payload)
        {
            return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        private static string ReadError(JsonElement root, string fallback)
        {
            return root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? fallback : fallback;
        }

        public static async Task<(string? Username, string? Error)> VerifyAccountAsync()
        {
            string? token = Utility.WebsiteAuth.GetToken();
            if (string.IsNullOrEmpty(token))
                return (null, GameChatStrings.VerifyNotSignedIn);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                request.Content = JsonBody(new { action = "identity" });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                using var doc = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, timeout.Token).ConfigureAwait(false));
                var root = doc.RootElement;
                if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    long robloxId = root.TryGetProperty("robloxId", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : 0;
                    string username = root.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
                    App.Settings.Prop.GameChatVerified = true;
                    App.Settings.Prop.GameChatRobloxUserId = robloxId;
                    App.Settings.SaveDeferred();
                    return (username, null);
                }
                return (null, ReadError(root, GameChatStrings.UnknownError));
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Account verification failed: " + ex.Message);
                return (null, GameChatStrings.VerifyChallengeFailed);
            }
        }

        public static Task UnverifyAsync()
        {
            App.Settings.Prop.GameChatVerified = false;
            App.Settings.Prop.GameChatRobloxUserId = 0;
            App.Settings.SaveDeferred();
            return Task.CompletedTask;
        }

        public static async Task<string?> SendEmoteAsync(string emoteName, long universeId, string jobId)
        {
            if (!App.Settings.Prop.GameChatVerified)
                return GameChatStrings.MustBeVerifiedEmote;

            string? token = Utility.WebsiteAuth.GetToken();
            if (string.IsNullOrEmpty(token))
                return GameChatStrings.MustBeVerifiedEmote;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                request.Content = JsonBody(new { action = "mail", jobId, universeId, type = "Emote", data = new { name = emoteName } });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    App.Settings.Prop.GameChatVerified = false;
                    App.Settings.Prop.GameChatRobloxUserId = 0;
                    App.Settings.SaveDeferred();
                    return GameChatStrings.MustBeVerifiedEmote;
                }
                if (!response.IsSuccessStatusCode)
                    return GameChatStrings.FailedToQueueEmote;
                return null;
            }
            catch (Exception ex)
            {
                return string.Format(GameChatStrings.EmoteError, ex.Message);
            }
        }
    }
}
