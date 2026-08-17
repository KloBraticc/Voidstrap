using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Voidstrap.Core;
using Voidstrap.Platform;

namespace Voidstrap.Desktop;

public sealed class RobloxBrowserWindow : Window
{
	private const string HomeUrl = "https://www.roblox.com/home";

	private const string BridgeBootstrapScript = """
(() => {
	if (window.__voidstrapBridgeReady) {
		return;
	}
	const send = payload => {
		const body = typeof payload === 'string' ? payload : JSON.stringify(payload);
		if (typeof invokeCSharpAction === 'function') {
			invokeCSharpAction(body);
			return;
		}
		if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
			window.chrome.webview.postMessage(payload);
		}
	};
	window.__voidstrapPostMessage = send;
	window.__voidstrapBridgeReady = true;
})();
""";

	private readonly IPlatformHost _host;
	private readonly CancellationTokenSource _lifetimeCancellation = new();
	private readonly DeploymentSessionStateMachine _deploymentSession;
	private readonly RobloxUpdateHeatmapService _updateHeatmapService;
	private readonly TextBlock _status;
	private readonly Button _backButton;
	private readonly Button _forwardButton;
	private readonly Button _homeButton;
	private readonly Grid _root;
	private NativeWebView _webView;
	private NativeWebViewHost _webViewHost;
	private PortableSettingsStore? _settingsStore;
	private SettingsDocument? _settings;
	private Task<OperationResult<SettingsLoadResult>>? _settingsLoadTask;
	private bool _closed;
	private bool _recreating;

	public RobloxBrowserWindow(IPlatformHost host)
	{
		_host = host;
		Title = "Roblox Web";
		Width = 1180;
		Height = 780;
		MinWidth = 760;
		MinHeight = 520;
		_updateHeatmapService = new RobloxUpdateHeatmapService();
		_deploymentSession = new DeploymentSessionStateMachine(
			new RuntimeLaunchCoordinator(host.PlayerRuntime, host.StudioRuntime),
			host.ResourceOptimization);

		_backButton = new Button
		{
			Content = "Back",
			Padding = new Thickness(12, 7)
		};
		_backButton.Click += OnBackClicked;
		_forwardButton = new Button
		{
			Content = "Forward",
			Padding = new Thickness(12, 7)
		};
		_forwardButton.Click += OnForwardClicked;
		_homeButton = new Button
		{
			Content = "Home",
			Padding = new Thickness(12, 7)
		};
		_homeButton.Click += OnHomeClicked;

		StackPanel navigation = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			Margin = new Thickness(12, 10, 12, 6)
		};
		navigation.Children.Add(_backButton);
		navigation.Children.Add(_forwardButton);
		navigation.Children.Add(_homeButton);
		_status = new TextBlock
		{
			Text = "Roblox web tools open in the native browser.",
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
			Opacity = 0.74
		};
		navigation.Children.Add(_status);

		_root = new Grid
		{
			RowDefinitions = new RowDefinitions("Auto,*")
		};
		_root.Children.Add(navigation);
		_webView = CreateWebView();
		_webViewHost = CreateWebViewHost(_webView);
		_root.Children.Add(_webView);
		Grid.SetRow(_webView, 1);
		_webView.Source = new Uri(HomeUrl);
		Content = _root;
	}

	protected override void OnClosed(EventArgs e)
	{
		if (!_closed)
		{
			_closed = true;
			_lifetimeCancellation.Cancel();
			_deploymentSession.Dispose();
			DetachWebView();
			DisposeWebView(_webView);
			_backButton.Click -= OnBackClicked;
			_forwardButton.Click -= OnForwardClicked;
			_homeButton.Click -= OnHomeClicked;
			_lifetimeCancellation.Dispose();
		}

		base.OnClosed(e);
	}

	private NativeWebView CreateWebView()
	{
		NativeWebView webView = new NativeWebView();
		webView.NavigationStarted += OnNavigationStarted;
		webView.NavigationCompleted += OnNavigationCompleted;
		webView.NewWindowRequested += OnNewWindowRequested;
		webView.EnvironmentRequested += OnEnvironmentRequested;
		return webView;
	}

	private NativeWebViewHost CreateWebViewHost(NativeWebView webView)
	{
		NativeWebViewHost host = new NativeWebViewHost(webView, RecreateNativeWebViewAsync);
		host.MessageReceived += OnMessageReceived;
		return host;
	}

	private void DetachWebView()
	{
		DetachWebView(_webView, _webViewHost);
	}

	private void DetachWebView(NativeWebView webView, NativeWebViewHost webViewHost)
	{
		webView.NavigationStarted -= OnNavigationStarted;
		webView.NavigationCompleted -= OnNavigationCompleted;
		webView.NewWindowRequested -= OnNewWindowRequested;
		webView.EnvironmentRequested -= OnEnvironmentRequested;
		webViewHost.MessageReceived -= OnMessageReceived;
		webViewHost.Dispose();
	}

	private static void DisposeWebView(NativeWebView webView)
	{
		try
		{
			(webView as IDisposable)?.Dispose();
		}
		catch
		{
		}
	}

	private void OnBackClicked(object? sender, RoutedEventArgs e)
	{
		if (_webView.CanGoBack)
		{
			_webView.GoBack();
		}
	}

	private void OnForwardClicked(object? sender, RoutedEventArgs e)
	{
		if (_webView.CanGoForward)
		{
			_webView.GoForward();
		}
	}

	private void OnHomeClicked(object? sender, RoutedEventArgs e)
	{
		_webView.Navigate(new Uri(HomeUrl));
	}

	private async void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
	{
		if (_closed || e.Request is null)
		{
			return;
		}

		if (RobloxWebNavigationPolicy.IsInAppRobloxUri(e.Request))
		{
			return;
		}

		e.Cancel = true;
		await HandleExternalOrDeeplinkAsync(e.Request);
	}

	private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
	{
		if (_closed)
		{
			return;
		}

		if (!e.IsSuccess)
		{
			_status.Text = "The Roblox page did not load.";
			return;
		}

		OperationResult<string> result = await _webViewHost.ExecuteScriptAsync(BridgeBootstrapScript, _lifetimeCancellation.Token);
		if (!result.Succeeded)
		{
			_status.Text = result.Failure?.Message ?? "The browser bridge could not be initialized.";
			return;
		}

		OperationResult pinResult = await SynchronizeLibraryPinsAsync();
		if (!pinResult.Succeeded)
		{
			_status.Text = pinResult.Failure?.Message ?? "The saved game library could not be loaded.";
		}
	}

	private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
	{
		if (!LinuxWebViewRuntimeDetector.ShouldPreferWebKitGtk())
		{
			return;
		}

		try
		{
			var property = e.GetType().GetProperty("PreferWebKitGtkInstead");
			if (property is { CanWrite: true } && property.PropertyType == typeof(bool))
			{
				property.SetValue(e, true);
			}
		}
		catch (Exception)
		{
		}
	}

	private async void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
	{
		if (_closed || e.Request is null)
		{
			return;
		}

		e.Handled = true;
		if (RobloxWebNavigationPolicy.IsInAppRobloxUri(e.Request))
		{
			await _webViewHost.NavigateAsync(e.Request, _lifetimeCancellation.Token);
			return;
		}

		await HandleExternalOrDeeplinkAsync(e.Request);
	}

	private async void OnMessageReceived(object? sender, BrowserMessageReceivedEventArgs e)
	{
		if (_closed || string.IsNullOrWhiteSpace(e.Message))
		{
			return;
		}

		OperationResult<RobloxBrowserBridgeMessage> parsed = RobloxBrowserBridgeMessageParser.Parse(e.Message);
		if (!parsed.Succeeded || parsed.Value is null)
		{
			_status.Text = parsed.Failure?.Message ?? "The browser bridge sent an invalid message.";
			return;
		}

		await HandleBridgeMessageAsync(parsed.Value);
	}

	private async Task HandleExternalOrDeeplinkAsync(Uri uri)
	{
		if (RobloxDeeplink.TryExtract(uri.AbsoluteUri, out Uri? deeplink) && deeplink is not null)
		{
			await LaunchDeeplinkAsync(deeplink.AbsoluteUri);
			return;
		}

		if (!RobloxWebNavigationPolicy.IsSafeExternalUri(uri))
		{
			_status.Text = "Only secure external links can be opened outside the browser.";
			return;
		}

		await OpenExternalAsync(uri);
	}

	private async Task OpenExternalAsync(Uri uri)
	{
		string? executable = _host.Id switch
		{
			PlatformId.Windows => _host.Processes.FindExecutable("explorer.exe"),
			PlatformId.MacOS => _host.Processes.FindExecutable("/usr/bin/open"),
			_ => FindLinuxExternalOpener()
		};
		if (executable is null)
		{
			_status.Text = "No desktop link opener is available.";
			return;
		}

		IReadOnlyList<string> arguments = Path.GetFileName(executable).StartsWith("gio", StringComparison.OrdinalIgnoreCase)
			? ["open", uri.AbsoluteUri]
			: [uri.AbsoluteUri];
		OperationResult<ProcessStartResult> result = await _host.Processes.StartAsync(
			new ProcessCommand(executable, arguments, CaptureOutput: false),
			_lifetimeCancellation.Token);
		_status.Text = result.Succeeded ? "Opened the external link." : result.Failure?.Message ?? "The external link could not be opened.";
	}

	private string? FindLinuxExternalOpener()
	{
		return _host.Processes.FindExecutable("xdg-open")
			?? _host.Processes.FindExecutable("gio")
			?? _host.Processes.FindExecutable("kde-open5")
			?? _host.Processes.FindExecutable("kde-open");
	}

	private async Task HandleBridgeMessageAsync(RobloxBrowserBridgeMessage message)
	{
		switch (message.Action)
		{
			case RobloxBrowserBridgeAction.Launch:
				await LaunchDeeplinkAsync(message.Url ?? string.Empty);
				return;
			case RobloxBrowserBridgeAction.OpenExternal:
				if (!Uri.TryCreate(message.Url, UriKind.Absolute, out Uri? externalUri) || !RobloxWebNavigationPolicy.IsSafeExternalUri(externalUri))
				{
					_status.Text = "Only secure external links can be opened outside the browser.";
					return;
				}
				await OpenExternalAsync(externalUri);
				return;
			case RobloxBrowserBridgeAction.PrivateServer:
				OperationResult<string> privateServer = RobloxBrowserBridgeMessageParser.BuildPrivateServerDeeplink(message.PlaceId, message.ServerId);
				if (!privateServer.Succeeded || privateServer.Value is null)
				{
					_status.Text = privateServer.Failure?.Message ?? "The private server request is invalid.";
					return;
				}
				await LaunchDeeplinkAsync(privateServer.Value);
				return;
			case RobloxBrowserBridgeAction.UpdateHeatmap:
				await SendUpdateHeatmapAsync(message.UniverseId);
				return;
			case RobloxBrowserBridgeAction.PinGame:
				await UpdateLibraryPinAsync(message.PlaceId, message.UniverseId, message.Name, false);
				return;
			case RobloxBrowserBridgeAction.UnpinGame:
				await UpdateLibraryPinAsync(message.PlaceId, null, null, true);
				return;
			case RobloxBrowserBridgeAction.ApiError:
				await RecreateBrowserAsync();
				return;
			case RobloxBrowserBridgeAction.Matchmake:
				_status.Text = "Smart Join needs the platform matchmaker bridge before it can run here.";
				return;
			case RobloxBrowserBridgeAction.ScriptRan:
				_status.Text = string.IsNullOrWhiteSpace(message.ScriptName) ? "A browser script ran." : "Browser script ran: " + message.ScriptName;
				return;
			case RobloxBrowserBridgeAction.Unknown:
				_status.Text = "The browser bridge sent an unsupported message.";
				return;
			default:
				_status.Text = "This browser message needs a platform provider before it can run here.";
				return;
		}
	}

	private async Task SendUpdateHeatmapAsync(long? universeId)
	{
		OperationResult<RobloxUpdateHeatmap> result = await _updateHeatmapService.GetAsync(universeId ?? 0, _lifetimeCancellation.Token);
		if (!result.Succeeded || result.Value is null)
		{
			_status.Text = result.Failure?.Message ?? "The update heatmap could not be loaded.";
			return;
		}

		OperationResult<string> scriptResult = await _webViewHost.ExecuteScriptAsync(RobloxUpdateHeatmapService.BuildCallbackScript(result.Value), _lifetimeCancellation.Token);
		_status.Text = scriptResult.Succeeded
			? "The update heatmap was refreshed."
			: scriptResult.Failure?.Message ?? "The update heatmap could not be applied.";
	}

	private async Task RecreateBrowserAsync()
	{
		Uri landingUri = _webView.Source ?? new Uri(HomeUrl);
		OperationResult result = await _webViewHost.RecreateAsync(landingUri, _lifetimeCancellation.Token);
		_status.Text = result.Succeeded
			? "The browser was recreated."
			: result.Failure?.Message ?? "The browser could not be recreated.";
	}

	private Task<OperationResult> RecreateNativeWebViewAsync(Uri landingUri, CancellationToken cancellationToken)
	{
		if (_closed)
		{
			return Task.FromResult(OperationResult.Fail("BrowserClosed", "The browser window is closed"));
		}

		if (_recreating)
		{
			return Task.FromResult(OperationResult.Fail("BrowserRecreationInProgress", "The browser is already being recreated"));
		}

		NativeWebView? replacement = null;
		NativeWebViewHost? replacementHost = null;
		bool adopted = false;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			_recreating = true;
			replacement = CreateWebView();
			replacementHost = CreateWebViewHost(replacement);
			_root.Children.Add(replacement);
			Grid.SetRow(replacement, 1);
			NativeWebView previous = _webView;
			DetachWebView();
			_root.Children.Remove(previous);
			DisposeWebView(previous);
			_webView = replacement;
			_webViewHost = replacementHost;
			adopted = true;
			_webView.Source = landingUri;
			return Task.FromResult(OperationResult.Success());
		}
		catch (OperationCanceledException)
		{
			return Task.FromResult(OperationResult.Fail("OperationCanceled", "Browser recreation was canceled"));
		}
		catch (Exception exception)
		{
			return Task.FromResult(OperationResult.Fail("BrowserRecreationFailed", exception.Message));
		}
		finally
		{
			if (!adopted && replacement is not null)
			{
				if (replacementHost is not null)
					DetachWebView(replacement, replacementHost);
				_root.Children.Remove(replacement);
				DisposeWebView(replacement);
			}
			_recreating = false;
		}
	}

	private async Task LaunchDeeplinkAsync(string value)
	{
		RuntimeKind kind = RobloxDeeplink.GetRuntimeKind(value);
		OperationResult<SettingsDocument> settingsResult = await GetSettingsAsync();
		SettingsDocument? settings = settingsResult.Succeeded ? settingsResult.Value : null;
		OperationResult<DeploymentLaunchResult> result = await _deploymentSession.LaunchAsync(kind, value, settings, _lifetimeCancellation.Token);
		_status.Text = result.Succeeded && result.Value is not null
			? result.Value.Summary
			: result.Failure?.Message ?? "The Roblox runtime did not accept the launch request.";
	}

	private async Task<OperationResult<SettingsDocument>> GetSettingsAsync()
	{
		if (_settings is not null)
		{
			return OperationResult<SettingsDocument>.Success(_settings);
		}

		_settingsStore ??= new PortableSettingsStore(_host.Paths);
		_settingsLoadTask ??= _settingsStore.LoadAsync(_lifetimeCancellation.Token);
		OperationResult<SettingsLoadResult> result = await _settingsLoadTask;
		if (!result.Succeeded || result.Value is null)
		{
			if (!_closed)
			{
				_settingsLoadTask = null;
			}

			return result.Failure is null
				? OperationResult<SettingsDocument>.Fail("SettingsUnavailable", "The shared settings are unavailable")
				: OperationResult<SettingsDocument>.Fail(result.Failure.Code, result.Failure.Message, result.Failure.State);
		}

		_settings = result.Value.Document;
		return OperationResult<SettingsDocument>.Success(_settings);
	}

	private async Task<OperationResult> SynchronizeLibraryPinsAsync()
	{
		OperationResult<SettingsDocument> settingsResult = await GetSettingsAsync();
		if (!settingsResult.Succeeded || settingsResult.Value is null)
		{
			return settingsResult.Failure is null
				? OperationResult.Fail("LibraryPinsUnavailable", "The saved game library is unavailable")
				: OperationResult.Fail(settingsResult.Failure.Code, settingsResult.Failure.Message, settingsResult.Failure.State);
		}

		string payload = LibraryPinStore.BuildBrowserPayload(settingsResult.Value);
		OperationResult<string> execution = await _webViewHost.ExecuteScriptAsync(
			"window.__vsLibraryPins = " + payload + "; window.dispatchEvent(new CustomEvent('voidstrapLibraryPinsChanged'));",
			_lifetimeCancellation.Token);
		return execution.Succeeded
			? OperationResult.Success()
			: execution.Failure is null
				? OperationResult.Fail("LibraryPinsSynchronizationFailed", "The saved game library could not be synchronized")
				: OperationResult.Fail(execution.Failure.Code, execution.Failure.Message, execution.Failure.State);
	}

	private async Task UpdateLibraryPinAsync(long? placeId, long? universeId, string? name, bool remove)
	{
		if (placeId is null || placeId.Value <= 0)
		{
			_status.Text = "The game library request did not include a valid place.";
			return;
		}

		OperationResult<SettingsDocument> settingsResult = await GetSettingsAsync();
		if (!settingsResult.Succeeded || settingsResult.Value is null || _settingsStore is null)
		{
			_status.Text = settingsResult.Failure?.Message ?? "The saved game library is unavailable.";
			return;
		}

		bool changed = false;
		OperationResult<SettingsDocument> updateResult = await _settingsStore.UpdateAsync(document =>
		{
			changed = remove
				? LibraryPinStore.Remove(document, placeId.Value)
				: LibraryPinStore.Add(document, placeId.Value, universeId ?? 0, name);
			return changed;
		}, _lifetimeCancellation.Token);
		if (!updateResult.Succeeded || updateResult.Value is null)
		{
			_status.Text = updateResult.Failure?.Message ?? "The saved game library could not be updated.";
			return;
		}
		_settings = updateResult.Value;
		if (!changed)
		{
			_status.Text = remove ? "The game is not pinned." : "The game is already pinned.";
			return;
		}

		OperationResult synchronization = await SynchronizeLibraryPinsAsync();
		if (!synchronization.Succeeded)
		{
			_status.Text = synchronization.Failure?.Message ?? "The saved game library was updated, but the page could not be refreshed.";
			return;
		}

		_status.Text = remove ? "The game was removed from the saved library." : "The game was added to the saved library.";
	}
}
