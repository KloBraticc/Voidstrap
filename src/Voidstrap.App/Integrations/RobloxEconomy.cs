using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations;

public sealed record RobloxPurchase(string Name, string Type, long Amount, DateTime CreatedUtc);

public static class RobloxEconomy
{
    private const string LOG_IDENT = "RobloxEconomy";
    private const int MaxApiResponseBytes = 2 * 1024 * 1024;
    private const int MaxTransactionPages = 5;
    private static readonly HttpClient Client = CreateClient();

    public static async Task<long?> GetRobuxAsync(long userId, CancellationToken token)
    {
        if (userId <= 0)
            return null;
        string? cookie = RobloxCookie.Get();
        if (string.IsNullOrEmpty(cookie))
            return null;
        using JsonDocument? doc = await GetJsonAsync($"https://economy.roblox.com/v1/users/{userId}/currency", cookie, token).ConfigureAwait(false);
        return ReadNumber(doc?.RootElement, "robux");
    }

    public static async Task<IReadOnlyList<RobloxPurchase>?> GetGamePurchasesAsync(long userId, long universeId, long placeId, CancellationToken token)
    {
        if (userId <= 0 || (universeId <= 0 && placeId <= 0))
            return Array.Empty<RobloxPurchase>();
        string? cookie = RobloxCookie.Get();
        if (string.IsNullOrEmpty(cookie))
            return null;

        List<RobloxPurchase> purchases = new();
        string? cursor = null;

        for (int page = 0; page < MaxTransactionPages; page++)
        {
            string url = $"https://economy.roblox.com/v2/users/{userId}/transactions?transactionType=Purchase&limit=100";
            if (!string.IsNullOrEmpty(cursor))
                url += "&cursor=" + Uri.EscapeDataString(cursor);

            using JsonDocument? doc = await GetJsonAsync(url, cookie, token).ConfigureAwait(false);
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
                return page == 0 ? null : purchases;

            if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!item.TryGetProperty("details", out JsonElement details) || details.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!details.TryGetProperty("place", out JsonElement place) || place.ValueKind != JsonValueKind.Object)
                        continue;

                    long? itemUniverseId = ReadNumber(place, "universeId");
                    long? itemPlaceId = ReadNumber(place, "placeId");
                    bool matches = universeId > 0 ? itemUniverseId == universeId : itemPlaceId == placeId;
                    if (!matches)
                        continue;

                    string createdRaw = ReadString(item, "created");
                    if (!DateTime.TryParse(createdRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime created))
                        continue;

                    long rawAmount = 0;
                    if (item.TryGetProperty("currency", out JsonElement currency) && currency.ValueKind == JsonValueKind.Object)
                        rawAmount = ReadNumber(currency, "amount") ?? 0;

                    long amount = rawAmount == long.MinValue ? 0 : Math.Abs(rawAmount);
                    string name = ReadString(details, "name");
                    if (string.IsNullOrWhiteSpace(name))
                        name = "Purchase";
                    string type = ReadString(details, "type");

                    purchases.Add(new RobloxPurchase(name, type, amount, created.ToUniversalTime()));
                }
            }

            cursor = ReadString(doc.RootElement, "nextPageCursor");
            if (string.IsNullOrEmpty(cursor))
                break;
        }

        return purchases;
    }

    private static string ReadString(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e
        && e.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long? ReadNumber(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e
        && e.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number)
            ? number
            : null;

    private static async Task<JsonDocument?> GetJsonAsync(string url, string? cookie, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
            using HttpResponseMessage response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                App.Logger.WriteLine(LOG_IDENT, $"{new Uri(url).Host} answered {(int)response.StatusCode} {response.StatusCode}");
                return null;
            }
            return JsonDocument.Parse(
                await Utility.Http.ReadStringBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false)
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Economy request failed: " + ex.Message);
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = Voidstrap.Utility.VpnHttpClient.Create(
            TimeSpan.FromSeconds(10L),
            handler =>
            {
                handler.UseCookies = false;
                handler.AllowAutoRedirect = false;
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }
        );
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap/1.0");
        return client;
    }
}
