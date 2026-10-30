using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Studio;

public enum StudioPluginPublishError
{
    None,
    FileMissing,
    FileType,
    FileTooLarge,
    FileNotRoblox,
    NameLength,
    NotSignedIn,
    NotAllowed,
    RateLimited,
    Failed,
}

public sealed record StudioPluginPublishResult(StudioPluginPublishError Error, long AssetId, string Detail);

public static partial class StudioPluginPublisher
{
    private const string LogIdent = "StudioPluginPublisher";

    public const long MaxFileBytes = 50L * 1024 * 1024;

    public const int MinNameLength = 3;

    public const int MaxNameLength = 50;

    private const string AssetsBase = "https://apis.roblox.com/assets/user-auth/v1/";

    [GeneratedRegex("^operations/[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex OperationPathPattern();

    public static StudioPluginPublishError ValidateFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return StudioPluginPublishError.FileMissing;
        string extension = Path.GetExtension(path);
        bool binary = extension.Equals(".rbxm", StringComparison.OrdinalIgnoreCase);
        bool xml = extension.Equals(".rbxmx", StringComparison.OrdinalIgnoreCase);
        if (!binary && !xml)
            return StudioPluginPublishError.FileType;
        FileInfo info = new(path);
        if (info.Length <= 0 || info.Length > MaxFileBytes)
            return StudioPluginPublishError.FileTooLarge;
        Span<byte> header = stackalloc byte[8];
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.ReadAtLeast(header, header.Length, false) < 7)
                return StudioPluginPublishError.FileNotRoblox;
        }
        string magic = Encoding.ASCII.GetString(header);
        bool valid = binary ? magic == "<roblox!" : magic.StartsWith("<roblox", StringComparison.Ordinal);
        return valid ? StudioPluginPublishError.None : StudioPluginPublishError.FileNotRoblox;
    }

    public static async Task<StudioPluginPublishResult> PublishAsync(
        string filePath,
        string name,
        string description,
        long existingAssetId,
        IProgress<double>? progress,
        CancellationToken token
    )
    {
        StudioPluginPublishError fileError = ValidateFile(filePath);
        if (fileError != StudioPluginPublishError.None)
            return new StudioPluginPublishResult(fileError, 0, string.Empty);
        string cleanName = StudioPluginCommunity.CleanText(name).Replace("\n", " ");
        if (cleanName.Length < MinNameLength || cleanName.Length > MaxNameLength)
            return new StudioPluginPublishResult(StudioPluginPublishError.NameLength, 0, string.Empty);
        string cleanDescription = StudioPluginCommunity.CleanText(description);

        RobloxAccount? account = await RobloxCookie.GetAccountAsync(token).ConfigureAwait(false);
        if (account == null || account.UserId <= 0)
            return new StudioPluginPublishResult(StudioPluginPublishError.NotSignedIn, 0, string.Empty);

        byte[] file = await File.ReadAllBytesAsync(filePath, token).ConfigureAwait(false);
        if (file.LongLength > MaxFileBytes)
            return new StudioPluginPublishResult(StudioPluginPublishError.FileTooLarge, 0, string.Empty);
        progress?.Report(0.2);

        string fileName = "plugin" + Path.GetExtension(filePath).ToLowerInvariant();
        RobloxAuthedResult result;
        if (existingAssetId > 0)
        {
            string json = JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["assetId"] = existingAssetId.ToString(CultureInfo.InvariantCulture),
                    ["displayName"] = cleanName,
                    ["description"] = cleanDescription,
                }
            );
            result = await RobloxAuthedClient
                .SendMultipartAsync(
                    HttpMethod.Patch,
                    AssetsBase + "assets/" + existingAssetId.ToString(CultureInfo.InvariantCulture),
                    json,
                    file,
                    fileName,
                    token
                )
                .ConfigureAwait(false);
        }
        else
        {
            string json = JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["assetType"] = "Plugin",
                    ["displayName"] = cleanName,
                    ["description"] = cleanDescription,
                    ["creationContext"] = new Dictionary<string, object>
                    {
                        ["creator"] = new Dictionary<string, string> { ["userId"] = account.UserId.ToString(CultureInfo.InvariantCulture) },
                    },
                }
            );
            result = await RobloxAuthedClient
                .SendMultipartAsync(HttpMethod.Post, AssetsBase + "assets", json, file, fileName, token)
                .ConfigureAwait(false);
        }
        progress?.Report(0.6);
        if (!result.Succeeded)
            return Failure(result);

        (bool done, long assetId, string? path, string error) = ReadOperation(result.Body);
        TimeSpan delay = TimeSpan.FromMilliseconds(500);
        for (int attempt = 0; !done && attempt < 10 && error.Length == 0; attempt++)
        {
            if (path == null || !OperationPathPattern().IsMatch(path))
                break;
            await Task.Delay(delay, token).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 8000));
            RobloxAuthedResult poll = await RobloxAuthedClient.GetAsync(AssetsBase + path, token).ConfigureAwait(false);
            if (!poll.Succeeded)
                return Failure(poll);
            (done, assetId, path, error) = ReadOperation(poll.Body);
            progress?.Report(Math.Min(0.95, 0.6 + attempt * 0.04));
        }
        if (error.Length > 0)
        {
            App.Logger?.WriteLine(LogIdent, "Roblox refused the plugin upload: " + error);
            return new StudioPluginPublishResult(StudioPluginPublishError.Failed, 0, error);
        }
        if (existingAssetId > 0 && assetId <= 0)
            assetId = existingAssetId;
        if (!done || assetId <= 0)
            return new StudioPluginPublishResult(StudioPluginPublishError.Failed, 0, string.Empty);
        progress?.Report(1.0);
        App.Logger?.WriteLine(
            LogIdent,
            (existingAssetId > 0 ? "Updated plugin " : "Published plugin ") + assetId.ToString(CultureInfo.InvariantCulture)
        );
        return new StudioPluginPublishResult(StudioPluginPublishError.None, assetId, string.Empty);
    }

    private static StudioPluginPublishResult Failure(RobloxAuthedResult result)
    {
        StudioPluginPublishError error = result.Status switch
        {
            RobloxAuthedStatus.NotSignedIn => StudioPluginPublishError.NotSignedIn,
            RobloxAuthedStatus.NotAllowed => StudioPluginPublishError.NotAllowed,
            RobloxAuthedStatus.RateLimited => StudioPluginPublishError.RateLimited,
            _ => StudioPluginPublishError.Failed,
        };
        return new StudioPluginPublishResult(error, 0, ReadMessage(result.Body));
    }

    private static (bool Done, long AssetId, string? Path, string Error) ReadOperation(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            bool done = root.TryGetProperty("done", out JsonElement doneElement) && doneElement.ValueKind == JsonValueKind.True;
            string? path =
                root.TryGetProperty("path", out JsonElement pathElement) && pathElement.ValueKind == JsonValueKind.String
                    ? pathElement.GetString()
                    : null;
            long assetId = 0;
            if (
                root.TryGetProperty("response", out JsonElement response)
                && response.ValueKind == JsonValueKind.Object
                && response.TryGetProperty("assetId", out JsonElement id)
            )
            {
                if (id.ValueKind == JsonValueKind.String)
                    long.TryParse(id.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out assetId);
                else if (id.ValueKind == JsonValueKind.Number)
                    id.TryGetInt64(out assetId);
            }
            string error = string.Empty;
            if (root.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.Object)
                error =
                    errorElement.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String
                        ? message.GetString() ?? "error"
                        : "error";
            return (done, assetId, path, error);
        }
        catch (JsonException)
        {
            return (false, 0, null, "unreadable response");
        }
    }

    private static string ReadMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? string.Empty;
            if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement error in errors.EnumerateArray())
                {
                    if (error.TryGetProperty("message", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                        return text.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException) { }
        return string.Empty;
    }
}
