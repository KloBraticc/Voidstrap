using System.Buffers;
using System.Net.Http.Headers;

namespace Voidstrap.Utility;

public sealed class WebsiteFlagProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("flags")]
    public Dictionary<string, string> Flags { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("revision")]
    public int Revision { get; set; }

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("updated")]
    public long Updated { get; set; }

    [JsonIgnore]
    public bool IsCloud { get; set; } = true;

    [JsonIgnore]
    public string LocalFileName { get; set; } = "";

    [JsonIgnore]
    public string Location => IsCloud ? "Account" : "Device";

    [JsonIgnore]
    public string FlagCount => Flags.Count == 1 ? "1 flag" : Flags.Count + " flags";

    [JsonIgnore]
    public string UpdatedText
    {
        get
        {
            if (Updated <= 0)
                return "Saved locally";
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(Updated).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            }
            catch
            {
                return "Saved";
            }
        }
    }
}

public static class WebsiteFlagProfiles
{
    private sealed class ProfileEnvelope
    {
        [JsonPropertyName("profile")]
        public WebsiteFlagProfile? Profile { get; set; }
    }

    private sealed class ProfilesEnvelope
    {
        [JsonPropertyName("profiles")]
        public List<WebsiteFlagProfile>? Profiles { get; set; }
    }

    private sealed class ErrorEnvelope
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private const int MaxResponseBytes = 750000;
    private static string ApiUrl => App.WebsiteBaseUrl + "/api/me/fflag-profiles";

    private const string CacheName = "fflagprofiles";

    public static Task<List<WebsiteFlagProfile>> GetAsync(CancellationToken token)
    {
        return GetAsync(token, true);
    }

    private static async Task<List<WebsiteFlagProfile>> GetAsync(CancellationToken token, bool allowCache)
    {
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get);
            using JsonDocument document = await SendAsync(request, token).ConfigureAwait(false);
            ProfilesEnvelope? envelope = document.Deserialize<ProfilesEnvelope>();
            List<WebsiteFlagProfile> profiles = envelope?.Profiles ?? new List<WebsiteFlagProfile>();
            foreach (WebsiteFlagProfile profile in profiles)
                profile.IsCloud = true;
            WebsiteCache.Save(CacheName, profiles);
            return profiles;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (!allowCache)
                throw;
            List<WebsiteFlagProfile>? cached = WebsiteCache.Load<List<WebsiteFlagProfile>>(CacheName);
            if (cached == null || cached.Count == 0)
                throw;
            foreach (WebsiteFlagProfile profile in cached)
                profile.IsCloud = true;
            App.Logger.WriteLine("WebsiteFlagProfiles::Get", "Site unreachable, using " + cached.Count + " cached profiles.");
            return cached;
        }
    }

    public static async Task<WebsiteFlagProfile> SaveAsync(string name, Dictionary<string, string> flags, WebsiteFlagProfile? existing, CancellationToken token)
    {
        Dictionary<string, string> snapshot = new Dictionary<string, string>(flags, StringComparer.Ordinal);
        if (snapshot.Count == 0)
            throw new InvalidOperationException("There are no flags to save");
        object payload = existing == null
            ? new { name, flags = snapshot }
            : new { id = existing.Id, revision = existing.Revision, name, flags = snapshot };
        using HttpRequestMessage request = CreateRequest(existing == null ? HttpMethod.Post : HttpMethod.Put);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using JsonDocument document = await SendAsync(request, token).ConfigureAwait(false);
        ProfileEnvelope? envelope = document.Deserialize<ProfileEnvelope>();
        WebsiteFlagProfile profile = envelope?.Profile ?? throw new InvalidDataException("The account returned an invalid profile");
        EnsureCompleteSnapshot(snapshot, profile.Flags);
        profile.IsCloud = true;
        List<WebsiteFlagProfile> storedProfiles = await GetAsync(token, false).ConfigureAwait(false);
        WebsiteFlagProfile stored = storedProfiles.FirstOrDefault(x => string.Equals(x.Id, profile.Id, StringComparison.Ordinal)) ?? throw new InvalidDataException("The saved profile could not be verified");
        EnsureCompleteSnapshot(snapshot, stored.Flags);
        profile.Flags = new Dictionary<string, string>(stored.Flags, StringComparer.Ordinal);
        return profile;
    }

    public static async Task DeleteAsync(WebsiteFlagProfile profile, CancellationToken token)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Delete);
        request.Content = new StringContent(JsonSerializer.Serialize(new { id = profile.Id, revision = profile.Revision }), Encoding.UTF8, "application/json");
        using JsonDocument document = await SendAsync(request, token).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method)
    {
        string? token = WebsiteAuth.GetToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Sign in to sync profiles with your account");
        HttpRequestMessage request = new HttpRequestMessage(method, ApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void EnsureCompleteSnapshot(Dictionary<string, string> expected, Dictionary<string, string>? actual)
    {
        if (actual == null || actual.Count != expected.Count)
            throw new InvalidDataException("The account did not save the complete flag list");
        foreach (KeyValuePair<string, string> flag in expected)
        {
            if (!actual.TryGetValue(flag.Key, out string? value) || !string.Equals(value, flag.Value, StringComparison.Ordinal))
                throw new InvalidDataException("The account did not save the complete flag list");
        }
    }

    private static async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        string text = await ReadBoundedTextAsync(response, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string message = response.StatusCode == HttpStatusCode.Unauthorized ? "Sign in again to sync account profiles" : "Account profiles could not be updated";
            try
            {
                ErrorEnvelope? error = JsonSerializer.Deserialize<ErrorEnvelope>(text);
                if (!string.IsNullOrWhiteSpace(error?.Error) && error.Error.Length <= 120)
                    message = error.Error;
            }
            catch
            {
            }
            throw new InvalidOperationException(message);
        }
        return JsonDocument.Parse(text);
    }

    private static async Task<string> ReadBoundedTextAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long length && (length < 0 || length > MaxResponseBytes))
            throw new InvalidDataException("The account response is too large");
        await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using MemoryStream output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > MaxResponseBytes)
                    throw new InvalidDataException("The account response is too large");
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
