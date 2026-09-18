using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Voidstrap.Integrations;

namespace Voidstrap.Utility;

internal static partial class LegacyMaterialTextures
{
	private const string LogIdent = "LegacyMaterialTextures";

	private const string ConfigName = "Voidstrap Classic Materials";

	private const string AssetInfoUrl = "https://assetdelivery.roblox.com/v2/assetId/";

	private const string AssetDetailsUrl = "https://economy.roblox.com/v2/assets/{0}/details";

	private const string ClientSettingsUrl = "https://clientsettingscdn.roblox.com/v2/settings/application/PCDesktopClient";

	private const int TexturePackTypeId = 63;

	private const int GeneratorVersion = 3;

	private const int ResolveConcurrency = 6;

	private const long MaxSourceBytes = 134217728L;

	private static readonly string[] FaceKeys = ["texture", "texture_top", "texture_side", "texture_bottom"];

	private static readonly HashSet<string> NonMaterialFolders = new(StringComparer.OrdinalIgnoreCase) { "plastic", "sky", "ui", "water" };

	private static readonly Dictionary<string, string> MaterialAliases = new(StringComparer.OrdinalIgnoreCase)
	{
		["corrodedmetal"] = "rust"
	};

	private static readonly string[] DefaultTerrainFaces =
	[
		"grass:texture_top", "grass:texture_side", "grass:texture_bottom", "slate:texture", "concrete:texture_top", "concrete:texture_side",
		"brick:texture", "sand:texture_top", "sand:texture_side", "woodplanks:texture", "rock:texture", "glacier:texture_top",
		"glacier:texture_side", "glacier:texture_bottom", "snow:texture", "sandstone:texture_top", "sandstone:texture_side", "sandstone:texture_bottom",
		"mud:texture", "basalt:texture", "ground:texture", "crackedlava:texture", "asphalt:texture_top", "asphalt:texture_side",
		"cobblestone:texture_top", "cobblestone:texture_side", "ice:texture_top", "ice:texture_side", "leafygrass:texture_top", "leafygrass:texture_side",
		"salt:texture_top", "salt:texture_side", "limestone:texture_top", "limestone:texture_side", "pavement:texture_top", "pavement:texture_side"
	];

	private static readonly JsonSerializerOptions CatalogJson = new() { WriteIndented = false };

	private static readonly JsonDocumentOptions LenientJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

	private static readonly HttpClient Http = CreateHttp();

	[GeneratedRegex("<color>(\\d+)<", RegexOptions.CultureInvariant)]
	private static partial Regex PackColor();

	[GeneratedRegex("<usage[^>]*>(\\d+)<", RegexOptions.CultureInvariant)]
	private static partial Regex PackUsage();

	[GeneratedRegex("rbxassetid://(\\d{6,20})", RegexOptions.CultureInvariant)]
	private static partial Regex AssetIdLiteral();

	private sealed class PackInfo
	{
		public int Type { get; set; }

		public long Color { get; set; }

		public string Name { get; set; } = "";

		public bool Resolved { get; set; }

		public int Usage { get; set; }
	}

	private sealed class Catalog
	{
		public string ClientKey { get; set; } = "";

		public List<long> Embedded { get; set; } = [];

		public Dictionary<long, PackInfo> Packs { get; set; } = [];
	}

	private sealed class Sources
	{
		public string? TerrainColor { get; set; }

		public string? TerrainTable { get; set; }

		public Dictionary<string, string> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);

		public bool IsEmpty => TerrainColor == null && Materials.Count == 0;
	}

	private static string CatalogPath => Path.Combine(Paths.Cache, "MaterialTexturePacks.json");

	private static string ConfigPath => Path.Combine(Paths.AssetProxy, "Configs", ConfigName + ".json");

	private static string AssetFolder => Path.Combine(Paths.AssetProxy, "Configs", "Assets", ConfigName);

	private static string SignaturePath => Path.Combine(AssetFolder, "source.sig");

	private static HttpClient CreateHttp()
	{
		HttpClient client = new(new SocketsHttpHandler
		{
			AutomaticDecompression = DecompressionMethods.All,
			ConnectTimeout = TimeSpan.FromSeconds(10),
			ConnectCallback = ConnectAroundAssetWarpAsync
		})
		{
			Timeout = TimeSpan.FromSeconds(20)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Roblox/WinInet");
		return client;
	}

	private static async ValueTask<Stream> ConnectAroundAssetWarpAsync(SocketsHttpConnectionContext context, CancellationToken token)
	{
		string host = context.DnsEndPoint.Host;
		IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
		if (addresses.Length == 0 || addresses.All(IPAddress.IsLoopback))
		{
			string? direct = await Voidstrap.Integrations.AssetProxy.DnsResolver.ResolveDirectAsync(host, token).ConfigureAwait(false);
			if (direct != null && IPAddress.TryParse(direct, out IPAddress? parsed))
			{
				addresses = [parsed];
			}
		}
		Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
		try
		{
			await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token).ConfigureAwait(false);
			return new NetworkStream(socket, ownsSocket: true);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	public static bool IsRedirectedSource(string relative)
	{
		string normalized = relative.Replace('\\', '/');
		if (normalized.StartsWith("PlatformContent/pc/terrain/", StringComparison.OrdinalIgnoreCase))
		{
			return normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
		}
		const string prefix = "PlatformContent/pc/textures/";
		if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string rest = normalized[prefix.Length..];
		int slash = rest.IndexOf('/');
		return slash > 0 && !NonMaterialFolders.Contains(rest[..slash]);
	}

	public static bool HasSources(IReadOnlyList<string> modFoldersByPriority)
	{
		return !CollectSources(modFoldersByPriority).IsEmpty;
	}

	public static void RemoveGenerated()
	{
		try
		{
			bool removed = false;
			if (File.Exists(ConfigPath))
			{
				File.Delete(ConfigPath);
				removed = true;
			}
			if (Directory.Exists(AssetFolder))
			{
				Directory.Delete(AssetFolder, recursive: true);
				removed = true;
			}
			if (removed)
			{
				Voidstrap.Integrations.AssetProxy.TextureStripper.InvalidateRuntimeState();
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The classic material redirects could not be removed: " + ex.Message);
		}
	}

	public static async Task ApplyAsync(IReadOnlyList<string> modFoldersByPriority, string clientFolder, CancellationToken token)
	{
		Sources sources = CollectSources(modFoldersByPriority);
		if (sources.IsEmpty)
		{
			RemoveGenerated();
			return;
		}
		string executable = Path.Combine(clientFolder, "RobloxPlayerBeta.exe");
		string signature = BuildSignature(sources, executable);
		if (File.Exists(ConfigPath) && File.Exists(SignaturePath) && string.Equals(File.ReadAllText(SignaturePath), signature, StringComparison.Ordinal))
		{
			LogAssetWarpState();
			return;
		}

		Catalog catalog = LoadCatalog();
		List<long> embedded = [.. ScanEmbeddedAssetIds(executable, catalog)];
		HashSet<long> known = [.. embedded];
		embedded.AddRange((await LoadLiveTerrainPackIdsAsync(token).ConfigureAwait(false)).Where(known.Add));
		bool complete = embedded.Count > 0 && await ResolvePacksAsync(catalog, embedded, token).ConfigureAwait(false);
		SaveCatalog(catalog);

		string staging = AssetFolder + ".new";
		if (Directory.Exists(staging))
		{
			Directory.Delete(staging, recursive: true);
		}
		Directory.CreateDirectory(staging);
		JsonArray rules = [];
		(int terrainCount, int partCount) = WriteRules(sources, embedded, catalog, staging, rules, token);
		token.ThrowIfCancellationRequested();

		if (rules.Count == 0)
		{
			Directory.Delete(staging, recursive: true);
			RemoveGenerated();
			App.Logger?.WriteLine(LogIdent, "No classic terrain or material textures could be matched to this Roblox version");
			return;
		}
		if (complete)
		{
			File.WriteAllText(Path.Combine(staging, "source.sig"), signature);
		}
		if (Directory.Exists(AssetFolder))
		{
			Directory.Delete(AssetFolder, recursive: true);
		}
		Directory.Move(staging, AssetFolder);
		JsonObject config = new() { ["replacement_rules"] = rules };
		string temporary = ConfigPath + ".tmp";
		File.WriteAllText(temporary, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		File.Move(temporary, ConfigPath, overwrite: true);
		Voidstrap.Integrations.AssetProxy.TextureStripper.InvalidateRuntimeState();
		App.Logger?.WriteLine(LogIdent, "Redirected classic textures for " + terrainCount + " terrain faces and " + partCount + " part materials through AssetWarp");
		LogAssetWarpState();
	}

	private static void LogAssetWarpState()
	{
		if (!App.Settings.Prop.AssetWarpEnabled)
		{
			App.Logger?.WriteLine(LogIdent, "Turn on AssetWarp to see the classic terrain and material textures, Roblox now downloads them instead of reading local files");
		}
	}

	private static Sources CollectSources(IReadOnlyList<string> modFolders)
	{
		Sources sources = new();
		foreach (string folder in modFolders)
		{
			try
			{
				string terrain = Path.Combine(folder, "PlatformContent", "pc", "terrain");
				if (sources.TerrainColor == null && File.Exists(Path.Combine(terrain, "diffusearray.dds")))
				{
					sources.TerrainColor = Path.Combine(terrain, "diffusearray.dds");
					string table = Path.Combine(terrain, "materials.json");
					sources.TerrainTable = File.Exists(table) ? table : null;
				}
				string textures = Path.Combine(folder, "PlatformContent", "pc", "textures");
				if (!Directory.Exists(textures))
				{
					continue;
				}
				foreach (string materialFolder in Directory.EnumerateDirectories(textures))
				{
					string name = Path.GetFileName(materialFolder);
					string diffuse = Path.Combine(materialFolder, "diffuse.dds");
					if (NonMaterialFolders.Contains(name) || sources.Materials.ContainsKey(name) || !File.Exists(diffuse))
					{
						continue;
					}
					sources.Materials[name] = diffuse;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.WriteLine(LogIdent, "Could not read the textures of a mod folder: " + ex.Message);
			}
		}
		return sources;
	}

	private static string BuildSignature(Sources sources, string executable)
	{
		StringBuilder builder = new();
		builder.Append(GeneratorVersion).Append('\n');
		void Add(string? path)
		{
			if (path == null || !File.Exists(path))
			{
				return;
			}
			FileInfo info = new(path);
			builder.Append(path).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
		}
		Add(executable);
		Add(sources.TerrainColor);
		Add(sources.TerrainTable);
		foreach ((string name, string diffuse) in sources.Materials.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
		{
			builder.Append(name).Append('\n');
			Add(diffuse);
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
	}

	private static async Task<List<long>> LoadLiveTerrainPackIdsAsync(CancellationToken token)
	{
		List<long> ids = [];
		try
		{
			using JsonDocument settings = await GetJsonAsync(ClientSettingsUrl, token).ConfigureAwait(false);
			if (!settings.RootElement.TryGetProperty("applicationSettings", out JsonElement flags) || flags.ValueKind != JsonValueKind.Object)
			{
				return ids;
			}
			foreach (JsonProperty flag in flags.EnumerateObject())
			{
				if (flag.Name.StartsWith("FStringTerrainMaterialTable", StringComparison.Ordinal) && flag.Value.ValueKind == JsonValueKind.String)
				{
					foreach (Match match in AssetIdLiteral().Matches(flag.Value.GetString() ?? ""))
					{
						ids.Add(long.Parse(match.Groups[1].Value));
					}
				}
			}
		}
		catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested)
		{
			App.Logger?.WriteLine(LogIdent, "The live terrain pack list could not be read, using the client list only: " + ex.Message);
		}
		return ids;
	}

	private static List<long> ScanEmbeddedAssetIds(string executable, Catalog catalog)
	{
		try
		{
			if (!File.Exists(executable))
			{
				return [];
			}
			FileInfo info = new(executable);
			string key = info.Length + "|" + info.LastWriteTimeUtc.Ticks;
			if (catalog.ClientKey == key && catalog.Embedded.Count > 0)
			{
				return catalog.Embedded;
			}
			byte[] data = File.ReadAllBytes(executable);
			ReadOnlySpan<byte> marker = "rbxassetid://"u8;
			HashSet<long> ids = [];
			ReadOnlySpan<byte> span = data;
			int position = 0;
			while (position < span.Length)
			{
				int found = span[position..].IndexOf(marker);
				if (found < 0)
				{
					break;
				}
				int start = position + found + marker.Length;
				int end = start;
				while (end < span.Length && end - start < 20 && span[end] >= (byte)'0' && span[end] <= (byte)'9')
				{
					end++;
				}
				if (end - start >= 6 && long.TryParse(Encoding.ASCII.GetString(data, start, end - start), out long id))
				{
					ids.Add(id);
				}
				position = end;
			}
			catalog.ClientKey = key;
			catalog.Embedded = [.. ids.Order()];
			return catalog.Embedded;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
		{
			App.Logger?.WriteLine(LogIdent, "The Roblox client could not be scanned for material packs: " + ex.Message);
			return [];
		}
	}

	private static async Task<bool> ResolvePacksAsync(Catalog catalog, IEnumerable<long> ids, CancellationToken token)
	{
		long[] pending = [.. ids.Where(id => !catalog.Packs.TryGetValue(id, out PackInfo? info) || !info.Resolved || info.Type == TexturePackTypeId && info.Usage == 0)];
		if (pending.Length == 0)
		{
			return true;
		}
		ConcurrentDictionary<long, PackInfo> resolved = new();
		int failures = 0;
		await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = ResolveConcurrency, CancellationToken = token }, async (id, itemToken) =>
		{
			try
			{
				resolved[id] = await ResolvePackAsync(id, itemToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !itemToken.IsCancellationRequested)
			{
				Interlocked.Increment(ref failures);
			}
		}).ConfigureAwait(false);
		foreach ((long id, PackInfo info) in resolved)
		{
			catalog.Packs[id] = info;
		}
		if (failures > 0)
		{
			App.Logger?.WriteLine(LogIdent, failures + " material packs could not be looked up, they will be retried on the next launch");
		}
		return failures == 0;
	}

	private static async Task<PackInfo> ResolvePackAsync(long id, CancellationToken token)
	{
		using JsonDocument info = await GetJsonAsync(AssetInfoUrl + id, token).ConfigureAwait(false);
		int type = info.RootElement.TryGetProperty("assetTypeId", out JsonElement typeElement) && typeElement.TryGetInt32(out int typeId) ? typeId : 0;
		if (type != TexturePackTypeId)
		{
			return new PackInfo { Type = type, Resolved = true };
		}
		string location = info.RootElement.TryGetProperty("locations", out JsonElement locations) && locations.ValueKind == JsonValueKind.Array && locations.GetArrayLength() > 0
			&& locations[0].TryGetProperty("location", out JsonElement locationElement)
			? locationElement.GetString() ?? ""
			: "";
		if (!Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("The material pack location is invalid");
		}
		string xml = await Http.GetStringAsync(uri, token).ConfigureAwait(false);
		Match usage = PackUsage().Match(xml);
		PackInfo pack = new() { Type = type, Resolved = true, Usage = usage.Success ? int.Parse(usage.Groups[1].Value) : -1 };
		Match color = PackColor().Match(xml);
		pack.Color = color.Success ? long.Parse(color.Groups[1].Value) : 0;
		if (pack.Color <= 0)
		{
			return pack;
		}
		using JsonDocument details = await GetJsonAsync(string.Format(AssetDetailsUrl, pack.Color), token).ConfigureAwait(false);
		pack.Name = NormalizeImageName(details.RootElement.TryGetProperty("Name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "");
		return pack;
	}

	private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
	{
		using HttpResponseMessage response = await Http.GetAsync(url, token).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
	}

	private static string NormalizeImageName(string name)
	{
		string value = name.Trim().ToLowerInvariant();
		int cut = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('-'));
		if (cut >= 0)
		{
			value = value[(cut + 1)..];
		}
		return value.EndsWith("_color", StringComparison.Ordinal) ? value[..^6] : "";
	}

	private static (string Material, string Face) SplitPackName(string packName)
	{
		foreach ((string suffix, string face) in (ReadOnlySpan<(string, string)>)[("_top", "texture_top"), ("_side", "texture_side"), ("_bottom", "texture_bottom")])
		{
			if (packName.EndsWith(suffix, StringComparison.Ordinal))
			{
				return (packName[..^suffix.Length], face);
			}
		}
		return (packName, "texture");
	}

	private static (int Terrain, int Parts) WriteRules(Sources sources, List<long> embedded, Catalog catalog, string staging, JsonArray rules, CancellationToken token)
	{
		byte[]? colorArray = sources.TerrainColor == null ? null : ReadSource(sources.TerrainColor);
		int slices = colorArray == null ? 0 : KtxDecoder.DdsArraySize(colorArray);
		Dictionary<string, int> order = LoadTerrainOrder(sources.TerrainTable);
		Dictionary<string, string?> written = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> terrainMatched = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> partsMatched = new(StringComparer.OrdinalIgnoreCase);
		foreach (long id in embedded)
		{
			token.ThrowIfCancellationRequested();
			if (!catalog.Packs.TryGetValue(id, out PackInfo? pack) || pack.Type != TexturePackTypeId || pack.Color <= 0 || pack.Name.Length == 0)
			{
				continue;
			}
			(string material, string face) = SplitPackName(pack.Name);
			string folder = !sources.Materials.ContainsKey(material) && MaterialAliases.TryGetValue(material, out string? alias) ? alias : material;
			int slice = colorArray == null ? -1 : FindSlice(order, material, face);
			bool hasSlice = slice >= 0 && slice < slices;
			bool usePart = sources.Materials.ContainsKey(folder) && (pack.Usage == 1 || !hasSlice);
			string key = usePart ? "part_" + folder.ToLowerInvariant() : "terrain_" + slice;
			if (!usePart && !hasSlice)
			{
				continue;
			}
			if (!written.TryGetValue(key, out string? file))
			{
				file = key + ".png";
				byte[]? source = usePart ? ReadSource(sources.Materials[folder]) : colorArray;
				DecodedImage? image = source == null ? null : usePart ? KtxDecoder.DecodeDds(source) : KtxDecoder.DecodeDds(source, slice);
				if (!SaveColor(image, Path.Combine(staging, file)))
				{
					file = null;
				}
				written[key] = file;
			}
			if (file == null)
			{
				continue;
			}
			rules.Add(new JsonObject
			{
				["name"] = pack.Name + " " + id,
				["replace_ids"] = new JsonArray(id),
				["mode"] = "local",
				["enabled"] = true,
				["local_path"] = "Assets/" + ConfigName + "/" + file
			});
			if (usePart)
			{
				partsMatched.Add(folder);
			}
			else
			{
				terrainMatched.Add(material + ":" + face);
			}
		}
		return (terrainMatched.Count, partsMatched.Count);
	}

	private static Dictionary<string, int> LoadTerrainOrder(string? tablePath)
	{
		Dictionary<string, int> order = new(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (tablePath != null && new FileInfo(tablePath).Length < 1048576)
			{
				using JsonDocument table = JsonDocument.Parse(File.ReadAllText(tablePath), LenientJson);
				if (table.RootElement.TryGetProperty("materials", out JsonElement materials) && materials.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement material in materials.EnumerateArray())
					{
						string name = material.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
						foreach (string face in FaceKeys)
						{
							if (name.Length > 0 && material.TryGetProperty(face, out _))
							{
								order[name + ":" + face] = order.Count;
							}
						}
					}
				}
			}
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The classic terrain table could not be read, using the default layout: " + ex.Message);
			order.Clear();
		}
		if (order.Count == 0)
		{
			foreach (string entry in DefaultTerrainFaces)
			{
				order[entry] = order.Count;
			}
		}
		return order;
	}

	private static int FindSlice(Dictionary<string, int> order, string material, string face)
	{
		foreach (string candidate in (string[])[face, "texture", "texture_top", "texture_side"])
		{
			if (order.TryGetValue(material + ":" + candidate, out int slice))
			{
				return slice;
			}
		}
		return -1;
	}

	private static byte[]? ReadSource(string path)
	{
		try
		{
			FileInfo info = new(path);
			return info.Exists && info.Length > 128 && info.Length <= MaxSourceBytes ? File.ReadAllBytes(path) : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "Could not read " + Path.GetFileName(path) + ": " + ex.Message);
			return null;
		}
	}

	private static bool SaveColor(DecodedImage? image, string path)
	{
		if (image == null)
		{
			return false;
		}
		byte[] pixels = image.Bgra;
		for (int index = 3; index < pixels.Length; index += 4)
		{
			pixels[index] = 255;
		}
		using Image<Bgra32> output = Image.LoadPixelData<Bgra32>(pixels, image.Width, image.Height);
		output.SaveAsPng(path);
		return true;
	}

	private static Catalog LoadCatalog()
	{
		try
		{
			if (File.Exists(CatalogPath))
			{
				return JsonSerializer.Deserialize<Catalog>(File.ReadAllText(CatalogPath), CatalogJson) ?? new Catalog();
			}
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The material pack cache could not be read: " + ex.Message);
		}
		return new Catalog();
	}

	private static void SaveCatalog(Catalog catalog)
	{
		try
		{
			Directory.CreateDirectory(Paths.Cache);
			string temporary = CatalogPath + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(catalog, CatalogJson));
			File.Move(temporary, CatalogPath, overwrite: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The material pack cache could not be saved: " + ex.Message);
		}
	}
}
