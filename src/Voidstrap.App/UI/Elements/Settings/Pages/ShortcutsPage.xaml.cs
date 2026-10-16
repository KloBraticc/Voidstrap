using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class ShortcutsPage : UiPage{
	private readonly string instanceFilePath = Path.Combine(Paths.UserData, "instance_id.txt");

	private static readonly HttpClient _httpClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(15));

	private static readonly HttpClient _noRedirectClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(15), handler => handler.AllowAutoRedirect = false);

	public ShortcutsPage()
	{
		base.DataContext = new ShortcutsViewModel();
		InitializeComponent();
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			StartMenuOption.Header = Voidstrap.Resources.Strings.Ui_Shortcuts_ApplicationsMenu;
		}
		try
		{
			string? directoryName = Path.GetDirectoryName(instanceFilePath);
			if (!string.IsNullOrEmpty(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			if (File.Exists(instanceFilePath))
			{
				((ShortcutsViewModel)base.DataContext).GameInstanceId = File.ReadAllText(instanceFilePath);
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox(string.Format(System.Globalization.CultureInfo.CurrentCulture, Voidstrap.Resources.Strings.Ui_Shortcuts_LoadFailed, ex.Message));
		}
	}

	private async void BtnLaunchGame_Click(object sender, RoutedEventArgs e)
	{
		SetGameActionsEnabled(false);
		try
		{
			(string LaunchUrl, string PlaceId)? launch = await BuildLaunchAsync();
			if (launch is null)
				return;
			SaveGameSettings();
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = Paths.LaunchExecutable,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(Paths.LaunchExecutable) ?? string.Empty
			};
			startInfo.ArgumentList.Add("-player");
			startInfo.ArgumentList.Add(launch.Value.LaunchUrl);
			using Process? process = Process.Start(startInfo);
			if (process == null)
				throw new InvalidOperationException("The launcher could not be started.");
			Application.Current.Shutdown();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox(string.Format(System.Globalization.CultureInfo.CurrentCulture, Voidstrap.Resources.Strings.Ui_Shortcuts_LaunchFailed, Describe(ex)));
		}
		finally
		{
			SetGameActionsEnabled(true);
		}
	}

	private async void BtnCreateShortcut_Click(object sender, RoutedEventArgs e)
	{
		SetGameActionsEnabled(false);
		try
		{
		ShortcutsViewModel shortcutsViewModel = (ShortcutsViewModel)base.DataContext;
		(string LaunchUrl, string PlaceId)? launch = await BuildLaunchAsync();
		if (launch is null)
			return;
		SaveGameSettings();
		string displayName = SafeShortcutName(shortcutsViewModel.IsSubplaceMode ? shortcutsViewModel.SelectedSubplace?.DisplayName : shortcutsViewModel.ResolvedGameName);
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
		string shortcutPath = Path.Combine(folderPath, displayName + Voidstrap.Utility.Shortcut.Suffix);
		string executable = File.Exists(Paths.Application) ? Paths.Application : Paths.LaunchExecutable;
		string iconPath = string.Empty;
		try
		{
			iconPath = await DownloadAndForceIcoAsync(await FetchGameIconUrlAsync(launch.Value.PlaceId), displayName);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Shortcuts::CreateShortcut", "Game icon lookup failed, using the Voidstrap icon instead");
			App.Logger.WriteException("Shortcuts::CreateShortcut", ex);
		}
		if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath))
		{
			iconPath = executable;
		}
			if (Voidstrap.Utility.Shortcut.Exists(shortcutPath))
				Voidstrap.Utility.Shortcut.Delete(shortcutPath);
			string arguments = "-player \"" + launch.Value.LaunchUrl.Replace("\"", "") + "\"";
			Voidstrap.Utility.Shortcut.Create(executable, arguments, shortcutPath, iconPath);
			if (!Voidstrap.Utility.Shortcut.Exists(shortcutPath))
			{
				Frontend.ShowMessageBox(Voidstrap.Resources.Strings.Ui_Shortcuts_DesktopWriteFailed);
				return;
			}
			Frontend.ShowMessageBox(string.Format(System.Globalization.CultureInfo.CurrentCulture, Voidstrap.Resources.Strings.Ui_Shortcuts_ShortcutCreated, displayName));
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("Shortcuts::CreateShortcut", ex);
			Frontend.ShowMessageBox(string.Format(System.Globalization.CultureInfo.CurrentCulture, Voidstrap.Resources.Strings.Ui_Shortcuts_ShortcutFailed, Describe(ex)));
		}
		finally
		{
			SetGameActionsEnabled(true);
		}
	}

	private async Task<(string LaunchUrl, string PlaceId)?> BuildLaunchAsync()
	{
		ShortcutsViewModel viewModel = (ShortcutsViewModel)base.DataContext;
		string gameId = viewModel.GameID?.Trim() ?? "";
		if (viewModel.IsSubplaceMode)
		{
			if (viewModel.IsLoadingSubplaces || viewModel.SelectedSubplace == null || !viewModel.Subplaces.Contains(viewModel.SelectedSubplace))
			{
				Frontend.ShowMessageBox(Voidstrap.Resources.Strings.Shortcuts_SubplaceRequired);
				return null;
			}
			gameId = viewModel.SelectedSubplace.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
		if (viewModel.IsPrivateServer && !viewModel.IsSubplaceMode)
		{
			(string placeId, string code) = await ResolvePrivateServerAsync(gameId, viewModel.PrivateServerCode);
			if (string.IsNullOrEmpty(placeId) || string.IsNullOrEmpty(code))
			{
				Frontend.ShowMessageBox(Voidstrap.Resources.Strings.Ui_Shortcuts_InvalidPrivateServer);
				return null;
			}
			return ("roblox://experiences/start?placeId=" + Uri.EscapeDataString(placeId) + "&privateServerLinkCode=" + Uri.EscapeDataString(code), placeId);
		}
		if (!long.TryParse(gameId, out long parsedGameId) || parsedGameId <= 0)
		{
			Frontend.ShowMessageBox(Voidstrap.Resources.Strings.Ui_Shortcuts_InvalidGameId);
			return null;
		}
		string launchUrl = "roblox://experiences/start?placeId=" + gameId;
		string instanceId = viewModel.GameInstanceId?.Trim() ?? "";
		if (instanceId.Length > 128 || instanceId.Any(char.IsControl))
		{
			Frontend.ShowMessageBox(Voidstrap.Resources.Strings.Ui_Shortcuts_InvalidInstanceId);
			return null;
		}
		if (instanceId.Length != 0)
			launchUrl += "&gameInstanceId=" + Uri.EscapeDataString(instanceId);
		return (launchUrl, gameId);
	}

	private static string SafeShortcutName(string? value)
	{
		string name = string.IsNullOrWhiteSpace(value) ? "Roblox Game" : value.Trim();
		foreach (char invalid in Path.GetInvalidFileNameChars())
			name = name.Replace(invalid, '_');
		name = name.Trim(' ', '.');
		if (name.Length == 0)
			name = "Roblox Game";
		if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
			name += " Game";
		return name.Length > 80 ? name[..80].TrimEnd(' ', '.') : name;
	}

	private void SaveGameSettings()
	{
		ShortcutsViewModel viewModel = (ShortcutsViewModel)base.DataContext;
		Directory.CreateDirectory(Paths.UserData);
		SaveOptionalText(instanceFilePath, viewModel.GameInstanceId);
		SaveOptionalText(Path.Combine(Paths.UserData, "PrivateServerCode.txt"), viewModel.IsPrivateServer ? viewModel.PrivateServerCode : null);
	}

	private static void SaveOptionalText(string path, string? value)
	{
		value = value?.Trim();
		if (string.IsNullOrEmpty(value))
		{
			if (File.Exists(path))
				File.Delete(path);
			return;
		}
		File.WriteAllText(path, value);
	}

	private void SetGameActionsEnabled(bool enabled)
	{
		GameShortcutPanel.IsEnabled = enabled;
	}

	private async void Page_Loaded(object sender, RoutedEventArgs e)
	{
		try
		{
			ShortcutsViewModel viewModel = (ShortcutsViewModel)DataContext;
			if (viewModel.IsSubplaceMode)
				await viewModel.LoadSubplacesAsync();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ShortcutsPage::Page_Loaded", ex);
		}
	}

	private void Page_Unloaded(object sender, RoutedEventArgs e)
	{
		((ShortcutsViewModel)DataContext).CancelSubplaceLoading();
	}

	private static string Describe(Exception ex)
	{
		return ex is OperationCanceledException
			? Voidstrap.Resources.Strings.Ui_Shortcuts_RequestTimedOut
			: string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
	}

	private static async Task<(string PlaceId, string Code)> ResolvePrivateServerAsync(string? gameId, string? input)
	{
		string placeId = gameId?.Trim() ?? "";
		string code = input?.Trim() ?? "";
		if (code.Length == 0 || code.Length > 2048)
			return ("", "");
		if (!Uri.TryCreate(code, UriKind.Absolute, out Uri? shareUri))
			return (placeId, code);
		if (shareUri.Scheme != Uri.UriSchemeHttps || (!shareUri.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) && !shareUri.Host.Equals("www.roblox.com", StringComparison.OrdinalIgnoreCase)))
			return ("", "");
		var query = HttpUtility.ParseQueryString(shareUri.Query);
		code = query["code"] ?? query["privateServerLinkCode"] ?? "";
		if (code.Length == 0 || code.Length > 512)
			return ("", "");
		try
		{
			using CancellationTokenSource redirectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
			Uri current = shareUri;
			Match initialMatch = GamePathPattern.Match(current.AbsolutePath);
			if (initialMatch.Success)
				placeId = initialMatch.Groups[1].Value;
			for (int i = 0; i < 5 && string.IsNullOrEmpty(placeId); i++)
			{
				using HttpResponseMessage response = await _noRedirectClient.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, redirectCts.Token);
				Uri? location = response.Headers.Location;
				if (location == null)
					break;
				current = location.IsAbsoluteUri ? location : new Uri(current, location);
				if (current.Scheme != Uri.UriSchemeHttps || (!current.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) && !current.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)))
					return ("", "");
				Match match = GamePathPattern.Match(current.AbsolutePath);
				if (match.Success)
					placeId = match.Groups[1].Value;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Shortcuts::ResolvePrivateServer", "Private server link resolution failed: " + ex.Message);
			return ("", "");
		}
		return long.TryParse(placeId, out long parsedPlaceId) && parsedPlaceId > 0 ? (placeId, code) : ("", "");
	}

	private static async Task<string?> FetchGameIconUrlAsync(string gameId)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			return null;
		}
		string url = "https://thumbnails.roblox.com/v1/places/gameicons?placeIds=" + Uri.EscapeDataString(gameId.Trim()) + "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false";
		using CancellationTokenSource requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
		for (int i = 0; i < 3; i++)
		{
			try
			{
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
				using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCts.Token).ConfigureAwait(continueOnCapturedContext: false);
				response.EnsureSuccessStatusCode();
				string json = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 262144, requestCts.Token).ConfigureAwait(false);
				using JsonDocument doc = JsonDocument.Parse(json);
				if (!doc.RootElement.TryGetProperty("data", out JsonElement property) || property.ValueKind != JsonValueKind.Array || property.GetArrayLength() == 0)
					return null;
				JsonElement first = property[0];
				if ((first.TryGetProperty("state", out var value) ? value.GetString() : null) == "Completed" && first.TryGetProperty("imageUrl", out var value2))
					return value2.GetString();
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("Shortcuts::FetchGameIcon", "Icon lookup attempt failed: " + ex.Message);
				if (i == 2)
					return null;
			}
			try
			{
				await Task.Delay(500, requestCts.Token);
			}
			catch (OperationCanceledException)
			{
				return null;
			}
		}
		return null;
	}

	private static async Task<string> DownloadAndForceIcoAsync(string? imageUrl, string baseName)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(imageUrl))
			{
				return string.Empty;
			}
			string text = Path.Combine(Paths.UserData, "Icons");
			Directory.CreateDirectory(text);
			using CancellationTokenSource imageCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
			using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, imageCts.Token).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			byte[] imageBytes = await Voidstrap.Utility.Http.ReadBytesBoundedAsync(response.Content, 4 * 1024 * 1024, imageCts.Token).ConfigureAwait(false);
			if (!Voidstrap.Utility.Raster.TryIdentify(imageBytes, out int imageWidth, out int imageHeight) || (long)imageWidth * imageHeight > 16_777_216)
				throw new InvalidDataException("Game icon dimensions are invalid");
			string iconId = Convert.ToHexString(SHA256.HashData(imageBytes))[..12];
			string iconPath = Path.Combine(text, baseName + "_" + iconId + ".ico");
			if (Voidstrap.Utility.Platform.UsesPortableUi)
			{
				string portablePath = Path.Combine(text, baseName + ".png");
				File.WriteAllBytes(portablePath, Voidstrap.Utility.Raster.Decode(imageBytes).EncodePng());
				return portablePath;
			}
			byte[] payload = Voidstrap.Utility.Raster.Decode(imageBytes).ResizeToPad(256, 256, Voidstrap.Utility.RasterFilter.Lanczos3).EncodePng();
			using FileStream output = new FileStream(iconPath, FileMode.Create, FileAccess.Write, FileShare.None);
			using BinaryWriter writer = new BinaryWriter(output);
			writer.Write((ushort)0);
			writer.Write((ushort)1);
			writer.Write((ushort)1);
			writer.Write((byte)0);
			writer.Write((byte)0);
			writer.Write((byte)0);
			writer.Write((byte)0);
			writer.Write((ushort)1);
			writer.Write((ushort)32);
			writer.Write((uint)payload.Length);
			writer.Write((uint)22);
			writer.Write(payload);
			return iconPath;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Shortcuts::DownloadIcon", "Could not build the game icon: " + ex.Message);
			return string.Empty;
		}
	}

    [GeneratedRegex("/games/(\\d+)(?:/|$)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex GamePathPattern { get; }
}
