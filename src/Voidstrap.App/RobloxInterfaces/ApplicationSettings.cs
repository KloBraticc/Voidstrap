using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Models.APIs.Roblox;

namespace Voidstrap.RobloxInterfaces;

public class ApplicationSettings
{
	private readonly record struct SourceResult(string? Body, bool InvalidBucket, Exception? Error);

	private const int MaxResponseBytes = 4 * 1024 * 1024;
	private const int MaxErrorBytes = 16 * 1024;
	private static readonly string[] Sources = ["https://clientsettingscdn.roblox.com", "https://clientsettings.roblox.com", "https://setup.rbxcdn.com"];
	private readonly string _applicationName;

	private readonly string _channelName;

	private bool _initialised;

	private Dictionary<string, string>? _flags;

	private readonly SemaphoreSlim _semaphore = new(1, 1);

	private const int MaxCachedSettings = 32;

	private static readonly ConcurrentDictionary<string, Lazy<ApplicationSettings>> _cache = new();

	private static readonly ConcurrentQueue<string> _cacheOrder = new();

	public static ApplicationSettings PCDesktopClient => GetSettings("PCDesktopClient");

	public static ApplicationSettings PCClientBootstrapper => GetSettings("PCClientBootstrapper");

	private ApplicationSettings(string applicationName, string channelName)
	{
		_applicationName = applicationName;
		_channelName = channelName;
	}

	private async Task FetchAsync()
	{
		if (_initialised)
		{
			return;
		}
		await _semaphore.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			if (_initialised)
			{
				return;
			}
			string logIdent = "ApplicationSettings::Fetch." + _applicationName + "." + _channelName;
			App.Logger.WriteLine(logIdent, "Fetching fast flags...");
			string path = "/v2/settings/application/" + _applicationName;
			if (!string.Equals(_channelName, "production", StringComparison.OrdinalIgnoreCase))
			{
				path = path + "/bucket/" + _channelName;
			}
			SourceResult result = await FetchFromSourcesAsync(path, logIdent).ConfigureAwait(false);
			if (result.Body == null && result.InvalidBucket && !string.Equals(_channelName, "production", StringComparison.OrdinalIgnoreCase))
			{
				App.Logger.WriteLine(logIdent, "Invalid bucket '" + _channelName + "'. Falling back to default channel...");
				path = "/v2/settings/application/" + _applicationName;
				result = await FetchFromSourcesAsync(path, logIdent).ConfigureAwait(false);
			}
			if (result.Body == null)
			{
				throw new Exception("All configuration sources failed.", result.Error);
			}
			if (string.IsNullOrWhiteSpace(result.Body))
			{
				throw new Exception("Empty response from configuration endpoint.");
			}
			ClientFlagSettings clientFlagSettings = JsonSerializer.Deserialize<ClientFlagSettings>(result.Body);
			if (clientFlagSettings?.ApplicationSettings == null)
			{
				throw new Exception("Deserialized ApplicationSettings is null!");
			}
			_flags = clientFlagSettings.ApplicationSettings;
			_initialised = true;
			App.Logger.WriteLine(logIdent, $"Fetched {_flags.Count} fast flags successfully.");
		}
		finally
		{
			_semaphore.Release();
		}
	}

	private static async Task<SourceResult> FetchFromSourcesAsync(string path, string logIdent)
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
		List<Task<SourceResult>> pending = Sources
			.Select(source => FetchSourceAsync(source, path, logIdent, timeout.Token))
			.ToList();
		bool invalidBucket = false;
		Exception? lastError = null;
		while (pending.Count != 0)
		{
			Task<SourceResult> completed = await Task.WhenAny(pending).ConfigureAwait(false);
			pending.Remove(completed);
			SourceResult result = await completed.ConfigureAwait(false);
			invalidBucket |= result.InvalidBucket;
			lastError = result.Error ?? lastError;
			if (result.Body == null)
			{
				continue;
			}
			timeout.Cancel();
			await Task.WhenAll(pending).ConfigureAwait(false);
			return result;
		}
		return new SourceResult(null, invalidBucket, lastError);
	}

	private static async Task<SourceResult> FetchSourceAsync(string source, string path, string logIdent, CancellationToken cancellationToken)
	{
		string url = source + path;
		try
		{
			App.Logger.WriteLine(logIdent, "Trying " + url);
			using HttpRequestMessage request = new(HttpMethod.Get, url);
			using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
			{
				string errorBody = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, MaxErrorBytes, cancellationToken).ConfigureAwait(false);
				bool invalidBucket = errorBody.Contains("bucket name is invalid", StringComparison.OrdinalIgnoreCase);
				return new SourceResult(null, invalidBucket, new HttpRequestException("Configuration source returned " + (int)response.StatusCode));
			}
			response.EnsureSuccessStatusCode();
			string body = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
			return new SourceResult(body, false, null);
		}
		catch (OperationCanceledException ex)
		{
			return new SourceResult(null, false, ex);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(logIdent, "Error contacting " + source + ": " + ex.Message);
			return new SourceResult(null, false, ex);
		}
	}

	public async Task<T?> GetAsync<T>(string name)
	{
		await FetchAsync().ConfigureAwait(continueOnCapturedContext: false);
		if (_flags == null || !_flags.TryGetValue(name, out string value))
		{
			return default;
		}
		try
		{
			TypeConverter converter = TypeDescriptor.GetConverter(typeof(T));
			if (converter != null && converter.CanConvertFrom(typeof(string)))
			{
				return (T)converter.ConvertFromInvariantString(value);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ApplicationSettings::GetAsync", ex);
		}
		return default;
	}

	public T? Get<T>(string name)
	{
		return GetAsync<T>(name).ConfigureAwait(continueOnCapturedContext: false).GetAwaiter().GetResult();
	}

	public static ApplicationSettings GetSettings(string applicationName, string channelName = "production", bool shouldCache = true)
	{
		channelName = channelName.ToLowerInvariant();
		if (!shouldCache)
		{
			return new ApplicationSettings(applicationName, channelName);
		}
		string cacheKey = applicationName + "\n" + channelName;
		Lazy<ApplicationSettings> candidate = new(() => new ApplicationSettings(applicationName, channelName));
		Lazy<ApplicationSettings> cached = _cache.GetOrAdd(cacheKey, candidate);
		if (ReferenceEquals(cached, candidate))
		{
			_cacheOrder.Enqueue(cacheKey);
			TrimCache();
		}
		return cached.Value;
	}

	private static void TrimCache()
	{
		while (_cache.Count > MaxCachedSettings && _cacheOrder.TryDequeue(out string? oldest))
		{
			_cache.TryRemove(oldest, out _);
		}
	}
}
