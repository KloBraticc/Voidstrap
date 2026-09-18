using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.CommunityMods;

public static class MarketplaceCatalog
{
	public const string SourceName = "Kliko's Mods";

	public const string SourceUrl = "https://github.com/klikos-modloader/marketplace";

	public const string DownloadPrefix = "https://github.com/klikos-modloader/marketplace/raw/refs/heads/main/";

	public const string RawPrefix = "https://raw.githubusercontent.com/klikos-modloader/marketplace/main/";

	public const string RawRefsPrefix = "https://raw.githubusercontent.com/klikos-modloader/marketplace/refs/heads/main/";

	private const string IndexUrl = RawPrefix + "index.json";

	private const string TreeUrl = "https://api.github.com/repos/klikos-modloader/marketplace/git/trees/main?recursive=1";

	private const string LogIdent = "MarketplaceCatalog";

	private static readonly HttpClient Client = CreateClient();

	private static readonly SemaphoreSlim IntegrityGate = new SemaphoreSlim(1, 1);

	private static Dictionary<string, string>? _blobHashes;

	public static readonly string[] SortOptions = { "Featured", "Name" };

	private static HttpClient CreateClient()
	{
		HttpClient client = new HttpClient(new SocketsHttpHandler
		{
			AutomaticDecompression = System.Net.DecompressionMethods.All,
			AllowAutoRedirect = true,
			MaxAutomaticRedirections = 4,
			ConnectTimeout = TimeSpan.FromSeconds(15)
		})
		{
			Timeout = TimeSpan.FromSeconds(120),
			MaxResponseContentBufferSize = CommunityModGuard.MaxPackageBytes
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
		return client;
	}

	public static bool IsMarketplaceUrl(string? url)
	{
		return !string.IsNullOrEmpty(url)
			&& (url.StartsWith(DownloadPrefix, StringComparison.Ordinal)
				|| url.StartsWith(RawPrefix, StringComparison.Ordinal)
				|| url.StartsWith(RawRefsPrefix, StringComparison.Ordinal));
	}

	public static async Task<CommunityModPage> BrowseAsync(string? sort, string? search, CancellationToken token)
	{
		using HttpResponseMessage response = await Client.GetAsync(IndexUrl, token).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

		List<CommunityModEntry> found = new List<CommunityModEntry>();
		int blocked = 0;
		if (document.RootElement.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement element in document.RootElement.EnumerateArray())
			{
				CommunityModEntry? entry = ReadEntry(element);
				if (entry == null)
				{
					blocked++;
					continue;
				}
				if (!string.IsNullOrWhiteSpace(search)
					&& entry.Name.IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) < 0
					&& entry.Summary.IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) < 0
					&& entry.Author.IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}
				CommunityModVerdict verdict = CommunityModGuard.InspectDetail(entry);
				if (!verdict.Allowed)
				{
					blocked++;
					App.Logger?.WriteLine(LogIdent, "Hidden " + entry.Name + ": " + verdict.Reason);
					continue;
				}
				entry.IsSafetyChecked = true;
				found.Add(entry);
			}
		}
		if (blocked > 0)
		{
			App.Logger?.WriteLine(LogIdent, "Hidden " + blocked + " entries because they did not pass the safety checks");
		}
		if (string.Equals(sort, "Name", StringComparison.OrdinalIgnoreCase))
		{
			found = found.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
		}
		CommunityModPage page = new CommunityModPage
		{
			TotalCount = found.Count,
			PageNumber = 1,
			PageSize = found.Count
		};
		foreach (CommunityModEntry entry in found)
		{
			page.Entries.Add(entry);
		}
		return page;
	}

	public static async Task<CommunityModEntry?> FindAsync(string slug, CancellationToken token)
	{
		if (string.IsNullOrWhiteSpace(slug))
		{
			return null;
		}
		try
		{
			CommunityModPage page = await BrowseAsync(null, null, token).ConfigureAwait(false);
			return page.Entries.FirstOrDefault(entry => string.Equals(entry.Slug, slug, StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The marketplace entry could not be found: " + ex.Message);
			return null;
		}
	}

	public static Task<CommunityModEntry?> GetDetailAsync(CommunityModEntry listing, CancellationToken token)
	{
		if (!CommunityModGuard.InspectDetail(listing).Allowed)
		{
			return Task.FromResult<CommunityModEntry?>(null);
		}
		listing.DetailLines.Clear();
		foreach (string line in (listing.DescriptionHtml ?? "").Split('\n', StringSplitOptions.TrimEntries))
		{
			if (line.Length > 0)
			{
				listing.DetailLines.Add(line);
			}
		}
		if (!string.IsNullOrEmpty(listing.IconUrl) && listing.Images.Count == 0)
		{
			listing.Images.Add(new CommunityModImage
			{
				Full = listing.IconUrl,
				Thumbnail = listing.IconUrl,
				Caption = listing.Name
			});
		}
		return Task.FromResult<CommunityModEntry?>(listing);
	}

	public static async Task<string> InstallAsync(CommunityModEntry entry, CommunityModFile file, IProgress<string>? progress, CancellationToken token)
	{
		if (!IsMarketplaceUrl(file.DownloadUrl))
		{
			throw new InvalidOperationException("The download does not come from the community marketplace repository.");
		}

		string staging = Path.Combine(Paths.TempUpdates, "CommunityMods", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(staging);
		string archivePath = Path.Combine(staging, "package.zip");
		try
		{
			progress?.Report("Downloading " + entry.Name);
			await DownloadAsync(file.DownloadUrl, archivePath, token).ConfigureAwait(false);

			progress?.Report("Verifying the published hash");
			await VerifyBlobHashAsync(entry, archivePath, token).ConfigureAwait(false);

			progress?.Report("Inspecting the package");
			CommunityModVerdict verdict = CommunityModGuard.InspectArchive(archivePath, token);
			if (!verdict.Allowed)
			{
				throw new InvalidOperationException(verdict.Reason);
			}

			progress?.Report("Installing");
			ManagedModRecord record = ManagedModStore.Create(SafeName(entry));
			try
			{
				string destination = ManagedModStore.GetFolder(record.Id);
				Directory.CreateDirectory(destination);
				GameBananaCatalog.ExtractVerified(archivePath, destination, token);
			}
			catch
			{
				GameBananaCatalog.TryDeleteRecord(record.Id);
				throw;
			}
			GameBananaCatalog.SavePackInfo(entry, record.Id, "My Mods");
			App.Logger?.WriteLine(LogIdent, "Installed " + entry.Name + " as managed mod " + record.Id);
			return record.Id;
		}
		finally
		{
			try
			{
				if (Directory.Exists(staging))
				{
					Directory.Delete(staging, recursive: true);
				}
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "The staging folder could not be removed: " + ex.Message);
			}
		}
	}

	private static async Task VerifyBlobHashAsync(CommunityModEntry entry, string archivePath, CancellationToken token)
	{
		Dictionary<string, string>? hashes = await GetBlobHashesAsync(token).ConfigureAwait(false);
		string key = "mods/" + entry.Slug + ".zip";
		if (hashes == null || !hashes.TryGetValue(key, out string? expected))
		{
			App.Logger?.WriteLine(LogIdent, "No published hash for " + key + ", relying on the package inspection");
			return;
		}
		string actual = await ComputeGitBlobSha1Async(archivePath, token).ConfigureAwait(false);
		if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("The download did not match the hash published by the marketplace repository, so it was discarded.");
		}
	}

	private static async Task<Dictionary<string, string>?> GetBlobHashesAsync(CancellationToken token)
	{
		if (_blobHashes != null)
		{
			return _blobHashes;
		}
		await IntegrityGate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			if (_blobHashes != null)
			{
				return _blobHashes;
			}
			using HttpResponseMessage response = await Client.GetAsync(TreeUrl, token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return null;
			}
			await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
			using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
			Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (document.RootElement.TryGetProperty("tree", out JsonElement tree) && tree.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement node in tree.EnumerateArray())
				{
					if (node.TryGetProperty("path", out JsonElement path)
						&& node.TryGetProperty("sha", out JsonElement sha)
						&& path.ValueKind == JsonValueKind.String
						&& sha.ValueKind == JsonValueKind.String)
					{
						hashes[path.GetString() ?? ""] = sha.GetString() ?? "";
					}
				}
			}
			_blobHashes = hashes;
			return hashes;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The published hashes could not be read: " + ex.Message);
			return null;
		}
		finally
		{
			IntegrityGate.Release();
		}
	}

	private static async Task<string> ComputeGitBlobSha1Async(string path, CancellationToken token)
	{
		FileInfo info = new FileInfo(path);
		byte[] header = Encoding.ASCII.GetBytes("blob " + info.Length + "\0");
		using SHA1 sha1 = SHA1.Create();
		sha1.TransformBlock(header, 0, header.Length, null, 0);
		await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] buffer = new byte[81920];
		while (true)
		{
			int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
			if (read == 0)
			{
				break;
			}
			sha1.TransformBlock(buffer, 0, read, null, 0);
		}
		sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
		return Convert.ToHexString(sha1.Hash ?? Array.Empty<byte>());
	}

	private static async Task DownloadAsync(string url, string destination, CancellationToken token)
	{
		using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		if (!IsMarketplaceUrl(response.RequestMessage?.RequestUri?.ToString()))
		{
			throw new InvalidOperationException("The download was redirected away from the marketplace repository and was cancelled.");
		}
		long? length = response.Content.Headers.ContentLength;
		if (length.HasValue && length.Value > CommunityModGuard.MaxPackageBytes)
		{
			throw new InvalidOperationException("The download is larger than the allowed package size.");
		}
		await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		await using FileStream target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
		byte[] buffer = new byte[81920];
		long written = 0;
		while (true)
		{
			int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
			if (read == 0)
			{
				break;
			}
			written += read;
			if (written > CommunityModGuard.MaxPackageBytes)
			{
				throw new InvalidOperationException("The download exceeded the allowed package size and was stopped.");
			}
			await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
		}
	}

	private static CommunityModEntry? ReadEntry(JsonElement element)
	{
		string id = ReadString(element, "id");
		string name = ReadString(element, "name");
		string download = ReadString(element, "download");
		string thumbnail = ReadString(element, "thumbnail");
		if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !IsMarketplaceUrl(download))
		{
			return null;
		}
		if (!IsMarketplaceUrl(thumbnail))
		{
			thumbnail = "";
		}
		string description = ReadString(element, "description");
		CommunityModEntry entry = new CommunityModEntry
		{
			Id = StableId(id),
			Slug = id,
			Name = name,
			Author = ReadString(element, "author"),
			ProfileUrl = SourceUrl,
			IconUrl = thumbnail,
			Category = "Roblox file mod",
			SuperCategory = "Marketplace",
			SourceName = SourceName,
			SupportsInstall = true
		};
		entry.DescriptionHtml = description;
		entry.Summary = description.Replace('\n', ' ').Trim();
		if (entry.Summary.Length > 240)
		{
			entry.Summary = entry.Summary[..240].TrimEnd() + "...";
		}
		entry.Files.Add(new CommunityModFile
		{
			FileName = id + ".zip",
			DownloadUrl = download,
			AntivirusState = "done",
			AntivirusResult = "clean",
			AnalysisState = "done",
			AnalysisResult = "ok"
		});
		return entry;
	}

	private static long StableId(string value)
	{
		long hash = 5381;
		foreach (char character in value)
		{
			hash = ((hash << 5) + hash + char.ToLowerInvariant(character)) & 0x7FFFFFFF;
		}
		return hash;
	}

	private static string SafeName(CommunityModEntry entry)
	{
		string name = entry.Name;
		foreach (char invalid in Path.GetInvalidFileNameChars())
		{
			name = name.Replace(invalid, ' ');
		}
		name = name.Trim();
		return string.IsNullOrEmpty(name) ? entry.Slug : name;
	}

	private static string ReadString(JsonElement element, string property)
	{
		return element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty(property, out JsonElement value)
			&& value.ValueKind == JsonValueKind.String
				? value.GetString() ?? ""
				: "";
	}
}
