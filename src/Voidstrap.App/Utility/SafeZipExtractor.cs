using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

public static class SafeZipExtractor
{
	private static readonly int WorkerLimit = Math.Clamp(Environment.ProcessorCount, 2, 8);

	public static void ExtractToDirectory(string archivePath, string destinationPath, bool overwrite = true, long maxExpandedBytes = 2147483648L, int maxEntries = 100000, CancellationToken token = default)
	{
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExpandedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        string root = Path.GetFullPath(destinationPath);
		string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
		StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		List<(int Index, string Target, long Length)> files = [];
		HashSet<string> directories = new HashSet<string>(comparer);
		using (ZipArchive archive = ZipFile.OpenRead(archivePath))
		{
			if (archive.Entries.Count > maxEntries)
				throw new InvalidDataException("The archive contains too many files");
			long declaredExpanded = 0;
			HashSet<string> targets = new HashSet<string>(comparer);
			for (int index = 0; index < archive.Entries.Count; index++)
			{
				token.ThrowIfCancellationRequested();
				ZipArchiveEntry entry = archive.Entries[index];
				if (IsSymbolicLink(entry))
					throw new InvalidDataException("The archive contains a symbolic link");
				declaredExpanded = checked(declaredExpanded + entry.Length);
				if (declaredExpanded > maxExpandedBytes)
					throw new InvalidDataException("The archive expands beyond the size limit");
				string target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
				if (!target.StartsWith(prefix, comparison) && !string.Equals(target, root, comparison))
					throw new InvalidDataException("The archive contains an invalid path");
				if (!targets.Add(target))
					throw new InvalidDataException("The archive contains duplicate paths");
				if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
				{
					directories.Add(target);
					continue;
				}
				string? directory = Path.GetDirectoryName(target);
				if (!string.IsNullOrEmpty(directory))
					directories.Add(directory);
				files.Add((index, target, entry.Length));
			}
		}
		EnsureSafeDirectory(root, root, comparison);
		foreach (string directory in directories)
		{
			token.ThrowIfCancellationRequested();
			EnsureSafeDirectory(root, directory, comparison);
		}
		if (files.Count == 0)
			return;
		long actualExpanded = 0;
		int workers = Math.Clamp(files.Count / 16, 1, WorkerLimit);
		try
		{
			Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, (worker, state) =>
			{
				using ZipArchive archive = ZipFile.OpenRead(archivePath);
				byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
				try
				{
					for (int i = worker; i < files.Count && !state.ShouldExitCurrentIteration; i += workers)
					{
						(int index, string target, long length) = files[i];
						ExtractEntry(archive.Entries[index], target, length, overwrite, buffer, ref actualExpanded, maxExpandedBytes, token);
					}
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(buffer);
				}
			});
		}
		catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
		{
			ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
		}
	}

	private static void ExtractEntry(ZipArchiveEntry entry, string target, long length, bool overwrite, byte[] buffer, ref long actualExpanded, long maxExpandedBytes, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("The extraction target is a symbolic link");
		string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			long entryBytes = 0;
			using Stream input = entry.Open();
			using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan))
			{
				while (true)
				{
					token.ThrowIfCancellationRequested();
					int read = input.Read(buffer, 0, buffer.Length);
					if (read == 0)
						break;
					entryBytes = checked(entryBytes + read);
					if (Interlocked.Add(ref actualExpanded, read) > maxExpandedBytes || entryBytes > length)
						throw new InvalidDataException("The archive expands beyond the size limit");
					output.Write(buffer, 0, read);
				}
			}
			if (entryBytes != length)
				throw new InvalidDataException("The archive entry size is invalid");
			File.Move(temporary, target, overwrite);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	private static void EnsureSafeDirectory(string root, string directory, StringComparison comparison)
	{
		string fullRoot = NormalizeDirectory(root);
		string fullDirectory = NormalizeDirectory(directory);
		if (!string.Equals(fullDirectory, fullRoot, comparison) && !fullDirectory.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
			throw new InvalidDataException("The archive contains an invalid directory");
		Directory.CreateDirectory(fullRoot);
		if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("The extraction root is a symbolic link");
		string relative = Path.GetRelativePath(fullRoot, fullDirectory);
		string current = fullRoot;
		foreach (string part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current, part);
			Directory.CreateDirectory(current);
			if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException("The extraction path contains a symbolic link");
		}
	}

	private static string NormalizeDirectory(string path)
	{
		string fullPath = Path.GetFullPath(path);
		string? volumeRoot = Path.GetPathRoot(fullPath);
		return volumeRoot != null && fullPath.Length == volumeRoot.Length
			? fullPath
			: fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}

	private static bool IsSymbolicLink(ZipArchiveEntry entry)
	{
		int mode = (entry.ExternalAttributes >> 16) & 61440;
		return mode == 40960 || (entry.ExternalAttributes & 1024) != 0;
	}
}
