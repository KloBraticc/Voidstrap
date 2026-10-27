using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class DiskImageCache
{
	private const int MaxImageBytes = 4 * 1024 * 1024;

	private const int MaxCachedImages = 80;

	private static readonly ConcurrentDictionary<string, byte> ImageDownloads = new(StringComparer.Ordinal);

	private static readonly object PruneGate = new();

	public static string Resolve(string? remote, string folderName)
	{
		if (string.IsNullOrWhiteSpace(remote) || !remote.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			return remote ?? string.Empty;
		string folder = Path.Combine(Paths.Cache, folderName);
		string path = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remote)))[..32] + ".img");
		try
		{
			if (File.Exists(path) && new FileInfo(path).Length > 0)
				return new Uri(path).AbsoluteUri;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		if (ImageDownloads.TryAdd(path, 0))
			_ = Task.Run(() => SaveImageAsync(remote, path, folder));
		return remote;
	}

	private static async Task SaveImageAsync(string remote, string path, string folder)
	{
		string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
			using HttpResponseMessage response = await App.HttpClient.GetAsync(remote, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
				return;
			byte[] data = await Http.ReadBytesBoundedAsync(response.Content, MaxImageBytes, cts.Token).ConfigureAwait(false);
			if (data.Length == 0)
				return;
			Directory.CreateDirectory(folder);
			await File.WriteAllBytesAsync(temporary, data, cts.Token).ConfigureAwait(false);
			File.Move(temporary, path, true);
			PruneImages(folder);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("DiskImageCache", "An image could not be saved: " + ex.Message);
		}
		finally
		{
			ImageDownloads.TryRemove(path, out _);
			try
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private static void PruneImages(string folder)
	{
		if (!Monitor.TryEnter(PruneGate))
			return;
		try
		{
			FileInfo[] files = new DirectoryInfo(folder).GetFiles("*.img");
			if (files.Length <= MaxCachedImages)
				return;
			foreach (FileInfo file in files.OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaxCachedImages))
			{
				if (ImageDownloads.ContainsKey(file.FullName) || !File.Exists(file.FullName))
					continue;
				FileAttributes attributes = File.GetAttributes(file.FullName);
				if ((attributes & FileAttributes.ReadOnly) != 0)
					File.SetAttributes(file.FullName, attributes & ~FileAttributes.ReadOnly);
				file.Delete();
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		finally
		{
			Monitor.Exit(PruneGate);
		}
	}
}
