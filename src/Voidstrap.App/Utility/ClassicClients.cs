using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Voidstrap.Utility
{
    public enum ClientLaunchType
    {
        Play,
        Studio,
        Host
    }

    public class ClassicCatalogEntry
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool HasStudio { get; set; } = true;
    }

    public class ClassicManifest
    {
        public long EngineSize { get; set; }
        public long MapsSize { get; set; }
        public Dictionary<string, long> ClientSizes { get; set; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public List<ClassicCatalogEntry> Clients { get; set; } = new List<ClassicCatalogEntry>();
    }

    public class ClassicClientConfig
    {
        public class _Player
        {
            public string ExecutableName { get; set; } = "";
            public string LaunchArguments { get; set; } = "";
            public bool Sensitive { get; set; }
        }

        public class _Studio
        {
            public string Type { get; set; } = "None";
            public string ExecutableName { get; set; } = "";
            public string MFCExecutableName { get; set; } = "";
            public string QtExecutableName { get; set; } = "";
        }

        public class _Server
        {
            public string ExecutableName { get; set; } = "";
            public string LaunchArguments { get; set; } = "";
            public string Directory { get; set; } = "Player";
        }

        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Domain { get; set; } = "";
        public _Player Player { get; set; } = new _Player();
        public _Studio Studio { get; set; } = new _Studio();
        public _Server Server { get; set; } = new _Server();
    }

    public static class ClassicClients
    {
        private sealed class ReleaseAssetIntegrity
        {
            public string Url { get; init; } = "";
            public long Size { get; init; }
            public string Sha256 { get; init; } = "";
        }

        private sealed class InstalledAssetRecord
        {
            public long Size { get; set; }
            public string Sha256 { get; set; } = "";
        }

        public const ushort DefaultPort = 53640;

        public const string DefaultBaseUrl = "https://github.com/Orc-Archive/Orc-Voidstrap/releases/tag/v1/";

        public const string LegacyDefaultBaseUrl = "https://github.com/voidstrap/RobloxClients/raw/main/";

        public const string WebServerExe = "VoidstrapClient.WebServer.exe";

        public const string ConfigFileName = "VoidstrapClient_Config.json";

        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var http = VpnHttpClient.Create(TimeSpan.FromMinutes(30));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
            return http;
        }

        private static readonly JsonSerializerOptions ConfigJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static readonly IReadOnlyList<ClassicCatalogEntry> Catalog = new List<ClassicCatalogEntry>
        {
            new ClassicCatalogEntry { Code = "2007E", Name = "March 2007", Description = "The earliest available Roblox client. A very primitive version of Roblox, lacking most of the features that can be seen in the present client." },
            new ClassicCatalogEntry { Code = "2007E-FakeFeb", Name = "Fake February 2007", Description = "A recreation of the February 2007 Roblox client using the March 2007 client as a base. Restores Color3 teams, the Eras Bold ITC font, and other removed functionality." },
            new ClassicCatalogEntry { Code = "2007M", Name = "August 2007", Description = "Wearable hats have been introduced and the chat bar color has changed from white to gray." },
            new ClassicCatalogEntry { Code = "2007L", Name = "December 2007", Description = "Introduces a new graphical look using the OGRE rendering engine. Introduces bevels, seen in many old clients." },
            new ClassicCatalogEntry { Code = "2008E", Name = "April 2008", Description = "Adds in sparkles and the new explosion effect, powered by OGRE's particle system." },
            new ClassicCatalogEntry { Code = "2008M", Name = "August 2008", Description = "Roblox brings you Shirts and Pants. They also brought you walk speed and team chat." },
            new ClassicCatalogEntry { Code = "2008L", Name = "December 2008", Description = "Client sided movement makes moving around feel much better. Heads and faces have been added." },
            new ClassicCatalogEntry { Code = "2009E", Name = "March 2009", Description = "Replaces the circle, Lego inspired studs with new square studs, in a short lived variant without the branding." },
            new ClassicCatalogEntry { Code = "2009M", Name = "July 2009", Description = "Contains the familiar square studs used for the next couple of years. Adds 31 new brick colors and a new default sky." },
            new ClassicCatalogEntry { Code = "2009L", Name = "October 2009", Description = "GUIs were introduced in a barebones state. Badges have been added, allowing players to collect badges on their profile." },
            new ClassicCatalogEntry { Code = "2010E", Name = "February 2010", Description = "The Arial font replaced the original Comic Sans font. A new Lua health bar replaced the previous one." },
            new ClassicCatalogEntry { Code = "2010M", Name = "July 2010", Description = "Packages have been added. That is all." },
            new ClassicCatalogEntry { Code = "2010L", Name = "December 2010", Description = "A user interface overhaul powered by Lua. Data persistence has been introduced, allowing games to save and load player data." },
            new ClassicCatalogEntry { Code = "2011E", Name = "April 2011", Description = "Added the ability to record videos, an experimental Lua player list, and the menu with an early form of shift lock." },
            new ClassicCatalogEntry { Code = "2011M", Name = "July 2011", Description = "Introduces the new player list and backpack, powered by Lua. Also overhauls the previous menu." },
            new ClassicCatalogEntry { Code = "2011L", Name = "October 2011", Description = "Introduces voxel terrain, allowing developers to place and manipulate millions of terrain voxels." },
            new ClassicCatalogEntry { Code = "2012E", Name = "March 2012", Description = "Removed the clicking noise that would have played when zooming your camera." },
            new ClassicCatalogEntry { Code = "2012M", Name = "August 2012", Description = "Water has been added to the available terrain materials." },
            new ClassicCatalogEntry { Code = "2012L", Name = "October 2012", Description = "They moved the shift lock button. For this version only." },
            new ClassicCatalogEntry { Code = "2013E", Name = "March 2013", Description = "Introduces an overhaul to the player list, backpack and chat." },
            new ClassicCatalogEntry { Code = "2013M", Name = "August 2013", Description = "Introduces dynamic lighting. Bevels are removed on all parts but the player characters. Jump delay is removed." },
            new ClassicCatalogEntry { Code = "2013L", Name = "December 2013", Description = "Materials are replaced with more high quality counterparts. A new sky and animation system are introduced." },
        };

        private static ClassicManifest? _manifestCache;
        private static long _manifestCacheTicks;
        private static readonly SemaphoreSlim ManifestLock = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim MutationLock = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim ReleaseLock = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, (ReleaseAssetIntegrity Asset, long Ticks)> ReleaseCache = new(StringComparer.OrdinalIgnoreCase);
        private const long MaxArchiveBytes = 2147483648L;
        private const int MaxReleaseMetadataBytes = 4194304;

		private const int MaxLaunchArgumentCharacters = 32768;

		private const int MaxLaunchArgumentCount = 256;

        public static async Task<ClassicManifest?> FetchManifestAsync(CancellationToken ct, bool force = false)
        {
            if (!force && _manifestCache != null && DateTime.UtcNow.Ticks - _manifestCacheTicks < TimeSpan.FromMinutes(5).Ticks)
                return _manifestCache;
            await ManifestLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!force && _manifestCache != null && DateTime.UtcNow.Ticks - _manifestCacheTicks < TimeSpan.FromMinutes(5).Ticks)
                    return _manifestCache;
                foreach (string url in BuildCandidateUrls("manifest.json"))
                {
                    try
                    {
                        string json = await Voidstrap.Utility.Http.GetStringBoundedAsync(Http, url, ct, MaxReleaseMetadataBytes).ConfigureAwait(false);
                        using JsonDocument doc = JsonDocument.Parse(json);
                        JsonElement root = doc.RootElement;
                        var manifest = new ClassicManifest();
                        if (root.TryGetProperty("engineSize", out JsonElement es) && es.TryGetInt64(out long esv))
                            manifest.EngineSize = esv;
                        if (root.TryGetProperty("mapsSize", out JsonElement ms) && ms.TryGetInt64(out long msv))
                            manifest.MapsSize = msv;
                        if (root.TryGetProperty("clients", out JsonElement clients) && clients.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement c in clients.EnumerateArray())
                            {
                                if (!c.TryGetProperty("code", out JsonElement codeEl))
                                    continue;
                                string code = codeEl.GetString();
                                if (string.IsNullOrWhiteSpace(code) || !IsSupportedClientCode(code))
                                    continue;
                                long size = (c.TryGetProperty("size", out JsonElement sz) && sz.TryGetInt64(out long szv)) ? szv : 0L;
                                manifest.ClientSizes[code] = size;
                                manifest.Clients.Add(new ClassicCatalogEntry
                                {
                                    Code = code,
                                    Name = (c.TryGetProperty("name", out JsonElement n) ? n.GetString() : null) ?? code,
                                    Description = (c.TryGetProperty("description", out JsonElement d) ? d.GetString() : null) ?? "",
                                    HasStudio = c.TryGetProperty("hasStudio", out JsonElement h) && h.ValueKind == JsonValueKind.True
                                });
                            }
                        }
                        if (manifest.Clients.Count > 0)
                        {
                            _manifestCache = manifest;
                            _manifestCacheTicks = DateTime.UtcNow.Ticks;
                            return manifest;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                    }
                }
                _manifestCache = new ClassicManifest
                {
                    Clients = Catalog.Select(entry => new ClassicCatalogEntry
                    {
                        Code = entry.Code,
                        Name = entry.Name,
                        Description = entry.Description,
                        HasStudio = entry.HasStudio
                    }).ToList()
                };
                _manifestCacheTicks = DateTime.UtcNow.Ticks;
                App.Logger?.WriteLine("ClassicClients::FetchManifest", "Remote manifest unavailable, using built in catalog");
                return _manifestCache;
            }
            finally
            {
                ManifestLock.Release();
            }
        }

        public static async Task<List<ClassicCatalogEntry>> FetchManifestClientsAsync(CancellationToken ct)
        {
            ClassicManifest? manifest = await FetchManifestAsync(ct).ConfigureAwait(false);
            return manifest?.Clients ?? new List<ClassicCatalogEntry>();
        }

        private static long ManifestSizeFor(ClassicManifest? manifest, string keyOrClient)
        {
            if (manifest == null)
                return -1L;
            if (keyOrClient == EngineKey)
                return manifest.EngineSize > 0 ? manifest.EngineSize : -1L;
            if (keyOrClient == MapsKey)
                return manifest.MapsSize > 0 ? manifest.MapsSize : -1L;
            return manifest.ClientSizes.TryGetValue(keyOrClient, out long s) && s > 0 ? s : -1L;
        }

        private static async Task<long> ResolveRemoteSizeAsync(string relativePath, string keyOrClient, CancellationToken ct)
        {
            ClassicManifest? manifest = await FetchManifestAsync(ct).ConfigureAwait(false);
            long fromManifest = ManifestSizeFor(manifest, keyOrClient);
            if (fromManifest > 0)
                return fromManifest;
            return await GetRemoteSizeAsync(relativePath, ct).ConfigureAwait(false);
        }

        public static string BaseUrl
        {
            get
            {
                string url = App.Settings.Prop.ClassicDownloadBaseUrl;
                if (string.IsNullOrWhiteSpace(url) || string.Equals(url, LegacyDefaultBaseUrl, StringComparison.OrdinalIgnoreCase))
                    url = DefaultBaseUrl;
				if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? source) || source.Scheme != Uri.UriSchemeHttps)
					url = DefaultBaseUrl;
                return url.EndsWith("/") ? url : url + "/";
            }
        }

        private static async Task<ReleaseAssetIntegrity> ResolveReleaseAssetAsync(string relativePath, CancellationToken ct)
        {
            string cacheKey = BaseUrl + "|" + relativePath;
            await ReleaseLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ReleaseCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow.Ticks - cached.Ticks < TimeSpan.FromMinutes(5).Ticks)
                    return cached.Asset;

                if (!TryGetGitHubRelease(BaseUrl, out string owner, out string repo, out string? tag))
                    throw new InvalidOperationException("Classic archives must come from a GitHub release with verified digests");

                string endpoint = tag == null
                    ? $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest"
                    : $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/tags/{Uri.EscapeDataString(tag)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long metadataLength && metadataLength > MaxReleaseMetadataBytes)
                    throw new InvalidDataException("The classic release metadata is too large");
                byte[] metadata = await ReadBoundedAsync(response.Content, MaxReleaseMetadataBytes, ct).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(metadata);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
                    throw new InvalidDataException("The classic release is still a draft");
                if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The classic release has no assets");

                string assetName = relativePath.Split('/').Last();
                foreach (JsonElement assetElement in assets.EnumerateArray())
                {
                    if (!assetElement.TryGetProperty("name", out JsonElement nameElement) || !string.Equals(nameElement.GetString(), assetName, StringComparison.Ordinal))
                        continue;
                    if (!assetElement.TryGetProperty("state", out JsonElement stateElement) || !string.Equals(stateElement.GetString(), "uploaded", StringComparison.Ordinal))
                        throw new InvalidDataException("The classic release asset is not ready");
                    if (!assetElement.TryGetProperty("size", out JsonElement sizeElement) || !sizeElement.TryGetInt64(out long size) || size <= 0 || size > MaxArchiveBytes)
                        throw new InvalidDataException("The classic release asset size is invalid");
                    if (!assetElement.TryGetProperty("digest", out JsonElement digestElement))
                        throw new InvalidDataException("The classic release asset has no digest");
                    string digest = digestElement.GetString() ?? "";
                    if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The classic release asset digest is invalid");
                    string sha256 = digest.Substring(7);
                    if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
                        throw new InvalidDataException("The classic release asset digest is invalid");
                    if (!assetElement.TryGetProperty("browser_download_url", out JsonElement urlElement) ||
                        !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out Uri? downloadUri) ||
                        downloadUri.Scheme != Uri.UriSchemeHttps ||
                        !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The classic release asset URL is invalid");

                    var resolved = new ReleaseAssetIntegrity
                    {
                        Url = downloadUri.AbsoluteUri,
                        Size = size,
                        Sha256 = sha256.ToUpperInvariant()
                    };
                    ReleaseCache[cacheKey] = (resolved, DateTime.UtcNow.Ticks);
                    return resolved;
                }

                throw new FileNotFoundException("The requested classic release asset is missing", assetName);
            }
            finally
            {
                ReleaseLock.Release();
            }
        }

        private static bool TryGetGitHubRelease(string baseUrl, out string owner, out string repo, out string? tag)
        {
            owner = "";
            repo = "";
            tag = null;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return false;
            string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3 || !string.Equals(segments[2], "releases", StringComparison.OrdinalIgnoreCase))
                return false;
            owner = segments[0];
            repo = segments[1];
            if (segments.Length == 3)
                return true;
            if (segments.Length == 4 && string.Equals(segments[3], "latest", StringComparison.OrdinalIgnoreCase))
                return true;
            if (segments.Length == 5 && string.Equals(segments[3], "tag", StringComparison.OrdinalIgnoreCase))
            {
                tag = Uri.UnescapeDataString(segments[4]);
                return !string.IsNullOrWhiteSpace(tag);
            }
            return false;
        }

        private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken ct)
        {
            using Stream input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            int capacity = content.Headers.ContentLength is long length ? (int)Math.Min(length, maxBytes) : 0;
            using var output = new MemoryStream(capacity);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes + 1 - (int)output.Length)), ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (output.Length > maxBytes)
                    throw new InvalidDataException("The response is too large");
            }
            return output.ToArray();
        }

        public static string Root
        {
            get
            {
                string custom = App.Settings.Prop.ClassicInstallLocation;
                return string.IsNullOrWhiteSpace(custom) ? Paths.RobloxClients : custom;
            }
        }

        public static string ClientsDir => Path.Combine(Root, "data", "clients");

        public static string MapsDir => Path.Combine(Root, "maps");

        public static string WebServerPath => Path.Combine(Root, WebServerExe);

        public static string UserAppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoidstrapClientData");

        public static string WebServerUserAppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoidstrapClient");

        public static string UserSettingsPath => Path.Combine(UserAppData, "UserSettings.json");

        public static bool EngineInstalled => File.Exists(WebServerPath);

        public static bool IsValidSource(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return false;
            return File.Exists(Path.Combine(dir, WebServerExe)) && Directory.Exists(Path.Combine(dir, "data", "clients"));
        }

        public static bool IsSupportedClientCode(string? code)
        {
			if (string.IsNullOrWhiteSpace(code) || code.Length > 64 || !Regex.IsMatch(code, @"^\d{4}[A-Za-z0-9](?:-[A-Za-z0-9]+)?$", RegexOptions.CultureInvariant))
				return false;
			return int.TryParse(code.AsSpan(0, 4), out int year) && year is >= 2006 and < 2014;
        }

        public static List<string> ListInstalledClients()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists(ClientsDir))
                {
                    foreach (string dir in Directory.GetDirectories(ClientsDir))
                    {
                        string name = Path.GetFileName(dir);
                        if (File.Exists(Path.Combine(dir, ConfigFileName)) && IsSupportedClientCode(name))
                            list.Add(name);
                    }
                }
            }
            catch
            {
            }
            list.Sort(CompareClientNames);
            return list;
        }

        public static bool IsClientInstalled(string client)
        {
			if (!IsSupportedClientCode(client))
				return false;
            return File.Exists(ResolvePathWithin(ClientsDir, client, ConfigFileName));
        }

        private static string ResolvePathWithin(string root, params string[] parts)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The ORC configuration contains an unsafe path.");
            return path;
        }

        private static void ValidateLaunchFiles(string client, ClassicClientConfig config, ClientLaunchType launchType, string? map = null)
        {
            if (!File.Exists(WebServerPath))
                throw new FileNotFoundException("The ORC local service is missing. Repair the ORC engine and try again.", WebServerPath);
            if (!File.Exists(Path.Combine(Root, "data", "PrivateKey.pem")))
                throw new FileNotFoundException("The ORC local authentication key is missing. Repair the ORC engine and try again.");

            string directoryName;
            string executableName;
            if (launchType == ClientLaunchType.Play)
            {
                directoryName = "Player";
                executableName = config.Player.ExecutableName;
            }
            else if (launchType == ClientLaunchType.Host)
            {
                directoryName = config.Server.Directory;
                executableName = config.Server.ExecutableName;
            }
            else
            {
                if (!HasStudio(config))
                    throw new InvalidOperationException("The selected ORC client does not include Studio.");
                directoryName = GetStudioDirectory(config);
                executableName = GetStudioExecutable(config);
            }

            if (string.IsNullOrWhiteSpace(directoryName) || string.IsNullOrWhiteSpace(executableName))
                throw new InvalidOperationException("The ORC client configuration is incomplete.");
            string executable = ResolvePathWithin(ClientsDir, client, directoryName, executableName);
            if (!File.Exists(executable))
                throw new FileNotFoundException("The ORC client executable is missing. Repair the selected client and try again.", executable);
            string helper = ResolvePathWithin(ClientsDir, client, directoryName, OriginalHelperName);
            if (!File.Exists(helper))
                throw new FileNotFoundException("The ORC compatibility helper is missing. Repair the selected client and try again.", helper);

            if (launchType == ClientLaunchType.Host)
            {
                string script = ResolvePathWithin(ClientsDir, client, "scripts", "host.lua");
                if (!File.Exists(script))
                    throw new FileNotFoundException("The ORC host script is missing. Repair the selected client and try again.", script);
            }
            else if (launchType == ClientLaunchType.Play)
            {
                string script = ResolvePathWithin(ClientsDir, client, "scripts", "join.lua");
                if (!File.Exists(script))
                    throw new FileNotFoundException("The ORC join script is missing. Repair the selected client and try again.", script);
            }

            if (map != null)
            {
                string mapPath = ResolvePathWithin(MapsDir, map);
                if (!File.Exists(mapPath))
                    throw new FileNotFoundException("The selected ORC map is missing. Choose another map or repair the map package.", mapPath);
            }
        }

        public static ClassicClientConfig? GetInstalledConfig(string client)
        {
            try
            {
				if (!IsSupportedClientCode(client))
					return null;
                string path = ResolvePathWithin(ClientsDir, client, ConfigFileName);
                if (!File.Exists(path))
                    return null;
				return JsonFile.Deserialize<ClassicClientConfig>(path, ConfigJsonOptions, 4194304);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("ClassicClients::GetConfig", $"Failed to parse config for {client}: {ex.Message}");
                return null;
            }
        }

        public static long GetDirectorySize(string dir)
        {
            long total = 0;
            try
            {
                if (!Directory.Exists(dir))
                    return 0;
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { }
                }
            }
            catch
            {
            }
            return total;
        }

        public static async Task InstallEngineAsync(Action<double, string> progress, CancellationToken ct)
        {
            await MutationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await InstallEngineCoreAsync(progress, ct).ConfigureAwait(false);
            }
            finally
            {
                MutationLock.Release();
            }
        }

        private static async Task InstallEngineCoreAsync(Action<double, string> progress, CancellationToken ct)
        {
            string localSource = App.Settings.Prop.ClassicSourceLocation;
            if (IsValidSource(localSource))
            {
                progress?.Invoke(0, "Copying engine from local folder");
                await StageLocalEngineAsync(localSource, progress, ct).ConfigureAwait(false);
                return;
            }
            ReleaseAssetIntegrity engine = await DownloadAndExtractAsync("engine.zip", progress, ct).ConfigureAwait(false);
            RecordInstalled(EngineKey, engine.Size, engine.Sha256);
            if (!Directory.Exists(MapsDir) || !Directory.EnumerateFileSystemEntries(MapsDir).Any())
            {
                try
                {
                    ReleaseAssetIntegrity maps = await DownloadAndExtractAsync("maps.zip", progress, ct).ConfigureAwait(false);
                    RecordInstalled(MapsKey, maps.Size, maps.Sha256);
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteLine("ClassicClients::InstallEngine", $"maps.zip skipped: {ex.Message}");
                }
            }
        }

        private const string EngineKey = "__engine";
        private const string MapsKey = "__maps";

        public static async Task AutoUpdateAllAsync(CancellationToken ct = default)
        {
            if (!EngineInstalled)
                return;

            await MutationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                const string LOG_IDENT = "ClassicClients::AutoUpdateAll";
                Action<double, string> quiet = delegate { };

                try { await FetchManifestAsync(ct, force: true).ConfigureAwait(false); }
                catch (Exception ex) { App.Logger?.WriteLine(LOG_IDENT, "Manifest refresh failed, skipping this pass: " + ex.Message); return; }

                int updated = 0;

                try
                {
                    if (await IsAssetUpdateAvailableAsync("engine.zip", EngineKey, ct).ConfigureAwait(false))
                    {
                        App.Logger?.WriteLine(LOG_IDENT, "Updating the ORC engine");
                        ReleaseAssetIntegrity engine = await DownloadAndExtractAsync("engine.zip", quiet, ct).ConfigureAwait(false);
                        RecordInstalled(EngineKey, engine.Size, engine.Sha256);
                        updated++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { App.Logger?.WriteLine(LOG_IDENT, "Engine update failed: " + ex.Message); }

                try
                {
                    if (await IsAssetUpdateAvailableAsync("maps.zip", MapsKey, ct).ConfigureAwait(false))
                    {
                        App.Logger?.WriteLine(LOG_IDENT, "Updating the ORC maps");
                        ReleaseAssetIntegrity maps = await DownloadAndExtractAsync("maps.zip", quiet, ct).ConfigureAwait(false);
                        RecordInstalled(MapsKey, maps.Size, maps.Sha256);
                        updated++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { App.Logger?.WriteLine(LOG_IDENT, "Maps update failed: " + ex.Message); }

                foreach (string installed in ListInstalledClients())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (!await IsClientUpdateAvailableAsync(installed, ct).ConfigureAwait(false))
                            continue;
                        App.Logger?.WriteLine(LOG_IDENT, "Updating classic client " + installed);
                        await InstallClientCoreAsync(installed, quiet, ct).ConfigureAwait(false);
                        updated++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { App.Logger?.WriteLine(LOG_IDENT, "Update for " + installed + " failed: " + ex.Message); }
                }

                App.Logger?.WriteLine(LOG_IDENT, updated == 0 ? "Everything is already up to date" : "Updated " + updated + " item(s)");
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                MutationLock.Release();
            }
        }

        public static async Task UpdateEverythingAsync(string client, Action<string> status, Action<double, string> progress, CancellationToken ct)
        {
            await MutationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await UpdateEverythingCoreAsync(client, status, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                MutationLock.Release();
            }
        }

        private static async Task UpdateEverythingCoreAsync(string client, Action<string> status, Action<double, string> progress, CancellationToken ct)
        {
            if (!EngineInstalled)
                return;
            status?.Invoke("Checking for updates");
            try { await FetchManifestAsync(ct, force: true).ConfigureAwait(false); } catch { }
            if (await IsAssetUpdateAvailableAsync("engine.zip", EngineKey, ct).ConfigureAwait(false))
            {
                status?.Invoke("Updating engine");
                ReleaseAssetIntegrity engine = await DownloadAndExtractAsync("engine.zip", progress, ct).ConfigureAwait(false);
                RecordInstalled(EngineKey, engine.Size, engine.Sha256);
            }
            if (await IsAssetUpdateAvailableAsync("maps.zip", MapsKey, ct).ConfigureAwait(false))
            {
                status?.Invoke("Updating maps");
                ReleaseAssetIntegrity maps = await DownloadAndExtractAsync("maps.zip", progress, ct).ConfigureAwait(false);
                RecordInstalled(MapsKey, maps.Size, maps.Sha256);
            }
            if (IsClientInstalled(client) && await IsClientUpdateAvailableAsync(client, ct).ConfigureAwait(false))
            {
                status?.Invoke("Updating client");
                await InstallClientCoreAsync(client, progress, ct).ConfigureAwait(false);
            }
        }

        public static async Task<bool> IsAssetUpdateAvailableAsync(string relativePath, string key, CancellationToken ct)
        {
            InstalledAssetRecord? installed = GetInstalledRecord(key);
            if (installed == null || installed.Size <= 0)
                return false;
            ReleaseAssetIntegrity remote = await ResolveReleaseAssetAsync(relativePath, ct).ConfigureAwait(false);
            return remote.Size != installed.Size || !string.Equals(remote.Sha256, installed.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        public static async Task InstallClientAsync(string client, Action<double, string> progress, CancellationToken ct)
        {
            await MutationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await InstallClientCoreAsync(client, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                MutationLock.Release();
            }
        }

        private static async Task InstallClientCoreAsync(string client, Action<double, string> progress, CancellationToken ct)
        {
            if (!IsSupportedClientCode(client))
                throw new ArgumentException("The classic client code is invalid", nameof(client));
            if (!EngineInstalled)
                await InstallEngineCoreAsync(progress, ct).ConfigureAwait(false);

            string localSource = App.Settings.Prop.ClassicSourceLocation;
            string localClient = IsValidSource(localSource) ? ResolvePathWithin(Path.Combine(localSource, "data", "clients"), client) : "";
            if (!string.IsNullOrEmpty(localClient) && Directory.Exists(localClient))
            {
                progress?.Invoke(0, "Copying client from local folder");
                await StageLocalClientAsync(client, localClient, progress, ct).ConfigureAwait(false);
                RecordInstalled(client, -1L, "");
                return;
            }
            ReleaseAssetIntegrity archive = await DownloadAndExtractAsync("clients/" + client + ".zip", progress, ct).ConfigureAwait(false);
            RecordInstalled(client, archive.Size, archive.Sha256);
        }

        private const string OriginalHelperName = "OnlyRetroRobloxHereClientHelper.dll";
        private const string RenamedHelperName = "VoidstrapClientHelper.dll";

        public static void NormalizeClientHelpers(string clientDir)
        {
            try
            {
                if (!Directory.Exists(clientDir))
                    return;
                foreach (string sub in Directory.GetDirectories(clientDir))
                {
                    string renamed = Path.Combine(sub, RenamedHelperName);
                    if (File.Exists(renamed))
                    {
                        foreach (string exe in Directory.GetFiles(sub, "*.exe"))
                            RestoreHelperImport(exe);
                        string moveTarget = Path.Combine(sub, OriginalHelperName);
                        if (File.Exists(moveTarget))
                        {
                            try { File.Delete(renamed); } catch { }
                        }
                        else
                        {
                            try { File.Move(renamed, moveTarget); } catch { }
                        }
                    }
                    string helper = Path.Combine(sub, OriginalHelperName);
                    if (File.Exists(helper))
                        FixHelperFolderString(helper);
                }
            }
            catch
            {
            }
        }

        private static void FixHelperFolderString(string helperPath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(helperPath);
                byte[] broken = Encoding.ASCII.GetBytes("VoidstrapClient\0\0\0\0");
                byte[] fixedBytes = Encoding.ASCII.GetBytes("VoidstrapClientData");
                int index = IndexOfBytes(data, broken, 0);
                if (index < 0)
                    return;
                Array.Copy(fixedBytes, 0, data, index, fixedBytes.Length);
                File.WriteAllBytes(helperPath, data);
            }
            catch
            {
            }
        }

        private static void RestoreHelperImport(string exePath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(exePath);
                if (data.Length < 2 || data[0] != (byte)'M' || data[1] != (byte)'Z')
                    return;
                byte[] oldName = Encoding.ASCII.GetBytes(RenamedHelperName);
                byte[] newName = Encoding.ASCII.GetBytes(OriginalHelperName);
                int index = IndexOfBytes(data, oldName, 0);
                if (index < 0)
                    return;
                if (index + newName.Length + 1 > data.Length)
                    return;
                for (int i = oldName.Length; i < newName.Length; i++)
                {
                    if (data[index + i] != 0)
                        return;
                }
                Array.Copy(newName, 0, data, index, newName.Length);
                data[index + newName.Length] = 0;
                File.WriteAllBytes(exePath, data);
            }
            catch
            {
            }
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle, int start)
        {
            for (int i = start; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }

        private static List<string> BuildCandidateUrls(string relativePath)
        {
            static string EscPath(string p) => string.Join("/", p.Split('/').Select(Uri.EscapeDataString));
            string flat = relativePath.Contains('/') ? relativePath.Substring(relativePath.LastIndexOf('/') + 1) : relativePath;

            string baseUrl = BaseUrl;
            var raw = Regex.Match(baseUrl, @"github\.com/([^/]+)/([^/]+)/raw/([^/]+)/", RegexOptions.IgnoreCase);
            var rel = Regex.Match(baseUrl, @"github\.com/([^/]+)/([^/]+)/releases/(?:tag/([^/]+)/)?", RegexOptions.IgnoreCase);

            string owner = null, repo = null, branch = "main", tag = null;
            if (raw.Success) { owner = raw.Groups[1].Value; repo = raw.Groups[2].Value; branch = raw.Groups[3].Value; }
            else if (rel.Success) { owner = rel.Groups[1].Value; repo = rel.Groups[2].Value; tag = rel.Groups[3].Success ? rel.Groups[3].Value : null; }

            var urls = new List<string>();
            if (owner != null)
            {
                if (!string.IsNullOrEmpty(tag))
                    urls.Add($"https://github.com/{owner}/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(flat)}");
                urls.Add($"https://github.com/{owner}/{repo}/releases/latest/download/{Uri.EscapeDataString(flat)}");
                urls.Add($"https://github.com/{owner}/{repo}/raw/{branch}/{EscPath(relativePath)}");
                urls.Add($"https://cdn.jsdelivr.net/gh/{owner}/{repo}@{branch}/{EscPath(relativePath)}");
            }
            else
            {
                urls.Add(baseUrl + EscPath(relativePath));
                if (!baseUrl.Contains("/releases/", StringComparison.OrdinalIgnoreCase))
                    urls.Add(baseUrl + Uri.EscapeDataString(flat));
            }
            return urls.Distinct().ToList();
        }

        private static async Task StageLocalEngineAsync(string localSource, Action<double, string> progress, CancellationToken ct)
        {
            string stagedRoot = CreateSiblingPath(Root, "staging");
            try
            {
                await Task.Run(() =>
                {
                    if (Directory.Exists(Root))
                        CopyTree(Root, stagedRoot, null, null, ct);
                    CopyTree(localSource, stagedRoot, Path.Combine(localSource, "data", "clients"), progress, ct);
                }, ct).ConfigureAwait(false);
                ValidateEngine(stagedRoot);
                CommitDirectory(stagedRoot, Root);
                progress?.Invoke(100, "Done");
            }
            finally
            {
                DeleteDirectoryIfExists(stagedRoot);
            }
        }

        private static async Task StageLocalClientAsync(string client, string localClient, Action<double, string> progress, CancellationToken ct)
        {
            string target = ResolvePathWithin(ClientsDir, client);
            string stagedClient = CreateSiblingPath(target, "staging");
            try
            {
                await Task.Run(() => CopyTree(localClient, stagedClient, null, progress, ct), ct).ConfigureAwait(false);
                NormalizeClientHelpers(stagedClient);
                ValidateClient(stagedClient);
                CommitDirectory(stagedClient, target);
                progress?.Invoke(100, "Done");
            }
            finally
            {
                DeleteDirectoryIfExists(stagedClient);
            }
        }

        private static async Task<ReleaseAssetIntegrity> DownloadAndExtractAsync(string relativePath, Action<double, string> progress, CancellationToken ct)
        {
            ReleaseAssetIntegrity asset = await ResolveReleaseAssetAsync(relativePath, ct).ConfigureAwait(false);
            string tempZip = Path.Combine(Path.GetTempPath(), "voidstrap_client_" + Guid.NewGuid().ToString("N") + ".zip");
            string stagedRoot = CreateSiblingPath(Root, "archive");
            try
            {
                await DownloadOneAsync(asset, tempZip, progress, ct).ConfigureAwait(false);
                progress?.Invoke(96, "Extracting");
                if (string.Equals(relativePath, "engine.zip", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Root))
                    await Task.Run(() => CopyTree(Root, stagedRoot, null, null, ct), ct).ConfigureAwait(false);
                await Task.Run(() => SafeZipExtractor.ExtractToDirectory(tempZip, stagedRoot, overwrite: true, maxExpandedBytes: 4294967296L), ct).ConfigureAwait(false);

                if (string.Equals(relativePath, "engine.zip", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateEngine(stagedRoot);
                    CommitDirectory(stagedRoot, Root);
                }
                else if (string.Equals(relativePath, "maps.zip", StringComparison.OrdinalIgnoreCase))
                {
                    string stagedMaps = ResolvePathWithin(stagedRoot, "maps");
                    ValidateMaps(stagedMaps);
                    CommitDirectory(stagedMaps, MapsDir);
                }
                else if (relativePath.StartsWith("clients/", StringComparison.OrdinalIgnoreCase) && relativePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    string client = Path.GetFileNameWithoutExtension(relativePath.Split('/').Last());
                    if (!IsSupportedClientCode(client))
                        throw new InvalidDataException("The classic client archive name is invalid");
                    string stagedClient = ResolvePathWithin(stagedRoot, "data", "clients", client);
                    NormalizeClientHelpers(stagedClient);
                    ValidateClient(stagedClient);
                    CommitDirectory(stagedClient, ResolvePathWithin(ClientsDir, client));
                }
                else
                {
                    throw new InvalidDataException("The classic archive type is invalid");
                }

                progress?.Invoke(100, "Done");
                return asset;
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                DeleteDirectoryIfExists(stagedRoot);
            }
        }

        private static async Task DownloadOneAsync(ReleaseAssetIntegrity asset, string tempZip, Action<double, string> progress, CancellationToken ct)
        {
            progress?.Invoke(0, "Connecting");
            using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != asset.Size)
                throw new InvalidDataException("The classic archive size does not match its release metadata");
            using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(tempZip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                read = checked(read + n);
                if (read > asset.Size || read > MaxArchiveBytes)
                    throw new InvalidDataException("The classic archive exceeds its release size");
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, n);
                progress?.Invoke(read / (double)asset.Size * 95.0, $"Downloading {FormatBytes(read)} of {FormatBytes(asset.Size)}");
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
            if (read != asset.Size)
                throw new InvalidDataException("The classic archive ended before its release size");
            byte[] expected = Convert.FromHexString(asset.Sha256);
            byte[] actual = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new CryptographicException("The classic archive digest does not match its release metadata");
        }

        private static string CreateSiblingPath(string target, string purpose)
        {
            string fullTarget = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(fullTarget);
            string name = Path.GetFileName(fullTarget);
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("The classic install location is invalid");
            Directory.CreateDirectory(parent);
            return Path.Combine(parent, "." + name + "." + purpose + "." + Guid.NewGuid().ToString("N"));
        }

        private static void ValidateEngine(string root)
        {
            if (!File.Exists(Path.Combine(root, WebServerExe)) || !File.Exists(Path.Combine(root, "data", "PrivateKey.pem")))
                throw new InvalidDataException("The classic engine archive is incomplete");
        }

        private static void ValidateMaps(string maps)
        {
            if (!Directory.Exists(maps) || !Directory.EnumerateFiles(maps, "*", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("The classic maps archive is empty");
        }

        private static void ValidateClient(string clientDirectory)
        {
            if (!File.Exists(Path.Combine(clientDirectory, ConfigFileName)))
                throw new InvalidDataException("The classic client archive is incomplete");
        }

        private static void CommitDirectory(string staged, string target)
        {
            string fullStaged = Path.GetFullPath(staged);
            string fullTarget = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(fullStaged))
                throw new DirectoryNotFoundException(fullStaged);
            string backup = CreateSiblingPath(fullTarget, "backup");
            bool movedTarget = false;
            try
            {
                if (Directory.Exists(fullTarget))
                {
                    Directory.Move(fullTarget, backup);
                    movedTarget = true;
                }
                Directory.Move(fullStaged, fullTarget);
            }
            catch
            {
                DeleteDirectoryIfExists(fullTarget);
                if (movedTarget && Directory.Exists(backup))
                    Directory.Move(backup, fullTarget);
                throw;
            }
            DeleteDirectoryIfExists(backup);
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static void CopyTree(string source, string target, string? excludeDir, Action<double, string>? progress, CancellationToken ct)
        {
            Directory.CreateDirectory(target);
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Where(f => excludeDir == null || !f.StartsWith(excludeDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToList();
            int done = 0;
            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(source, file);
                string dest = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, true);
                done++;
                if (files.Count > 0)
                    progress?.Invoke(done / (double)files.Count * 100.0, $"Copying files: {done} of {files.Count}");
            }
        }

        public static async Task UninstallClientAsync(string client, CancellationToken ct = default)
        {
            await MutationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!IsSupportedClientCode(client))
                    throw new ArgumentException("The classic client code is invalid", nameof(client));
                string dir = ResolvePathWithin(ClientsDir, client);
                if (Directory.Exists(dir))
                    await Task.Run(() => Directory.Delete(dir, true), ct).ConfigureAwait(false);
                var index = LoadIndex();
                if (index.Remove(client))
                    SaveIndex(index);
            }
            finally
            {
                MutationLock.Release();
            }
        }

        private static string InstalledIndexPath => Path.Combine(Root, "installed.json");

        private static Dictionary<string, InstalledAssetRecord> LoadIndex()
        {
            try
            {
                if (File.Exists(InstalledIndexPath))
                {
                    using JsonDocument document = JsonDocument.Parse(JsonFile.ReadText(InstalledIndexPath, 4194304));
                    var result = new Dictionary<string, InstalledAssetRecord>(StringComparer.OrdinalIgnoreCase);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        return result;
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (property.Value.TryGetInt64(out long legacySize))
                        {
                            result[property.Name] = new InstalledAssetRecord { Size = legacySize };
                            continue;
                        }
                        if (property.Value.ValueKind != JsonValueKind.Object)
                            continue;
                        long size = property.Value.TryGetProperty("Size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsedSize) ? parsedSize : -1L;
                        string sha256 = property.Value.TryGetProperty("Sha256", out JsonElement digestElement) ? digestElement.GetString() ?? "" : "";
                        result[property.Name] = new InstalledAssetRecord { Size = size, Sha256 = sha256 };
                    }
                    return result;
                }
            }
            catch
            {
            }
            return new Dictionary<string, InstalledAssetRecord>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveIndex(Dictionary<string, InstalledAssetRecord> index)
        {
            try
            {
                Directory.CreateDirectory(Root);
				JsonFile.SerializeAtomic(InstalledIndexPath, index);
            }
            catch
            {
            }
        }

        public static long GetInstalledSize(string client)
        {
            return LoadIndex().TryGetValue(client, out InstalledAssetRecord? record) ? record.Size : -1L;
        }

        private static InstalledAssetRecord? GetInstalledRecord(string client)
        {
            return LoadIndex().TryGetValue(client, out InstalledAssetRecord? record) ? record : null;
        }

        private static void RecordInstalled(string client, long size, string sha256)
        {
            var index = LoadIndex();
            index[client] = new InstalledAssetRecord { Size = size, Sha256 = sha256 };
            SaveIndex(index);
        }

        public static Task<long> GetRemoteClientSizeAsync(string client, CancellationToken ct)
        {
            return GetRemoteSizeAsync("clients/" + client + ".zip", ct);
        }

        public static async Task<long> GetRemoteSizeAsync(string relativePath, CancellationToken ct)
        {
            try
            {
                return (await ResolveReleaseAssetAsync(relativePath, ct).ConfigureAwait(false)).Size;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
            foreach (string url in BuildCandidateUrls(relativePath))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Head, url);
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength.HasValue)
                        return response.Content.Headers.ContentLength.Value;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
            }
            return -1L;
        }

        public static async Task<bool> IsClientUpdateAvailableAsync(string client, CancellationToken ct)
        {
            if (!IsClientInstalled(client))
                return false;
            InstalledAssetRecord? installed = GetInstalledRecord(client);
            if (installed == null || installed.Size <= 0)
                return false;
            ReleaseAssetIntegrity remote = await ResolveReleaseAssetAsync("clients/" + client + ".zip", ct).ConfigureAwait(false);
            return remote.Size != installed.Size || !string.Equals(remote.Sha256, installed.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        public static List<string> ListMaps()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists(MapsDir))
                {
                    foreach (string file in Directory.EnumerateFiles(MapsDir, "*", SearchOption.AllDirectories))
                    {
                        if (file.EndsWith(".rbxl", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".rbxl.gz", StringComparison.OrdinalIgnoreCase))
                            list.Add(Path.GetRelativePath(MapsDir, file));
                    }
                }
            }
            catch
            {
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private static string? _robloxUsername;
        private static long _robloxUserId;

        public static async Task ResolveRobloxUserAsync(CancellationToken ct)
        {
            try
            {
                var account = await Voidstrap.Integrations.RobloxCookie.GetAccountAsync(ct).ConfigureAwait(false);
                if (account != null && !string.IsNullOrWhiteSpace(account.Username))
                {
                    _robloxUsername = account.Username;
                    _robloxUserId = account.UserId;
                }
            }
            catch
            {
            }
        }

        public static void WriteUserSettings(string client, string map)
        {
            Directory.CreateDirectory(UserAppData);

            int playerId = 0;
            string playerName = "";
            try
            {
                if (File.Exists(UserSettingsPath) && JsonNode.Parse(JsonFile.ReadText(UserSettingsPath, 4194304)) is JsonObject existing && existing["Player"] is JsonObject existingPlayer)
                {
                    playerId = existingPlayer["Id"]?.GetValue<int>() ?? 0;
                    playerName = existingPlayer["Name"]?.GetValue<string>() ?? "";
                }
            }
            catch
            {
            }
            if (playerId <= 0)
                playerId = Random.Shared.Next(1, 10000000);
            if (!string.IsNullOrWhiteSpace(_robloxUsername))
                playerName = _robloxUsername;
            if (string.IsNullOrWhiteSpace(playerName))
                playerName = "Player";

            var launch = new JsonObject
            {
                ["SelectedMap"] = string.IsNullOrEmpty(map) ? null : JsonValue.Create(map),
                ["SelectedClient"] = client,
                ["PreferredStudioType"] = "MFC",
                ["BootstrapperLaunchType"] = 2,
                ["AutoSaveEnabled"] = true,
                ["ExperimentalPlayerlistEnabled"] = false,
                ["CustomGraphicsApi"] = 0,
                ["EggHuntMode"] = false,
                ["AutoSaveDebug"] = false,
                ["AutoSaveChangesPerPlayer"] = 100,
                ["AutoSaveCheckInterval"] = 1800,
                ["AutoSaveCooldown"] = 900,
                ["ClientLargerPlayerListNameLength"] = true,
                ["ClientLegacyUIDarkMode"] = false,
                ["ClientHigherQualityScreenshots"] = true,
                ["DebugShowWebServerWindow"] = false,
                ["CustomInGameApisEnabled"] = false,
                ["FPSCap"] = 60,
                ["FPSCapOlder"] = 30,
                ["TTSEnabled"] = false,
                ["TTSType"] = "SAPI5",
                ["TTSVoice"] = "",
                ["CustomMapsDirectory"] = "",
                ["DisplayPort"] = -1,
                ["DisplayIP"] = "",
                ["SelectedMembershipButton"] = "None",
                ["MembershipDebt"] = 0.0,
                ["ServerNo3D"] = true,
                ["OptimisedClientStartup"] = true,
                ["RemoveUploadDialogs"] = true,
                ["DisabledAssetPacks"] = new JsonArray()
            };

            var root = new JsonObject
            {
                ["Launch"] = launch,
                ["Player"] = new JsonObject
                {
                    ["Id"] = playerId,
                    ["Name"] = playerName,
                    ["Membership"] = 0
                },
                ["Character"] = new JsonObject
                {
                    ["Head"] = 24,
                    ["Torso"] = 23,
                    ["RightArm"] = 24,
                    ["LeftArm"] = 24,
                    ["RightLeg"] = 119,
                    ["LeftLeg"] = 119,
                    ["Equipped"] = new JsonObject(),
                    ["FigureCharacterType"] = 1
                },
                ["Inventory"] = new JsonArray(),
                ["OutfitPreferences"] = new JsonObject()
            };

            string json = root.ToJsonString();
            WriteSettingsFile(UserAppData, json);
            WriteSettingsFile(WebServerUserAppData, json);
        }

        private static void WriteSettingsFile(string folder, string json)
        {
            try
            {
                Directory.CreateDirectory(folder);
				JsonFile.WriteAtomicText(Path.Combine(folder, "UserSettings.json"), json, false);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("ClassicClients::WriteUserSettings", "Failed writing settings to " + folder + ": " + ex.Message);
            }
        }

        private static string BuildQueryString(params (string Name, object Value)[] values)
        {
            NameValueCollection query = HttpUtility.ParseQueryString(string.Empty);
            foreach (var (name, value) in values)
                query.Add(name, value.ToString());
            return query.ToString() ?? "";
        }

        private static string ResolveHost(string domain)
        {
			if (string.IsNullOrWhiteSpace(domain))
				return "www.roblox.com";
			string normalized = domain.Trim().TrimEnd('.');
			if (normalized.Length > 253 || Uri.CheckHostName(normalized) != UriHostNameType.Dns)
				throw new InvalidDataException("The ORC client domain is invalid");
			return normalized.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? normalized : "www." + normalized;
        }

        public static string CreateJoinUrl(string ip, ushort port, string domain = "")
        {
            string userName = "Player";
            int userId = 1;
            try
            {
				if (File.Exists(UserSettingsPath) && JsonNode.Parse(JsonFile.ReadText(UserSettingsPath, 4194304), documentOptions: new JsonDocumentOptions
				{
					AllowTrailingCommas = true,
					CommentHandling = JsonCommentHandling.Skip
				}) is JsonObject root && root["Player"] is JsonObject player)
                {
                    userName = player["Name"]?.GetValue<string>() ?? userName;
                    userId = player["Id"]?.GetValue<int>() ?? userId;
                }
            }
            catch
            {
            }
            string query = BuildQueryString(("UserName", userName), ("serverIP", ip), ("serverPort", port), ("UserID", userId), ("chatStyle", "ClassicAndBubble"), ("testMode", false));
            return "http://" + ResolveHost(domain) + "/game/join.ashx?" + query;
        }

        public static string CreateHostUrl(ushort port, string domain = "")
        {
            return "http://" + ResolveHost(domain) + "/game/host.ashx?" + BuildQueryString(("serverPort", port));
        }

        private static string GetStudioDirectory(ClassicClientConfig config)
        {
            return config.Studio.Type switch
            {
                "App" => "Player",
                "MFC" or "Qt" => "Studio",
                "MFCnQt" => "MFCStudio",
                _ => "Player"
            };
        }

        private static string GetStudioExecutable(ClassicClientConfig config)
        {
            return config.Studio.Type switch
            {
                "MFCnQt" => config.Studio.MFCExecutableName,
                _ => config.Studio.ExecutableName
            };
        }

        public static bool HasStudio(ClassicClientConfig config)
        {
            return config.Studio.Type != "None" && !string.IsNullOrEmpty(config.Studio.Type);
        }

        public static Process? Launch(string client, ClientLaunchType launchType, ushort port = DefaultPort, string ip = "localhost", Action<string>? status = null)
        {
            ClassicClientConfig? config = GetInstalledConfig(client);
            if (config == null)
                throw new InvalidOperationException("The client config for " + client + " is missing.");

            ValidateLaunchFiles(client, config, launchType);

            Voidstrap.Integrations.AssetProxy.AssetProxyServer.DisableForUnsupportedClient();
            Voidstrap.Integrations.Studio.StudioPluginInstaller.StashForClassicClient();

            string? redirectFailure = Voidstrap.Integrations.ClassicHostRedirect.Set(enable: true);
            if (redirectFailure != null)
            {
                App.Logger?.WriteLine("ClassicClients::Launch", "Host redirect failed: " + redirectFailure);
                throw new InvalidOperationException(redirectFailure);
            }

            string? serviceFailure = EnsureWebServerRunning(client, status);
            if (serviceFailure != null)
            {
                App.Logger?.WriteLine("ClassicClients::Launch", "Local service failed: " + serviceFailure);
                throw new InvalidOperationException(serviceFailure);
            }

            string directoryName;
            string executableName;
            string argumentsFormat;
            string extraArgument;
            string url;

            switch (launchType)
            {
                case ClientLaunchType.Play:
                    directoryName = "Player";
                    executableName = config.Player.ExecutableName;
                    argumentsFormat = config.Player.LaunchArguments;
                    extraArgument = config.Player.Sensitive ? "" : "-ip";
                    url = CreateJoinUrl(ip, port, config.Domain);
                    break;
                case ClientLaunchType.Studio:
                    if (!HasStudio(config))
                        throw new InvalidOperationException("The selected client has no studio.");
                    directoryName = GetStudioDirectory(config);
                    executableName = GetStudioExecutable(config);
                    argumentsFormat = "";
                    extraArgument = config.Player.Sensitive ? "" : "-is";
                    url = "";
                    break;
                case ClientLaunchType.Host:
                    directoryName = config.Server.Directory;
                    executableName = config.Server.ExecutableName;
                    argumentsFormat = config.Server.LaunchArguments;
                    extraArgument = "-ih";
                    url = CreateHostUrl(port, config.Domain);
                    break;
                default:
                    throw new NotImplementedException(launchType.ToString());
            }

            if (executableName.Contains('\\') || executableName.Contains('/'))
                throw new InvalidOperationException("The client config failed a security check.");

            string executableDir = ResolvePathWithin(ClientsDir, client, directoryName);
            string executablePath = ResolvePathWithin(executableDir, executableName);
            if (!File.Exists(executablePath))
                throw new FileNotFoundException("Could not find the client executable: " + executablePath);

            string domainValue = string.IsNullOrWhiteSpace(config.Domain) ? "roblox.com" : config.Domain.Trim();
			if (argumentsFormat == null || argumentsFormat.Length > MaxLaunchArgumentCharacters || argumentsFormat.Any(c => c is '\r' or '\n' or '\0'))
				throw new InvalidDataException("The ORC launch arguments are invalid");
            string arguments = argumentsFormat.Replace("{domain}", domainValue).Replace("{0}", url);
            if (!string.IsNullOrEmpty(extraArgument))
                arguments = (arguments + " " + extraArgument).Trim();
			if (arguments.Length > MaxLaunchArgumentCharacters)
				throw new InvalidDataException("The ORC launch arguments are too long");

            if (launchType == ClientLaunchType.Host)
                VerifyHostScript(config.Domain);

            var process = new Process();
			process.StartInfo.UseShellExecute = false;
            process.StartInfo.FileName = executablePath;
            process.StartInfo.WorkingDirectory = executableDir;
			foreach (string argument in SplitLaunchArguments(arguments))
				process.StartInfo.ArgumentList.Add(argument);
            App.Logger?.WriteLine("ClassicClients::Launch", $"Launching {client} {launchType}: {executablePath} {arguments}");
            process.Start();
            return process;
        }

		private static IReadOnlyList<string> SplitLaunchArguments(string commandLine)
		{
			List<string> arguments = [];
			int position = 0;
			while (position < commandLine.Length)
			{
				while (position < commandLine.Length && char.IsWhiteSpace(commandLine[position]))
					position++;
				if (position >= commandLine.Length)
					break;
				StringBuilder argument = new();
				bool quoted = false;
				while (position < commandLine.Length)
				{
					int slashCount = 0;
					while (position < commandLine.Length && commandLine[position] == '\\')
					{
						slashCount++;
						position++;
					}
					if (position < commandLine.Length && commandLine[position] == '"')
					{
						argument.Append('\\', slashCount / 2);
						if ((slashCount & 1) == 0)
							quoted = !quoted;
						else
							argument.Append('"');
						position++;
						continue;
					}
					argument.Append('\\', slashCount);
					if (position >= commandLine.Length || (!quoted && char.IsWhiteSpace(commandLine[position])))
						break;
					argument.Append(commandLine[position]);
					position++;
				}
				if (quoted)
					throw new InvalidDataException("The ORC launch arguments contain an unmatched quote");
				arguments.Add(argument.ToString());
				if (arguments.Count > MaxLaunchArgumentCount)
					throw new InvalidDataException("The ORC launch arguments contain too many values");
			}
			return arguments;
		}

        public static bool IsUdpPortAvailable(int port)
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().All(e => e.Port != port);
            }
            catch
            {
                return true;
            }
        }

        public static void ShutdownAll()
        {
            try { ClassicIntegrations.Stop(killProcesses: true); }
            catch (Exception ex) { App.Logger?.WriteLine("ClassicClients::ShutdownAll", "Integration stop failed: " + ex.Message); }

            KillWebServer();
            KillClientProcesses();

            try { Voidstrap.Integrations.ClassicHostRedirect.RemoveIfStale(); }
            catch (Exception ex) { App.Logger?.WriteLine("ClassicClients::ShutdownAll", "Host redirect cleanup failed: " + ex.Message); }

            try { Voidstrap.Integrations.Studio.StudioPluginInstaller.RestoreAfterClassicClient(); }
            catch (Exception ex) { App.Logger?.WriteLine("ClassicClients::ShutdownAll", "Studio plugin restore failed: " + ex.Message); }
        }

        private static string _webServerClient = "";

        private static readonly object WebServerGate = new object();

        public static string? EnsureWebServerRunning(string client, Action<string>? status = null)
        {
            lock (WebServerGate)
            {
                return EnsureWebServerRunningCore(client, status);
            }
        }

        private static string? EnsureWebServerRunningCore(string client, Action<string>? status)
        {
            Process[] existing = Process.GetProcessesByName("VoidstrapClient.WebServer");
            int aliveCount = 0;
            try
            {
                aliveCount = existing.Count(p => !p.HasExited);
            }
            catch
            {
            }
            finally
            {
                foreach (Process p in existing)
                {
                    try { p.Dispose(); } catch { }
                }
            }

            if (aliveCount > 1)
            {
                App.Logger?.WriteLine("ClassicClients::EnsureWebServerRunning", aliveCount + " local services are running, replacing them with a single instance for " + client);
                _webServerClient = "";
                KillWebServer();
                Thread.Sleep(300);
            }
            else if (aliveCount == 1)
            {
                bool sameClient = string.Equals(_webServerClient, client, StringComparison.OrdinalIgnoreCase);
                if (sameClient && IsWebServerAnswering())
                    return null;
                App.Logger?.WriteLine("ClassicClients::EnsureWebServerRunning", sameClient
                    ? "The local service is not answering, restarting it for " + client
                    : "The local service is not serving " + client + ", restarting it");
                _webServerClient = "";
                KillWebServer();
                Thread.Sleep(300);
            }

            if (!File.Exists(WebServerPath))
            {
                App.Logger?.WriteLine("ClassicClients::EnsureWebServerRunning", "The ORC local service is missing at " + WebServerPath);
                return "The ORC local service is missing at " + WebServerPath + ". Repair the ORC engine and try again.";
            }

            string? runtimeRoot = null;
            try
            {
                runtimeRoot = EnsureWebServerRuntime(force: false, status);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("ClassicClients::EnsureWebServerRunning", "Could not prepare the .NET runtime: " + ex.Message);
            }

            string? result = LaunchWebServerOnce(client, runtimeRoot, out bool hostFailure);
            if (result == null)
                return null;
            if (!hostFailure || runtimeRoot != null)
                return result;

            try
            {
                runtimeRoot = EnsureWebServerRuntime(force: true, status);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("ClassicClients::EnsureWebServerRunning", "Automatic runtime install failed: " + ex.Message);
                return result + " Voidstrap also tried to install the runtime automatically but could not: " + ex.Message;
            }
            return LaunchWebServerOnce(client, runtimeRoot, out _);
        }

        private static string? LaunchWebServerOnce(string client, string? runtimeRoot, out bool hostFailure)
        {
            hostFailure = false;
            try
            {
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = WebServerPath,
                    WorkingDirectory = Root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                start.ArgumentList.Add("--client=" + client);
                start.ArgumentList.Add("--httpsys");
                start.ArgumentList.Add("--prefixes=" + Voidstrap.Integrations.ClassicHostRedirect.ServerPrefix);
                start.ArgumentList.Add("--pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                start.Environment["DOTNET_ROLL_FORWARD"] = "Major";
                if (runtimeRoot != null)
                {
                    string arch = Path.GetFileName(runtimeRoot).ToUpperInvariant();
                    start.Environment["DOTNET_ROOT"] = runtimeRoot;
                    start.Environment["DOTNET_ROOT_" + arch] = runtimeRoot;
                    if (arch == "X86")
                        start.Environment["DOTNET_ROOT(x86)"] = runtimeRoot;
                    App.Logger?.WriteLine("ClassicClients::EnsureWebServerRunning", "Using the private .NET runtime at " + runtimeRoot);
                }
                Process? server = Process.Start(start);
                if (server == null)
                {
                    App.Logger?.WriteLine("ClassicClients::EnsureWebServerRunning", "The ORC local service did not start");
                    return "Windows did not start the ORC local service.";
                }
                App.Logger?.WriteLine("ClassicClients::EnsureWebServerRunning", "Started the ORC local service on " + Voidstrap.Integrations.ClassicHostRedirect.ServerPrefix);
                for (int i = 0; i < 40; i++)
                {
                    if (server.HasExited)
                    {
                        App.Logger?.WriteLine("ClassicClients::EnsureWebServerRunning", "The ORC local service exited during startup with code " + server.ExitCode);
                        hostFailure = IsHostExitCode(server.ExitCode);
                        string hostReason = DescribeHostExitCode(server.ExitCode);
                        if (hostReason.Length > 0)
                            return "The ORC local service closed during startup with code " + server.ExitCode.ToString(CultureInfo.InvariantCulture) + ". " + hostReason;
                        return "The ORC local service closed during startup with code " + server.ExitCode.ToString(CultureInfo.InvariantCulture) + ". " + DescribeServiceFailure("exit code " + server.ExitCode.ToString(CultureInfo.InvariantCulture));
                    }
                    if (IsWebServerAnswering())
                    {
                        _webServerClient = client;
                        return null;
                    }
                    Thread.Sleep(250);
                }
                App.Logger?.WriteLine("ClassicClients::EnsureWebServerRunning", "The ORC local service did not answer within 10 seconds");
                return "The ORC local service did not answer within 10 seconds. " + DescribeServiceFailure("no response");
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("ClassicClients::EnsureWebServerRunning", "Could not start the ORC local service: " + ex.Message);
                return "Could not start the ORC local service: " + ex.Message;
            }
        }

        private static readonly HttpClient ProbeClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private static bool ProbeHostScript(string domain, out string detail)
        {
            try
            {
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/game/host.ashx?serverPort=" + DefaultPort.ToString(CultureInfo.InvariantCulture));
                request.Headers.Host = ResolveHost(domain);
                using HttpResponseMessage response = ProbeClient.Send(request, HttpCompletionOption.ResponseContentRead);
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode && body.Contains("loadfile(", StringComparison.Ordinal))
                {
                    detail = "";
                    return true;
                }
                string firstLine = "";
                foreach (string line in body.Split('\n'))
                {
                    if (line.Trim().Length > 0)
                    {
                        firstLine = line.Trim();
                        break;
                    }
                }
                if (firstLine.Length > 60)
                    firstLine = firstLine.Substring(0, 60) + "...";
                detail = "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                    + (firstLine.Length > 0 ? ", first line: " + firstLine : ", empty response");
                return false;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        private static void VerifyHostScript(string domain)
        {
            const string LOG_IDENT = "ClassicClients::VerifyHostScript";
            string detail = "";
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (ProbeHostScript(domain, out detail))
                {
                    App.Logger?.WriteLine(LOG_IDENT, "Host script check passed");
                    return;
                }
                Thread.Sleep(500);
            }
            App.Logger?.WriteLine(LOG_IDENT, "Host script check failed: " + detail);
            throw new InvalidOperationException("The local ORC server did not return the startup script. " + DescribeServiceFailure(detail));
        }

        private static bool IsHostExitCode(int exitCode)
        {
            uint code = unchecked((uint)exitCode);
            return code >= 0x80008081 && code <= 0x8000809F;
        }

        private static List<KeyValuePair<string, Version>> ReadWebServerFrameworks()
        {
            var frameworks = new List<KeyValuePair<string, Version>>();
            try
            {
                string configPath = Path.Combine(Root, Path.GetFileNameWithoutExtension(WebServerExe) + ".runtimeconfig.json");
                if (!File.Exists(configPath))
                    return frameworks;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!doc.RootElement.TryGetProperty("runtimeOptions", out JsonElement options))
                    return frameworks;
                void Add(JsonElement fx)
                {
                    string? name = fx.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    string? version = fx.TryGetProperty("version", out JsonElement v) ? v.GetString() : null;
                    if (!string.IsNullOrEmpty(name) && Version.TryParse(version, out Version? parsed))
                        frameworks.Add(new KeyValuePair<string, Version>(name!, parsed!));
                }
                if (options.TryGetProperty("frameworks", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement fx in list.EnumerateArray())
                        Add(fx);
                }
                else if (options.TryGetProperty("framework", out JsonElement single))
                {
                    Add(single);
                }
            }
            catch
            {
            }
            return frameworks;
        }

        private static string WebServerArch()
        {
            try
            {
                using FileStream fs = File.OpenRead(WebServerPath);
                using BinaryReader reader = new BinaryReader(fs);
                fs.Seek(0x3C, SeekOrigin.Begin);
                int peOffset = reader.ReadInt32();
                fs.Seek(peOffset + 4, SeekOrigin.Begin);
                return reader.ReadUInt16() switch
                {
                    0x014C => "x86",
                    0xAA64 => "arm64",
                    _ => "x64",
                };
            }
            catch
            {
                return "x64";
            }
        }

        private static bool RuntimeSatisfied(string dotnetRoot, List<KeyValuePair<string, Version>> frameworks)
        {
            try
            {
                foreach (KeyValuePair<string, Version> framework in frameworks)
                {
                    string sharedDir = Path.Combine(dotnetRoot, "shared", framework.Key);
                    if (!Directory.Exists(sharedDir))
                        return false;
                    bool found = false;
                    foreach (string dir in Directory.GetDirectories(sharedDir))
                    {
                        if (Version.TryParse(Path.GetFileName(dir), out Version? installed)
                            && installed!.Major == framework.Value.Major
                            && installed >= framework.Value
                            && File.Exists(Path.Combine(dir, ".version")))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        return false;
                }
                return frameworks.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string SystemDotnetRoot(string arch)
        {
            return Path.Combine(Environment.GetFolderPath(arch == "x86" ? Environment.SpecialFolder.ProgramFilesX86 : Environment.SpecialFolder.ProgramFiles), "dotnet");
        }

        private static string? EnsureWebServerRuntime(bool force, Action<string>? status)
        {
            var frameworks = ReadWebServerFrameworks();
            if (frameworks.Count == 0)
                return null;

            string arch = WebServerArch();
            string systemRoot = SystemDotnetRoot(arch);
            if (!force && RuntimeSatisfied(systemRoot, frameworks))
                return null;

            int major = frameworks.Max(f => f.Value.Major);
            App.Logger?.WriteLine("ClassicClients::EnsureWebServerRuntime", "The " + arch + " .NET " + major + " runtime is not installed, installing it now");

            if (TryInstallSystemRuntime(arch, major, status) && RuntimeSatisfied(systemRoot, frameworks))
            {
                App.Logger?.WriteLine("ClassicClients::EnsureWebServerRuntime", "The .NET " + major + " runtime is now installed for every ORC component");
                return null;
            }

            string privateRoot = Path.Combine(Root, "runtime", arch);
            if (RuntimeSatisfied(privateRoot, frameworks))
                return privateRoot;
            App.Logger?.WriteLine("ClassicClients::EnsureWebServerRuntime", "Falling back to a private runtime copy for the ORC local service");
            InstallPrivateRuntime(privateRoot, arch, major, status);
            App.Logger?.WriteLine("ClassicClients::EnsureWebServerRuntime", "Installed the private .NET runtime at " + privateRoot);
            return privateRoot;
        }

        private static (string Url, string? Hash) FindRuntimeDownload(int major, string rid, string extension)
        {
            string metadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/" + major.ToString(CultureInfo.InvariantCulture) + ".0/releases.json";
            string metadataJson;
            using (HttpResponseMessage metadataResponse = Http.GetAsync(metadataUrl).GetAwaiter().GetResult())
            {
                metadataResponse.EnsureSuccessStatusCode();
                metadataJson = metadataResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            using JsonDocument doc = JsonDocument.Parse(metadataJson);
            foreach (JsonElement release in doc.RootElement.GetProperty("releases").EnumerateArray())
            {
                if (!release.TryGetProperty("aspnetcore-runtime", out JsonElement runtime) || !runtime.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement file in files.EnumerateArray())
                {
                    string? fileRid = file.TryGetProperty("rid", out JsonElement r) ? r.GetString() : null;
                    string? url = file.TryGetProperty("url", out JsonElement u) ? u.GetString() : null;
                    if (fileRid == rid && url != null && url.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                        return (url, file.TryGetProperty("hash", out JsonElement h) ? h.GetString() : null);
                }
            }
            throw new InvalidOperationException("No ASP.NET Core " + major.ToString(CultureInfo.InvariantCulture) + " runtime download was found for " + rid);
        }

        private static void DownloadVerified(string url, string? hash, string destination, Action<string>? status)
        {
            App.Logger?.WriteLine("ClassicClients::EnsureWebServerRuntime", "Downloading " + url);
            status?.Invoke("Downloading the .NET runtime");
            using (HttpResponseMessage download = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                download.EnsureSuccessStatusCode();
                long total = download.Content.Headers.ContentLength ?? 0;
                using Stream source = download.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using FileStream target = File.Create(destination);
                byte[] buffer = new byte[81920];
                long copied = 0;
                int lastPercent = -1;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    target.Write(buffer, 0, read);
                    copied += read;
                    if (total > 0)
                    {
                        int percent = (int)(copied * 100 / total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            status?.Invoke("Downloading the .NET runtime, " + percent.ToString(CultureInfo.InvariantCulture) + "%");
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(hash))
                return;
            using FileStream verify = File.OpenRead(destination);
            string actual = Convert.ToHexString(SHA512.HashData(verify));
            if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The runtime download failed its integrity check");
        }

        private static bool TryInstallSystemRuntime(string arch, int major, Action<string>? status)
        {
            string installerPath = Path.Combine(Path.GetTempPath(), "voidstrap_aspnetcore_" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                (string url, string? hash) = FindRuntimeDownload(major, "win-" + arch, ".exe");
                DownloadVerified(url, hash, installerPath, status);

                status?.Invoke("Installing the .NET runtime, accept the Windows prompt");
                ProcessStartInfo installer = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                };
                using Process? process = Process.Start(installer);
                if (process == null)
                    return false;
                process.WaitForExit();
                int exitCode = process.ExitCode;
                App.Logger?.WriteLine("ClassicClients::EnsureWebServerRuntime", "The .NET runtime installer finished with code " + exitCode.ToString(CultureInfo.InvariantCulture));
                return exitCode == 0 || exitCode == 3010 || exitCode == 1638;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("ClassicClients::EnsureWebServerRuntime", "The .NET runtime installer could not run: " + ex.Message);
                return false;
            }
            finally
            {
                try { File.Delete(installerPath); } catch { }
            }
        }

        private static void InstallPrivateRuntime(string privateRoot, string arch, int major, Action<string>? status)
        {
            (string url, string? hash) = FindRuntimeDownload(major, "win-" + arch, ".zip");
            string zipPath = Path.Combine(Path.GetTempPath(), "voidstrap_aspnetcore_" + Guid.NewGuid().ToString("N") + ".zip");
            string staging = privateRoot + ".staging";
            try
            {
                DownloadVerified(url, hash, zipPath, status);
                status?.Invoke("Installing the .NET runtime");
                DeleteDirectoryIfExists(staging);
                SafeZipExtractor.ExtractToDirectory(zipPath, staging, overwrite: true, maxExpandedBytes: 4294967296L);
                Directory.CreateDirectory(Path.Combine(Root, "runtime"));
                CommitDirectory(staging, privateRoot);
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
                DeleteDirectoryIfExists(staging);
            }
        }

        private static string DescribeHostExitCode(int exitCode)
        {
            if (!IsHostExitCode(exitCode))
                return "";
            string required = string.Join(", ", ReadWebServerFrameworks().Select(f => f.Key + " " + f.Value));
            uint code = unchecked((uint)exitCode);
            if (code == 0x80008096)
                return "The .NET runtime the service needs is not installed" + (required.Length > 0 ? " (it needs " + required + " or a newer major version)" : "") + ". Install the matching ASP.NET Core runtime from dotnet.microsoft.com and try again.";
            return "The .NET runtime host could not start the service (host error 0x" + code.ToString("X8", CultureInfo.InvariantCulture) + (required.Length > 0 ? ", it needs " + required : "") + "). Reinstall the .NET runtime and repair the ORC engine, then try again.";
        }

        private static string DescribeServiceFailure(string detail)
        {
            if (!File.Exists(WebServerPath))
                return "The ORC local service is missing at " + WebServerPath + ". Repair the ORC engine and try again.";

            bool running = false;
            foreach (Process existing in Process.GetProcessesByName("VoidstrapClient.WebServer"))
            {
                try { running |= !existing.HasExited; }
                catch { }
                finally { try { existing.Dispose(); } catch { } }
            }

            bool portTaken = false;
            try
            {
                portTaken = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == 80);
            }
            catch
            {
            }

            if (portTaken && !running)
                return "Port 80 is already in use by another program, so the local service could not start. Close whatever is using port 80 and try again.";
            if (!Voidstrap.Integrations.ClassicHostRedirect.IsUrlAclReserved())
                return "Windows has not reserved " + Voidstrap.Integrations.ClassicHostRedirect.ServerPrefix + " for the local service. Relaunch and accept the elevation prompt.";
            if (!running)
                return "The ORC local service is not running. Restart Voidstrap and try again.";
            return "The service answered with " + detail + ". Repair the selected client and try again.";
        }

        private static bool IsWebServerAnswering()
        {
            return ProbeHostScript("", out _);
        }

        private static void KillWebServer()
        {
            foreach (Process server in Process.GetProcessesByName("VoidstrapClient.WebServer"))
            {
                try { server.Kill(); server.WaitForExit(2000); }
                catch { }
                finally { server.Dispose(); }
            }
        }

        private static void KillClientProcesses()
        {
            ScanClientProcesses(kill: true);
        }

        public static bool AnyClassicClientRunning()
        {
            return ScanClientProcesses(kill: false);
        }

        private static bool ScanClientProcesses(bool kill)
        {
            string root;
            try { root = Path.GetFullPath(ClientsDir); }
            catch { return false; }
            if (string.IsNullOrEmpty(root))
                return false;

            bool found = false;
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        if (kill)
                        {
                            process.Kill();
                            process.WaitForExit(2000);
                        }
                    }
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
            return found;
        }

        private static async Task WaitForHostReadyAsync(Process host, ushort port, CancellationToken ct)
        {
            for (int i = 0; i < 120; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsUdpPortAvailable(port))
                    return;
                if (host.HasExited)
                    throw new InvalidOperationException("The ORC local server closed before it became ready.");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            throw new TimeoutException("The ORC local server did not become ready within 60 seconds.");
        }

        public static async Task<Process?> PlaySoloAsync(string client, string map, Action<string>? status, CancellationToken ct = default)
        {
            Voidstrap.Integrations.AssetProxy.AssetProxyServer.DisableForUnsupportedClient();

            NormalizeClientHelpers(Path.Combine(ClientsDir, client));
            try
            {
                using var userCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                userCts.CancelAfter(TimeSpan.FromSeconds(8));
                await ResolveRobloxUserAsync(userCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
            WriteUserSettings(client, map);

            ClassicClientConfig? playConfig = GetInstalledConfig(client);
            if (playConfig == null)
                throw new InvalidOperationException("The selected ORC client configuration is missing or invalid.");
            ValidateLaunchFiles(client, playConfig, ClientLaunchType.Play, map);
            ValidateLaunchFiles(client, playConfig, ClientLaunchType.Host);
            if (!IsUdpPortAvailable(DefaultPort))
                throw new InvalidOperationException("ORC local port 53640 is already in use. Close the existing local server, then try again.");

            status?.Invoke("Starting the local server");
            Process? host = Launch(client, ClientLaunchType.Host, DefaultPort, "localhost", status);
            if (host == null)
                throw new InvalidOperationException("The ORC local server could not be started.");
            try
            {
                await WaitForHostReadyAsync(host, DefaultPort, ct).ConfigureAwait(true);
                status?.Invoke("Joining the game");
                Process? player = Launch(client, ClientLaunchType.Play, DefaultPort, "127.0.0.1");
                if (player == null)
                    throw new InvalidOperationException("The ORC player could not be started.");
                await Task.Delay(2000, ct).ConfigureAwait(true);
                if (player.HasExited)
                {
                    player.Dispose();
                    throw new InvalidOperationException("The ORC player closed during startup.");
                }
                ClassicIntegrations.Start(player, host, playConfig.Name, map);
                return player;
            }
            catch
            {
                try
                {
                    if (!host.HasExited)
                        host.Kill();
                }
                catch
                {
                }
                host.Dispose();
                throw;
            }
        }

        public static int CompareClientNames(string? x, string? y)
        {
            if (x == null || y == null)
                return string.CompareOrdinal(x, y);
            var (yearX, eraX, okX) = ParseClientYear(x);
            var (yearY, eraY, okY) = ParseClientYear(y);
            if (!okX && !okY)
                return string.CompareOrdinal(x, y);
            if (okX != okY)
                return okX ? -1 : 1;
            if (yearX != yearY)
                return yearX.CompareTo(yearY);
            if (eraX != eraY)
                return eraX.CompareTo(eraY);
            return string.CompareOrdinal(x, y);
        }

        private static (int Year, int Era, bool Ok) ParseClientYear(string name)
        {
            if (name.Length < 5 || !int.TryParse(name.AsSpan(0, 4), out int year))
                return (0, 0, false);
            int era = char.ToUpperInvariant(name[4]) switch
            {
                'E' => 0,
                'M' => 1,
                'L' => 2,
                _ => -1
            };
            if (era < 0)
                return (0, 0, false);
            return (year, era, true);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1073741824L)
                return (bytes / 1073741824.0).ToString("0.0") + " GB";
            if (bytes >= 1048576L)
                return (bytes / 1048576.0).ToString("0") + " MB";
            if (bytes >= 1024L)
                return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }
    }
}
