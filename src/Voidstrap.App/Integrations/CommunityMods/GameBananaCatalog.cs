using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.CommunityMods;

public static partial class GameBananaCatalog
{
	public const string SourceName = "GameBanana";

	public const string SourceUrl = "https://gamebanana.com/games/2879";

	private const string LogIdent = "GameBananaCatalog";

	private const string ApiRoot = "https://gamebanana.com/apiv11";

	private const int PageSize = 35;

	private const int SearchPageSize = 15;

	private const int MaxCommunityPages = 4;

	private const int BatchSize = PageSize;

	public const int MinSearchLength = 2;

	private const string DetailProperties = "_idRow,_sText,_aFiles,_aGame,_nDownloadCount,_nLikeCount,_nPostCount,_sLicense,_aSubmitter,_aLicenseChecklist,_aPreviewMedia";

	private static readonly HttpClient Client = CreateClient();

	private const long MinSegmentBytes = 524288;

	private const long MinSegmentedBytes = 4194304;

	private const long PackageCacheLimitBytes = 536870912;

	private static readonly SemaphoreSlim SegmentGate = new SemaphoreSlim(16, 16);

	private static readonly ConcurrentDictionary<long, (DateTime ExpiresUtc, string Json)> ProfileCache = new();

	public static readonly string[] SortOptions = { "Featured", "Newest", "Recently updated" };

	[GeneratedRegex("<[^>]+>", RegexOptions.Compiled)]
	private static partial Regex HtmlTagPattern();

	[GeneratedRegex("\\s{2,}", RegexOptions.Compiled)]
	private static partial Regex WhitespacePattern();

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
			Timeout = TimeSpan.FromSeconds(45),
			MaxResponseContentBufferSize = CommunityModGuard.MaxPackageBytes
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
		return client;
	}

	public static async Task<IReadOnlyList<CommunityModCategory>> GetCategoriesAsync(CancellationToken token)
	{
		string url = ApiRoot + "/Mod/Categories?_idGameRow=" + CommunityModGuard.RobloxGameId + "&_sSort=a_to_z&_bShowEmpty=false";
		using JsonDocument document = await GetJsonAsync(url, token).ConfigureAwait(false);
		List<CommunityModCategory> categories = new List<CommunityModCategory>();
		if (document.RootElement.ValueKind != JsonValueKind.Array)
		{
			return categories;
		}
		foreach (JsonElement element in document.RootElement.EnumerateArray())
		{
			if (element.TryGetProperty("_bIsObsolete", out JsonElement obsolete) && obsolete.ValueKind == JsonValueKind.True)
			{
				continue;
			}
			string name = ReadString(element, "_sName");
			long id = ReadLong(element, "_idRow");
			if (id <= 0 || string.IsNullOrWhiteSpace(name) || CommunityModGuard.HiddenCategories.Contains(name))
			{
				continue;
			}
			categories.Add(new CommunityModCategory
			{
				Id = id,
				Name = name,
				ItemCount = ReadInt(element, "_nItemCount")
			});
		}
		return categories;
	}

	public static async Task<CommunityModPage> BrowseAsync(int page, string? sort, CommunityModCategory? category, string? search, CancellationToken token)
	{
		string url;
		string query = (search ?? "").Trim();
		if (query.Length >= MinSearchLength)
		{
			url = ApiRoot + "/Util/Search/Results?_sSearchString=" + Uri.EscapeDataString(query)
				+ "&_idGameRow=" + CommunityModGuard.RobloxGameId
				+ "&_sModelName=Mod&_nPage=" + page;
		}
		else if (category != null && category.Id > 0)
		{
			url = ApiRoot + "/Mod/Index?_nPage=" + page + "&_nPerpage=" + PageSize
				+ "&" + Uri.EscapeDataString("_aFilters[Generic_Game]") + "=" + CommunityModGuard.RobloxGameId
				+ "&" + Uri.EscapeDataString("_aFilters[Generic_Category]") + "=" + category.Id;
		}
		else
		{
			string sortKey = sort switch
			{
				"Newest" => "Generic_Newest",
				"Recently updated" => "Generic_LatestModified",
				_ => ""
			};
			url = ApiRoot + "/Mod/Index?_nPage=" + page + "&_nPerpage=" + PageSize
				+ "&" + Uri.EscapeDataString("_aFilters[Generic_Game]") + "=" + CommunityModGuard.RobloxGameId
				+ (string.IsNullOrEmpty(sortKey) ? "" : "&_sSort=" + sortKey);
		}

		using JsonDocument document = await GetJsonAsync(url, token).ConfigureAwait(false);
		JsonElement root = document.RootElement;
		int total = root.TryGetProperty("_aMetadata", out JsonElement metadata) && metadata.TryGetProperty("_nRecordCount", out JsonElement count)
			? count.GetInt32()
			: 0;
		CommunityModPage result = new CommunityModPage
		{
			TotalCount = total,
			PageNumber = page,
			PageSize = query.Length >= MinSearchLength ? SearchPageSize : PageSize
		};
		if (!root.TryGetProperty("_aRecords", out JsonElement records) || records.ValueKind != JsonValueKind.Array)
		{
			return result;
		}
		int blocked = 0;
		int returned = 0;
		HashSet<long> seen = new HashSet<long>();
		List<CommunityModEntry> candidates = new List<CommunityModEntry>();
		foreach (JsonElement record in records.EnumerateArray())
		{
			returned++;
			CommunityModEntry? entry = ReadListing(record);
			if (entry == null)
			{
				blocked++;
				continue;
			}
			CommunityModVerdict verdict = CommunityModGuard.InspectListing(entry);
			if (!verdict.Allowed)
			{
				blocked++;
				continue;
			}
			if (seen.Add(entry.Id))
			{
				candidates.Add(entry);
			}
		}
		result.ReturnedCount = returned;

		await ApplyBatchDetailsAsync(candidates, token).ConfigureAwait(false);
		await ProbeContentsAsync(candidates, token).ConfigureAwait(false);
		foreach (CommunityModEntry entry in candidates)
		{
			if (!entry.DetailLoaded)
			{
				blocked++;
				continue;
			}
			CommunityModVerdict verdict = CommunityModGuard.InspectDetail(entry);
			if (!verdict.Allowed)
			{
				blocked++;
				continue;
			}
			result.Entries.Add(entry);
		}
		if (blocked > 0)
		{
			App.Logger?.WriteLine(LogIdent, "Hidden " + blocked + " of " + returned + " entries on page " + page + " because they did not pass the safety checks");
		}
		return result;
	}

	public static async Task<CommunityModEntry?> GetDetailAsync(CommunityModEntry listing, CancellationToken token)
	{
		if (!listing.DetailLoaded && await LoadProfileAsync(listing, token).ConfigureAwait(false) == null)
		{
			return null;
		}
		await ProbeContentsAsync([listing], token).ConfigureAwait(false);
		CommunityModVerdict verdict = CommunityModGuard.InspectDetail(listing);
		if (!verdict.Allowed)
		{
			App.Logger?.WriteLine(LogIdent, "Hidden mod " + listing.Id + " on open: " + verdict.Reason);
			return null;
		}
		await LoadCommunityDataAsync(listing, token).ConfigureAwait(false);
		return listing;
	}

	private static async Task<CommunityModEntry?> LoadProfileAsync(CommunityModEntry listing, CancellationToken token)
	{
		using JsonDocument document = await GetProfileAsync(listing.Id, token).ConfigureAwait(false);
		if (!ApplyProfile(listing, document.RootElement))
		{
			App.Logger?.WriteLine(LogIdent, "Refused mod " + listing.Id + " because it does not belong to Roblox");
			return null;
		}
		return listing;
	}

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string[]> ArchiveListings = new();

	private const int ProbeConcurrency = 12;

	private const int MaxListingBytes = 2 * 1024 * 1024;

	private static async Task ProbeContentsAsync(IReadOnlyList<CommunityModEntry> entries, CancellationToken token)
	{
		List<(CommunityModEntry Entry, CommunityModFile File)> work = [];
		foreach (CommunityModEntry entry in entries)
		{
			foreach (CommunityModFile file in entry.Files)
			{
				if (file.ContentsProbed
					|| !CommunityModGuard.ConvertiblePackageExtensions.Contains(Path.GetExtension(file.FileName))
						&& !string.Equals(Path.GetExtension(file.FileName), ".zip", StringComparison.OrdinalIgnoreCase)
					|| !CommunityModGuard.IsInstallableFile(file))
				{
					continue;
				}
				work.Add((entry, file));
			}
		}
		if (work.Count == 0)
		{
			return;
		}

		using SemaphoreSlim gate = new SemaphoreSlim(ProbeConcurrency);
		int hidden = 0;
		Task[] tasks = work.Select(async item =>
		{
			await gate.WaitAsync(token).ConfigureAwait(false);
			try
			{
				string[]? listing = await GetArchiveListingAsync(item.File.Id, token).ConfigureAwait(false);
				item.File.ContentsProbed = true;
				if (listing == null || listing.Length == 0)
				{
					return;
				}
				CommunityModGuard.ArchiveListingSummary summary = CommunityModGuard.SummarizeListing(listing);
				item.File.ListingKnown = true;
				item.File.ListingHasRobloxContent = summary.RobloxContent;
				item.File.ListingHasConfig = summary.Config;
				item.File.ListingHasAssets = summary.Assets;
				item.File.ListingHasCacheEntries = summary.CacheEntries;
				if (listing.Any(CommunityModGuard.IsDangerousName))
				{
					item.File.ContainsExecutable = true;
					Interlocked.Increment(ref hidden);
					return;
				}
				if (!CommunityModGuard.ArchiveListingIsRobloxMod(listing, item.Entry.RequiresFleasion))
				{
					item.File.LacksRobloxContent = true;
					Interlocked.Increment(ref hidden);
				}
			}
			finally
			{
				gate.Release();
			}
		}).ToArray();
		await Task.WhenAll(tasks).ConfigureAwait(false);
		if (hidden > 0)
		{
			App.Logger?.WriteLine(LogIdent, "Inspected " + work.Count + " archives and set aside " + hidden + " that are not Roblox mod files");
		}
	}

	private static async Task<string[]?> GetArchiveListingAsync(long fileId, CancellationToken token)
	{
		if (fileId <= 0)
		{
			return null;
		}
		if (ArchiveListings.TryGetValue(fileId, out string[]? cached))
		{
			return cached;
		}
		string url = ApiRoot + "/File/" + fileId + "/RawFileList";
		if (!CommunityModGuard.IsTrustedUrl(url))
		{
			return null;
		}
		try
		{
			using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return null;
			}
			if (response.Content.Headers.ContentLength is long length && length > MaxListingBytes)
			{
				return null;
			}
			await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
			using StreamReader reader = new StreamReader(stream);
			List<string> lines = [];
			int read = 0;
			string? line;
			while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) != null)
			{
				read += line.Length + 1;
				if (read > MaxListingBytes)
				{
					return null;
				}
				string trimmed = line.Trim();
				if (trimmed.Length > 0)
				{
					lines.Add(trimmed);
				}
			}
			string[] result = [.. lines];
			ArchiveListings[fileId] = result;
			return result;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
		{
			return null;
		}
	}

	private static async Task ApplyBatchDetailsAsync(IReadOnlyList<CommunityModEntry> entries, CancellationToken token)
	{
		for (int offset = 0; offset < entries.Count; offset += BatchSize)
		{
			token.ThrowIfCancellationRequested();
			CommunityModEntry[] chunk = entries.Skip(offset).Take(BatchSize).ToArray();
			string ids = string.Join(",", chunk.Select(entry => entry.Id));
			using JsonDocument document = await GetJsonAsync(ApiRoot + "/Mod/Multi?_csvRowIds=" + ids + "&_csvProperties=" + DetailProperties, token).ConfigureAwait(false);
			if (document.RootElement.ValueKind != JsonValueKind.Array)
			{
				continue;
			}
			Dictionary<long, JsonElement> byId = new Dictionary<long, JsonElement>();
			foreach (JsonElement element in document.RootElement.EnumerateArray())
			{
				long id = ReadLong(element, "_idRow");
				if (id > 0)
				{
					byId[id] = element;
				}
			}
			foreach (CommunityModEntry entry in chunk)
			{
				if (byId.TryGetValue(entry.Id, out JsonElement element))
				{
					ApplyProfile(entry, element);
				}
			}
		}
	}

	private static bool ReadExecutableWarning(JsonElement file)
	{
		if (!file.TryGetProperty("_aAnalysisWarnings", out JsonElement warnings) || warnings.ValueKind != JsonValueKind.Object)
		{
			return false;
		}
		foreach (JsonProperty warning in warnings.EnumerateObject())
		{
			if (warning.Name.Contains("exe", StringComparison.OrdinalIgnoreCase)
				|| warning.Name.Contains("executable", StringComparison.OrdinalIgnoreCase)
				|| warning.Name.Contains("script", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static bool ApplyProfile(CommunityModEntry listing, JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			return false;
		}
		if (root.TryGetProperty("_aGame", out JsonElement game)
			&& game.ValueKind == JsonValueKind.Object
			&& game.TryGetProperty("_idRow", out JsonElement gameId)
			&& gameId.ValueKind == JsonValueKind.Number
			&& gameId.TryGetInt32(out int parsedGameId)
			&& parsedGameId != CommunityModGuard.RobloxGameId)
		{
			return false;
		}

		listing.Comments.Clear();
		listing.Updates.Clear();
		listing.Issues.Clear();
		listing.DescriptionHtml = ReadString(root, "_sText");
		listing.Summary = Summarize(listing.DescriptionHtml);
		listing.DownloadCount = ReadInt(root, "_nDownloadCount");
		listing.LikeCount = Math.Max(listing.LikeCount, ReadInt(root, "_nLikeCount"));
		listing.CommentCount = ReadInt(root, "_nPostCount");
		listing.UpdateCount = ReadInt(root, "_nUpdatesCount");
		listing.IssueCount = ReadInt(root, "_nAllTodosCount");
		listing.LicenseText = PlainText(ReadString(root, "_sLicense"));
		listing.Submitter = root.TryGetProperty("_aSubmitter", out JsonElement submitter) && submitter.ValueKind == JsonValueKind.Object
			? ReadPerson(submitter)
			: null;
		listing.LicenseRules.Clear();
		ReadLicenseRules(root, listing.LicenseRules);
		listing.Images.Clear();
		listing.Files.Clear();

		if (root.TryGetProperty("_aPreviewMedia", out JsonElement media)
			&& media.ValueKind == JsonValueKind.Object
			&& media.TryGetProperty("_aImages", out JsonElement images)
			&& images.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement image in images.EnumerateArray())
			{
				string baseUrl = ReadString(image, "_sBaseUrl");
				string file = ReadString(image, "_sFile");
				string thumb = ReadString(image, "_sFile220");
				if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(file))
				{
					continue;
				}
				string full = baseUrl.TrimEnd('/') + "/" + file;
				string thumbnail = string.IsNullOrEmpty(thumb) ? full : baseUrl.TrimEnd('/') + "/" + thumb;
				if (!CommunityModGuard.IsTrustedUrl(full) || !CommunityModGuard.IsTrustedUrl(thumbnail))
				{
					continue;
				}
				listing.Images.Add(new CommunityModImage
				{
					Full = full,
					Thumbnail = thumbnail,
					Caption = ReadString(image, "_sCaption")
				});
			}
		}

		if (root.TryGetProperty("_aFiles", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement file in files.EnumerateArray())
			{
				if (file.ValueKind != JsonValueKind.Object)
				{
					continue;
				}
				listing.Files.Add(new CommunityModFile
				{
					Id = ReadLong(file, "_idRow"),
					FileName = ReadString(file, "_sFile"),
					DownloadUrl = ReadString(file, "_sDownloadUrl"),
					Md5 = ReadString(file, "_sMd5Checksum"),
					SizeBytes = ReadLong(file, "_nFilesize"),
					Description = ReadString(file, "_sDescription"),
					AntivirusState = ReadString(file, "_sAvState"),
					AntivirusResult = ReadString(file, "_sAvResult"),
					AnalysisState = ReadString(file, "_sAnalysisState"),
					AnalysisResult = ReadString(file, "_sAnalysisResult"),
					ContainsExecutable = ReadExecutableWarning(file)
				});
			}
		}

		listing.RequiresFleasion = FleasionModInstaller.LooksRequired(listing);
		listing.IsSafetyChecked = true;
		listing.DetailLoaded = true;
		return true;
	}

	public static async Task<string> InstallAsync(CommunityModEntry entry, CommunityModFile file, IProgress<string>? progress, CancellationToken token, CommunityModInstallTarget target = CommunityModInstallTarget.ManagedMods)
	{
		CommunityModVerdict fileVerdict = CommunityModGuard.InspectFile(file);
		if (!fileVerdict.Allowed)
		{
			throw new InvalidOperationException(fileVerdict.Reason);
		}

		string staging = Path.Combine(Paths.TempUpdates, "CommunityMods", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(staging);
		string archivePath = Path.Combine(staging, "package.zip");
		try
		{
			progress?.Report("Downloading " + file.FileName);
			await AcquirePackageAsync(file, archivePath, token).ConfigureAwait(false);

			progress?.Report("Inspecting the package");
			string extension = Path.GetExtension(file.FileName);
			if (CommunityModGuard.ConvertiblePackageExtensions.Contains(extension))
			{
				progress?.Report("Unpacking the " + extension.TrimStart('.') + " archive");
				archivePath = await ConvertToZipAsync(archivePath, extension, staging, token).ConfigureAwait(false);
				extension = ".zip";
			}
			if (target is CommunityModInstallTarget.AssetWarp or CommunityModInstallTarget.Fleasion)
			{
				string? paired = await PairTwinsAsync(entry, file, archivePath, extension, staging, progress, token).ConfigureAwait(false);
				if (paired != null)
				{
					archivePath = paired;
					extension = ".zip";
				}
			}
			bool hasContent = false;
			if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
			{
				CommunityModVerdict safety = CommunityModGuard.InspectArchive(archivePath, out hasContent, token);
				if (!safety.Allowed)
				{
					throw new InvalidOperationException(safety.Reason);
				}
			}
			bool hasConfig = await FleasionModInstaller.ContainsConfigAsync(archivePath, extension, token).ConfigureAwait(false);
			bool hasCacheEntries = !hasContent && ContainsCacheEntries(archivePath, token);
			bool hasAssets = !string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
				&& FleasionModInstaller.ArchiveHasAssets(archivePath, token);
			target = ChooseTarget(entry, target, extension, hasContent, hasConfig, hasCacheEntries, hasAssets);

			if (target == CommunityModInstallTarget.AssetCache)
			{
				return InstallAssetCache(entry, archivePath, staging, progress, token);
			}

			if (target == CommunityModInstallTarget.AssetWarp)
			{
				string? assetWarpResult = await FleasionModInstaller.TryInstallAssetWarpAsync(entry, file, archivePath, token, extension).ConfigureAwait(false);
				if (assetWarpResult == null)
				{
					throw new InvalidDataException("This package does not contain an AssetWarp compatible replacement config.");
				}
				token.ThrowIfCancellationRequested();
				RegisterExternalMod(entry, "AssetWarp", assetWarpResult);
				App.Logger?.WriteLine(LogIdent, "Installed " + entry.Name + " for AssetWarp");
				return assetWarpResult;
			}
			if (target == CommunityModInstallTarget.Fleasion)
			{
				string? fleasionResult = await FleasionModInstaller.TryInstallAsync(entry, file, archivePath, token, extension).ConfigureAwait(false);
				if (fleasionResult == null)
				{
					throw new InvalidDataException("This package does not contain a Fleasion compatible replacement config.");
				}
				token.ThrowIfCancellationRequested();
				RegisterExternalMod(entry, "Fleasion", fleasionResult);
				App.Logger?.WriteLine(LogIdent, "Installed " + entry.Name + " for Fleasion");
				return fleasionResult;
			}
			progress?.Report("Placing the files into the Roblox layout");
			ManagedModRecord record = ManagedModStore.Create(BuildModName(entry));
			try
			{
				string destination = ManagedModStore.GetFolder(record.Id);
				Directory.CreateDirectory(destination);
				int placed = ExtractVerified(archivePath, destination, token);
				App.Logger?.WriteLine(LogIdent, "Placed " + placed + " files for " + entry.Name);
				try
				{
					int options = ModVariantStore.CaptureSources(record.Id, archivePath, token);
					App.Logger?.WriteLine(LogIdent, "Kept " + options + " files as selectable options for " + entry.Name);
				}
				catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
				{
					App.Logger?.WriteLine(LogIdent, "The options for " + entry.Name + " could not be kept: " + ex.Message);
				}
				token.ThrowIfCancellationRequested();
				SavePackInfo(entry, record.Id, "My Mods");
				token.ThrowIfCancellationRequested();
			}
			catch
			{
				TryDeleteRecord(record.Id);
				throw;
			}
			App.Logger?.WriteLine(LogIdent, "Installed " + entry.Name + " (" + entry.Id + ") as managed mod " + record.Id);
			return record.Id;
		}
		finally
		{
			TryDeleteDirectory(staging);
		}
	}

	private static async Task<string?> PairTwinsAsync(CommunityModEntry entry, CommunityModFile file, string packagePath, string extension, string staging, IProgress<string>? progress, CancellationToken token)
	{
		string baseName = Path.GetFileNameWithoutExtension(file.FileName);
		if (baseName.Length == 0)
		{
			return null;
		}
		List<CommunityModFile> twins = entry.Files
			.Where(candidate => candidate.Id != file.Id
				&& string.Equals(Path.GetFileNameWithoutExtension(candidate.FileName), baseName, StringComparison.OrdinalIgnoreCase)
				&& CommunityModGuard.IsInstallableFile(candidate))
			.ToList();
		if (twins.Count == 0
			&& string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
			&& entry.Files.Count(candidate => CommunityModGuard.IsInstallableFile(candidate)
				&& string.Equals(Path.GetExtension(candidate.FileName), ".json", StringComparison.OrdinalIgnoreCase)
				&& !CommunityModGuard.IsFlagFileName(candidate.FileName)) == 1)
		{
			twins = entry.Files
				.Where(candidate => candidate.Id != file.Id
					&& CommunityModGuard.IsInstallableFile(candidate)
					&& !string.Equals(Path.GetExtension(candidate.FileName), ".json", StringComparison.OrdinalIgnoreCase)
					&& (!candidate.ListingKnown || candidate.ListingHasAssets && !candidate.ListingHasRobloxContent))
				.ToList();
		}
		if (twins.Count == 0)
		{
			return null;
		}

		string combined = Path.Combine(staging, "paired");
		Directory.CreateDirectory(combined);
		if (!TryAddToPair(packagePath, extension, file.FileName, combined, token))
		{
			return null;
		}
		List<string> added = [];
		foreach (CommunityModFile twin in twins)
		{
			token.ThrowIfCancellationRequested();
			progress?.Report("Downloading the matching " + twin.FileName);
			string twinExtension = Path.GetExtension(twin.FileName);
			string twinStaging = Path.Combine(staging, "twin" + twin.Id);
			Directory.CreateDirectory(twinStaging);
			string twinPath = Path.Combine(twinStaging, "package" + twinExtension);
			await AcquirePackageAsync(twin, twinPath, token).ConfigureAwait(false);
			if (CommunityModGuard.ConvertiblePackageExtensions.Contains(twinExtension))
			{
				twinPath = await ConvertToZipAsync(twinPath, twinExtension, twinStaging, token).ConfigureAwait(false);
				twinExtension = ".zip";
			}
			if (TryAddToPair(twinPath, twinExtension, twin.FileName, combined, token))
			{
				added.Add(twin.FileName);
			}
		}
		if (added.Count == 0)
		{
			return null;
		}

		string paired = Path.Combine(staging, "paired.zip");
		await Task.Run(() => CreateZip(combined, paired, token), token).ConfigureAwait(false);
		App.Logger?.WriteLine(LogIdent, "Paired " + file.FileName + " with " + string.Join(", ", added) + " for " + entry.Name);
		return paired;
	}

	private static bool TryAddToPair(string packagePath, string extension, string originalName, string combined, CancellationToken token)
	{
		try
		{
			token.ThrowIfCancellationRequested();
			if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
			{
				string fileName = Path.GetFileName(originalName);
				CommunityModVerdict verdict = CommunityModGuard.InspectRelativePath(fileName);
				if (!verdict.Allowed)
				{
					return false;
				}
				File.Copy(packagePath, Path.Combine(combined, fileName), overwrite: true);
				token.ThrowIfCancellationRequested();
				return true;
			}
			return ExtractFlat(packagePath, combined, CommunityModGuard.AssetExtensions, token) > 0;
		}
		catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
		{
			App.Logger?.WriteLine(LogIdent, "The file " + originalName + " could not be paired: " + ex.Message);
			return false;
		}
	}

	private static async Task<string> ConvertToZipAsync(string archivePath, string extension, string staging, CancellationToken token)
	{
		string raw = Path.Combine(staging, "raw");
		Directory.CreateDirectory(raw);
		string tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
		if (!File.Exists(tool))
		{
			throw new InvalidDataException("This system cannot open " + extension.TrimStart('.') + " packages.");
		}
		ProcessStartInfo startInfo = new ProcessStartInfo(tool)
		{
			WorkingDirectory = raw,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add("-xf");
		startInfo.ArgumentList.Add(archivePath);
		using Process? process = Process.Start(startInfo);
		if (process == null)
		{
			throw new InvalidDataException("The " + extension.TrimStart('.') + " package could not be unpacked.");
		}
		try
		{
			await process.WaitForExitAsync(token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			throw;
		}
		if (process.ExitCode != 0)
		{
			throw new InvalidDataException("The " + extension.TrimStart('.') + " package could not be unpacked.");
		}

		long extracted = 0;
		int files = 0;
		foreach (string entry in Directory.EnumerateFiles(raw, "*", SearchOption.AllDirectories))
		{
			token.ThrowIfCancellationRequested();
			files++;
			extracted += new FileInfo(entry).Length;
			if (files > CommunityModGuard.MaxArchiveEntries || extracted > CommunityModGuard.MaxExtractedBytes)
			{
				throw new InvalidOperationException("The package expands far beyond the allowed size and was discarded.");
			}
		}
		if (files == 0)
		{
			throw new InvalidDataException("The package is empty.");
		}

		string converted = Path.Combine(staging, "converted.zip");
		await Task.Run(() => CreateZip(raw, converted, token), token).ConfigureAwait(false);
		return converted;
	}

	private static void CreateZip(string folder, string zipPath, CancellationToken token)
	{
		try
		{
			using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
			foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
			{
				token.ThrowIfCancellationRequested();
				archive.CreateEntryFromFile(file, Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/'), CompressionLevel.NoCompression);
			}
		}
		catch
		{
			TryDeleteFile(zipPath);
			throw;
		}
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(true);
			}
		}
		catch (Exception)
		{
		}
	}

	private static async Task AcquirePackageAsync(CommunityModFile file, string destination, CancellationToken token)
	{
		string? cachePath = GetPackageCachePath(file.Md5);
		if (cachePath != null && File.Exists(cachePath))
		{
			try
			{
				string cachedMd5 = await ComputeMd5Async(cachePath, token).ConfigureAwait(false);
				if (string.Equals(cachedMd5, file.Md5, StringComparison.OrdinalIgnoreCase))
				{
					await CopyFileAsync(cachePath, destination, token).ConfigureAwait(false);
					try
					{
						File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
					}
					return;
				}
				TryDeleteFile(cachePath);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.WriteLine(LogIdent, "The cached package could not be reused: " + ex.Message);
			}
		}

		await DownloadAsync(file, destination, token).ConfigureAwait(false);
		string actual = await ComputeMd5Async(destination, token).ConfigureAwait(false);
		if (!string.Equals(actual, file.Md5, StringComparison.OrdinalIgnoreCase))
		{
			TryDeleteFile(destination);
			throw new InvalidOperationException("The download did not match the checksum GameBanana published, so it was discarded.");
		}
		if (cachePath == null)
		{
			return;
		}
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
			string temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				await CopyFileAsync(destination, temporary, token).ConfigureAwait(false);
				File.Move(temporary, cachePath, true);
			}
			finally
			{
				TryDeleteFile(temporary);
			}
			PrunePackageCache();
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The package could not be cached: " + ex.Message);
		}
	}

	private static string? GetPackageCachePath(string md5)
	{
		if (md5.Length != 32 || md5.Any(character => !Uri.IsHexDigit(character)))
		{
			return null;
		}
		return Path.Combine(Paths.Cache, "CommunityPackages", md5.ToUpperInvariant() + ".package");
	}

	private static async Task CopyFileAsync(string source, string destination, CancellationToken token)
	{
		await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
		await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
		await input.CopyToAsync(output, 131072, token).ConfigureAwait(false);
		await output.FlushAsync(token).ConfigureAwait(false);
	}

	private static void PrunePackageCache()
	{
		try
		{
			string folder = Path.Combine(Paths.Cache, "CommunityPackages");
			FileInfo[] files = Directory.Exists(folder)
				? new DirectoryInfo(folder).EnumerateFiles("*.package").OrderByDescending(file => file.LastWriteTimeUtc).ToArray()
				: [];
			long total = 0;
			foreach (FileInfo file in files)
			{
				total += file.Length;
				if (total > PackageCacheLimitBytes)
				{
					TryDeleteFile(file.FullName);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The community package cache could not be pruned: " + ex.Message);
		}
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static async Task DownloadAsync(CommunityModFile file, string destination, CancellationToken token)
	{
		for (int attempt = 1; ; attempt++)
		{
			try
			{
				await DownloadOnceAsync(file, destination, attempt == 1, token).ConfigureAwait(false);
				return;
			}
			catch (Exception ex) when (attempt < 3 && !token.IsCancellationRequested && ex is HttpRequestException or IOException)
			{
				App.Logger?.WriteLine(LogIdent, "Download attempt " + attempt + " for " + file.FileName + " failed: " + ex.Message);
				await Task.Delay(TimeSpan.FromSeconds(2 * attempt), token).ConfigureAwait(false);
			}
		}
	}

	private static async Task DownloadOnceAsync(CommunityModFile file, string destination, bool allowSegments, CancellationToken token)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
		using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		if (!CommunityModGuard.IsTrustedUrl(response.RequestMessage?.RequestUri?.ToString()))
		{
			throw new InvalidOperationException("The download was redirected off GameBanana and was cancelled.");
		}
		long? length = response.Content.Headers.ContentLength;
		if (length.HasValue && length.Value > CommunityModGuard.MaxPackageBytes)
		{
			throw new InvalidOperationException("The download is larger than the allowed package size.");
		}
		Uri? finalUri = response.RequestMessage?.RequestUri;
		int segments = DownloadConfiguration.NormalizeSegments(App.Settings.Prop.MaxDownloadSegments);
		if (allowSegments && finalUri != null && length.HasValue && segments > 1 && length.Value >= MinSegmentedBytes && (response.Headers.AcceptRanges?.Contains("bytes") ?? false))
		{
			response.Dispose();
			await DownloadSegmentedAsync(finalUri, destination, length.Value, segments, token).ConfigureAwait(false);
			return;
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

	private static async Task DownloadSegmentedAsync(Uri uri, string destination, long length, int maxSegments, CancellationToken token)
	{
		int count = (int)Math.Min(maxSegments, length / MinSegmentBytes);
		long size = length / count;
		using SafeFileHandle handle = File.OpenHandle(destination, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous | FileOptions.RandomAccess);
		RandomAccess.SetLength(handle, length);
		using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(token);
		Exception? failure = null;
		Task[] tasks = Enumerable.Range(0, count).Select(index =>
		{
			long start = index * size;
			long end = index == count - 1 ? length - 1 : start + size - 1;
			return DownloadRangeAsync(uri, handle, start, end, length, exception =>
			{
				Interlocked.CompareExchange(ref failure, exception, null);
				failed.Cancel();
			}, failed);
		}).ToArray();
		try
		{
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}
		catch when (failure != null)
		{
			ExceptionDispatchInfo.Capture(failure).Throw();
			throw;
		}
	}

	private static async Task DownloadRangeAsync(Uri uri, SafeFileHandle handle, long start, long end, long length, Action<Exception> fail, CancellationTokenSource failed)
	{
		CancellationToken token = failed.Token;
		await SegmentGate.WaitAsync(token).ConfigureAwait(false);
		byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
		try
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
			request.Headers.Range = new RangeHeaderValue(start, end);
			using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			if (!CommunityModGuard.IsTrustedUrl(response.RequestMessage?.RequestUri?.ToString()))
			{
				throw new InvalidOperationException("The download was redirected off GameBanana and was cancelled.");
			}
			ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
			if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != start || range.To != end || range.Length != length)
			{
				throw new IOException("GameBanana returned an unexpected byte range.");
			}
			await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
			long position = start;
			while (position <= end)
			{
				int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position + 1)), token).ConfigureAwait(false);
				if (read == 0)
				{
					throw new IOException("The download ended before the file was complete.");
				}
				await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, token).ConfigureAwait(false);
				position += read;
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			fail(ex);
			throw;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
			SegmentGate.Release();
		}
	}

	private static CommunityModInstallTarget ChooseTarget(CommunityModEntry entry, CommunityModInstallTarget requested, string extension, bool hasContent, bool hasConfig, bool hasCacheEntries, bool hasAssets)
	{
		if (requested == CommunityModInstallTarget.ManagedMods)
		{
			if (hasContent)
			{
				return requested;
			}
			if (hasConfig)
			{
				App.Logger?.WriteLine(LogIdent, "Mod " + entry.Id + " holds a replacement config, installing it for AssetWarp instead of My Mods");
				return CommunityModInstallTarget.AssetWarp;
			}
			if (hasCacheEntries)
			{
				App.Logger?.WriteLine(LogIdent, "Mod " + entry.Id + " holds Roblox asset cache entries, installing it into the asset cache");
				return CommunityModInstallTarget.AssetCache;
			}
			throw new InvalidOperationException(DescribeEmptyPackage(extension));
		}
		if (hasConfig)
		{
			return requested;
		}
		if (hasContent)
		{
			App.Logger?.WriteLine(LogIdent, "Mod " + entry.Id + " holds no replacement config, installing it into My Mods instead");
			return CommunityModInstallTarget.ManagedMods;
		}
		if (hasCacheEntries)
		{
			App.Logger?.WriteLine(LogIdent, "Mod " + entry.Id + " holds Roblox asset cache entries, installing it into the asset cache");
			return CommunityModInstallTarget.AssetCache;
		}
		if (hasAssets)
		{
			App.Logger?.WriteLine(LogIdent, "Mod " + entry.Id + " holds replacement assets without a config, placing them beside the configs");
			return requested;
		}
		throw new InvalidDataException(DescribeEmptyPackage(extension));
	}

	private static bool ContainsCacheEntries(string archivePath, CancellationToken token)
	{
		try
		{
			using ZipArchive archive = ZipFile.OpenRead(archivePath);
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				token.ThrowIfCancellationRequested();
				if (string.IsNullOrEmpty(entry.Name)
					|| entry.FullName.Contains("RESTORE", StringComparison.OrdinalIgnoreCase)
					|| RobloxAssetCache.ExtractHash(entry.Name) is null
					|| entry.Length <= RobloxAssetCache.HeaderBytes
					|| entry.Length > RobloxAssetCache.MaxEntryBytes)
				{
					continue;
				}
				byte[] header = new byte[RobloxAssetCache.HeaderBytes];
				using Stream stream = entry.Open();
				if (stream.ReadAtLeast(header, RobloxAssetCache.HeaderBytes, throwOnEndOfStream: false) == RobloxAssetCache.HeaderBytes
					&& RobloxAssetCache.IsCacheEntry(header, entry.Length))
				{
					return true;
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The package could not be scanned for asset cache entries: " + ex.Message);
		}
		return false;
	}

	private static string InstallAssetCache(CommunityModEntry entry, string archivePath, string staging, IProgress<string>? progress, CancellationToken token)
	{
		progress?.Report("Unpacking the cached assets");
		string unpacked = Path.Combine(staging, "cache");
		Directory.CreateDirectory(unpacked);
		using (ZipArchive archive = ZipFile.OpenRead(archivePath))
		{
			foreach (ZipArchiveEntry zipEntry in archive.Entries)
			{
				token.ThrowIfCancellationRequested();
				if (string.IsNullOrEmpty(zipEntry.Name) || zipEntry.Length > RobloxAssetCache.MaxEntryBytes)
				{
					continue;
				}
				string? hash = RobloxAssetCache.ExtractHash(zipEntry.Name);
				if (hash is null || zipEntry.FullName.Contains("RESTORE", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				zipEntry.ExtractToFile(Path.Combine(unpacked, hash), overwrite: true);
			}
		}

		IReadOnlyList<AssetCacheEntry> entries = RobloxAssetCache.Collect(unpacked, token);
		if (entries.Count == 0)
		{
			throw new InvalidDataException("This package does not contain any usable Roblox asset cache entries.");
		}

		progress?.Report("Replacing the cached assets");
		ManagedModRecord record = ManagedModStore.Create(BuildModName(entry) + " (Asset cache)");
		string folder = ManagedModStore.GetFolder(record.Id);
		try
		{
			token.ThrowIfCancellationRequested();
			Directory.CreateDirectory(folder);
			int installed = RobloxAssetCache.Install(entries, Path.Combine(folder, RobloxAssetCache.BackupFolderName), token);
			if (installed == 0)
			{
				throw new InvalidOperationException("None of the cached assets could be replaced.");
			}
			token.ThrowIfCancellationRequested();
			SavePackInfo(entry, record.Id, "Asset cache");
			token.ThrowIfCancellationRequested();
			App.Logger?.WriteLine(LogIdent, "Installed " + entry.Name + " as asset cache mod " + record.Id);
		}
		catch
		{
			RobloxAssetCache.Restore(Path.Combine(folder, RobloxAssetCache.BackupFolderName));
			TryDeleteRecord(record.Id);
			throw;
		}
		return record.Id;
	}

	private static string DescribeEmptyPackage(string extension)
	{
		return string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
			? "This JSON file is not a replacement config, so it cannot be installed. FastFlag preset files belong in the FastFlags page."
			: "Nothing in the package could be matched to a Roblox client folder and it holds no replacement config, so there is nothing to install.";
	}

	internal static void SavePackInfo(CommunityModEntry entry, string modId, string installKind)
	{
		try
		{
			ModPackInfo.Write(ManagedModStore.GetFolder(modId), new ModPackInfo
			{
				Source = entry.SourceName,
				Id = entry.Id,
				Slug = entry.Slug,
				Name = entry.Name,
				Author = entry.Author,
				IconUrl = entry.IconUrl,
				ProfileUrl = entry.ProfileUrl,
				Category = entry.CategoryDisplay,
				InstallKind = installKind
			});
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The mod pack details could not be stored: " + ex.Message);
		}
	}

	private static void RegisterExternalMod(CommunityModEntry entry, string label, string detail)
	{
		string name = BuildModName(entry) + " (" + label + ")";
		try
		{
			if (ManagedModStore.Load().Any(record => string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase)))
			{
				return;
			}
			ManagedModRecord created = ManagedModStore.Create(name);
			string folder = ManagedModStore.GetFolder(created.Id);
			Directory.CreateDirectory(folder);
			File.WriteAllText(Path.Combine(folder, label + ".lock"), detail);
			SavePackInfo(entry, created.Id, label);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The My Mods entry for " + label + " could not be created: " + ex.Message);
		}
	}

	internal static void TryDeleteRecord(string id)
	{
		try
		{
			ManagedModStore.Delete(id);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The half installed mod entry could not be removed: " + ex.Message);
		}
	}

	internal static async Task<string> InstallModAsync(long modId, long fileId, IProgress<string>? progress, CancellationToken token)
	{
		using JsonDocument document = await GetProfileAsync(modId, token).ConfigureAwait(false);
		CommunityModEntry listing = new CommunityModEntry
		{
			Id = modId,
			Name = ReadString(document.RootElement, "_sName"),
			ProfileUrl = "https://gamebanana.com/mods/" + modId
		};
		if (!ApplyProfile(listing, document.RootElement))
		{
			throw new InvalidOperationException("The mod page no longer belongs to Roblox.");
		}
		CommunityModFile? file = listing.Files.FirstOrDefault(candidate => candidate.Id == fileId)
			?? listing.Files.FirstOrDefault(CommunityModGuard.IsInstallableFile);
		CommunityModVerdict verdict = CommunityModGuard.InspectFile(file);
		if (file == null || !verdict.Allowed)
		{
			throw new InvalidOperationException(verdict.Reason);
		}
		return await InstallAsync(listing, file, progress, token, CommunityModInstallTarget.ManagedMods).ConfigureAwait(false);
	}

	internal static async Task<int> FetchAllAssetsAsync(long modId, string destination, IReadOnlyCollection<string> allowedExtensions, IProgress<string>? progress, CancellationToken token)
	{
		using JsonDocument document = await GetProfileAsync(modId, token).ConfigureAwait(false);
		CommunityModEntry listing = new CommunityModEntry
		{
			Id = modId,
			Name = ReadString(document.RootElement, "_sName"),
			ProfileUrl = "https://gamebanana.com/mods/" + modId
		};
		if (!ApplyProfile(listing, document.RootElement))
		{
			return 0;
		}
		char[] invalid = Path.GetInvalidFileNameChars();
		HashSet<string> usedFolders = new(StringComparer.OrdinalIgnoreCase);
		List<(CommunityModFile File, string Folder)> work = [];
		foreach (CommunityModFile file in listing.Files.Where(CommunityModGuard.IsInstallableFile))
		{
			string extension = Path.GetExtension(file.FileName);
			if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase) && !CommunityModGuard.ConvertiblePackageExtensions.Contains(extension))
			{
				continue;
			}
			string folderName = new string(Path.GetFileNameWithoutExtension(file.FileName).Where(character => !invalid.Contains(character)).ToArray()).Trim();
			if (folderName.Length == 0 || !usedFolders.Add(folderName))
			{
				folderName = (folderName.Length == 0 ? "" : folderName + " ") + file.Id.ToString(CultureInfo.InvariantCulture);
				usedFolders.Add(folderName);
			}
			work.Add((file, Path.Combine(destination, folderName)));
		}
		int total = 0;
		int finished = 0;
		ParallelOptions options = new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = token };
		await Parallel.ForEachAsync(work, options, async (item, itemToken) =>
		{
			try
			{
				int count = await FetchListedFileAsync(item.File, item.Folder, allowedExtensions, null, itemToken).ConfigureAwait(false);
				Interlocked.Add(ref total, count);
			}
			catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or HttpRequestException or IOException)
			{
				App.Logger?.WriteLine(LogIdent, "The file " + item.File.FileName + " could not be added as options: " + ex.Message);
			}
			progress?.Report("Downloaded " + Interlocked.Increment(ref finished) + " of " + work.Count + " files");
		}).ConfigureAwait(false);
		return total;
	}

	internal static async Task<int> FetchFileAsync(long modId, long fileId, string destination, IReadOnlyCollection<string> allowedExtensions, IProgress<string>? progress, CancellationToken token)
	{
		using JsonDocument document = await GetProfileAsync(modId, token).ConfigureAwait(false);
		CommunityModEntry listing = new CommunityModEntry
		{
			Id = modId,
			Name = ReadString(document.RootElement, "_sName"),
			ProfileUrl = "https://gamebanana.com/mods/" + modId
		};
		if (!ApplyProfile(listing, document.RootElement))
		{
			throw new InvalidOperationException("The mod page no longer belongs to Roblox.");
		}
		CommunityModFile? file = listing.Files.FirstOrDefault(candidate => candidate.Id == fileId)
			?? listing.Files.FirstOrDefault(CommunityModGuard.IsInstallableFile);
		CommunityModVerdict verdict = CommunityModGuard.InspectFile(file);
		if (file == null || !verdict.Allowed)
		{
			throw new InvalidOperationException(verdict.Reason);
		}

		return await FetchListedFileAsync(file, destination, allowedExtensions, progress, token).ConfigureAwait(false);
	}

	private static async Task<int> FetchListedFileAsync(CommunityModFile file, string destination, IReadOnlyCollection<string> allowedExtensions, IProgress<string>? progress, CancellationToken token)
	{
		string staging = Path.Combine(Paths.TempUpdates, "CommunityMods", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(staging);
		string archivePath = Path.Combine(staging, "package" + Path.GetExtension(file.FileName));
		try
		{
			progress?.Report("Downloading " + file.FileName);
			await AcquirePackageAsync(file, archivePath, token).ConfigureAwait(false);

			string extension = Path.GetExtension(file.FileName);
			if (CommunityModGuard.ConvertiblePackageExtensions.Contains(extension))
			{
				progress?.Report("Unpacking the " + extension.TrimStart('.') + " archive");
				archivePath = await ConvertToZipAsync(archivePath, extension, staging, token).ConfigureAwait(false);
			}

			progress?.Report("Unpacking the files");
			return ExtractFlat(archivePath, destination, allowedExtensions, token);
		}
		finally
		{
			TryDeleteDirectory(staging);
		}
	}

	internal static int ExtractFlat(string archivePath, string destination, IReadOnlyCollection<string> allowedExtensions, CancellationToken token = default)
	{
		string root = Path.GetFullPath(destination);
		Directory.CreateDirectory(root);
		List<(int Index, string Target)> plan = [];
		using (ZipArchive archive = ZipFile.OpenRead(archivePath))
		{
			if (archive.Entries.Count > CommunityModGuard.MaxArchiveEntries)
			{
				throw new InvalidOperationException("The package contains more than " + CommunityModGuard.MaxArchiveEntries + " files.");
			}
			long extracted = 0;
			for (int index = 0; index < archive.Entries.Count; index++)
			{
				token.ThrowIfCancellationRequested();
				ZipArchiveEntry entry = archive.Entries[index];
				string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
				if (relative.EndsWith(Path.DirectorySeparatorChar) || string.IsNullOrWhiteSpace(entry.Name) || !allowedExtensions.Contains(Path.GetExtension(relative)))
				{
					continue;
				}
				CommunityModVerdict verdict = CommunityModGuard.InspectRelativePath(relative);
				if (!verdict.Allowed)
				{
					throw new InvalidOperationException(verdict.Reason);
				}
				extracted += entry.Length;
				if (extracted > CommunityModGuard.MaxExtractedBytes)
				{
					throw new InvalidOperationException("The package expands to more than " + CommunityModGuard.MaxExtractedBytes / 1048576 + " MB.");
				}
				string target = Path.GetFullPath(Path.Combine(root, relative));
				if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidOperationException("The package tried to write outside its own folder.");
				}
				plan.Add((index, target));
			}
		}
		int written = ParallelZipWriter.Extract(archivePath, ParallelZipWriter.LastWins(plan), token);
		if (written == 0)
		{
			throw new InvalidOperationException("The package did not contain the expected files.");
		}
		return written;
	}

	internal static int ExtractVerified(string archivePath, string destination, CancellationToken token = default)
	{
		string root = Path.GetFullPath(destination);
		Directory.CreateDirectory(root);
		List<(int Index, string Target)> plan = [];
		using (ZipArchive archive = ZipFile.OpenRead(archivePath))
		{
			if (archive.Entries.Count > CommunityModGuard.MaxArchiveEntries)
			{
				throw new InvalidOperationException("The package contains more than " + CommunityModGuard.MaxArchiveEntries + " files.");
			}
			long extracted = 0;
			for (int index = 0; index < archive.Entries.Count; index++)
			{
				token.ThrowIfCancellationRequested();
				ZipArchiveEntry entry = archive.Entries[index];
				string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
				if (relative.EndsWith(Path.DirectorySeparatorChar) || string.IsNullOrWhiteSpace(entry.Name))
				{
					continue;
				}
				CommunityModVerdict verdict = CommunityModGuard.InspectRelativePath(relative);
				if (!verdict.Allowed)
				{
					throw new InvalidOperationException(verdict.Reason);
				}
				if (!CommunityModGuard.IsInstallableAsset(relative))
				{
					continue;
				}
				string? placed = RobloxContentPlacer.Resolve(relative);
				if (placed == null || ModAutoFixer.IsVersionLocked(placed))
				{
					continue;
				}
				extracted += entry.Length;
				if (extracted > CommunityModGuard.MaxExtractedBytes)
				{
					throw new InvalidOperationException("The package expands to more than " + CommunityModGuard.MaxExtractedBytes / 1048576 + " MB.");
				}
				string target = Path.GetFullPath(Path.Combine(root, placed));
				if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidOperationException("The package tried to write outside its own folder.");
				}
				plan.Add((index, target));
			}
		}
		int written = ParallelZipWriter.Extract(archivePath, ParallelZipWriter.LastWins(plan), token);
		if (written == 0)
		{
			throw new InvalidOperationException("Nothing in the package could be matched to a Roblox client folder, so nothing was installed.");
		}
		return written;
	}

	private static async Task<string> ComputeMd5Async(string path, CancellationToken token)
	{
		await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		using MD5 md5 = MD5.Create();
		byte[] hash = await md5.ComputeHashAsync(stream, token).ConfigureAwait(false);
		return Convert.ToHexString(hash);
	}

	private static async Task<JsonDocument> GetProfileAsync(long modId, CancellationToken token)
	{
		if (ProfileCache.TryGetValue(modId, out (DateTime ExpiresUtc, string Json) cached) && cached.ExpiresUtc > DateTime.UtcNow)
		{
			token.ThrowIfCancellationRequested();
			return JsonDocument.Parse(cached.Json);
		}
		using JsonDocument document = await GetJsonAsync(ApiRoot + "/Mod/" + modId + "/ProfilePage", token).ConfigureAwait(false);
		string json = document.RootElement.GetRawText();
		if (ProfileCache.Count >= 64)
		{
			ProfileCache.Clear();
		}
		ProfileCache[modId] = (DateTime.UtcNow.AddMinutes(10), json);
		return JsonDocument.Parse(json);
	}

	private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
	{
		if (!CommunityModGuard.IsTrustedUrl(url))
		{
			throw new InvalidOperationException("Refused to contact an untrusted host.");
		}
		using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException(await DescribeErrorAsync(response, token).ConfigureAwait(false), null, response.StatusCode);
		}
		await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
	}

	private static async Task<string> DescribeErrorAsync(HttpResponseMessage response, CancellationToken token)
	{
		string summary = "The community API returned " + (int)response.StatusCode + " for " + (response.RequestMessage?.RequestUri?.AbsolutePath ?? "the request");
		try
		{
			string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
			int start = body.AsSpan().IndexOf('{');
			if (start < 0)
			{
				return summary;
			}
			using JsonDocument document = JsonDocument.Parse(body.AsMemory(start));
			List<string> messages = new List<string>();
			CollectErrorMessages(document.RootElement, messages, 0);
			return messages.Count == 0 ? summary : summary + ": " + string.Join("; ", messages);
		}
		catch (Exception)
		{
			return summary;
		}
	}

	private static void CollectErrorMessages(JsonElement element, ICollection<string> messages, int depth)
	{
		if (depth > 4 || element.ValueKind != JsonValueKind.Object || messages.Count >= 4)
		{
			return;
		}
		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (property.Value.ValueKind == JsonValueKind.String
				&& property.NameEquals("_sErrorMessage")
				&& !string.IsNullOrWhiteSpace(property.Value.GetString()))
			{
				messages.Add(property.Value.GetString()!);
			}
			else if (property.Value.ValueKind == JsonValueKind.Object)
			{
				CollectErrorMessages(property.Value, messages, depth + 1);
			}
		}
	}

	private static async Task<JsonDocument> GetJsonLenientAsync(string url, CancellationToken token)
	{
		if (!CommunityModGuard.IsTrustedUrl(url))
		{
			throw new InvalidOperationException("Refused to contact an untrusted host.");
		}
		using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException(await DescribeErrorAsync(response, token).ConfigureAwait(false), null, response.StatusCode);
		}
		string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
		int start = text.AsSpan().IndexOfAny('{', '[');
		if (start < 0)
		{
			throw new JsonException("The community response did not contain JSON.");
		}
		return JsonDocument.Parse(text.AsMemory(start));
	}

	private static async Task<List<JsonElement>> GetRecordsAsync(string url, bool allPages, CancellationToken token)
	{
		List<JsonElement> results = new List<JsonElement>();
		for (int page = 1; page <= MaxCommunityPages; page++)
		{
			token.ThrowIfCancellationRequested();
			string pageUrl = url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "_nPage=" + page;
			using JsonDocument document = await GetJsonLenientAsync(pageUrl, token).ConfigureAwait(false);
			JsonElement root = document.RootElement;
			if (!root.TryGetProperty("_aRecords", out JsonElement records) || records.ValueKind != JsonValueKind.Array)
			{
				break;
			}
			int before = results.Count;
			foreach (JsonElement record in records.EnumerateArray())
			{
				results.Add(record.Clone());
			}
			if (!allPages || results.Count == before)
			{
				break;
			}
			if (!root.TryGetProperty("_aMetadata", out JsonElement metadata))
			{
				break;
			}
			int total = ReadInt(metadata, "_nRecordCount");
			if (results.Count >= total || ReadBool(metadata, "_bIsComplete"))
			{
				break;
			}
		}
		return results;
	}

	private static async Task<List<JsonElement>> GetRecordsSafeAsync(string url, bool allPages, CancellationToken token)
	{
		try
		{
			return await GetRecordsAsync(url, allPages, token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Optional community details could not be loaded: " + ex.Message);
			return new List<JsonElement>();
		}
	}

	private static async Task LoadCommunityDataAsync(CommunityModEntry entry, CancellationToken token)
	{
		Task<List<JsonElement>> commentsTask = GetRecordsSafeAsync(ApiRoot + "/Mod/" + entry.Id + "/Posts", false, token);
		Task<List<JsonElement>> updatesTask = GetRecordsSafeAsync(ApiRoot + "/Mod/" + entry.Id + "/Updates", true, token);
		Task<List<JsonElement>> issuesTask = GetRecordsSafeAsync(ApiRoot + "/Mod/" + entry.Id + "/Todos", true, token);
		await Task.WhenAll(commentsTask, updatesTask, issuesTask).ConfigureAwait(false);

		entry.Comments.Clear();
		foreach (JsonElement record in await commentsTask.ConfigureAwait(false))
		{
			JsonElement author = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("_aPoster", out JsonElement poster) ? poster : default;
			entry.Comments.Add(new CommunityModComment
			{
				Id = ReadLong(record, "_idRow"),
				Author = ReadPerson(author),
				Paragraphs = ToParagraphs(ReadString(record, "_sText")),
				CreatedUtc = ReadTimestamp(record, "_tsDateAdded", "_tsDateModified"),
				Score = ReadInt(record, "_nStampScore"),
				ReplyCount = ReadInt(record, "_nReplyCount")
			});
		}

		entry.Updates.Clear();
		foreach (JsonElement record in await updatesTask.ConfigureAwait(false))
		{
			string summary = record.TryGetProperty("_aPreviewMedia", out JsonElement preview)
				&& preview.TryGetProperty("_aMetadata", out JsonElement metadata)
					? ReadString(metadata, "_sSnippet")
					: "";
			entry.Updates.Add(new CommunityModUpdate
			{
				Id = ReadLong(record, "_idRow"),
				Name = ReadString(record, "_sName"),
				Version = ReadString(record, "_sVersion"),
				Summary = summary,
				Paragraphs = ToParagraphs(ReadString(record, "_sText")),
				CreatedUtc = ReadTimestamp(record, "_tsDateAdded", "_tsDateModified"),
				IsSignificant = ReadBool(record, "_bIsSignificant")
			});
		}

		entry.Issues.Clear();
		foreach (JsonElement record in await issuesTask.ConfigureAwait(false))
		{
			string name = ReadString(record, "_sName");
			if (string.IsNullOrWhiteSpace(name))
			{
				name = ReadString(record, "_sTitle");
			}
			string body = ReadString(record, "_sText");
			if (string.IsNullOrWhiteSpace(body))
			{
				body = ReadString(record, "_sDescription");
			}
			entry.Issues.Add(new CommunityModIssue
			{
				Id = ReadLong(record, "_idRow"),
				Name = name,
				Status = ReadString(record, "_sStatus"),
				Paragraphs = ToParagraphs(body),
				CreatedUtc = ReadTimestamp(record, "_tsDateAdded", "_tsDateModified")
			});
		}

		entry.CommentCount = Math.Max(entry.CommentCount, entry.Comments.Count);
		entry.UpdateCount = Math.Max(entry.UpdateCount, entry.Updates.Count);
		entry.IssueCount = Math.Max(entry.IssueCount, entry.Issues.Count);
	}

	private static CommunityModPerson ReadPerson(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return new CommunityModPerson();
		}
		List<string> achievements = new List<string>();
		foreach (string property in new[] { "_aNormalMedals", "_aRareMedals", "_aLegendaryMedals" })
		{
			if (!element.TryGetProperty(property, out JsonElement medals) || medals.ValueKind != JsonValueKind.Array)
			{
				continue;
			}
			foreach (JsonElement medal in medals.EnumerateArray())
			{
				if (medal.ValueKind == JsonValueKind.Array && medal.GetArrayLength() > 1)
				{
					string value = medal[1].ValueKind == JsonValueKind.String ? medal[1].GetString() ?? "" : "";
					if (!string.IsNullOrWhiteSpace(value))
					{
						achievements.Add(value);
					}
				}
			}
		}
		return new CommunityModPerson
		{
			Name = ReadString(element, "_sName"),
			AvatarUrl = ReadString(element, "_sAvatarUrl"),
			ProfileUrl = ReadString(element, "_sProfileUrl"),
			Title = ReadString(element, "_sUserTitle"),
			HonoraryTitle = ReadString(element, "_sHonoraryTitle"),
			Points = ReadInt(element, "_nPoints"),
			JoinedUtc = ReadTimestamp(element, "_tsJoinDate"),
			Achievements = achievements
		};
	}

	private static void ReadLicenseRules(JsonElement root, ICollection<CommunityModLicenseRule> rules)
	{
		if (!root.TryGetProperty("_aLicenseChecklist", out JsonElement checklist) || checklist.ValueKind != JsonValueKind.Object)
		{
			return;
		}
		foreach ((string property, string group) in new[] { ("yes", "Allowed"), ("ask", "Ask first"), ("no", "Not allowed") })
		{
			if (!checklist.TryGetProperty(property, out JsonElement items) || items.ValueKind != JsonValueKind.Array)
			{
				continue;
			}
			foreach (JsonElement item in items.EnumerateArray())
			{
				string text = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : ReadString(item, "_sText");
				if (!string.IsNullOrWhiteSpace(text))
				{
					rules.Add(new CommunityModLicenseRule { Group = group, Text = PlainText(text) });
				}
			}
		}
	}

	private static CommunityModEntry? ReadListing(JsonElement record)
	{
		if (!string.Equals(ReadString(record, "_sModelName"), "Mod", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		if (record.TryGetProperty("_bIsObsolete", out JsonElement obsolete) && obsolete.ValueKind == JsonValueKind.True)
		{
			return null;
		}
		if (record.TryGetProperty("_bHasContentRatings", out JsonElement rated) && rated.ValueKind == JsonValueKind.True)
		{
			return null;
		}
		if (!record.TryGetProperty("_bHasFiles", out JsonElement hasFiles) || hasFiles.ValueKind != JsonValueKind.True)
		{
			return null;
		}
		if (record.TryGetProperty("_aGame", out JsonElement listingGame)
			&& listingGame.ValueKind == JsonValueKind.Object
			&& listingGame.TryGetProperty("_idRow", out JsonElement listingGameId)
			&& listingGameId.ValueKind == JsonValueKind.Number
			&& listingGameId.GetInt32() != CommunityModGuard.RobloxGameId)
		{
			return null;
		}

		List<string> tags = new List<string>();
		if (record.TryGetProperty("_aTags", out JsonElement tagArray) && tagArray.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement tag in tagArray.EnumerateArray())
			{
				string value = tag.ValueKind == JsonValueKind.String ? tag.GetString() ?? "" : ReadString(tag, "_sTitle");
				if (!string.IsNullOrWhiteSpace(value))
				{
					tags.Add(value);
				}
			}
		}

		JsonElement submitter = record.TryGetProperty("_aSubmitter", out JsonElement s) ? s : default;
		JsonElement rootCategory = record.TryGetProperty("_aRootCategory", out JsonElement rc) ? rc : default;
		JsonElement subCategory = record.TryGetProperty("_aSubCategory", out JsonElement sc) ? sc : default;

		return new CommunityModEntry
		{
			Id = ReadLong(record, "_idRow"),
			Name = ReadString(record, "_sName"),
			SourceName = SourceName,
			SupportsInstall = true,
			Version = ReadString(record, "_sVersion"),
			ProfileUrl = ReadString(record, "_sProfileUrl"),
			SuperCategory = rootCategory.ValueKind == JsonValueKind.Object ? ReadString(rootCategory, "_sName") : "",
			Category = subCategory.ValueKind == JsonValueKind.Object ? ReadString(subCategory, "_sName") : "",
			Author = submitter.ValueKind == JsonValueKind.Object ? ReadString(submitter, "_sName") : "",
			AuthorAvatar = submitter.ValueKind == JsonValueKind.Object ? ReadString(submitter, "_sAvatarUrl") : "",
			IconUrl = ReadPreviewIcon(record),
			LikeCount = ReadInt(record, "_nLikeCount"),
			ViewCount = ReadInt(record, "_nViewCount"),
			UpdatedUtc = ReadTimestamp(record, "_tsDateUpdated", "_tsDateModified", "_tsDateAdded"),
			Tags = tags
		};
	}

	private static string ReadPreviewIcon(JsonElement record)
	{
		if (!record.TryGetProperty("_aPreviewMedia", out JsonElement media))
		{
			return "";
		}
		if (!media.TryGetProperty("_aImages", out JsonElement images) || images.ValueKind != JsonValueKind.Array)
		{
			return "";
		}
		foreach (JsonElement image in images.EnumerateArray())
		{
			string baseUrl = ReadString(image, "_sBaseUrl");
			string file = ReadString(image, "_sFile220");
			if (string.IsNullOrEmpty(file))
			{
				file = ReadString(image, "_sFile");
			}
			if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(file))
			{
				continue;
			}
			string url = baseUrl.TrimEnd('/') + "/" + file;
			if (CommunityModGuard.IsTrustedUrl(url))
			{
				return url;
			}
		}
		return "";
	}

	private static string BuildModName(CommunityModEntry entry)
	{
		string name = entry.Name;
		foreach (char invalid in Path.GetInvalidFileNameChars())
		{
			name = name.Replace(invalid, ' ');
		}
		name = WhitespacePattern().Replace(name, " ").Trim();
		if (name.Length > 60)
		{
			name = name[..60].Trim();
		}
		return string.IsNullOrEmpty(name) ? "Community mod " + entry.Id : name;
	}

	private static string Summarize(string html)
	{
		if (string.IsNullOrWhiteSpace(html))
		{
			return "";
		}
		string text = PlainText(html);
		return text.Length > 240 ? text[..240].TrimEnd() + "..." : text;
	}

	private static string PlainText(string html)
	{
		string text = HtmlTagPattern().Replace(html, " ");
		text = System.Net.WebUtility.HtmlDecode(text);
		return WhitespacePattern().Replace(text.Replace('\n', ' ').Replace('\r', ' '), " ").Trim();
	}

	public static IReadOnlyList<string> ToParagraphs(string html)
	{
		if (string.IsNullOrWhiteSpace(html))
		{
			return Array.Empty<string>();
		}
		string normalized = Regex.Replace(html, "<(strong|b)(?:\\s[^>]*)?>", "**", RegexOptions.IgnoreCase);
		normalized = Regex.Replace(normalized, "</(strong|b)>", "**", RegexOptions.IgnoreCase);
		normalized = Regex.Replace(normalized, "<(em|i)(?:\\s[^>]*)?>", "*", RegexOptions.IgnoreCase);
		normalized = Regex.Replace(normalized, "</(em|i)>", "*", RegexOptions.IgnoreCase);
		normalized = Regex.Replace(normalized, "<h[1-6](?:\\s[^>]*)?>", "\n**", RegexOptions.IgnoreCase);
		normalized = Regex.Replace(normalized, "</h[1-6]>", "**\n", RegexOptions.IgnoreCase);
		normalized = Regex.Replace(normalized, "<li(?:\\s[^>]*)?>", "\n▪ ", RegexOptions.IgnoreCase);
		normalized = normalized
			.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
			.Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
			.Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
			.Replace("</p>", "\n\n", StringComparison.OrdinalIgnoreCase)
			.Replace("</div>", "\n", StringComparison.OrdinalIgnoreCase)
			.Replace("</li>", "\n", StringComparison.OrdinalIgnoreCase);
		string text = System.Net.WebUtility.HtmlDecode(HtmlTagPattern().Replace(normalized, ""));
		return text
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(line => line.Length > 0)
			.ToList();
	}

	private static string ReadString(JsonElement element, string property)
	{
		if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement value))
		{
			return "";
		}
		return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
	}

	private static int ReadInt(JsonElement element, string property)
	{
		return element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty(property, out JsonElement value)
			&& value.ValueKind == JsonValueKind.Number
			&& value.TryGetInt32(out int parsed)
				? parsed
				: 0;
	}

	private static long ReadLong(JsonElement element, string property)
	{
		return element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty(property, out JsonElement value)
			&& value.ValueKind == JsonValueKind.Number
			&& value.TryGetInt64(out long parsed)
				? parsed
				: 0L;
	}

	private static bool ReadBool(JsonElement element, string property)
	{
		return element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty(property, out JsonElement value)
			&& value.ValueKind == JsonValueKind.True;
	}

	private static DateTime ReadTimestamp(JsonElement element, params string[] properties)
	{
		foreach (string property in properties)
		{
			long seconds = ReadLong(element, property);
			if (seconds > 0)
			{
				return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
			}
		}
		return default;
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The staging folder could not be removed: " + ex.Message);
		}
	}
}
