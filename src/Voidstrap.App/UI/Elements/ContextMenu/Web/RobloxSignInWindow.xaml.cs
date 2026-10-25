using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Voidstrap.Integrations;
using Voidstrap.Resources;
using Voidstrap.UI;

namespace Voidstrap.UI.Elements.Overlay;

public partial class RobloxSignInWindow
{
	private const string LogIdent = "RobloxSignIn";

	private const string LoginUrl = "https://www.roblox.com/login";

	private const string SignUpUrl = "https://www.roblox.com/signup";

	private readonly DispatcherTimer _pollTimer;

	private readonly string _dataFolder;

	private readonly string _startUrl;

	private CoreWebView2? _core;

	private bool _checking;

	private bool _closed;

	public string? Cookie { get; private set; }

	private RobloxSignInWindow(bool signUp)
	{
		InitializeComponent();
		_startUrl = signUp ? SignUpUrl : LoginUrl;
		string root = Path.Combine(Paths.Cache, "AccountSignIn");
		DeleteStaleProfiles(root);
		_dataFolder = Path.Combine(root, Guid.NewGuid().ToString("N"));
		_pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.0) };
		_pollTimer.Tick += OnPollTick;
		Loaded += OnLoaded;
		Closed += OnClosed;
	}

	public static string? Prompt(bool signUp)
	{
		RobloxSignInWindow window = new RobloxSignInWindow(signUp);
		window.ShowOwnedDialog();
		return window.Cookie;
	}

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		Loaded -= OnLoaded;
		try
		{
			CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, _dataFolder);
			await WebView.EnsureCoreWebView2Async(environment);
			if (_closed)
				return;
			_core = WebView.CoreWebView2;
			_core.Settings.AreDevToolsEnabled = false;
			_core.Settings.IsStatusBarEnabled = false;
			_core.Settings.AreDefaultContextMenusEnabled = true;
			_core.NavigationStarting += OnNavigationStarting;
			_core.NavigationCompleted += OnNavigationCompleted;
			_core.NewWindowRequested += OnNewWindowRequested;
			_core.Navigate(_startUrl);
			_pollTimer.Start();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The sign in browser could not start: " + ex.Message);
			StatusText.Text = Strings.Accounts_SignIn_BrowserFailed;
		}
	}

	private void LogIn_Click(object sender, RoutedEventArgs e) => _core?.Navigate(LoginUrl);

	private void SignUp_Click(object sender, RoutedEventArgs e) => _core?.Navigate(SignUpUrl);

	private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
	{
		if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) && !RobloxCookie.IsRobloxUrl(uri))
		{
			e.Cancel = true;
			Utilities.OpenWebLink(e.Uri);
		}
	}

	private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
	{
		e.Handled = true;
		if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) && RobloxCookie.IsRobloxUrl(uri))
			_core?.Navigate(e.Uri);
		else
			Utilities.OpenWebLink(e.Uri);
	}

	private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => _ = CheckCookieAsync();

	private void OnPollTick(object? sender, EventArgs e) => _ = CheckCookieAsync();

	private async Task CheckCookieAsync()
	{
		if (_checking || _closed || _core == null)
			return;
		_checking = true;
		try
		{
			var cookies = await _core.CookieManager.GetCookiesAsync("https://www.roblox.com");
			string? value = cookies.FirstOrDefault(c => c.Name == ".ROBLOSECURITY")?.Value;
			if (string.IsNullOrEmpty(value) || _closed)
				return;
			StatusText.Text = Strings.Accounts_SignIn_Verifying;
			RobloxAccount? account = await RobloxCookie.GetAccountAsync(value);
			if (account == null || _closed)
				return;
			_pollTimer.Stop();
			Cookie = value;
			App.Logger?.WriteLine(LogIdent, "Captured a Roblox sign in from the browser");
			Close();
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
		{
			App.Logger?.WriteLine(LogIdent, "The sign in cookie check failed: " + ex.Message);
		}
		finally
		{
			_checking = false;
		}
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		_closed = true;
		Closed -= OnClosed;
		_pollTimer.Stop();
		_pollTimer.Tick -= OnPollTick;
		if (_core != null)
		{
			_core.NavigationStarting -= OnNavigationStarting;
			_core.NavigationCompleted -= OnNavigationCompleted;
			_core.NewWindowRequested -= OnNewWindowRequested;
			try
			{
				_core.CookieManager.DeleteAllCookies();
			}
			catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
			{
			}
			_core = null;
		}
		WebView.Dispose();
	}

	private static void DeleteStaleProfiles(string root)
	{
		if (!Directory.Exists(root))
			return;
		foreach (string folder in Directory.EnumerateDirectories(root))
		{
			try
			{
				Directory.Delete(folder, true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
	}
}
