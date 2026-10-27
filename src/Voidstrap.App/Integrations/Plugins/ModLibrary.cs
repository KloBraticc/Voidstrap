using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Plugins;

public static partial class ModLibrary
{
	public const string Repository = "KloBraticc/voidstrap-mods-library";

	public const string RepositoryUrl = "https://github.com/" + Repository;

	private const string IndexUrl = "https://raw.githubusercontent.com/" + Repository + "/index/index.json";

	private const string RawPrefix = "https://raw.githubusercontent.com/" + Repository + "/";

	private const string LogIdent = "ModLibrary";

	private const long MaxIndexBytes = 8L * 1024 * 1024;

	private const long MaxModBytes = 1024L * 1024;

	private const long MaxFileBytes = 256L * 1024;

	private const int MaxFiles = 64;

	private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

	private static readonly HttpClient Client = CreateClient();

	private static readonly SemaphoreSlim IndexGate = new(1, 1);

	private static Index? _cached;

	private static DateTime _cachedAt;

	[GeneratedRegex("^[0-9a-f]{40}$")]
	private static partial Regex CommitPattern { get; }

	[GeneratedRegex("^[0-9a-f]{64}$")]
	private static partial Regex HashPattern { get; }

	[GeneratedRegex("^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+){0,3}$")]
	private static partial Regex PathPattern { get; }

	[GeneratedRegex("^[A-Za-z0-9-]{1,39}$")]
	private static partial Regex LoginPattern { get; }

	public sealed record LibraryFile(string Path, long Size, string Sha256);

	public sealed record LibraryMod(string Id, string Name, long OwnerId, string Login, string Version, string Description, IReadOnlyList<string> Network, bool HasIcon, IReadOnlyList<LibraryFile> Files);

	public sealed record Index(string Commit, IReadOnlyList<LibraryMod> Mods);

	public enum InstallState
	{
		NotInstalled,
		Installed,
		UpdateAvailable,
		NameTaken
	}

	private static HttpClient CreateClient()
	{
		HttpClient client = new(new SocketsHttpHandler
		{
			UseCookies = false,
			AllowAutoRedirect = false,
			ConnectTimeout = TimeSpan.FromSeconds(15),
			AutomaticDecompression = System.Net.DecompressionMethods.All
		})
		{
			Timeout = TimeSpan.FromSeconds(60)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
		return client;
	}

	public static async Task<Index> GetIndexAsync(bool refresh, CancellationToken token)
	{
		await IndexGate.WaitAsync(token);
		try
		{
			if (!refresh && _cached != null && DateTime.UtcNow - _cachedAt < CacheLifetime)
				return _cached;
			byte[] bytes = await DownloadAsync(IndexUrl + "?t=" + DateTime.UtcNow.Ticks, MaxIndexBytes, token);
			Index index = ParseIndex(bytes);
			_cached = index;
			_cachedAt = DateTime.UtcNow;
			App.Logger.WriteLine(LogIdent, "Loaded " + index.Mods.Count + " library mods at " + index.Commit[..12]);
			return index;
		}
		finally
		{
			IndexGate.Release();
		}
	}

	private static Index ParseIndex(byte[] bytes)
	{
		JsonObject root = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("the library index is not a JSON object");
		if (root["schema"]?.GetValue<int>() != 1)
			throw new InvalidDataException("the library index has an unknown format, update Voidstrap");
		if (!string.Equals(root["repository"]?.GetValue<string>(), Repository, StringComparison.Ordinal))
			throw new InvalidDataException("the library index belongs to another repository");
		string commit = root["commit"]?.GetValue<string>() ?? "";
		if (!CommitPattern.IsMatch(commit))
			throw new InvalidDataException("the library index has no valid commit");
		List<LibraryMod> mods = [];
		HashSet<string> names = new(StringComparer.Ordinal);
		foreach (JsonNode? node in root["mods"] as JsonArray ?? [])
		{
			try
			{
				LibraryMod mod = ParseMod(node as JsonObject ?? throw new InvalidDataException("not an object"));
				if (names.Add(mod.Name))
					mods.Add(mod);
			}
			catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException or JsonException)
			{
				App.Logger.WriteLine(LogIdent, "Skipped an invalid library entry: " + ex.Message);
			}
		}
		return new Index(commit, mods.OrderBy(m => m.Name, StringComparer.Ordinal).ToList());
	}

	private static LibraryMod ParseMod(JsonObject node)
	{
		string name = node["name"]?.GetValue<string>() ?? "";
		long ownerId = node["ownerId"]?.GetValue<long>() ?? 0;
		string login = node["login"]?.GetValue<string>() ?? "";
		string version = node["version"]?.GetValue<string>() ?? "";
		if (!ModFiles.IsValidName(name) || ownerId <= 0 || !LoginPattern.IsMatch(login) || !Version.TryParse(version, out _))
			throw new InvalidDataException("bad identity for " + name);
		if (!string.Equals(node["id"]?.GetValue<string>(), ownerId + "/" + name, StringComparison.Ordinal))
			throw new InvalidDataException("bad id for " + name);
		List<LibraryFile> files = [];
		long total = 0;
		foreach (JsonNode? fileNode in node["files"] as JsonArray ?? [])
		{
			string path = fileNode?["path"]?.GetValue<string>() ?? "";
			long size = fileNode?["size"]?.GetValue<long>() ?? -1;
			string hash = fileNode?["sha256"]?.GetValue<string>() ?? "";
			if (!PathPattern.IsMatch(path) || path.Split('/').Any(p => p is "." or "..") || size < 0 || size > MaxFileBytes || !HashPattern.IsMatch(hash))
				throw new InvalidDataException("bad file entry in " + name);
			total += size;
			files.Add(new LibraryFile(path, size, hash));
		}
		if (files.Count == 0 || files.Count > MaxFiles || total > MaxModBytes)
			throw new InvalidDataException(name + " is outside the size limits");
		if (!files.Any(f => f.Path == PluginManifest.ManifestFolder + "/plugin.json") || !files.Any(f => f.Path == "hooks/hooks.json"))
			throw new InvalidDataException(name + " is missing required files");
		List<string> network = (node["network"] as JsonArray ?? []).Select(h => h?.GetValue<string>() ?? "").Where(h => Uri.CheckHostName(h) == UriHostNameType.Dns).ToList();
		string description = node["description"]?.GetValue<string>() ?? "";
		if (description.Length > 300)
			description = description[..300];
		return new LibraryMod(ownerId + "/" + name, name, ownerId, login, version, description, network, files.Any(f => f.Path == ModFiles.IconFile), files);
	}

	public static (long OwnerId, string Version)? GetInstalled(string name)
	{
		try
		{
			string manifestPath = Path.Combine(Paths.Plugins, name, PluginManifest.ManifestFolder, "plugin.json");
			if (!File.Exists(manifestPath))
				return null;
			JsonObject? manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
			long ownerId = (manifest?["library"] as JsonObject)?["ownerId"]?.GetValue<long>() ?? 0;
			return (ownerId, manifest?["version"]?.GetValue<string>() ?? "0.0.0");
		}
		catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
		{
			return (0, "0.0.0");
		}
	}

	public static InstallState GetState(LibraryMod mod)
	{
		if (GetInstalled(mod.Name) is not { } installed)
			return InstallState.NotInstalled;
		if (installed.OwnerId != mod.OwnerId)
			return InstallState.NameTaken;
		return Version.TryParse(installed.Version, out Version? local) && Version.TryParse(mod.Version, out Version? remote) && remote > local
			? InstallState.UpdateAvailable
			: InstallState.Installed;
	}

	public static async Task<byte[]?> GetIconAsync(Index index, LibraryMod mod, CancellationToken token)
	{
		LibraryFile? icon = mod.Files.FirstOrDefault(f => f.Path == ModFiles.IconFile);
		if (icon == null)
			return null;
		try
		{
			return await DownloadVerifiedAsync(index, mod, icon, token);
		}
		catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
		{
			return null;
		}
	}

	public static async Task InstallAsync(Index index, LibraryMod mod, CancellationToken token)
	{
		InstallState state = GetState(mod);
		if (state == InstallState.NameTaken)
			throw new InvalidOperationException("a different mod named " + mod.Name + " is already installed");
		string staging = Path.Combine(Paths.Temp, "ModLibrary", Guid.NewGuid().ToString("N"));
		string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
		try
		{
			foreach (LibraryFile file in mod.Files)
			{
				byte[] bytes = await DownloadVerifiedAsync(index, mod, file, token);
				string target = Path.GetFullPath(Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar)));
				if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException("unsafe path " + file.Path);
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				await File.WriteAllBytesAsync(target, bytes, token);
			}
			PluginManifest manifest = PluginManifest.Read(staging) ?? throw new InvalidDataException("the mod has no manifest");
			if (!string.Equals(manifest.Name, mod.Name, StringComparison.Ordinal))
				throw new InvalidDataException("the mod manifest name does not match the library");
			StampLibrary(staging, mod);
			ModFiles.Install(new ModFiles.Package(manifest, staging, staging));
			App.Logger.WriteLine(LogIdent, (state == InstallState.UpdateAvailable ? "Updated " : "Installed ") + mod.Id + " " + mod.Version);
		}
		finally
		{
			ModFiles.TryDelete(staging);
		}
	}

	private static void StampLibrary(string folder, LibraryMod mod)
	{
		string path = Path.Combine(folder, PluginManifest.ManifestFolder, "plugin.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("bad manifest");
		manifest["library"] = new JsonObject { ["id"] = mod.Id, ["ownerId"] = mod.OwnerId, ["login"] = mod.Login };
		File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
	}

	private static async Task<byte[]> DownloadVerifiedAsync(Index index, LibraryMod mod, LibraryFile file, CancellationToken token)
	{
		string url = RawPrefix + index.Commit + "/mods/" + Uri.EscapeDataString(mod.Name) + "/" + string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString));
		byte[] bytes = await DownloadAsync(url, file.Size, token);
		if (bytes.LongLength != file.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256, StringComparison.Ordinal))
			throw new InvalidDataException(file.Path + " of " + mod.Name + " does not match the library index, the download was refused");
		return bytes;
	}

	private static async Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken token)
	{
		using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength is long length && length > maxBytes)
			throw new InvalidDataException("the download is larger than expected");
		await using Stream stream = await response.Content.ReadAsStreamAsync(token);
		using MemoryStream buffer = new();
		byte[] chunk = new byte[81920];
		int read;
		while ((read = await stream.ReadAsync(chunk, token)) > 0)
		{
			if (buffer.Length + read > maxBytes)
				throw new InvalidDataException("the download is larger than expected");
			buffer.Write(chunk, 0, read);
		}
		return buffer.ToArray();
	}
}
