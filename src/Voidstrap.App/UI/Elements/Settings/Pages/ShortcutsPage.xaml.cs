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
using SixLabors.ImageSharp.Processing;
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
			StartMenuOption.Header = "Applications menu";
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
			Frontend.ShowMessageBox("Failed to load saved settings.\n\nError: " + ex.Message);
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
			Application.Current.Shutdown();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to launch the game.\n\nError: " + Describe(ex));
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
		string displayName = SafeShortcutName(shortcutsViewModel.DisplayGameName);
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
		string shortcutPath = Path.Combine(folderPath, displayName + (Voidstrap.Utility.Platform.IsLinux ? ".desktop" : ".lnk"));
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
			if (File.Exists(shortcutPath))
				File.Delete(shortcutPath);
			string arguments = "-player \"" + launch.Value.LaunchUrl.Replace("\"", "") + "\"";
			Voidstrap.Utility.Shortcut.Create(executable, arguments, shortcutPath, iconPath);
			if (!File.Exists(shortcutPath))
			{
				Frontend.ShowMessageBox("Failed to create shortcut.\n\nVoidstrap could not write to the desktop folder.");
				return;
			}
			Frontend.ShowMessageBox("Shortcut created:\n" + displayName);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("Shortcuts::CreateShortcut", ex);
			Frontend.ShowMessageBox("Failed to create shortcut.\n\nError: " + Describe(ex));
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
		if (viewModel.IsPrivateServer)
		{
			(string placeId, string code) = await ResolvePrivateServerAsync(gameId, viewModel.PrivateServerCode);
			if (string.IsNullOrEmpty(placeId) || string.IsNullOrEmpty(code))
			{
				Frontend.ShowMessageBox("Enter a valid private server share link, or provide both a Game ID and access code.");
				return null;
			}
			return ("roblox://experiences/start?placeId=" + Uri.EscapeDataString(placeId) + "&privateServerLinkCode=" + Uri.EscapeDataString(code), placeId);
		}
		if (!long.TryParse(gameId, out long parsedGameId) || parsedGameId <= 0)
		{
			Frontend.ShowMessageBox("Enter a valid numeric Game ID.");
			return null;
		}
		string launchUrl = "roblox://experiences/start?placeId=" + gameId;
		string instanceId = viewModel.GameInstanceId?.Trim() ?? "";
		if (instanceId.Length > 128 || instanceId.Any(char.IsControl))
		{
			Frontend.ShowMessageBox("Enter a valid server instance ID.");
			return null;
		}
		if (instanceId.Length != 0)
			launchUrl += "&gameInstanceId=" + Uri.EscapeDataString(instanceId);
		return (launchUrl, gameId);
	}

	private static string SafeShortcutName(string? value)
	{
		string name = string.IsNullOrWhiteSpace(value) || value == "Unknown Game" || value == "Enter a valid Game ID" ? "Roblox Game" : value.Trim();
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
		LaunchGameButton.IsEnabled = enabled;
		CreateGameShortcutButton.IsEnabled = enabled;
	}

	private static string Describe(Exception ex)
	{
		return ex is OperationCanceledException
			? "The request timed out."
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
			SixLabors.ImageSharp.ImageInfo? imageInfo = SixLabors.ImageSharp.Image.Identify(imageBytes);
			if (imageInfo == null || imageInfo.Width <= 0 || imageInfo.Height <= 0 || (long)imageInfo.Width * imageInfo.Height > 16_777_216)
				throw new InvalidDataException("Game icon dimensions are invalid");
			string iconId = Convert.ToHexString(SHA256.HashData(imageBytes))[..12];
			string iconPath = Path.Combine(text, baseName + "_" + iconId + ".ico");
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				string portablePath = Path.Combine(text, baseName + ".png");
				using SixLabors.ImageSharp.Image portable = SixLabors.ImageSharp.Image.Load(imageBytes);
				using FileStream portableStream = File.Create(portablePath);
				portable.Save(portableStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
				return portablePath;
			}
			using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(imageBytes);
			image.Mutate(context => context.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
			{
				Size = new SixLabors.ImageSharp.Size(256, 256),
				Mode = SixLabors.ImageSharp.Processing.ResizeMode.Pad,
				PadColor = SixLabors.ImageSharp.Color.Transparent,
				Sampler = SixLabors.ImageSharp.Processing.KnownResamplers.Lanczos3
			}));
			using MemoryStream png = new MemoryStream();
			image.Save(png, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
			byte[] payload = png.ToArray();
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
