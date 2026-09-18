using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class RemoteData
{
	public const string BaseUrl = "https://raw.githubusercontent.com/KloBraticc/Voidstrap-Resources/main";

	private const string LOG_IDENT = "RemoteData";

	private const long MaxFileBytes = 67108864;

	private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

	private static readonly SemaphoreSlim Gate = new(1, 1);

	private static int _serverLocationsImported;

	public static string TranslationsPath => Path.Combine(Paths.Data, "Translations.json");

	public static string ServerLocationsPath => Path.Combine(Paths.Data, "ServerLocations.json");

	public static string? FileUrl(string relativePath)
	{
		return string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl.TrimEnd('/') + "/" + relativePath;
	}

	public static string? BinaryUrl(string fileName)
	{
		return FileUrl("bin/" + Uri.EscapeDataString(fileName));
	}

	public static async Task RefreshAsync(CancellationToken token)
	{
		await RefreshTranslationsAsync(false, token).ConfigureAwait(false);
		await RefreshServerLocationsAsync(false, token).ConfigureAwait(false);
	}

	public static async Task RefreshTranslationsAsync(bool force, CancellationToken token)
	{
		if (await DownloadAsync(FileUrl("Translations.json"), TranslationsPath, force, token).ConfigureAwait(false))
			TranslationService.ReloadCommunityTranslations();
	}

	public static async Task<int> RefreshServerLocationsAsync(bool force, CancellationToken token)
	{
		bool downloaded = await DownloadAsync(FileUrl("ServerLocations.json"), ServerLocationsPath, force, token).ConfigureAwait(false);
		bool firstImport = Interlocked.Exchange(ref _serverLocationsImported, 1) == 0;
		if (!downloaded && !firstImport)
			return 0;
		return Voidstrap.Integrations.ServerFetchStore.ImportServerLocations();
	}

	private static async Task<bool> DownloadAsync(string? url, string path, bool force, CancellationToken token)
	{
		if (string.IsNullOrWhiteSpace(url) || !Paths.Initialized)
			return false;
		await Gate.WaitAsync(token).ConfigureAwait(false);
		string name = Path.GetFileName(path);
		string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			FileInfo file = new(path);
			if (!force && file.Exists && DateTime.UtcNow - file.LastWriteTimeUtc < RefreshInterval)
				return false;
			string tagPath = path + ".etag";
			using HttpRequestMessage request = new(HttpMethod.Get, url);
			if (file.Exists && File.Exists(tagPath))
				request.Headers.TryAddWithoutValidation("If-None-Match", File.ReadAllText(tagPath).Trim());
			using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			if (response.StatusCode == HttpStatusCode.NotModified)
			{
				File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
				return false;
			}
			response.EnsureSuccessStatusCode();
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await Http.DownloadToFileBoundedAsync(response.Content, temporary, MaxFileBytes, token).ConfigureAwait(false);
			await using (FileStream stream = File.OpenRead(temporary))
			{
				using JsonDocument document = await JsonDocument.ParseAsync(stream, default, token).ConfigureAwait(false);
				if (document.RootElement.ValueKind != JsonValueKind.Object)
					throw new InvalidDataException("The downloaded file is not a JSON object");
			}
			File.Move(temporary, path, true);
			string? tag = response.Headers.ETag?.ToString();
			if (string.IsNullOrEmpty(tag))
				File.Delete(tagPath);
			else
				File.WriteAllText(tagPath, tag);
			App.Logger.WriteLine(LOG_IDENT, "Downloaded the latest " + name);
			return true;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Could not refresh " + name + ": " + ex.Message);
			return false;
		}
		finally
		{
			try
			{
				File.Delete(temporary);
			}
			catch
			{
			}
			Gate.Release();
		}
	}
}
