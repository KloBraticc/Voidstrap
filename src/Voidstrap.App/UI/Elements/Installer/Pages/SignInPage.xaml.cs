using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.Resources;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Installer.Pages;

public partial class SignInPage : UiPage
{
	private CancellationTokenSource? _pollCts;

	private string _sessionId = "";

	private bool _busy;

	public SignInPage()
	{
		InitializeComponent();
		Unloaded += UiPage_Unloaded;
	}

	private void UiPage_Loaded(object sender, RoutedEventArgs e)
	{
		Unloaded -= UiPage_Unloaded;
		Unloaded += UiPage_Unloaded;
		if (Window.GetWindow(this) is MainWindow mainWindow)
		{
			mainWindow.SetNextButtonText(Strings.Common_Navigation_Next);
			mainWindow.SetButtonEnabled("next", state: true);
		}
	}

	private void UiPage_Unloaded(object sender, RoutedEventArgs e)
	{
		Unloaded -= UiPage_Unloaded;
		CancelPolling();
		_busy = false;
	}

	private void SignIn_Click(object sender, RoutedEventArgs e)
	{
		if (_busy)
		{
			return;
		}

		_busy = true;
		byte[] bytes = new byte[32];
		System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
		_sessionId = Convert.ToHexString(bytes).ToLowerInvariant();

		if (!OpenSignInPage())
		{
			_busy = false;
			return;
		}

		IdlePanel.Visibility = Visibility.Collapsed;
		DonePanel.Visibility = Visibility.Collapsed;
		WaitingPanel.Visibility = Visibility.Visible;
		WaitingStatus.Text = "";

		_pollCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		_ = PollAsync(_sessionId, _pollCts.Token);
	}

	private bool OpenSignInPage()
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = App.WebsiteBaseUrl + "/pages/app-signin.html#session=" + _sessionId,
				UseShellExecute = true
			});
			return true;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("SignInPage::OpenSignInPage", ex);
			Frontend.ShowMessageBox("Could not open the sign in page in your browser. Please try again.", MessageBoxImage.Warning);
			return false;
		}
	}

	private void Reopen_Click(object sender, RoutedEventArgs e)
	{
		OpenSignInPage();
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		CancelPolling();
		_busy = false;
		WaitingPanel.Visibility = Visibility.Collapsed;
		DonePanel.Visibility = Visibility.Collapsed;
		IdlePanel.Visibility = Visibility.Visible;
	}

	private void CancelPolling()
	{
		try
		{
			_pollCts?.Cancel();
			_pollCts?.Dispose();
		}
		catch
		{
		}

		_pollCts = null;
	}

	private async Task PollAsync(string sessionId, CancellationToken token)
	{
		string pollUrl = App.WebsiteBaseUrl + "/api/app/auth/poll";
		string? vsToken = null;
		int failures = 0;

		while (!token.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(2000, token).ConfigureAwait(true);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			try
			{
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, pollUrl);
				request.Headers.TryAddWithoutValidation("x-app-session", sessionId);
				using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(true);
				if (!response.IsSuccessStatusCode)
				{
					failures++;
					App.Logger.WriteLine("SignInPage::Poll", "Poll returned " + (int)response.StatusCode + " " + response.ReasonPhrase + " for " + pollUrl);
					if (failures == 3)
					{
						WaitingStatus.Text = "The sign in service is not responding, status " + (int)response.StatusCode + ". Still trying.";
					}
					continue;
				}
				failures = 0;

				string json = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 262144, token).ConfigureAwait(true);
				using JsonDocument document = JsonDocument.Parse(json);
				if (document.RootElement.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind == JsonValueKind.True
					&& document.RootElement.TryGetProperty("vs_token", out JsonElement tokenElement) && tokenElement.ValueKind == JsonValueKind.String)
				{
					vsToken = tokenElement.GetString();
					break;
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("SignInPage::Poll", "Poll error, retrying: " + ex.Message);
			}
		}

		if (string.IsNullOrWhiteSpace(vsToken))
		{
			if (!token.IsCancellationRequested)
			{
				WaitingStatus.Text = "Sign in timed out. You can try again or continue without signing in.";
				_busy = false;
			}

			CancelPolling();
			return;
		}

		try
		{
			Voidstrap.Utility.WebsiteAuth.Save(vsToken.Trim());
			await ShowProfileAsync(vsToken.Trim(), token).ConfigureAwait(true);
		}
		finally
		{
			CancelPolling();
		}
	}

	private async Task ShowProfileAsync(string authToken, CancellationToken token)
	{
		string displayName = "";
		string username = "";
		string avatar = "";
		string avatarBorder = "";
		string borderJson = "";
		string banner = "";
		string userId = "";

		try
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, App.WebsiteBaseUrl + "/api/me");
			request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);
			using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(true);
			App.Logger.WriteLine("SignInPage::ShowProfile", "/api/me responded " + (int)response.StatusCode);
			if (response.IsSuccessStatusCode)
			{
				string json = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 4 * 1024 * 1024, token).ConfigureAwait(true);
				using JsonDocument document = JsonDocument.Parse(json);
				if (document.RootElement.TryGetProperty("user", out JsonElement user) && user.ValueKind == JsonValueKind.Object)
				{
					displayName = ReadString(user, "displayName");
					username = ReadString(user, "username");
					userId = ReadString(user, "id");
					avatar = ReadString(user, "avatar");
					banner = ReadString(user, "banner");
					avatarBorder = ReadString(user, "avatarBorder");
					if (user.TryGetProperty("equippedBorder", out JsonElement equipped) && equipped.ValueKind == JsonValueKind.Object)
					{
						borderJson = equipped.GetRawText();
					}
				}
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("SignInPage::ShowProfile", "Could not load the profile: " + ex.Message);
		}

		if (userId.Length > 0)
		{
			try
			{
				string label = displayName.Length > 0 ? displayName : username;
				Voidstrap.Utility.WebsiteAuth.AddOrUpdateAccount(authToken, userId, label, avatar);
				Voidstrap.Installer.PendingAuthToken = authToken;
				Voidstrap.Installer.PendingAuthId = userId;
				Voidstrap.Installer.PendingAuthLabel = label;
				Voidstrap.Installer.PendingAuthAvatar = avatar;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("SignInPage::ShowProfile", "Could not register the account: " + ex.Message);
			}
		}

		DoneName.Text = displayName.Length > 0 ? displayName : (username.Length > 0 ? username : "Signed in");
		DoneUsername.Text = username.Length > 0 ? "@" + username : "";

		AvatarRing.Fill = Voidstrap.Utility.GradientWebsite.Parse(avatarBorder)
			?? (Brush)FindResource("SystemAccentColorSecondaryBrush");

		App.Logger.WriteLine("SignInPage::ShowProfile", "avatar=" + (avatar.Length > 0 ? avatar : "(none)") + " border=" + (avatarBorder.Length > 0 ? avatarBorder : "(none)") + " border=" + (borderJson.Length > 0 ? "yes" : "(none)") + " banner=" + (banner.Length > 0 ? banner : "(none)"));

		List<Task> pending = new List<Task>();
		Task<BitmapSource?> avatarTask = Voidstrap.Utility.AppImage.LoadAsync(ResolveUrl(avatar), 256);
		pending.Add(avatarTask);
		Task<BitmapSource?>? bannerTask = null;
		if (banner.Length > 0)
		{
			bannerTask = Voidstrap.Utility.GradientWebsite.LoadBannerImageAsync(ResolveUrl(banner));
			pending.Add(bannerTask);
		}
		Task<Voidstrap.Utility.BorderRender?>? borderTask = null;
		if (borderJson.Length > 0)
		{
			borderTask = Task.Run(() =>
			{
				try
				{
					using JsonDocument borderDocument = JsonDocument.Parse(borderJson);
					return Voidstrap.Utility.WebsiteBorderRenderer.Build(borderDocument.RootElement, 104.0, 170.0);
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("SignInPage::ShowProfile", "Could not build the profile border: " + ex.Message);
					return null;
				}
			});
			pending.Add(borderTask);
		}

		await Task.WhenAll(pending).ConfigureAwait(true);
		token.ThrowIfCancellationRequested();

		BitmapSource? avatarBitmap = await avatarTask.ConfigureAwait(true);
		if (avatarBitmap != null)
		{
			ImageBrush avatarBrush = new ImageBrush(avatarBitmap) { Stretch = Stretch.UniformToFill };
			if (avatarBrush.CanFreeze)
			{
				avatarBrush.Freeze();
			}
			AvatarFill.Fill = avatarBrush;
		}
		else
		{
			AvatarFill.Fill = (Brush)FindResource("ControlFillColorDefaultBrush");
		}

		if (bannerTask != null)
		{
			BitmapSource? bannerBitmap = await bannerTask.ConfigureAwait(true);
			if (bannerBitmap != null)
			{
				BannerImage.Source = bannerBitmap;
				BannerHost.Visibility = Visibility.Visible;
			}
		}

		if (borderTask != null)
		{
			Voidstrap.Utility.BorderRender? render = await borderTask.ConfigureAwait(true);
			if (render?.Image != null)
			{
				AvatarBorderImage.Source = render.Image;
				AvatarBorderImage.Width = render.Width;
				AvatarBorderImage.Height = render.Height;
				AvatarBorderImage.Margin = render.Margin;
				AvatarBorderImage.Visibility = Visibility.Visible;
			}
		}

		_busy = false;
		WaitingPanel.Visibility = Visibility.Collapsed;
		IdlePanel.Visibility = Visibility.Collapsed;
		DonePanel.Visibility = Visibility.Visible;
	}

	private static string ReadBorderImage(JsonElement user)
	{
		foreach (string name in new[] { "avatarBorderImage", "borderImage" })
		{
			string direct = ReadString(user, name);
			if (direct.Length > 0)
			{
				return direct;
			}
		}

		if (user.TryGetProperty("avatarBorder", out JsonElement border) && border.ValueKind == JsonValueKind.Object)
		{
			return ReadString(border, "image");
		}

		return "";
	}

	private static string ReadString(JsonElement element, string name)
	{
		return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? ""
			: "";
	}

	private static string ResolveUrl(string url)
	{
		string trimmed = (url ?? "").Trim();
		if (trimmed.Length == 0)
		{
			return "";
		}

		if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			|| trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
		{
			return trimmed;
		}

		return App.WebsiteBaseUrl.TrimEnd('/') + "/" + trimmed.TrimStart('/');
	}

	private void SwitchAccounts_Click(object sender, RoutedEventArgs e)
	{
		CancelPolling();
		_busy = false;
		DonePanel.Visibility = Visibility.Collapsed;
		WaitingPanel.Visibility = Visibility.Collapsed;
		IdlePanel.Visibility = Visibility.Visible;
	}

	private void Skip_Click(object sender, RoutedEventArgs e)
	{
		CancelPolling();
		Advance();
	}

	private void Advance()
	{
		if (Window.GetWindow(this) is MainWindow mainWindow)
		{
			mainWindow.Navigate(typeof(WelcomePage));
			mainWindow.SetButtonEnabled("back", state: true);
			mainWindow.SetButtonEnabled("next", state: true);
		}
	}
}
