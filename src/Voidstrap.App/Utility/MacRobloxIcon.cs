using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Models.Entities;

namespace Voidstrap.Utility;

internal static class MacRobloxIcon
{
	private const string LogIdent = "MacRobloxIcon";
	private const int MaxIconBytes = 2 * 1024 * 1024;

	private static string IconFile => Path.Combine(Paths.Cache, "RobloxGameIcon.png");

	private static string MarkerFile => Path.Combine(Paths.Cache, "RobloxGameIcon.applied");

	internal static async Task ApplyForLaunchAsync(string bundlePath, string? launchTarget, CancellationToken cancellationToken)
	{
		if (!OperatingSystem.IsMacOS())
			return;
		long placeId = string.IsNullOrEmpty(launchTarget) ? 0 : Voidstrap.Integrations.LaunchInterceptor.ExtractPlaceId(launchTarget);
		if (!App.Settings.Prop.UseGameIconForRobloxWindow || placeId <= 0)
		{
			Restore(bundlePath);
			return;
		}
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(5));
			long universeId = await ResolveUniverseAsync(placeId, timeout.Token).ConfigureAwait(false);
			if (universeId <= 0)
			{
				App.Logger.WriteLine(LogIdent, $"Place {placeId} has no universe, keeping the Roblox icon");
				Restore(bundlePath);
				return;
			}
			UniverseDetails? details = UniverseDetails.LoadFromCache(universeId);
			if (string.IsNullOrWhiteSpace(details?.Thumbnail?.ImageUrl))
			{
				await UniverseDetails.FetchSingle(universeId).WaitAsync(timeout.Token).ConfigureAwait(false);
				details = UniverseDetails.LoadFromCache(universeId);
			}
			string? url = details?.Thumbnail?.ImageUrl;
			if (string.IsNullOrWhiteSpace(url))
			{
				Restore(bundlePath);
				return;
			}
			using HttpResponseMessage response = await App.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				Restore(bundlePath);
				return;
			}
			byte[] data = await Http.ReadBytesBoundedAsync(response.Content, MaxIconBytes, timeout.Token).ConfigureAwait(false);
			if (data.Length == 0)
			{
				Restore(bundlePath);
				return;
			}
			Directory.CreateDirectory(Paths.Cache);
			await File.WriteAllBytesAsync(IconFile, data, timeout.Token).ConfigureAwait(false);
			if (Voidstrap.Platform.MacOS.MacOSShortcut.SetCustomIcon(bundlePath, IconFile))
			{
				File.WriteAllText(MarkerFile, bundlePath);
				App.Logger.WriteLine(LogIdent, $"Roblox uses the icon of universe {universeId} in the Dock for this session");
			}
			else
			{
				App.Logger.WriteLine(LogIdent, "The game icon could not be set on the Roblox app");
			}
		}
		catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or JsonException or UnauthorizedAccessException)
		{
			App.Logger.WriteLine(LogIdent, "The game icon is skipped for this launch: " + ex.Message);
			Restore(bundlePath);
		}
	}

	internal static void Restore(string? bundlePath = null)
	{
		if (!OperatingSystem.IsMacOS())
			return;
		try
		{
			if (!File.Exists(MarkerFile))
				return;
			string path = string.IsNullOrWhiteSpace(bundlePath) ? File.ReadAllText(MarkerFile).Trim() : bundlePath;
			if (Voidstrap.Platform.MacOS.MacOSShortcut.SetCustomIcon(path, null))
				App.Logger.WriteLine(LogIdent, "Roblox has its own icon again");
			File.Delete(MarkerFile);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger.WriteLine(LogIdent, "The Roblox icon could not be restored: " + ex.Message);
		}
	}

	private static async Task<long> ResolveUniverseAsync(long placeId, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await App.HttpClient.GetAsync("https://apis.roblox.com/universes/v1/places/" + placeId + "/universe", cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
			return 0;
		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
		return document.RootElement.TryGetProperty("universeId", out JsonElement value) && value.TryGetInt64(out long universeId) ? universeId : 0;
	}
}
