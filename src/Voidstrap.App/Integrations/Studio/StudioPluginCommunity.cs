using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Studio;

public sealed record StudioPluginReply(string Username, bool Verified, string Text, DateTime? Created);

public sealed record StudioPluginReview(
    string Id,
    long UserId,
    string Username,
    bool Verified,
    string Text,
    bool Recommended,
    int HelpfulCount,
    DateTime? Created,
    StudioPluginReply? Reply
);

public sealed record StudioPluginReviewPage(IReadOnlyList<StudioPluginReview> Reviews, string? NextCursor);

public static partial class StudioPluginCommunity
{
    public const int MaxTextLength = 1000;

    private const string ReviewsBase = "https://apis.roblox.com/asset-reviews-api/v1";

    [GeneratedRegex("^[A-Za-z0-9+/=_-]{1,128}$")]
    private static partial Regex CommentIdPattern();

    [GeneratedRegex("^[A-Za-z0-9+/=_-]{1,256}$")]
    private static partial Regex CursorPattern();

    public static async Task<(RobloxAuthedStatus Status, StudioPluginReviewPage? Page)> GetReviewsAsync(
        long assetId,
        string? cursor,
        CancellationToken token
    )
    {
        RequireAsset(assetId);
        string url =
            ReviewsBase + "/assets/" + assetId.ToString(CultureInfo.InvariantCulture) + "/comments?limit=10&sortByHelpfulCount=true";
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!CursorPattern().IsMatch(cursor))
                throw new ArgumentException("The review page cursor is not valid", nameof(cursor));
            url += "&cursor=" + Uri.EscapeDataString(cursor);
        }
        RobloxAuthedResult result = await RobloxAuthedClient.GetAsync(url, token).ConfigureAwait(false);
        if (!result.Succeeded)
            return (result.Status, null);
        using JsonDocument document = JsonDocument.Parse(result.Body);
        JsonElement root = document.RootElement;
        List<StudioPluginReview> reviews = new();
        if (root.TryGetProperty("commentResponses", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                StudioPluginReply? reply = null;
                if (item.TryGetProperty("reply", out JsonElement replyElement) && replyElement.ValueKind == JsonValueKind.Object)
                {
                    reply = new StudioPluginReply(
                        ReadString(replyElement, "replyingUsername"),
                        ReadBool(replyElement, "replyingUserHasVerifiedBadge"),
                        ReadString(replyElement, "text"),
                        ReadTime(replyElement, "createdUtc")
                    );
                }
                reviews.Add(
                    new StudioPluginReview(
                        ReadString(item, "id"),
                        item.TryGetProperty("commentingUserId", out JsonElement user) && user.TryGetInt64(out long userId) ? userId : 0,
                        ReadString(item, "commentingUsername"),
                        ReadBool(item, "commentingUserHasVerifiedBadge"),
                        ReadString(item, "text"),
                        ReadBool(item, "isRecommended"),
                        item.TryGetProperty("helpfulCount", out JsonElement helpful) && helpful.TryGetInt32(out int count) ? count : 0,
                        ReadTime(item, "createdUtc"),
                        reply
                    )
                );
            }
        }
        bool hasMore = root.TryGetProperty("hasMore", out JsonElement more) && more.ValueKind == JsonValueKind.True;
        string? next = hasMore ? ReadString(root, "nextCursor") : null;
        return (RobloxAuthedStatus.Ok, new StudioPluginReviewPage(reviews, string.IsNullOrEmpty(next) ? null : next));
    }

    public static async Task<bool> IsCreatorAsync(long assetId, CancellationToken token)
    {
        RequireAsset(assetId);
        RobloxAuthedResult result = await RobloxAuthedClient
            .GetAsync(ReviewsBase + "/assets/" + assetId.ToString(CultureInfo.InvariantCulture) + "/user/isCreator", token)
            .ConfigureAwait(false);
        return result.Succeeded && result.Body.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static Task<RobloxAuthedResult> PostReviewAsync(long assetId, string text, CancellationToken token)
    {
        return PostCommentAsync(assetId, text, null, token);
    }

    public static Task<RobloxAuthedResult> ReplyAsync(long assetId, string reviewId, string text, CancellationToken token)
    {
        RequireComment(reviewId);
        return PostCommentAsync(assetId, text, reviewId, token);
    }

    public static Task<RobloxAuthedResult> MarkHelpfulAsync(string reviewId, CancellationToken token)
    {
        RequireComment(reviewId);
        string json = JsonSerializer.Serialize(new Dictionary<string, string> { ["reaction"] = "Helpful" });
        return RobloxAuthedClient.PostJsonAsync(ReviewsBase + "/comments/" + Uri.EscapeDataString(reviewId) + "/reactions", json, token);
    }

    public static async Task<bool?> GetVoteAsync(long assetId, CancellationToken token)
    {
        RequireAsset(assetId);
        RobloxAuthedResult result = await RobloxAuthedClient
            .GetAsync(
                "https://apis.roblox.com/toolbox-service/v1/voting/vote?assetType=Plugin&assetId="
                    + assetId.ToString(CultureInfo.InvariantCulture),
                token
            )
            .ConfigureAwait(false);
        if (!result.Succeeded)
            return null;
        using JsonDocument document = JsonDocument.Parse(result.Body);
        return
            document.RootElement.TryGetProperty("userVote", out JsonElement vote)
            && vote.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? vote.GetBoolean()
            : null;
    }

    public static Task<RobloxAuthedResult> VoteAsync(long assetId, bool up, CancellationToken token)
    {
        RequireAsset(assetId);
        return RobloxAuthedClient.PostAsync(
            "https://apis.roblox.com/voting-api/vote/asset/"
                + assetId.ToString(CultureInfo.InvariantCulture)
                + "?vote="
                + (up ? "true" : "false"),
            token
        );
    }

    public static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        StringBuilder builder = new(text.Length);
        foreach (char c in text.Trim())
        {
            if (c == '\n' || !char.IsControl(c))
                builder.Append(c);
        }
        string cleaned = builder.ToString().Replace("\r", string.Empty);
        return cleaned.Length > MaxTextLength ? cleaned[..MaxTextLength] : cleaned;
    }

    private static Task<RobloxAuthedResult> PostCommentAsync(long assetId, string text, string? parentId, CancellationToken token)
    {
        RequireAsset(assetId);
        string cleaned = CleanText(text);
        if (cleaned.Length == 0)
            throw new ArgumentException("The review text is empty", nameof(text));
        Dictionary<string, string?> body = new() { ["text"] = cleaned, ["parentId"] = parentId };
        return RobloxAuthedClient.PostJsonAsync(
            ReviewsBase + "/assets/" + assetId.ToString(CultureInfo.InvariantCulture) + "/comments",
            JsonSerializer.Serialize(body),
            token
        );
    }

    private static void RequireAsset(long assetId)
    {
        if (assetId <= 0)
            throw new ArgumentOutOfRangeException(nameof(assetId));
    }

    private static void RequireComment(string reviewId)
    {
        if (string.IsNullOrEmpty(reviewId) || !CommentIdPattern().IsMatch(reviewId))
            throw new ArgumentException("The review id is not valid", nameof(reviewId));
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }

    private static DateTime? ReadTime(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long millis) && millis > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(millis).LocalDateTime
            : null;
    }
}
