using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Plugins;

public static class GitHubPublisher
{
    public const string ClientId = "Ov23linuZx4KCDfnTc0V";

    public static bool IsConfigured => ClientId.Length > 0;

    private const string LogIdent = "ModPublisher";

    private const string RulesUrl = "https://raw.githubusercontent.com/" + ModLibrary.Repository + "/main/scripts/rules.json";

    private static readonly string[] AllowedHosts = ["github.com", "api.github.com", "raw.githubusercontent.com"];

    private static readonly HttpClient Client = CreateClient();

    private static readonly object TokenGate = new();

    public sealed record Account(long Id, string Login);

    public sealed record DeviceCode(string UserCode, string VerificationUri, string DeviceCodeValue, int Interval, DateTime ExpiresAt);

    public sealed record Preflight(
        string Name,
        string Version,
        string? PublishedVersion,
        bool IsUpdate,
        IReadOnlyList<string> Files,
        IReadOnlyList<string> Network,
        IReadOnlyList<string> Problems,
        IReadOnlyList<string> Warnings
    );

    private static HttpClient CreateClient()
    {
        HttpClient client = new(
            new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            }
        )
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
        return client;
    }

    private static string TokenPath => Path.Combine(Paths.Config, "GitHubPublishing.dat");

    private static string AccountPath => Path.Combine(Paths.Config, "GitHubPublishing.json");

    public static Account? SignedIn
    {
        get
        {
            try
            {
                if (!File.Exists(AccountPath) || LoadToken() == null)
                    return null;
                JsonObject? node = JsonNode.Parse(File.ReadAllText(AccountPath)) as JsonObject;
                long id = node?["id"]?.GetValue<long>() ?? 0;
                string login = node?["login"]?.GetValue<string>() ?? "";
                return id > 0 && login.Length > 0 ? new Account(id, login) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public static void SignOut()
    {
        lock (TokenGate)
        {
            TryDeleteFile(TokenPath);
            TryDeleteFile(AccountPath);
        }
        App.Logger.WriteLine(LogIdent, "Signed out of GitHub publishing");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Logger.WriteLine(LogIdent, "Could not delete a sign in file: " + ex.GetType().Name);
        }
    }

    private static void SaveToken(string token)
    {
        lock (TokenGate)
        {
            Directory.CreateDirectory(Paths.Config);
            byte[] plain = Encoding.UTF8.GetBytes(token);
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(TokenPath, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            }
            else
            {
                using FileStream stream = new(
                    TokenPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Create,
                        Access = FileAccess.Write,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    }
                );
                stream.Write(plain);
                File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private static string? LoadToken()
    {
        lock (TokenGate)
        {
            try
            {
                if (!File.Exists(TokenPath))
                    return null;
                byte[] bytes = File.ReadAllBytes(TokenPath);
                if (OperatingSystem.IsWindows())
                    bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                string token = Encoding.UTF8.GetString(bytes).Trim();
                return token.Length > 0 ? token : null;
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public static async Task<DeviceCode> StartSignInAsync(CancellationToken token)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("publishing is not set up in this build of Voidstrap");
        JsonObject response = await PostFormAsync(
            "https://github.com/login/device/code",
            new Dictionary<string, string> { ["client_id"] = ClientId, ["scope"] = "public_repo" },
            token
        );
        string userCode = response["user_code"]?.GetValue<string>() ?? throw new InvalidDataException("GitHub did not return a code");
        string deviceCode = response["device_code"]?.GetValue<string>() ?? throw new InvalidDataException("GitHub did not return a code");
        string uri = response["verification_uri"]?.GetValue<string>() ?? "https://github.com/login/device";
        if (!uri.StartsWith("https://github.com/", StringComparison.Ordinal))
            uri = "https://github.com/login/device";
        int interval = Math.Clamp(response["interval"]?.GetValue<int>() ?? 5, 5, 60);
        int expires = Math.Clamp(response["expires_in"]?.GetValue<int>() ?? 900, 60, 1800);
        return new DeviceCode(userCode, uri, deviceCode, interval, DateTime.UtcNow.AddSeconds(expires));
    }

    public static async Task<Account> CompleteSignInAsync(DeviceCode code, CancellationToken token)
    {
        int interval = code.Interval;
        while (DateTime.UtcNow < code.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), token);
            JsonObject response = await PostFormAsync(
                "https://github.com/login/oauth/access_token",
                new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = code.DeviceCodeValue,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                },
                token
            );
            string? access = response["access_token"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(access))
            {
                string scopes = response["scope"]?.GetValue<string>() ?? "";
                if (!scopes.Split(',', StringSplitOptions.TrimEntries).Contains("public_repo"))
                    throw new InvalidOperationException("GitHub did not grant the public_repo permission");
                JsonObject user =
                    (await SendAsync(HttpMethod.Get, "https://api.github.com/user", null, access, token)) as JsonObject
                    ?? throw new InvalidDataException("GitHub did not return the account");
                long id = user["id"]?.GetValue<long>() ?? 0;
                string login = user["login"]?.GetValue<string>() ?? "";
                if (id <= 0 || login.Length == 0)
                    throw new InvalidDataException("GitHub did not return the account");
                if (!string.Equals(user["type"]?.GetValue<string>(), "User", StringComparison.Ordinal))
                    throw new InvalidOperationException("only personal GitHub accounts can publish");
                SaveToken(access);
                File.WriteAllText(AccountPath, new JsonObject { ["id"] = id, ["login"] = login }.ToJsonString());
                App.Logger.WriteLine(LogIdent, "Signed in to GitHub publishing as " + login);
                return new Account(id, login);
            }
            switch (response["error"]?.GetValue<string>())
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval = Math.Min(interval + 5, 60);
                    break;
                case "access_denied":
                    throw new OperationCanceledException("the sign in was cancelled on GitHub");
                case "expired_token":
                    throw new TimeoutException("the code expired, start the sign in again");
                default:
                    throw new InvalidOperationException("GitHub refused the sign in");
            }
        }
        throw new TimeoutException("the code expired, start the sign in again");
    }

    public static async Task<Preflight> CheckAsync(string name, CancellationToken token)
    {
        string folder = Path.Combine(Paths.Plugins, name);
        List<string> problems = [];
        List<string> warnings = [];
        if (!PluginManifest.TryRead(folder, out PluginManifest? manifest, out string? manifestError) || manifest == null)
        {
            if (manifestError != null)
                problems.Add(manifestError);
            problems.Add("the mod has no valid .voidstrap-mod/plugin.json");
            return new Preflight(name, "0.0.0", null, false, [], [], problems, warnings);
        }
        if (manifest.Description.Trim().Length == 0)
            problems.Add("add a description to plugin.json so people know what the mod does");
        if (!Regex.IsMatch(manifest.Version, "^(0|[1-9]\\d{0,5})\\.(0|[1-9]\\d{0,5})\\.(0|[1-9]\\d{0,5})$"))
            problems.Add("the version must look like 1.0.0");

        List<(string Rel, string Full)> files = CollectFiles(folder, problems);
        JsonObject rules = await GetRulesAsync(token);
        ApplyRules(rules, name, files, problems, warnings);

        StringWriter validation = new();
        if (await Task.Run(() => ModTools.Validate(folder, validation), token) != 0)
            problems.Add("the mod does not pass validation:" + Environment.NewLine + validation.ToString().Trim());

        ModLibrary.Index index = await ModLibrary.GetIndexAsync(true, token);
        ModLibrary.LibraryMod? published = index.Mods.FirstOrDefault(m => m.Name == name);
        Account? account = SignedIn;
        if (published != null && account != null && published.OwnerId != account.Id)
            problems.Add("the name " + name + " is already taken in the library by @" + published.Login + ", rename your mod");
        if (
            published != null
            && Version.TryParse(published.Version, out Version? old)
            && Version.TryParse(manifest.Version, out Version? now)
            && now <= old
        )
            problems.Add("raise the version above " + published.Version + " in plugin.json before publishing an update");
        return new Preflight(
            name,
            manifest.Version,
            published?.Version,
            published != null,
            files.Select(f => f.Rel).ToList(),
            manifest.NetworkHosts,
            problems,
            warnings
        );
    }

    private static List<(string Rel, string Full)> CollectFiles(string folder, List<string> problems)
    {
        List<(string, string)> files = [];
        string root = Path.GetFullPath(folder);
        foreach (string full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            if (
                rel.StartsWith("node_modules/", StringComparison.Ordinal)
                || (
                    rel.StartsWith(PluginManifest.ManifestFolder + "/", StringComparison.Ordinal)
                    && rel != PluginManifest.ManifestFolder + "/plugin.json"
                )
                || rel == "owner.json"
                || rel == "tsconfig.json"
            )
                continue;
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            {
                problems.Add(rel + " is a link, publish real files only");
                continue;
            }
            files.Add((rel, full));
        }
        return files;
    }

    private static async Task<JsonObject> GetRulesAsync(CancellationToken token)
    {
        using HttpResponseMessage response = await Client.GetAsync(RulesUrl, token);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) as JsonObject
            ?? throw new InvalidDataException("the library rules could not be read");
    }

    private static void ApplyRules(
        JsonObject rules,
        string modName,
        List<(string Rel, string Full)> files,
        List<string> problems,
        List<string> warnings
    )
    {
        JsonObject limits = rules["limits"] as JsonObject ?? new JsonObject();
        long maxModBytes = limits["maxModBytes"]?.GetValue<long>() ?? 1048576;
        int maxFiles = limits["maxFiles"]?.GetValue<int>() ?? 64;
        long maxFileBytes = limits["maxFileBytes"]?.GetValue<long>() ?? 262144;
        int maxLine = limits["maxLineLength"]?.GetValue<int>() ?? 400;
        int maxEncoded = limits["maxEncodedRun"]?.GetValue<int>() ?? 200;
        HashSet<string> allowed = (rules["allowedExtensions"] as JsonArray ?? [])
            .Select(n => n?.GetValue<string>() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> code = (rules["codeExtensions"] as JsonArray ?? [])
            .Select(n => n?.GetValue<string>() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<(Regex Pattern, string Reason, bool Review)> blocked = [];
        foreach (JsonNode? rule in rules["blocked"] as JsonArray ?? [])
        {
            try
            {
                blocked.Add(
                    (
                        new Regex(rule?["pattern"]?.GetValue<string>() ?? "$^", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)),
                        rule?["reason"]?.GetValue<string>() ?? "",
                        rule?["level"]?.GetValue<string>() == "review"
                    )
                );
            }
            catch (ArgumentException) { }
        }
        string spacedModName = modName.Replace('-', ' ');
        foreach ((Regex pattern, string reason, bool review) in blocked)
        {
            try
            {
                if (!review && pattern.IsMatch(spacedModName))
                    problems.Add("mod name: " + reason);
            }
            catch (RegexMatchTimeoutException) { }
        }
        Regex encoded = new("[A-Za-z0-9+/=]{" + maxEncoded + ",}", RegexOptions.None, TimeSpan.FromMilliseconds(250));
        if (files.Count > maxFiles)
            problems.Add("the mod has " + files.Count + " files, the library allows " + maxFiles);
        long total = 0;
        foreach ((string rel, string full) in files)
        {
            string spacedRel = Regex.Replace(rel, "[/_.-]", " ");
            foreach ((Regex pattern, string reason, bool review) in blocked)
            {
                try
                {
                    if (!review && pattern.IsMatch(spacedRel))
                        problems.Add(rel + ": " + reason);
                }
                catch (RegexMatchTimeoutException) { }
            }
            long size = new FileInfo(full).Length;
            total += size;
            string ext = Path.GetExtension(rel);
            if (!allowed.Contains(ext))
            {
                problems.Add(rel + " has a file type the library does not accept");
                continue;
            }
            if (ext.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                long maxIconBytes = limits["maxIconBytes"]?.GetValue<long>() ?? 262144;
                if (!rel.Equals("icon.ico", StringComparison.Ordinal))
                    problems.Add(rel + " must be named icon.ico at the root of the mod");
                else if (size > maxIconBytes)
                    problems.Add(rel + " is larger than " + maxIconBytes / 1024 + " KB");
                else if (UI.Elements.Dialogs.CreateVoidstrapModDialog.LoadIcon(full) == null)
                    problems.Add(rel + " is not a valid .ico icon");
                continue;
            }
            if (ext.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp")
            {
                long maxMediaBytes = limits["maxMediaBytes"]?.GetValue<long>() ?? 16 * 1024 * 1024;
                if (size > maxMediaBytes)
                    problems.Add(rel + " exceeds the media size limit");
                continue;
            }
            if (size > maxFileBytes)
            {
                problems.Add(rel + " is larger than " + maxFileBytes / 1024 + " KB");
                continue;
            }
            if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase) && !ModSvgRules.IsSafeSvg(File.ReadAllText(full)))
                problems.Add(rel + " holds scripts, embedded content or outside links, only plain drawing shapes are allowed in an SVG");
            string[] lines = File.ReadAllLines(full);
            for (int i = 0; i < lines.Length; i++)
            {
                string where = rel + ":" + (i + 1);
                if (code.Contains(ext) && lines[i].Length > maxLine && !ModSvgRules.IsSvgStringLine(lines[i]))
                    problems.Add(where + " is longer than " + maxLine + " characters");
                try
                {
                    if (encoded.IsMatch(lines[i]))
                        problems.Add(where + " holds a long encoded blob");
                    foreach ((Regex pattern, string reason, bool review) in blocked)
                    {
                        if (!pattern.IsMatch(lines[i]))
                            continue;
                        (review ? warnings : problems).Add(where + " " + reason);
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    problems.Add(where + " could not be checked");
                }
            }
        }
        if (total > maxModBytes)
            problems.Add("the mod is " + total / 1024 + " KB, the library allows " + maxModBytes / 1024 + " KB");
    }

    public static async Task<string> PublishAsync(string name, string notes, IProgress<string>? progress, CancellationToken token)
    {
        Account account = SignedIn ?? throw new InvalidOperationException("sign in to GitHub first");
        string access = LoadToken() ?? throw new InvalidOperationException("sign in to GitHub first");
        Preflight check = await CheckAsync(name, token);
        if (check.Problems.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, check.Problems));
        ModLibrary.Index index = await ModLibrary.GetIndexAsync(false, token);
        ModLibrary.LibraryMod? published = index.Mods.FirstOrDefault(m => m.Name == name);
        if (published != null && published.OwnerId != account.Id)
            throw new InvalidOperationException("the name " + name + " belongs to another account in the library");

        string upstream = ModLibrary.Repository;
        bool isOwner = upstream.StartsWith(account.Login + "/", StringComparison.OrdinalIgnoreCase);
        string forkName;
        if (isOwner)
        {
            forkName = upstream;
        }
        else
        {
            progress?.Report("fork");
            JsonObject fork =
                await SendAsync(
                    HttpMethod.Post,
                    "https://api.github.com/repos/" + upstream + "/forks",
                    new JsonObject { ["default_branch_only"] = true },
                    access,
                    token
                ) as JsonObject
                ?? throw new InvalidDataException("GitHub did not create the fork");
            forkName = fork["full_name"]?.GetValue<string>() ?? throw new InvalidDataException("GitHub did not create the fork");
            if (!forkName.StartsWith(account.Login + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the fork was created in an unexpected place");
            for (
                int i = 0;
                i < 30
                    && await SendAsync(
                        HttpMethod.Get,
                        "https://api.github.com/repos/" + forkName + "/git/ref/heads/main",
                        null,
                        access,
                        token,
                        allowNotFound: true
                    ) == null;
                i++
            )
                await Task.Delay(2000, token);
            await SendAsync(
                HttpMethod.Post,
                "https://api.github.com/repos/" + forkName + "/merge-upstream",
                new JsonObject { ["branch"] = "main" },
                access,
                token,
                allowConflict: true
            );
        }

        JsonObject mainRef =
            await SendAsync(HttpMethod.Get, "https://api.github.com/repos/" + forkName + "/git/ref/heads/main", null, access, token)
                as JsonObject
            ?? throw new InvalidDataException("the fork has no main branch");
        string baseCommit =
            mainRef["object"]?["sha"]?.GetValue<string>() ?? throw new InvalidDataException("the fork has no main branch");
        JsonObject baseCommitNode =
            await SendAsync(HttpMethod.Get, "https://api.github.com/repos/" + forkName + "/git/commits/" + baseCommit, null, access, token)
                as JsonObject
            ?? throw new InvalidDataException("the fork is not up to date");
        string baseTree =
            baseCommitNode["tree"]?["sha"]?.GetValue<string>() ?? throw new InvalidDataException("the fork is not up to date");

        progress?.Report("upload");
        string folder = Path.Combine(Paths.Plugins, name);
        string prefix = "mods/" + name + "/";
        JsonArray tree = [];
        HashSet<string> written = new(StringComparer.Ordinal);
        foreach (string rel in check.Files)
        {
            byte[] content =
                rel == PluginManifest.ManifestFolder + "/plugin.json"
                    ? StampedManifest(Path.Combine(folder, PluginManifest.ManifestFolder, "plugin.json"), name, account)
                    : await File.ReadAllBytesAsync(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar)), token);
            tree.Add(await CreateBlobEntryAsync(forkName, prefix + rel, content, access, token));
            written.Add(rel);
        }
        byte[] owner = Encoding.UTF8.GetBytes(
            new JsonObject { ["id"] = account.Id, ["login"] = account.Login }.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }
            ) + "\n"
        );
        tree.Add(await CreateBlobEntryAsync(forkName, prefix + "owner.json", owner, access, token));
        written.Add("owner.json");
        foreach (ModLibrary.LibraryFile old in published?.Files ?? [])
        {
            if (
                !written.Contains(old.Path)
                && await SendAsync(
                    HttpMethod.Get,
                    "https://api.github.com/repos/" + forkName + "/contents/" + prefix + old.Path + "?ref=" + baseCommit,
                    null,
                    access,
                    token,
                    allowNotFound: true
                ) != null
            )
                tree.Add(
                    new JsonObject
                    {
                        ["path"] = prefix + old.Path,
                        ["mode"] = "100644",
                        ["type"] = "blob",
                        ["sha"] = null,
                    }
                );
        }
        JsonObject newTree =
            await SendAsync(
                HttpMethod.Post,
                "https://api.github.com/repos/" + forkName + "/git/trees",
                new JsonObject { ["base_tree"] = baseTree, ["tree"] = tree },
                access,
                token
            ) as JsonObject
            ?? throw new InvalidDataException("GitHub refused the files");
        string message = (published == null ? "feature: publish " : "update: ") + name + " " + check.Version;
        JsonObject commit =
            await SendAsync(
                HttpMethod.Post,
                "https://api.github.com/repos/" + forkName + "/git/commits",
                new JsonObject
                {
                    ["message"] = message,
                    ["tree"] = newTree["sha"]?.GetValue<string>(),
                    ["parents"] = new JsonArray(baseCommit),
                },
                access,
                token
            ) as JsonObject
            ?? throw new InvalidDataException("GitHub refused the commit");
        string commitSha = commit["sha"]?.GetValue<string>() ?? throw new InvalidDataException("GitHub refused the commit");
        string branch = "mod/" + name;
        JsonNode? existingBranch = await SendAsync(
            HttpMethod.Get,
            "https://api.github.com/repos/" + forkName + "/git/ref/heads/" + branch,
            null,
            access,
            token,
            allowNotFound: true
        );
        if (existingBranch == null)
            await SendAsync(
                HttpMethod.Post,
                "https://api.github.com/repos/" + forkName + "/git/refs",
                new JsonObject { ["ref"] = "refs/heads/" + branch, ["sha"] = commitSha },
                access,
                token
            );
        else
            await SendAsync(
                HttpMethod.Patch,
                "https://api.github.com/repos/" + forkName + "/git/refs/heads/" + branch,
                new JsonObject { ["sha"] = commitSha, ["force"] = true },
                access,
                token
            );

        progress?.Report("pull");
        string head = account.Login + ":" + branch;
        JsonArray? open =
            await SendAsync(
                HttpMethod.Get,
                "https://api.github.com/repos/" + upstream + "/pulls?state=open&head=" + Uri.EscapeDataString(head),
                null,
                access,
                token
            ) as JsonArray;
        string? url = open?.FirstOrDefault()?["html_url"]?.GetValue<string>();
        if (url == null)
        {
            JsonObject pr =
                await SendAsync(
                    HttpMethod.Post,
                    "https://api.github.com/repos/" + upstream + "/pulls",
                    new JsonObject
                    {
                        ["title"] = message,
                        ["head"] = head,
                        ["base"] = "main",
                        ["maintainer_can_modify"] = false,
                        ["body"] = BuildBody(check, notes, account),
                    },
                    access,
                    token
                ) as JsonObject
                ?? throw new InvalidDataException("GitHub refused the pull request");
            url = pr["html_url"]?.GetValue<string>() ?? ModLibrary.RepositoryUrl + "/pulls";
        }
        if (!url.StartsWith("https://github.com/" + upstream + "/pull/", StringComparison.Ordinal))
            url = ModLibrary.RepositoryUrl + "/pulls";
        StampLocal(folder, name, account);
        App.Logger.WriteLine(LogIdent, "Published " + name + " " + check.Version + " for review");
        return url;
    }

    private static byte[] StampedManifest(string path, string name, Account account)
    {
        JsonObject manifest =
            JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("plugin.json is not a JSON object");
        manifest.Remove("$schema");
        manifest["library"] = new JsonObject { ["id"] = account.Id + "/" + name, ["ownerId"] = account.Id };
        return Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static void StampLocal(string folder, string name, Account account)
    {
        try
        {
            string path = Path.Combine(folder, PluginManifest.ManifestFolder, "plugin.json");
            File.WriteAllBytes(path, StampedManifest(path, name, account));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            App.Logger.WriteLine(LogIdent, "Could not mark the local mod as published: " + ex.GetType().Name);
        }
    }

    private static string BuildBody(Preflight check, string notes, Account account)
    {
        StringBuilder body = new();
        body.AppendLine("## Mod").AppendLine();
        body.AppendLine("- Name: `" + check.Name + "`");
        body.AppendLine(
            "- Version: " + check.Version + (check.PublishedVersion != null ? " (was " + check.PublishedVersion + ")" : " (new)")
        );
        body.AppendLine("- Owner: @" + account.Login + " (" + account.Id + ")").AppendLine();
        body.AppendLine("## What changed").AppendLine();
        body.AppendLine(string.IsNullOrWhiteSpace(notes) ? "No notes." : notes.Trim().Replace("\r", "")).AppendLine();
        body.AppendLine("## Websites it contacts").AppendLine();
        body.AppendLine(check.Network.Count == 0 ? "None" : string.Join(Environment.NewLine, check.Network.Select(h => "- `" + h + "`")))
            .AppendLine();
        if (check.Warnings.Count > 0)
        {
            body.AppendLine("## Flagged for review").AppendLine();
            foreach (string warning in check.Warnings)
                body.AppendLine("- " + warning);
            body.AppendLine();
        }
        body.AppendLine("Published from Voidstrap.");
        return body.ToString();
    }

    private static async Task<JsonObject> CreateBlobEntryAsync(
        string repo,
        string path,
        byte[] content,
        string access,
        CancellationToken token
    )
    {
        JsonObject blob =
            await SendAsync(
                HttpMethod.Post,
                "https://api.github.com/repos/" + repo + "/git/blobs",
                new JsonObject { ["content"] = Convert.ToBase64String(content), ["encoding"] = "base64" },
                access,
                token
            ) as JsonObject
            ?? throw new InvalidDataException("GitHub refused a file");
        return new JsonObject
        {
            ["path"] = path,
            ["mode"] = "100644",
            ["type"] = "blob",
            ["sha"] = blob["sha"]?.GetValue<string>(),
        };
    }

    private static void EnsureAllowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("refused to contact " + uri.Host);
    }

    private static async Task<JsonObject> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken token)
    {
        Uri uri = new(url);
        EnsureAllowed(uri);
        using HttpRequestMessage request = new(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await Client.SendAsync(request, token);
        App.Logger.WriteLine(LogIdent, "POST " + uri.AbsolutePath + " " + (int)response.StatusCode);
        string text = await response.Content.ReadAsStringAsync(token);
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("GitHub returned an unexpected answer");
    }

    private static async Task<JsonNode?> SendAsync(
        HttpMethod method,
        string url,
        JsonNode? body,
        string access,
        CancellationToken token,
        bool allowNotFound = false,
        bool allowConflict = false
    )
    {
        Uri uri = new(url);
        EnsureAllowed(uri);
        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = new(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (body != null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Client.SendAsync(request, token);
            App.Logger.WriteLine(LogIdent, method.Method + " " + uri.AbsolutePath + " " + (int)response.StatusCode);
            if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
                return null;
            if (
                (response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.UnprocessableEntity)
                && allowConflict
            )
                return null;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                SignOut();
                throw new InvalidOperationException("GitHub no longer accepts this sign in, sign in again");
            }
            if (
                (
                    response.StatusCode == HttpStatusCode.Forbidden
                    || response.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= 500
                )
                && attempt < 2
            )
            {
                await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)), token);
                continue;
            }
            string text = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                string reason = "";
                try
                {
                    reason = (JsonNode.Parse(text) as JsonObject)?["message"]?.GetValue<string>() ?? "";
                }
                catch (JsonException) { }
                throw new HttpRequestException("GitHub returned " + (int)response.StatusCode + (reason.Length > 0 ? ": " + reason : ""));
            }
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
    }
}
