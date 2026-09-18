using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Voidstrap.Utility;

public static class PackageCache
{
	private const string LOG_IDENT = "PackageCache";

	private const long MaxBudgetBytes = 6L * 1024 * 1024 * 1024;

	public static void Touch(string path)
	{
		try
		{
			File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	public static string ClassicPath(string sha256) => Path.Combine(Paths.Downloads, sha256.ToUpperInvariant() + ".zip");

	public static bool TryUseClassic(string sha256, long size, string destination)
	{
		string cached = ClassicPath(sha256);
		try
		{
			if (!File.Exists(cached) || new FileInfo(cached).Length != size)
				return false;
			using (FileStream stream = File.OpenRead(cached))
			{
				if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
				{
					stream.Dispose();
					File.Delete(cached);
					return false;
				}
			}
			File.Copy(cached, destination, overwrite: true);
			Touch(cached);
			App.Logger.WriteLine(LOG_IDENT, "Classic archive served from cache: " + Path.GetFileName(cached));
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger.WriteLine(LOG_IDENT, "Classic cache read failed: " + ex.Message);
			return false;
		}
	}

	public static void StoreClassic(string sha256, string source)
	{
		try
		{
			Directory.CreateDirectory(Paths.Downloads);
			File.Copy(source, ClassicPath(sha256), overwrite: true);
			HashSet<string> keep = new(App.State.Prop.Player?.PackageHashes.Values ?? Enumerable.Empty<string>());
			keep.UnionWith(App.State.Prop.Studio?.PackageHashes.Values ?? Enumerable.Empty<string>());
			Trim(keep);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger.WriteLine(LOG_IDENT, "Classic cache write failed: " + ex.Message);
		}
	}

	public static void Trim(IReadOnlySet<string> keep)
	{
		try
		{
			if (!Directory.Exists(Paths.Downloads))
				return;
			long budget = Math.Min(MaxBudgetBytes, Math.Max(0, Filesystem.GetFreeDiskSpace(Paths.Downloads) / 4));
			List<FileInfo> files = new DirectoryInfo(Paths.Downloads).GetFiles()
				.Where(f => !f.Name.Contains(".part", StringComparison.OrdinalIgnoreCase) && !f.Name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
				.ToList();
			long total = files.Sum(f => f.Length);
			foreach (FileInfo file in files.Where(f => !keep.Contains(f.Name)).OrderBy(f => f.LastWriteTimeUtc))
			{
				if (total <= budget)
					break;
				try
				{
					long length = file.Length;
					file.Delete();
					total -= length;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					App.Logger.WriteLine(LOG_IDENT, "Could not evict " + file.Name + ": " + ex.Message);
				}
			}
			App.Logger.WriteLine(LOG_IDENT, $"Cache holds {total / 1048576} MB, budget {budget / 1048576} MB");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger.WriteLine(LOG_IDENT, "Trim failed: " + ex.Message);
		}
	}
}
