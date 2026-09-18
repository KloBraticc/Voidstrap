using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.AppData;
using ZstdSharp;

namespace Voidstrap.Utility;

internal sealed class InsufficientSpaceException(string message) : IOException(message)
{
}

internal static class RobloxInstallCompression
{
	private const string LOG_IDENT = "RobloxInstallCompression";

	public const string ArchiveExtension = ".tar.zst";

	private const string TemporaryArchiveExtension = ".tar.zst.tmp";

	private const string ExtractingSuffix = ".extracting";

	private const string DeletingSuffix = ".deleting";

	private const int CompressionLevel = 6;

	private const double RequiredFreeSpaceFactor = 2.5;

	private static readonly int CompressionWorkers = Math.Max(1, Environment.ProcessorCount - 1);

	private static readonly SemaphoreSlim CompressGate = new(1, 1);

	private static readonly long InUseHoldMilliseconds = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

	private static long _inUseUntil;

	private static string LockPath => Path.Combine(Paths.Base, "InstallCompression.lock");

	public static bool Supported => Platform.IsWindows;

	public static IEnumerable<IAppData> Installs()
	{
		yield return new RobloxPlayerData();
		yield return new RobloxStudioData();
	}

	public static string ArchivePathFor(string directory)
	{
		return directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ArchiveExtension;
	}

	public static bool HasInstall(IAppData data)
	{
		return InstallDirectory(data) is string directory && (File.Exists(Path.Combine(directory, data.ExecutableName)) || File.Exists(ArchivePathFor(directory)));
	}

	public static bool IsCompressed(IAppData data)
	{
		return InstallDirectory(data) is string directory && !Directory.Exists(directory) && File.Exists(ArchivePathFor(directory));
	}

	private static string? InstallDirectory(IAppData data)
	{
		string directory = Path.GetFullPath(data.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string root = Path.GetFullPath(data.VersionsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(directory, root, StringComparison.OrdinalIgnoreCase) ? null : directory;
	}

	private static bool IsRunning(IAppData data)
	{
		Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(data.ExecutableName));
		try
		{
			return processes.Length > 0;
		}
		finally
		{
			foreach (Process process in processes)
				process.Dispose();
		}
	}

	public static async Task<IDisposable?> AcquireLockAsync(TimeSpan wait, CancellationToken token)
	{
		if (!Paths.Initialized)
			return null;
		Stopwatch clock = Stopwatch.StartNew();
		while (true)
		{
			try
			{
				Directory.CreateDirectory(Paths.Base);
				return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
			}
			catch (IOException) when (clock.Elapsed < wait)
			{
				await Task.Delay(250, token).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}
	}

	public static string? PlayerFolder()
	{
		RobloxPlayerData player = new();
		return EnsureExtracted(player) ? player.Directory : null;
	}

	public static void ReleaseInUseHold()
	{
		Interlocked.Exchange(ref _inUseUntil, 0);
	}

	public static bool EnsureExtracted(IAppData data)
	{
		try
		{
			if (IsCompressed(data))
				Interlocked.Exchange(ref _inUseUntil, Environment.TickCount64 + InUseHoldMilliseconds);
			return Task.Run(() => EnsureExtractedAsync(data, null, false, CancellationToken.None)).GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Could not unpack " + data.ProductName + ": " + ex.Message);
			return false;
		}
	}

	public static async Task<bool> EnsureExtractedAsync(IAppData data, Action<string>? status, bool lockHeld, CancellationToken token, Action<string, double>? progress = null)
	{
		if (!Supported || InstallDirectory(data) is not string directory)
			return false;
		await CleanupLeftoversAsync(data).ConfigureAwait(false);
		string archive = ArchivePathFor(directory);
		if (Directory.Exists(directory) || !File.Exists(archive))
			return Directory.Exists(directory);
		using IDisposable? heldLock = lockHeld ? null : await AcquireLockAsync(TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
		if (Directory.Exists(directory) || !File.Exists(archive))
			return Directory.Exists(directory);
		long archiveBytes = new FileInfo(archive).Length;
		long required = (long)(archiveBytes * RequiredFreeSpaceFactor);
		long free = Filesystem.GetFreeDiskSpace(directory);
		if (free > 0 && free < required)
			throw new InsufficientSpaceException($"Not enough free disk space to unpack {data.ProductName}: {required / 1048576} MB needed, {free / 1048576} MB free");
		status?.Invoke("Unpacking " + data.ProductName);
		Action<double> report = Throttle(progress, "Unpacking " + data.ProductName, 0.0, 1.0);
		report(0.0);
		Stopwatch clock = Stopwatch.StartNew();
		string extracting = directory + ExtractingSuffix;
		await DeleteDirectoryQuietlyAsync(extracting).ConfigureAwait(false);
		Directory.CreateDirectory(extracting);
		try
		{
			await Task.Run(() => ExtractArchive(archive, extracting, token, report), token).ConfigureAwait(false);
			Directory.Move(extracting, directory);
		}
		catch
		{
			await DeleteDirectoryQuietlyAsync(extracting).ConfigureAwait(false);
			throw;
		}
		File.Delete(archive);
		App.Logger.WriteLine(LOG_IDENT, $"Unpacked {data.ProductName} in {clock.ElapsedMilliseconds} ms");
		return true;
	}

	public static async Task<bool> CompressAsync(IAppData data, CancellationToken token, Action<string, double>? progress = null)
	{
		if (!Supported || InstallDirectory(data) is not string directory || !Directory.Exists(directory))
			return false;
		if (!File.Exists(Path.Combine(directory, data.ExecutableName)))
			return false;
		if (IsRunning(data))
		{
			App.Logger.WriteLine(LOG_IDENT, data.ProductName + " is running, compression skipped");
			return false;
		}
		if (data is RobloxPlayerData && App.Settings.Prop.LaunchWithoutVoidstrap)
		{
			App.Logger.WriteLine(LOG_IDENT, data.ProductName + " launches without Voidstrap, so it stays uncompressed");
			return false;
		}
		if (Environment.TickCount64 < Interlocked.Read(ref _inUseUntil))
		{
			App.Logger.WriteLine(LOG_IDENT, data.ProductName + " was unpacked for a feature in this window, compression waits until the next Roblox session ends");
			return false;
		}
		await CompressGate.WaitAsync(token).ConfigureAwait(false);
		string archive = ArchivePathFor(directory);
		string temporary = directory + TemporaryArchiveExtension;
		try
		{
			Stopwatch clock = Stopwatch.StartNew();
			string fingerprint = Fingerprint(directory, out long rawBytes, out int fileCount);
			string title = "Compressing " + data.ProductName;
			Action<double> writing = Throttle(progress, title, 0.0, 0.8);
			writing(0.0);
			Task write = Task.Run(() => WriteArchive(directory, temporary, rawBytes, token, writing), token);
			Task<Dictionary<string, byte[]>> hashing = Task.Run(() => HashFiles(directory, token), token);
			await Task.WhenAll(write, hashing).ConfigureAwait(false);
			Dictionary<string, byte[]> hashes = await hashing.ConfigureAwait(false);
			if (hashes.Count != fileCount)
				throw new InvalidDataException($"The install changed while it was read, {hashes.Count} files now, {fileCount} before");
			await Task.Run(() => VerifyArchive(directory, temporary, hashes, token, Throttle(progress, title, 0.8, 0.2)), token).ConfigureAwait(false);
			using (IDisposable? heldLock = await AcquireLockAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false))
			{
				if (heldLock == null || IsRunning(data) || Fingerprint(directory, out _, out _) != fingerprint)
				{
					App.Logger.WriteLine(LOG_IDENT, data.ProductName + " changed or started during compression, keeping it uncompressed");
					File.Delete(temporary);
					return false;
				}
				File.Move(temporary, archive, overwrite: true);
				try
				{
					Directory.Move(directory, directory + DeletingSuffix);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					File.Delete(archive);
					App.Logger.WriteLine(LOG_IDENT, data.ProductName + " files are in use, keeping it uncompressed: " + ex.Message);
					return false;
				}
			}
			await DeleteDirectoryQuietlyAsync(directory + DeletingSuffix).ConfigureAwait(false);
			long packedBytes = new FileInfo(archive).Length;
			App.Logger.WriteLine(LOG_IDENT, $"Compressed {data.ProductName}: {rawBytes / 1048576} MB to {packedBytes / 1048576} MB ({(double)rawBytes / Math.Max(1, packedBytes):0.00}x) in {clock.Elapsed.TotalSeconds:0.0}s");
			return true;
		}
		catch (OperationCanceledException)
		{
			TryDelete(temporary);
			App.Logger.WriteLine(LOG_IDENT, "Compression of " + data.ProductName + " was cancelled, it stays uncompressed");
			throw;
		}
		catch (Exception ex)
		{
			TryDelete(temporary);
			App.Logger.WriteLine(LOG_IDENT, "Could not compress " + data.ProductName + ": " + ex.Message);
			return false;
		}
		finally
		{
			CompressGate.Release();
		}
	}

	public static async Task CompressAllAsync(CancellationToken token, Action<string, double>? progress = null)
	{
		if (!Supported || !App.Settings.Prop.CompressRobloxInstalls)
			return;
		foreach (IAppData data in Installs())
		{
			if (!App.Settings.Prop.CompressRobloxInstalls)
				return;
			await CompressAsync(data, token, progress).ConfigureAwait(false);
		}
	}

	public static async Task ExtractAllAsync(CancellationToken token, Action<string, double>? progress = null)
	{
		if (!Supported)
			return;
		foreach (IAppData data in Installs())
		{
			try
			{
				await EnsureExtractedAsync(data, null, false, token, progress).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.WriteLine(LOG_IDENT, "Could not unpack " + data.ProductName + ": " + ex.Message);
			}
		}
	}

	private static Action<double> Throttle(Action<string, double>? progress, string title, double start, double span)
	{
		int last = -1;
		return fraction =>
		{
			int percent = (int)(Math.Clamp(start + (fraction * span), 0.0, 0.99) * 100.0);
			if (progress == null || percent == last)
				return;
			last = percent;
			progress(title, percent / 100.0);
		};
	}

	private static string EntryPath(string root, string name)
	{
		string path = Path.GetFullPath(Path.Combine(root, name));
		if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("Archive entry escapes the install folder: " + name);
		return path;
	}

	private static void WriteArchive(string directory, string temporary, long totalBytes, CancellationToken token, Action<double> progress)
	{
		using FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
		using (CompressionStream compressed = new(output, CompressionLevel))
		{
			compressed.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
			compressed.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_nbWorkers, CompressionWorkers);
			using TarWriter writer = new(compressed, TarEntryFormat.Pax, leaveOpen: true);
			long written = 0;
			foreach (FileSystemInfo item in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
			{
				token.ThrowIfCancellationRequested();
				string name = Path.GetRelativePath(directory, item.FullName).Replace(Path.DirectorySeparatorChar, '/');
				if (item is DirectoryInfo)
				{
					writer.WriteEntry(item.FullName, name + "/");
					continue;
				}
				writer.WriteEntry(item.FullName, name);
				written += ((FileInfo)item).Length;
				progress((double)written / Math.Max(1, totalBytes));
			}
		}
		output.Flush(flushToDisk: true);
	}

	private static Dictionary<string, byte[]> HashFiles(string directory, CancellationToken token)
	{
		Dictionary<string, byte[]> hashes = new(StringComparer.OrdinalIgnoreCase);
		foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
		{
			token.ThrowIfCancellationRequested();
			using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
			hashes[Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/')] = SHA256.HashData(stream);
		}
		return hashes;
	}

	private static void VerifyArchive(string directory, string archive, Dictionary<string, byte[]> expected, CancellationToken token, Action<double> progress)
	{
		int files = 0;
		using FileStream input = new(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
		double length = Math.Max(1, input.Length);
		using DecompressionStream decompressed = new(input);
		using TarReader reader = new(decompressed);
		while (reader.GetNextEntry() is TarEntry entry)
		{
			token.ThrowIfCancellationRequested();
			string path = EntryPath(directory, entry.Name);
			if (entry.EntryType == TarEntryType.Directory)
			{
				if (!Directory.Exists(path))
					throw new InvalidDataException("Archive has a folder the install does not: " + entry.Name);
				continue;
			}
			if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
				throw new InvalidDataException("Archive has an unsupported entry: " + entry.Name);
			if (entry.DataStream == null || !expected.TryGetValue(entry.Name, out byte[]? original))
				throw new InvalidDataException("Archive entry does not match the install: " + entry.Name);
			if (!SHA256.HashData(entry.DataStream).AsSpan().SequenceEqual(original))
				throw new InvalidDataException("Archived copy differs from the install: " + entry.Name);
			files++;
			progress(input.Position / length);
		}
		if (files != expected.Count)
			throw new InvalidDataException($"Archive holds {files} files, the install has {expected.Count}");
	}

	private static void ExtractArchive(string archive, string destination, CancellationToken token, Action<double> progress)
	{
		using FileStream input = new(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
		double length = Math.Max(1, input.Length);
		using DecompressionStream decompressed = new(input);
		using TarReader reader = new(decompressed);
		while (reader.GetNextEntry() is TarEntry entry)
		{
			token.ThrowIfCancellationRequested();
			string path = EntryPath(destination, entry.Name);
			if (entry.EntryType == TarEntryType.Directory)
			{
				Directory.CreateDirectory(path);
				continue;
			}
			if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
				throw new InvalidDataException("Archive has an unsupported entry: " + entry.Name);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			entry.ExtractToFile(path, overwrite: false);
			progress(input.Position / length);
		}
	}

	private static string Fingerprint(string directory, out long totalBytes, out int fileCount)
	{
		long bytes = 0;
		int count = 0;
		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.Ordinal))
		{
			hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks + "\n"));
			bytes += file.Length;
			count++;
		}
		totalBytes = bytes;
		fileCount = count;
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	private static async Task CleanupLeftoversAsync(IAppData data)
	{
		if (InstallDirectory(data) is not string directory)
			return;
		if (!Directory.Exists(directory + ExtractingSuffix) && !Directory.Exists(directory + DeletingSuffix) && !File.Exists(directory + TemporaryArchiveExtension))
			return;
		if (IsLockBusy())
			return;
		await DeleteDirectoryQuietlyAsync(directory + ExtractingSuffix).ConfigureAwait(false);
		await DeleteDirectoryQuietlyAsync(directory + DeletingSuffix).ConfigureAwait(false);
		TryDelete(directory + TemporaryArchiveExtension);
	}

	public static void DeleteStaleArchives(string versionsRoot, IEnumerable<string> keepFolderNames)
	{
		try
		{
			if (!Directory.Exists(versionsRoot))
				return;
			HashSet<string> keep = new(keepFolderNames.Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
			foreach (string archive in Directory.GetFiles(versionsRoot, "version-*" + ArchiveExtension))
			{
				string name = Path.GetFileName(archive)[..^ArchiveExtension.Length];
				if (CommonAppData.IsVersionGuidValid(name) && !keep.Contains(name))
				{
					TryDelete(archive);
					App.Logger.WriteLine(LOG_IDENT, "Deleted stale archive " + Path.GetFileName(archive));
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger.WriteLine(LOG_IDENT, "Stale archive cleanup failed: " + ex.Message);
		}
	}

	private static bool IsLockBusy()
	{
		if (!Paths.Initialized || !File.Exists(LockPath))
			return false;
		try
		{
			using FileStream probe = new(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
			return false;
		}
		catch (IOException)
		{
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return true;
		}
	}

	private static async Task DeleteDirectoryQuietlyAsync(string path)
	{
		for (int attempt = 0; attempt < 3 && Directory.Exists(path); attempt++)
		{
			try
			{
				Directory.Delete(path, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				if (attempt == 2)
					App.Logger.WriteLine(LOG_IDENT, "Could not delete " + path + ": " + ex.Message);
				else
					await Task.Delay(200 * (attempt + 1)).ConfigureAwait(false);
			}
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}
}
