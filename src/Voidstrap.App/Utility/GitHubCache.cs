using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class GitHubCache
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};

	private static readonly SemaphoreSlim WriteLock = new SemaphoreSlim(1, 1);
	private const long MaxCacheBytes = 8L * 1024L * 1024L;

	public static async Task<string?> GetStringAsync(string url, TimeSpan maxAge, CancellationToken token = default(CancellationToken), bool useStaleOnFailure = true)
	{
		string dir = Path.Combine(Paths.Cache, "GitHub");
		string file = Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".json");
		string? cached = null;
		try
		{
			if (File.Exists(file))
			{
				if (new FileInfo(file).Length > MaxCacheBytes)
					throw new InvalidDataException("The cached response is too large");
				cached = await File.ReadAllTextAsync(file, token).ConfigureAwait(continueOnCapturedContext: false);
				if (!string.IsNullOrEmpty(cached) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < maxAge)
				{
					return cached;
				}
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("GitHubCache::Read", ex);
		}
		try
		{
			string fresh = await Http.GetString(url, token).ConfigureAwait(continueOnCapturedContext: false);
			await WriteLock.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				Directory.CreateDirectory(dir);
				string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
				try
				{
					await File.WriteAllTextAsync(temporary, fresh, token).ConfigureAwait(continueOnCapturedContext: false);
					File.Move(temporary, file, true);
				}
				finally
				{
					if (File.Exists(temporary))
					{
						File.Delete(temporary);
					}
				}
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex2)
			{
				App.Logger.WriteException("GitHubCache::Write", ex2);
			}
			finally
			{
				WriteLock.Release();
			}
			return fresh;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex3)
		{
			App.Logger.WriteLine("GitHubCache::Fetch", $"Fetch failed for {url}, using cached copy: {cached != null}. {ex3.Message}");
			return useStaleOnFailure ? cached : null;
		}
	}

	public static async Task<T?> GetJsonAsync<T>(string url, TimeSpan maxAge, CancellationToken token = default(CancellationToken)) where T : class
	{
		string? json = await GetStringAsync(url, maxAge, token).ConfigureAwait(continueOnCapturedContext: false);
		if (string.IsNullOrEmpty(json))
		{
			return null;
		}
		try
		{
			return JsonSerializer.Deserialize<T>(json, JsonOptions);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("GitHubCache::Deserialize", ex);
			return null;
		}
	}

	private static int _primaryReachable = 1;

	public static bool PrimaryRepositoryReachable => Volatile.Read(ref _primaryReachable) == 1;

	public static string PreferredRepository => PrimaryRepositoryReachable ? "https://github.com/KloBraticc/Voidstrap" : App.ProjectFallbackRepository;

	public static async Task<string?> GetStringWithFallbackAsync(string primaryUrl, string fallbackUrl, TimeSpan maxAge, CancellationToken token = default)
	{
		string? value = await GetStringAsync(primaryUrl, maxAge, token, false).ConfigureAwait(false);
		if (!string.IsNullOrEmpty(value))
		{
			Volatile.Write(ref _primaryReachable, 1);
			return value;
		}
		Volatile.Write(ref _primaryReachable, 0);
		App.Logger.WriteLine("GitHubCache::Fallback", "Primary repository unreachable, using backup for " + fallbackUrl);
		return await GetStringAsync(fallbackUrl, maxAge, token).ConfigureAwait(false);
	}

	public static async Task<T?> GetJsonWithFallbackAsync<T>(string primaryUrl, string fallbackUrl, TimeSpan maxAge, CancellationToken token = default) where T : class
	{
		string? json = await GetStringWithFallbackAsync(primaryUrl, fallbackUrl, maxAge, token).ConfigureAwait(false);
		if (string.IsNullOrEmpty(json))
			return null;
		try
		{
			return JsonSerializer.Deserialize<T>(json, JsonOptions);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("GitHubCache::Deserialize", ex);
			return null;
		}
	}
}
