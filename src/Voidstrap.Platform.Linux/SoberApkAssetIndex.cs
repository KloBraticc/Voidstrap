using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Voidstrap.Platform;

namespace Voidstrap.Platform.Linux;

public sealed record SoberApkAssetIndexPaths(
	string SoberDataDirectory,
	string CacheFile)
{
	public string PackagesDirectory => Path.Combine(SoberDataDirectory, "packages");

	public string ActivePackageFile => Path.Combine(SoberDataDirectory, "assets", "base.apk");

	public static SoberApkAssetIndexPaths CreateDefault()
	{
		string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrWhiteSpace(home) || !Path.IsPathRooted(home))
			throw new DirectoryNotFoundException("The user home directory is unavailable");

		string cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? Path.Combine(home, ".cache");
		if (!Path.IsPathRooted(cacheHome))
			cacheHome = Path.Combine(home, ".cache");

		return new SoberApkAssetIndexPaths(
			Path.Combine(home, ".var", "app", "org.vinegarhq.Sober", "data", "sober"),
			Path.Combine(cacheHome, "voidstrap", "sober.apk.assets.json"));
	}
}

public sealed class SoberApkAssetIndex
{
	private readonly Dictionary<string, string> _canonicalPaths;

	internal SoberApkAssetIndex(string packageFile, string packageSha256, IEnumerable<string> canonicalPaths)
	{
		PackageFile = packageFile;
		PackageSha256 = packageSha256;
		_canonicalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string path in canonicalPaths)
		{
			OperationResult<string> normalized = Normalize(path);
			if (!normalized.Succeeded || normalized.Value is null || !string.Equals(normalized.Value, path, StringComparison.Ordinal))
				throw new InvalidDataException("The Sober package contains an invalid asset path");
			if (!_canonicalPaths.TryAdd(normalized.Value, normalized.Value))
				throw new InvalidDataException("The Sober package contains asset paths that differ only by case");
		}
	}

	public string PackageFile { get; }

	public string PackageSha256 { get; }

	public IReadOnlyCollection<string> CanonicalPaths => _canonicalPaths.Values;

	public OperationResult<string> Resolve(string logicalPath)
	{
		OperationResult<string> normalized = Normalize(logicalPath);
		if (!normalized.Succeeded || normalized.Value is null)
			return normalized;

		return _canonicalPaths.TryGetValue(normalized.Value, out string? canonical)
			? OperationResult<string>.Success(canonical)
			: OperationResult<string>.Fail("SoberAssetNotInPackage", "The modification does not match an asset in the installed Sober Roblox package");
	}

	internal static OperationResult<string> Normalize(string? logicalPath)
	{
		if (string.IsNullOrWhiteSpace(logicalPath) || logicalPath.IndexOf('\0') >= 0)
			return OperationResult<string>.Fail("SoberAssetPathInvalid", "The modification contains an invalid asset path");

		string normalized = logicalPath.Replace('\\', '/').Normalize(NormalizationForm.FormC);
		if (normalized.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(normalized) || normalized.Contains(':', StringComparison.Ordinal))
			return OperationResult<string>.Fail("SoberAssetPathInvalid", "The modification contains an unsafe asset path");

		string[] segments = normalized.Split('/', StringSplitOptions.None);
		if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
			return OperationResult<string>.Fail("SoberAssetPathInvalid", "The modification contains an unsafe asset path");

		return OperationResult<string>.Success(string.Join('/', segments));
	}
}

public sealed class SoberApkAssetIndexProvider
{
	private const int MaximumAssetEntries = 300000;
	private const int MaximumPathLength = 4096;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	private readonly SoberApkAssetIndexPaths _paths;

	public SoberApkAssetIndexProvider(SoberApkAssetIndexPaths paths)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	public static SoberApkAssetIndexProvider CreateDefault()
	{
		return new SoberApkAssetIndexProvider(SoberApkAssetIndexPaths.CreateDefault());
	}

	public async Task<OperationResult<SoberApkAssetIndex>> LoadAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			OperationResult<string> packageResult = FindPackageFile(cancellationToken);
			if (!packageResult.Succeeded || packageResult.Value is null)
				return OperationResult<SoberApkAssetIndex>.Fail(packageResult.Failure!.Code, packageResult.Failure.Message, packageResult.Failure.State);

			string packageFile = packageResult.Value;
			string sha256;
			await using (FileStream stream = new(packageFile, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
				sha256 = Convert.ToHexString(digest);
			}

			OperationResult<SoberApkAssetIndex>? cached = await TryReadCacheAsync(packageFile, sha256, cancellationToken).ConfigureAwait(false);
			if (cached is not null)
				return cached;

			OperationResult<List<string>> readResult = ReadPackageAssets(packageFile, cancellationToken);
			if (!readResult.Succeeded || readResult.Value is null)
				return OperationResult<SoberApkAssetIndex>.Fail(readResult.Failure!.Code, readResult.Failure.Message, readResult.Failure.State);

			SoberApkAssetIndex index;
			try
			{
				index = new SoberApkAssetIndex(packageFile, sha256, readResult.Value);
			}
			catch (InvalidDataException ex)
			{
				return OperationResult<SoberApkAssetIndex>.Fail("SoberApkAssetCaseCollision", ex.Message);
			}

			await WriteCacheAsync(index, cancellationToken).ConfigureAwait(false);
			return OperationResult<SoberApkAssetIndex>.Success(index);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (InvalidDataException ex)
		{
			return OperationResult<SoberApkAssetIndex>.Fail("SoberApkInvalid", "The installed Sober Roblox package is invalid: " + ex.Message);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			return OperationResult<SoberApkAssetIndex>.Fail("SoberApkReadFailed", "The installed Sober Roblox package could not be indexed: " + ex.Message);
		}
	}

	private OperationResult<string> FindPackageFile(CancellationToken cancellationToken)
	{
		string activePackage = Path.GetFullPath(_paths.ActivePackageFile);
		if (File.Exists(activePackage))
			return ValidatePackageFile(activePackage);

		string packagesRoot = Path.GetFullPath(_paths.PackagesDirectory);
		if (!Directory.Exists(packagesRoot))
			return OperationResult<string>.Fail("SoberApkMissing", "Sober must finish installing Roblox before modifications can be applied", CapabilityState.RequiresExternalRuntime);
		if (IsSymbolicLink(new DirectoryInfo(packagesRoot)))
			return OperationResult<string>.Fail("SoberApkPathInvalid", "The Sober packages directory cannot be a symbolic link");

		List<FileInfo> candidates = [];
		Stack<DirectoryInfo> pending = new();
		pending.Push(new DirectoryInfo(packagesRoot));
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DirectoryInfo directory = pending.Pop();
			foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (IsSymbolicLink(entry))
					return OperationResult<string>.Fail("SoberApkPathInvalid", "The Sober packages directory contains a symbolic link");
				if (entry is DirectoryInfo child)
				{
					pending.Push(child);
					continue;
				}
				if (entry is FileInfo file
					&& file.Extension.Equals(".apk", StringComparison.OrdinalIgnoreCase)
					&& string.Equals(file.Directory?.Name, "com.roblox.client", StringComparison.Ordinal))
				{
					candidates.Add(file);
				}
			}
		}

		if (candidates.Count == 0)
			return OperationResult<string>.Fail("SoberApkMissing", "Sober must finish installing Roblox before modifications can be applied", CapabilityState.RequiresExternalRuntime);

		List<FileInfo> preferred = candidates
			.Where(static candidate => string.Equals(candidate.Name, "base.apk", StringComparison.Ordinal))
			.ToList();
		if (preferred.Count == 0)
			preferred = candidates;

		preferred.Sort(static (left, right) =>
		{
			int size = right.Length.CompareTo(left.Length);
			return size != 0 ? size : StringComparer.Ordinal.Compare(left.FullName, right.FullName);
		});

		return ValidatePackageFile(preferred[0].FullName);
	}

	private static OperationResult<string> ValidatePackageFile(string packageFile)
	{
		FileInfo file = new(packageFile);
		if (IsSymbolicLink(file) || (file.Attributes & FileAttributes.Device) != 0)
			return OperationResult<string>.Fail("SoberApkPathInvalid", "The Sober Roblox package must be a regular file");
		if (file.Length == 0)
			return OperationResult<string>.Fail("SoberApkInvalid", "The installed Sober Roblox package is empty");
		return OperationResult<string>.Success(file.FullName);
	}

	private static OperationResult<List<string>> ReadPackageAssets(string packageFile, CancellationToken cancellationToken)
	{
		using FileStream stream = new(packageFile, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
		using ZipArchive archive = new(stream, ZipArchiveMode.Read, false);
		List<string> paths = [];
		HashSet<string> exact = new(StringComparer.Ordinal);
		HashSet<string> insensitive = new(StringComparer.OrdinalIgnoreCase);
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string fullName = entry.FullName.Normalize(NormalizationForm.FormC);
			if (!fullName.StartsWith("assets/", StringComparison.Ordinal) || fullName.EndsWith("/", StringComparison.Ordinal))
				continue;
			if (paths.Count >= MaximumAssetEntries)
				return OperationResult<List<string>>.Fail("SoberApkTooLarge", "The installed Sober Roblox package contains too many assets");
			if (fullName.Length > MaximumPathLength)
				return OperationResult<List<string>>.Fail("SoberApkPathInvalid", "The installed Sober Roblox package contains an invalid asset path");

			int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
			if (unixType != 0 && unixType != 0x8000)
				return OperationResult<List<string>>.Fail("SoberApkAssetTypeInvalid", "The installed Sober Roblox package contains a nonregular asset");

			OperationResult<string> normalized = SoberApkAssetIndex.Normalize(fullName[7..]);
			if (!normalized.Succeeded || normalized.Value is null)
				return OperationResult<List<string>>.Fail("SoberApkPathInvalid", "The installed Sober Roblox package contains an invalid asset path");
			if (!exact.Add(normalized.Value))
				return OperationResult<List<string>>.Fail("SoberApkDuplicateAsset", "The installed Sober Roblox package contains a duplicate asset path");
			if (!insensitive.Add(normalized.Value))
				return OperationResult<List<string>>.Fail("SoberApkAssetCaseCollision", "The installed Sober Roblox package contains asset paths that differ only by case");
			paths.Add(normalized.Value);
		}

		if (paths.Count == 0)
			return OperationResult<List<string>>.Fail("SoberApkAssetsMissing", "The installed Sober Roblox package does not contain an assets directory");

		paths.Sort(StringComparer.Ordinal);
		return OperationResult<List<string>>.Success(paths);
	}

	private async Task<OperationResult<SoberApkAssetIndex>?> TryReadCacheAsync(string packageFile, string sha256, CancellationToken cancellationToken)
	{
		string cacheFile = Path.GetFullPath(_paths.CacheFile);
		if (!File.Exists(cacheFile))
			return null;
		if (IsSymbolicLink(new FileInfo(cacheFile)))
			return OperationResult<SoberApkAssetIndex>.Fail("SoberApkCacheInvalid", "The Sober asset index cache cannot be a symbolic link");

		await using FileStream stream = new(cacheFile, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		CacheDocument? cache = await JsonSerializer.DeserializeAsync<CacheDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
		if (cache is null || !string.Equals(cache.PackageSha256, sha256, StringComparison.Ordinal) || cache.Paths is null)
			return null;

		try
		{
			return OperationResult<SoberApkAssetIndex>.Success(new SoberApkAssetIndex(packageFile, sha256, cache.Paths));
		}
		catch (InvalidDataException ex)
		{
			return OperationResult<SoberApkAssetIndex>.Fail("SoberApkCacheInvalid", ex.Message);
		}
	}

	private async Task WriteCacheAsync(SoberApkAssetIndex index, CancellationToken cancellationToken)
	{
		string cacheFile = Path.GetFullPath(_paths.CacheFile);
		string? directory = Path.GetDirectoryName(cacheFile);
		if (string.IsNullOrWhiteSpace(directory))
			throw new IOException("The Sober asset index cache path is invalid");
		Directory.CreateDirectory(directory);
		if (IsSymbolicLink(new DirectoryInfo(directory)) || (File.Exists(cacheFile) && IsSymbolicLink(new FileInfo(cacheFile))))
			throw new IOException("The Sober asset index cache path is unsafe");

		string temporary = Path.Combine(directory, ".voidstrap." + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			CacheDocument cache = new(index.PackageSha256, index.CanonicalPaths.Order(StringComparer.Ordinal).ToArray());
			await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, cancellationToken).ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
				stream.Flush(true);
			}
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			File.Move(temporary, cacheFile, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	private static bool IsSymbolicLink(FileSystemInfo entry)
	{
		entry.Refresh();
		return entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0);
	}

	private sealed record CacheDocument(string PackageSha256, string[] Paths);
}
