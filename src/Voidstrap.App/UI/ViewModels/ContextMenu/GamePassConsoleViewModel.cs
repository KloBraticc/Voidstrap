using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Enums;
using Voidstrap.Integrations;

namespace Voidstrap.UI.ViewModels.ContextMenu;

internal class GamePassConsoleViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private static readonly HttpClient _httpClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(20));
	private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
	private CancellationTokenSource? _loadCancellation;
	private bool _disposed;

	public EventHandler? RequestCloseEvent;

	public ObservableCollection<GamePassData> GamePassesCollection { get; } = new();

	public GenericTriState LoadState { get; private set; } = GenericTriState.Unknown;

	public string ErrorMessage { get; private set; } = string.Empty;

	public ICommand CloseWindowCommand => new RelayCommand(RequestClose);

	public ICommand LoadGamePassesCommand { get; }

	public GamePassConsoleViewModel()
	{
		LoadGamePassesCommand = new AsyncRelayCommand<long>(LoadGamePassesAsync);
	}

	private async Task LoadGamePassesAsync(long userId)
	{
		_loadCancellation?.Cancel();
		_loadCancellation?.Dispose();
		_loadCancellation = new CancellationTokenSource();
		CancellationToken token = _loadCancellation.Token;
		LoadState = GenericTriState.Unknown;
		ErrorMessage = string.Empty;
		GamePassesCollection.Clear();
		NotifyState();

		try
		{
			List<GamePassData>? passes = await FetchAuthorizedPassesAsync(userId, token);
			passes ??= await FetchCreatedPassesAsync(userId, token);
			await EnrichPassesAsync(passes, token);
			token.ThrowIfCancellationRequested();

			foreach (GamePassData pass in passes.OrderBy(pass => pass.Name, StringComparer.CurrentCultureIgnoreCase))
				GamePassesCollection.Add(pass);

			if (passes.Count == 0)
			{
				ErrorMessage = "No gamepasses were found for this player.";
				LoadState = GenericTriState.Failed;
			}
			else
			{
				LoadState = GenericTriState.Successful;
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			return;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("GamePassConsoleViewModel", "Gamepass fetch failed: " + ex.Message);
			ErrorMessage = "Could not load gamepasses right now. Please try again.";
			LoadState = GenericTriState.Failed;
		}
		finally
		{
			NotifyState();
		}
	}

	private static async Task<List<GamePassData>?> FetchAuthorizedPassesAsync(long userId, CancellationToken token)
	{
		string cookie = RobloxCookie.Get();
		if (string.IsNullOrWhiteSpace(cookie))
			return null;

		using HttpRequestMessage request = new(HttpMethod.Get, $"https://apis.roblox.com/game-passes/v1/users/{userId}/game-passes?count=100");
		request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
		using HttpResponseMessage response = await _httpClient.SendAsync(request, token);
		if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			return null;
		response.EnsureSuccessStatusCode();
		await using var stream = await response.Content.ReadAsStreamAsync(token);
		GamePassResponse? result = await JsonSerializer.DeserializeAsync<GamePassResponse>(stream, _jsonOptions, token);
		return result?.GamePasses ?? new List<GamePassData>();
	}

	private static async Task<List<GamePassData>> FetchCreatedPassesAsync(long userId, CancellationToken token)
	{
		List<(long UniverseId, string CreatorName)> universes = new();
		string? cursor = null;
		do
		{
			string url = $"https://games.roblox.com/v2/users/{userId}/games?accessFilter=Public&sortOrder=Asc&limit=50";
			if (!string.IsNullOrWhiteSpace(cursor))
				url += "&cursor=" + Uri.EscapeDataString(cursor);

			using JsonDocument document = await GetJsonAsync(url, token);
			if (document.RootElement.TryGetProperty("data", out JsonElement games) && games.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement game in games.EnumerateArray())
				{
					long universeId = game.TryGetProperty("id", out JsonElement id) && id.TryGetInt64(out long value) ? value : 0;
					if (universeId > 0)
						universes.Add((universeId, string.Empty));
				}
			}

			cursor = document.RootElement.TryGetProperty("nextPageCursor", out JsonElement next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
		}
		while (!string.IsNullOrWhiteSpace(cursor));

		string creatorName = await GetCreatorNameAsync(userId, token);
		List<GamePassData> passes = new();
		foreach ((long UniverseId, string _)[] batch in universes.Chunk(8))
		{
			List<GamePassData>[] results = await Task.WhenAll(batch.Select(item => FetchUniversePassesAsync(item.UniverseId, creatorName, userId, token)));
			foreach (List<GamePassData> result in results)
				passes.AddRange(result);
		}

		return passes.GroupBy(pass => pass.GamePassId).Select(group => group.First()).ToList();
	}

	private static async Task<List<GamePassData>> FetchUniversePassesAsync(long universeId, string creatorName, long creatorId, CancellationToken token)
	{
		using JsonDocument document = await GetJsonAsync($"https://apis.roblox.com/game-passes/v1/universes/{universeId}/game-passes?limit=100&sortOrder=1", token);
		List<GamePassData> passes = new();
		if (!document.RootElement.TryGetProperty("gamePasses", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
			return passes;

		foreach (JsonElement item in data.EnumerateArray())
		{
			long id = item.TryGetProperty("id", out JsonElement idProperty) && idProperty.TryGetInt64(out long idValue) ? idValue : 0;
			if (id == 0)
				continue;

			passes.Add(new GamePassData
			{
				GamePassId = id,
				IconAssetId = item.TryGetProperty("displayIconImageAssetId", out JsonElement icon) && icon.TryGetInt64(out long iconValue) ? iconValue : 0,
				Name = ReadString(item, "displayName", "name"),
				Description = ReadString(item, "displayDescription"),
				IsForSale = item.TryGetProperty("isForSale", out JsonElement sale) && sale.ValueKind == JsonValueKind.True,
				Creator = new GamePassCreator { CreatorType = "User", CreatorId = creatorId, Name = creatorName }
			});
		}

		return passes;
	}

	private static async Task EnrichPassesAsync(List<GamePassData> passes, CancellationToken token)
	{
		foreach (GamePassData pass in passes)
		{
			pass.Description = string.IsNullOrWhiteSpace(pass.Description) ? "No description" : pass.Description;
			pass.DisplayPrice = pass.IsForSale ? "For sale" : "Not for sale";
		}

		foreach (GamePassData[] batch in passes.Where(pass => pass.IsForSale).Chunk(8))
			await Task.WhenAll(batch.Select(pass => LoadPriceAsync(pass, token)));

		foreach (GamePassData[] batch in passes.Chunk(100))
		{
			string ids = string.Join(',', batch.Select(pass => pass.GamePassId));
			using JsonDocument document = await GetJsonAsync($"https://thumbnails.roblox.com/v1/game-passes?gamePassIds={ids}&size=150x150&format=Png&isCircular=false", token);
			if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
				continue;

			foreach (JsonElement item in data.EnumerateArray())
			{
				long id = item.TryGetProperty("targetId", out JsonElement target) && target.TryGetInt64(out long value) ? value : 0;
				string imageUrl = item.TryGetProperty("imageUrl", out JsonElement image) && image.ValueKind == JsonValueKind.String ? image.GetString() ?? string.Empty : string.Empty;
				GamePassData? pass = batch.FirstOrDefault(candidate => candidate.GamePassId == id);
				if (pass != null)
					pass.IconUrl = imageUrl;
			}
		}
	}

	private static async Task LoadPriceAsync(GamePassData pass, CancellationToken token)
	{
		try
		{
			using JsonDocument document = await GetJsonAsync($"https://apis.roblox.com/game-passes/v1/game-passes/{pass.GamePassId}/product-info", token);
			if (document.RootElement.TryGetProperty("PriceInRobux", out JsonElement price) && price.TryGetInt32(out int value))
			{
				pass.Price = value;
				pass.DisplayPrice = $"{value:N0}";
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("GamePassConsoleViewModel", "Gamepass price failed: " + ex.Message);
		}
	}

	private static async Task<string> GetCreatorNameAsync(long userId, CancellationToken token)
	{
		try
		{
			using JsonDocument document = await GetJsonAsync($"https://users.roblox.com/v1/users/{userId}", token);
			return ReadString(document.RootElement, "displayName", "name");
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return "Unknown";
		}
	}

	private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
	{
		using HttpResponseMessage response = await _httpClient.GetAsync(url, token);
		response.EnsureSuccessStatusCode();
		await using var stream = await response.Content.ReadAsStreamAsync(token);
		return await JsonDocument.ParseAsync(stream, cancellationToken: token);
	}

	private static string ReadString(JsonElement element, params string[] names)
	{
		foreach (string name in names)
		{
			if (element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String)
			{
				string? value = property.GetString();
				if (!string.IsNullOrWhiteSpace(value))
					return value;
			}
		}
		return string.Empty;
	}

	private void NotifyState()
	{
		OnPropertyChanged(nameof(ErrorMessage));
		OnPropertyChanged(nameof(LoadState));
		OnPropertyChanged(nameof(GamePassesCollection));
	}

	private void RequestClose()
	{
		RequestCloseEvent?.Invoke(this, EventArgs.Empty);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_loadCancellation?.Cancel();
		_loadCancellation?.Dispose();
		_loadCancellation = null;
		RequestCloseEvent = null;
		_disposed = true;
		GC.SuppressFinalize(this);
	}
}
