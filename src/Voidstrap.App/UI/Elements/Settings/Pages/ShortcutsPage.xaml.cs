using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
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
using Size = System.Drawing.Size;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class ShortcutsPage : UiPage{
	private readonly string instanceFilePath = Path.Combine(Paths.UserData, "instance_id.txt");

	private static readonly HttpClient _httpClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(15));

	private static readonly HttpClient _noRedirectClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(15), handler => handler.AllowAutoRedirect = false);

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	private static extern bool DestroyIcon(nint handle);

	public ShortcutsPage()
	{
		base.DataContext = new ShortcutsViewModel();
		InitializeComponent();
		try
		{
			string directoryName = Path.GetDirectoryName(instanceFilePath);
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
		ShortcutsViewModel obj = (ShortcutsViewModel)base.DataContext;
		string text = obj.GameID?.Trim();
		string text2 = obj.GameInstanceId?.Trim();
		bool isPrivateServer = obj.IsPrivateServer;
		string text3 = obj.PrivateServerCode?.Trim();
		if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(text3))
		{
			Frontend.ShowMessageBox("Please enter a Game ID or a Private Server Link.");
			return;
		}
		try
		{
			if (!string.IsNullOrEmpty(text2))
			{
				string directoryName = Path.GetDirectoryName(instanceFilePath);
				if (!string.IsNullOrEmpty(directoryName))
				{
					Directory.CreateDirectory(directoryName);
				}
				File.WriteAllText(instanceFilePath, text2);
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to save Game Instance ID.\n\nError: " + ex.Message);
		}
		if (isPrivateServer && !string.IsNullOrEmpty(text3))
		{
			try
			{
				string text4 = Paths.UserData;
				Directory.CreateDirectory(text4);
				File.WriteAllText(Path.Combine(text4, "PrivateServerCode.txt"), text3);
			}
			catch (Exception ex2)
			{
				Frontend.ShowMessageBox("Failed to save Private Server Code.\n\nError: " + ex2.Message);
			}
		}
		string text6;
		if (isPrivateServer)
		{
			if (string.IsNullOrEmpty(text3))
			{
				Frontend.ShowMessageBox("Please enter your Private Server Share Link or Code.");
				return;
			}
			(string placeId, string code) = await ResolvePrivateServerAsync(text, text3);
			if (string.IsNullOrEmpty(placeId) || string.IsNullOrEmpty(code))
			{
				Frontend.ShowMessageBox("Could not detect Game ID from the share link.");
				return;
			}
			text6 = "roblox://experiences/start?placeId=" + Uri.EscapeDataString(placeId) + "&privateServerLinkCode=" + Uri.EscapeDataString(code);
		}
		else
		{
			text6 = "roblox://experiences/start?placeId=" + Uri.EscapeDataString(text);
			if (!string.IsNullOrEmpty(text2))
			{
				text6 = text6 + "&gameInstanceId=" + Uri.EscapeDataString(text2);
			}
		}
		try
		{
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = Paths.Process,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(Paths.Process) ?? string.Empty
			};
			startInfo.ArgumentList.Add("-player");
			startInfo.ArgumentList.Add(text6);
			using Process? process = Process.Start(startInfo);
			Application.Current.Shutdown();
		}
		catch (Exception ex4)
		{
			Frontend.ShowMessageBox("Failed to launch the game.\n\nError: " + ex4.Message);
		}
	}

	private async void BtnCreateShortcut_Click(object sender, RoutedEventArgs e)
	{
		ShortcutsViewModel shortcutsViewModel = (ShortcutsViewModel)base.DataContext;
		string displayName = (string.IsNullOrWhiteSpace(shortcutsViewModel.DisplayGameName) ? "Roblox Game" : shortcutsViewModel.DisplayGameName);
		char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
		foreach (char oldChar in invalidFileNameChars)
		{
			displayName = displayName.Replace(oldChar, '_');
		}
		if (displayName.Length > 80)
		{
			displayName = displayName.Substring(0, 80);
		}
		string launchUrl;
		string iconPlaceId;
		if (shortcutsViewModel.IsPrivateServer && !string.IsNullOrWhiteSpace(shortcutsViewModel.PrivateServerCode))
		{
			(string placeId, string code) = await ResolvePrivateServerAsync(shortcutsViewModel.GameID, shortcutsViewModel.PrivateServerCode);
			if (string.IsNullOrEmpty(placeId) || string.IsNullOrEmpty(code))
			{
				Frontend.ShowMessageBox("Could not detect Game ID from the private server link.");
				return;
			}
			launchUrl = "roblox://experiences/start?placeId=" + Uri.EscapeDataString(placeId) + "&privateServerLinkCode=" + Uri.EscapeDataString(code);
			iconPlaceId = placeId;
		}
		else
		{
			if (string.IsNullOrWhiteSpace(shortcutsViewModel.GameID))
			{
				Frontend.ShowMessageBox("Please enter a Game ID first.");
				return;
			}
			launchUrl = "roblox://experiences/start?placeId=" + Uri.EscapeDataString(shortcutsViewModel.GameID.Trim());
			iconPlaceId = shortcutsViewModel.GameID.Trim();
		}
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
		string shortcutPath = Path.Combine(folderPath, displayName + ".lnk");
		string executable = File.Exists(Paths.Application) ? Paths.Application : Paths.Process;
		string iconPath = string.Empty;
		try
		{
			iconPath = await DownloadAndForceIcoAsync(await FetchGameIconUrlAsync(iconPlaceId), displayName);
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
		try
		{
			if (File.Exists(shortcutPath))
			{
				File.Delete(shortcutPath);
			}
			string arguments = "-player \"" + launchUrl.Replace("\"", "") + "\"";
			Voidstrap.Utility.Shortcut.Create(executable, arguments, shortcutPath, iconPath);
			if (!File.Exists(shortcutPath))
			{
				Frontend.ShowMessageBox("Failed to create shortcut.\n\nWindows did not let Voidstrap write to the desktop.");
				return;
			}
			Frontend.ShowMessageBox("Shortcut created:\n" + displayName);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("Shortcuts::CreateShortcut", ex);
			Frontend.ShowMessageBox("Failed to create shortcut.\n\nError: " + Describe(ex));
		}
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
		code = HttpUtility.ParseQueryString(shareUri.Query)["code"] ?? "";
		if (code.Length == 0 || code.Length > 512)
			return ("", "");
		try
		{
			using CancellationTokenSource redirectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
			Uri current = shareUri;
			for (int i = 0; i < 5 && string.IsNullOrEmpty(placeId); i++)
			{
				using HttpResponseMessage response = await _noRedirectClient.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, redirectCts.Token);
				Uri? location = response.Headers.Location;
				if (location == null)
					break;
				current = location.IsAbsoluteUri ? location : new Uri(current, location);
				if (current.Scheme != Uri.UriSchemeHttps || (!current.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) && !current.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)))
					return ("", "");
				Match match = Regex.Match(current.AbsolutePath, "/games/(\\d+)(?:/|$)", RegexOptions.IgnoreCase);
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

	private async Task<string?> FetchGameIconUrlAsync(string gameId)
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

	private async Task<string> DownloadAndForceIcoAsync(string? imageUrl, string baseName)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(imageUrl))
			{
				return string.Empty;
			}
			string text = Path.Combine(Paths.UserData, "Icons");
			Directory.CreateDirectory(text);
			string iconPath = Path.Combine(text, baseName + ".ico");
			using CancellationTokenSource imageCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
			using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, imageCts.Token).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			byte[] imageBytes = await Voidstrap.Utility.Http.ReadBytesBoundedAsync(response.Content, 4 * 1024 * 1024, imageCts.Token).ConfigureAwait(false);
			SixLabors.ImageSharp.ImageInfo? imageInfo = SixLabors.ImageSharp.Image.Identify(imageBytes);
			if (imageInfo == null || imageInfo.Width <= 0 || imageInfo.Height <= 0 || (long)imageInfo.Width * imageInfo.Height > 16_777_216)
				throw new InvalidDataException("Game icon dimensions are invalid");
			using MemoryStream stream = new MemoryStream(imageBytes, writable: false);
			using Bitmap original = new Bitmap(stream);
			using Bitmap bitmap = new Bitmap(original, new Size(64, 64));
			nint hicon = bitmap.GetHicon();
			try
			{
				using (Icon icon = Icon.FromHandle(hicon))
				{
					using FileStream outputStream = new FileStream(iconPath, FileMode.Create, FileAccess.Write);
					icon.Save(outputStream);
				}
			}
			finally
			{
				DestroyIcon(hicon);
			}
			return iconPath;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Shortcuts::DownloadIcon", "Could not build the game icon: " + ex.Message);
			return string.Empty;
		}
	}
}



