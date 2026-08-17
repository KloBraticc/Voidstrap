using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Voidstrap.Core;
using Voidstrap.Platform;

namespace Voidstrap.Desktop;

public sealed class PlatformOverviewWindow : Window
{
	private static readonly string[] ThemeValues = ["Default", "Dark", "Light", "Voidstrap", "UltraGray", "Berry", "Blue", "Cyan", "Green", "Orange", "Pink", "Purple", "Red", "Yellow", "Custom"];
	private static readonly string[] BackdropValues = ["Mica", "Aero", "Acrylic", "Default", "MicaAlt", "None"];

	private readonly IPlatformHost _host;
	private readonly CancellationTokenSource _lifetimeCancellation = new();
	private readonly DeploymentSessionStateMachine _deploymentSession;
	private readonly ObservableCollection<DesktopPageDefinition> _visiblePages = new();
	private readonly List<ComboBox> _settingSelectors = new();
	private readonly List<CheckBox> _settingToggles = new();
	private readonly List<TextBox> _storedSettingEditors = new();
	private readonly List<Button> _actionButtons = new();
	private readonly List<TextBox> _actionInputs = new();
	private readonly TextBox _searchBox;
	private readonly ListBox _navigation;
	private readonly StackPanel _content;
	private readonly Border _sidebarSurface;
	private readonly Border _contentSurface;
	private readonly SolidColorBrush _cardBorderBrush = new(Color.FromArgb(54, 255, 255, 255));
	private readonly SolidColorBrush _accentBrush = new(Color.FromRgb(0, 120, 215));
	private readonly SolidColorBrush _accentForegroundBrush = new(Colors.White);
	private IPlatformSettings? _platformSettings;
	private DesktopPageDefinition? _selectedPage;
	private PortableSettingsStore? _settingsStore;
	private SettingsDocument? _settings;
	private RuntimeInstallation? _playerRuntime;
	private RuntimeInstallation? _studioRuntime;
	private IReadOnlyCollection<ExtensionManifest> _extensions = Array.Empty<ExtensionManifest>();
	private IReadOnlyCollection<SettingsCatalogEntry> _settingsCatalog = Array.Empty<SettingsCatalogEntry>();
	private IReadOnlyCollection<PortFeatureStatus> _portFeatureInventory = Array.Empty<PortFeatureStatus>();
	private IReadOnlyCollection<GameHistoryEntry> _gameHistory = Array.Empty<GameHistoryEntry>();
	private string _launchDeeplink = string.Empty;
	private string? _deploymentStatus;
	private string? _integrationStatus;
	private string? _protocolRegistrationStatus;
	private int _renderVersion;
	private bool _closed;

	private enum StoredSettingValueKind
	{
		String,
		Int32,
		Int64,
		Double,
		Decimal
	}

	private sealed record StoredSettingBinding(string Key, StoredSettingValueKind Kind);

	private sealed record LaunchDeeplinkAction(string Deeplink);

	private readonly record struct ThemePalette(Color Sidebar, Color Content);

	public PlatformOverviewWindow(IPlatformHost host)
	{
		_host = host;
		_deploymentSession = new DeploymentSessionStateMachine(
			new RuntimeLaunchCoordinator(host.PlayerRuntime, host.StudioRuntime),
			host.ResourceOptimization);
		Title = "Voidstrap";
		Width = 1120;
		Height = 720;
		MinWidth = 800;
		MinHeight = 520;

		foreach (DesktopPageDefinition page in DesktopPageCatalog.SidebarPages)
		{
			_visiblePages.Add(page);
		}

		_searchBox = new TextBox
		{
			PlaceholderText = "Search pages",
			Margin = new Thickness(14, 0, 14, 12)
		};
		_searchBox.TextChanged += OnSearchTextChanged;

		_navigation = new ListBox
		{
			ItemsSource = _visiblePages,
			Margin = new Thickness(8, 0, 8, 8)
		};
		_navigation.SelectionChanged += OnNavigationSelectionChanged;

		StackPanel sidebar = new StackPanel
		{
			Spacing = 4
		};
		sidebar.Children.Add(new TextBlock
		{
			Text = "Voidstrap",
			FontSize = 22,
			FontWeight = FontWeight.SemiBold,
			Margin = new Thickness(14, 18, 14, 10)
		});
		sidebar.Children.Add(_searchBox);
		sidebar.Children.Add(_navigation);

		_content = new StackPanel
		{
			Spacing = 14,
			Margin = new Thickness(28, 24, 28, 28)
		};

		Grid root = new Grid
		{
			ColumnDefinitions = new ColumnDefinitions("236,*")
		};
		_sidebarSurface = new Border
		{
			Background = new SolidColorBrush(Color.FromRgb(30, 30, 32)),
			Child = sidebar
		};
		root.Children.Add(_sidebarSurface);
		Grid.SetColumn(_sidebarSurface, 0);

		ScrollViewer scrollViewer = new ScrollViewer
		{
			Content = _content,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};
		_contentSurface = new Border
		{
			Background = new SolidColorBrush(Color.FromRgb(24, 24, 26)),
			Child = scrollViewer
		};
		root.Children.Add(_contentSurface);
		Grid.SetColumn(_contentSurface, 1);
		Content = root;

		_selectedPage = DesktopPageCatalog.GetRequired("Home");
		_navigation.SelectedItem = _selectedPage;
		RenderPage(_selectedPage);
	}

	protected override async void OnOpened(EventArgs e)
	{
		base.OnOpened(e);
		try
		{
			InitializePlatformSettings();
			await LoadSharedStateAsync();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			_deploymentStatus = "Desktop initialization could not complete: " + exception.Message;
			if (!_closed && _selectedPage is not null)
			{
				RenderPage(_selectedPage);
			}
		}
	}

	protected override void OnClosed(EventArgs e)
	{
		if (!_closed)
		{
			_closed = true;
			_lifetimeCancellation.Cancel();
			_searchBox.TextChanged -= OnSearchTextChanged;
			_navigation.SelectionChanged -= OnNavigationSelectionChanged;
			if (_platformSettings is not null)
			{
				_platformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
				_platformSettings = null;
			}
			DetachContentHandlers();
			_deploymentSession.Dispose();
			_lifetimeCancellation.Dispose();
		}

		base.OnClosed(e);
	}

	private async Task LoadSharedStateAsync()
	{
		if (_closed)
		{
			return;
		}

		_settingsStore = new PortableSettingsStore(_host.Paths);
		OperationResult<SettingsLoadResult> settingsResult = await _settingsStore.LoadAsync(_lifetimeCancellation.Token);
		if (settingsResult.Succeeded && settingsResult.Value is not null)
		{
			_settings = settingsResult.Value.Document;
			ApplyTheme(GetThemeValue());
			ApplyWindowPresentation();
		}

		await RefreshRuntimeStatusAsync(false);
		await LoadGameHistoryAsync();
		await LoadExtensionsAsync();
		await LoadSettingsCatalogAsync();
		_portFeatureInventory = PortFeatureInventory.Evaluate(GetEffectiveCapabilities(), _host.Paths.Storage, _settingsCatalog, _extensions);
		await RegisterNonWindowsProtocolsAsync();
		await LaunchInitialDeeplinkAsync();
		if (!_closed && _selectedPage is not null)
		{
			RenderPage(_selectedPage);
		}
	}

	private async Task RefreshRuntimeStatusAsync(bool render)
	{
		try
		{
			_playerRuntime = await _host.PlayerRuntime.FindInstallationAsync(_lifetimeCancellation.Token);
			_studioRuntime = await _host.StudioRuntime.FindInstallationAsync(_lifetimeCancellation.Token);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		_portFeatureInventory = PortFeatureInventory.Evaluate(GetEffectiveCapabilities(), _host.Paths.Storage, _settingsCatalog, _extensions);

		if (render && !_closed && _selectedPage is not null)
		{
			RenderPage(_selectedPage);
		}
	}

	private async Task LoadExtensionsAsync()
	{
		string manifestDirectory = Path.Combine(AppContext.BaseDirectory, "ExtensionManifests");
		OperationResult<IReadOnlyCollection<ExtensionManifest>> result = await ExtensionManifestStore.LoadDirectoryAsync(manifestDirectory, _lifetimeCancellation.Token);
		_extensions = result.Succeeded && result.Value is not null
			? result.Value
			: Array.Empty<ExtensionManifest>();
	}

	private async Task LoadGameHistoryAsync()
	{
		OperationResult<IReadOnlyCollection<GameHistoryEntry>> result = await new GameHistoryStore(_host.Paths).LoadAsync(_lifetimeCancellation.Token);
		_gameHistory = result.Succeeded && result.Value is not null
			? result.Value
			: Array.Empty<GameHistoryEntry>();
	}

	private async Task LoadSettingsCatalogAsync()
	{
		OperationResult<IReadOnlyCollection<SettingsCatalogEntry>> result = await SettingsCatalogImporter.LoadAsync(cancellationToken: _lifetimeCancellation.Token);
		_settingsCatalog = result.Succeeded && result.Value is not null
			? result.Value
			: Array.Empty<SettingsCatalogEntry>();
	}

	private async Task RegisterNonWindowsProtocolsAsync()
	{
		string? applicationPath = GetProtocolApplicationPath();
		if (_host.Id == PlatformId.Windows || string.IsNullOrWhiteSpace(applicationPath))
		{
			return;
		}

		string[] schemes = _host.Id == PlatformId.MacOS
			? ["roblox"]
			: ["roblox", "roblox-player", "roblox-studio", "roblox-studio-auth"];
		foreach (string scheme in schemes)
		{
			OperationResult result;
			try
			{
				result = await _host.ProtocolRegistration.RegisterAsync(
					new ProtocolRegistrationRequest(scheme, applicationPath, "Voidstrap"),
					_lifetimeCancellation.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			if (!result.Succeeded)
			{
				_protocolRegistrationStatus = result.Failure is null
					? "Protocol registration did not complete."
					: $"{result.Failure.State}: {result.Failure.Message}";
				return;
			}
		}

		_protocolRegistrationStatus = "Roblox protocol registration is active.";
	}

	private string? GetProtocolApplicationPath()
	{
		if (_host.Id == PlatformId.Linux)
		{
			string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
			if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage))
			{
				return appImage;
			}
		}

		return !string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath)
			? Environment.ProcessPath
			: null;
	}

	private async Task LaunchInitialDeeplinkAsync()
	{
		string? deeplink = DesktopRuntime.InitialDeeplink;
		if (string.IsNullOrWhiteSpace(deeplink))
		{
			return;
		}

		_launchDeeplink = deeplink;
		DesktopPageDefinition deployment = DesktopPageCatalog.GetRequired("Deployment");
		_selectedPage = deployment;
		_navigation.SelectedItem = deployment;
		RuntimeKind kind = RobloxDeeplink.GetRuntimeKind(deeplink);
		await LaunchRuntimeAsync(kind);
	}

	private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
	{
		string query = _searchBox.Text?.Trim() ?? string.Empty;
		IEnumerable<DesktopPageDefinition> pages = DesktopPageCatalog.SidebarPages;
		if (!string.IsNullOrWhiteSpace(query))
		{
			pages = pages.Where(page => page.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || page.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
		}

		_visiblePages.Clear();
		foreach (DesktopPageDefinition page in pages)
		{
			_visiblePages.Add(page);
		}

		if (_selectedPage is not null && _visiblePages.Contains(_selectedPage))
		{
			_navigation.SelectedItem = _selectedPage;
		}
	}

	private void OnNavigationSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_navigation.SelectedItem is not DesktopPageDefinition page || ReferenceEquals(_selectedPage, page))
		{
			return;
		}

		_selectedPage = page;
		RenderPage(page);
	}

	private IPlatformCapabilities GetEffectiveCapabilities()
	{
		return new RuntimeAwarePlatformCapabilities(_host.Capabilities, _playerRuntime, _studioRuntime);
	}

	private void RenderPage(DesktopPageDefinition page)
	{
		int version = ++_renderVersion;
		Dispatcher.UIThread.Post(
			() =>
			{
				if (!_closed && version == _renderVersion)
				{
					RenderPageNow(page);
				}
			},
			DispatcherPriority.Background);
	}

	private void RenderPageNow(DesktopPageDefinition page)
	{
		DetachContentHandlers();
		_content.Children.Clear();
		_content.Children.Add(new TextBlock
		{
			Text = page.Title,
			FontSize = 30,
			FontWeight = FontWeight.SemiBold
		});
		_content.Children.Add(new TextBlock
		{
			Text = page.Description,
			FontSize = 15,
			Opacity = 0.78,
			TextWrapping = TextWrapping.Wrap
		});

		if (page.CapabilityFeature is FeatureId feature)
		{
			_content.Children.Add(CreateCapabilityCard(GetEffectiveCapabilities().Get(feature)));
		}

		switch (page.Id)
		{
			case "Home":
				RenderHomePage();
				break;
			case "Integrations":
				RenderIntegrationsPage();
				break;
			case "Appearance":
				RenderAppearancePage();
				break;
			case "Deployment":
			case "Manager":
				RenderDeploymentPage();
				break;
			case "Extensions":
				RenderExtensionsPage();
				break;
			case "Settings":
				RenderSettingsPage();
				break;
			case "Mods":
				RenderModsPage();
				break;
			case "About":
				RenderAboutPage();
				break;
			default:
				RenderFeatureSummary(page);
				break;
		}

		RenderCatalogSettings(page);
	}

	private void RenderHomePage()
	{
		_content.Children.Add(CreateSectionTitle("Platform"));
		_content.Children.Add(CreateDetailCard("Operating system", _host.Id.ToString()));
		_content.Children.Add(CreateDetailCard("Player runtime", FormatRuntime(_playerRuntime)));
		_content.Children.Add(CreateDetailCard("Studio runtime", FormatRuntime(_studioRuntime)));
		RenderPinnedGames();
		RenderRecentGames();
	}

	private void RenderPinnedGames()
	{
		_content.Children.Add(CreateSectionTitle("Saved games"));
		IReadOnlyList<LibraryPin> pins = _settings is null ? Array.Empty<LibraryPin>() : LibraryPinStore.Get(_settings);
		if (pins.Count == 0)
		{
			_content.Children.Add(CreateDetailCard("Saved games", "No games are pinned yet."));
			return;
		}

		foreach (LibraryPin pin in pins.Take(12))
		{
			string title = string.IsNullOrWhiteSpace(pin.Name)
				? "Place " + pin.PlaceId.ToString(CultureInfo.InvariantCulture)
				: pin.Name;
			string details = "Place " + pin.PlaceId.ToString(CultureInfo.InvariantCulture)
				+ (pin.UniverseId > 0 ? ", universe " + pin.UniverseId.ToString(CultureInfo.InvariantCulture) : string.Empty);
			GameHistoryEntry entry = new(pin.PlaceId, pin.UniverseId, string.Empty, string.Empty, null, null, string.Empty);
			string? deeplink = entry.BuildDeeplink();
			_content.Children.Add(string.IsNullOrWhiteSpace(deeplink)
				? CreateDetailCard(title, details)
				: CreateLaunchableDetailCard(title, details, deeplink));
		}
	}

	private void RenderRecentGames()
	{
		_content.Children.Add(CreateSectionTitle("Recent games"));
		if (_gameHistory.Count == 0)
		{
			_content.Children.Add(CreateDetailCard("Recent games", "No recent game sessions are available."));
			return;
		}

		IReadOnlyList<LibraryPin> pins = _settings is null ? Array.Empty<LibraryPin>() : LibraryPinStore.Get(_settings);
		foreach (GameHistoryEntry entry in _gameHistory.Take(12))
		{
			LibraryPin? pin = pins.FirstOrDefault(pin =>
				(entry.UniverseId > 0 && pin.UniverseId == entry.UniverseId)
				|| pin.PlaceId == entry.PlaceId);
			string title = pin is null || string.IsNullOrWhiteSpace(pin.Name)
				? "Place " + entry.PlaceId.ToString(CultureInfo.InvariantCulture)
				: pin.Name;
			string details = BuildHistoryDetails(entry);
			string? deeplink = entry.BuildDeeplink();
			_content.Children.Add(string.IsNullOrWhiteSpace(deeplink)
				? CreateDetailCard(title, details)
				: CreateLaunchableDetailCard(title, details, deeplink));
		}
	}

	private void RenderAppearancePage()
	{
		_content.Children.Add(CreateSectionTitle("Theme"));
		ComboBox selector = new ComboBox
		{
			ItemsSource = ThemeValues,
			SelectedItem = GetThemeValue(),
			HorizontalAlignment = HorizontalAlignment.Left,
			MinWidth = 180,
			Tag = "Theme2"
		};
		selector.SelectionChanged += OnSettingSelectorChanged;
		_settingSelectors.Add(selector);
		_content.Children.Add(CreateSettingCard("Application theme", "Applies the shared desktop theme immediately.", selector));

		ComboBox backdrop = new ComboBox
		{
			ItemsSource = BackdropValues,
			SelectedItem = GetBackdropValue(),
			HorizontalAlignment = HorizontalAlignment.Left,
			MinWidth = 180,
			Tag = "WindowBackdrop"
		};
		backdrop.SelectionChanged += OnSettingSelectorChanged;
		_settingSelectors.Add(backdrop);
		_content.Children.Add(CreateSettingCard("Window backdrop", "Updates the shared desktop surface immediately.", backdrop));
		if (string.Equals(GetThemeValue(), "Custom", StringComparison.OrdinalIgnoreCase))
		{
			_content.Children.Add(CreateDetailCard("Custom theme", "The custom WPF theme editor remains in the Windows baseline during migration."));
		}
	}

	private void RenderIntegrationsPage()
	{
		_content.Children.Add(CreateSectionTitle("Native notifications"));
		CapabilityDescriptor capability = _host.Notifications.Capability;
		_content.Children.Add(CreateDetailCard("Notification service", capability.Reason));
		if (capability.IsAvailable)
		{
			_content.Children.Add(CreateActionButton("Send test notification", "SendTestNotification"));
		}

		if (!string.IsNullOrWhiteSpace(_integrationStatus))
		{
			_content.Children.Add(CreateDetailCard("Notification status", _integrationStatus));
		}
	}

	private void RenderDeploymentPage()
	{
		_content.Children.Add(CreateSectionTitle("Runtime status"));
		_content.Children.Add(CreateRuntimeCard("Roblox player", _playerRuntime));
		_content.Children.Add(CreateRuntimeCard("Roblox Studio", _studioRuntime));
		_content.Children.Add(CreateDetailCard("Launch resource profile", FormatResourceProfile()));
		_content.Children.Add(CreateCapabilityCard(_host.ResourceOptimization.Capability));
		if (!string.IsNullOrWhiteSpace(_protocolRegistrationStatus))
		{
			_content.Children.Add(CreateDetailCard("Protocol registration", _protocolRegistrationStatus));
		}
		_content.Children.Add(CreateSectionTitle("Launch deeplink"));
		TextBox launchInput = new TextBox
		{
			Text = _launchDeeplink,
			PlaceholderText = "Paste a Roblox deeplink",
			Tag = "LaunchDeeplink"
		};
		launchInput.TextChanged += OnActionInputTextChanged;
		_actionInputs.Add(launchInput);
		_content.Children.Add(launchInput);
		StackPanel launchButtons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8
		};
		Button playerLaunch = CreateActionButton("Launch player", "LaunchPlayer");
		Button studioLaunch = CreateActionButton("Launch Studio", "LaunchStudio");
		Button browserLaunch = CreateActionButton("Open Roblox web", "OpenRobloxWeb");
		launchButtons.Children.Add(playerLaunch);
		launchButtons.Children.Add(studioLaunch);
		launchButtons.Children.Add(browserLaunch);
		_content.Children.Add(launchButtons);
		if (!string.IsNullOrWhiteSpace(_deploymentStatus))
		{
			_content.Children.Add(CreateDetailCard("Launch status", _deploymentStatus));
		}
		Button refresh = new Button
		{
			Content = "Refresh runtime status",
			Tag = "RefreshRuntime",
			HorizontalAlignment = HorizontalAlignment.Left,
			Padding = new Thickness(14, 8)
		};
		refresh.Click += OnActionButtonClicked;
		_actionButtons.Add(refresh);
		_content.Children.Add(refresh);
	}

	private void RenderExtensionsPage()
	{
		_content.Children.Add(CreateSectionTitle("Extension availability"));
		if (_extensions.Count == 0)
		{
			_content.Children.Add(CreateDetailCard("Extensions", "No extension manifests were found."));
			return;
		}

		foreach (ExtensionManifest extension in _extensions.OrderBy(static extension => extension.DisplayName, StringComparer.OrdinalIgnoreCase))
		{
			ExtensionAvailability availability = ExtensionCapabilityEvaluator.Evaluate(extension, GetEffectiveCapabilities(), _host.Paths.Storage);
			_content.Children.Add(CreateDetailCard(extension.DisplayName, $"{availability.Capability.State}: {availability.Capability.Reason}"));
		}
	}

	private void RenderSettingsPage()
	{
		_content.Children.Add(CreateSectionTitle("Application settings"));
		bool updateCheck = _settings?.Get("CheckForUpdates", true) ?? true;
		PortableSettingSupport updateSupport = PortableSettingSupportResolver.Resolve(_host, "CheckForUpdates");
		_content.Children.Add(CreateDetailCard("Updates", (updateCheck ? "Enabled." : "Disabled.") + " " + updateSupport.Reason));
		_content.Children.Add(CreateDetailCard("Settings location", _settingsStore?.FilePath ?? _host.Paths.Storage.Configuration));
		_content.Children.Add(CreateSectionTitle("Platform capabilities"));
		foreach (CapabilityDescriptor capability in GetEffectiveCapabilities().Features)
		{
			_content.Children.Add(CreateCapabilityCard(capability));
		}
	}

	private void RenderModsPage()
	{
		_content.Children.Add(CreateSectionTitle("Platform dependent enhancements"));
		_content.Children.Add(CreateCapabilityCard(GetEffectiveCapabilities().Get(FeatureId.Overlay)));
		_content.Children.Add(CreateCapabilityCard(GetEffectiveCapabilities().Get(FeatureId.AssetInjection)));
		_content.Children.Add(CreateCapabilityCard(GetEffectiveCapabilities().Get(FeatureId.FrameGeneration)));
		_content.Children.Add(CreateCapabilityCard(GetEffectiveCapabilities().Get(FeatureId.VirtualController)));
	}

	private void RenderAboutPage()
	{
		_content.Children.Add(CreateDetailCard("Desktop host", "Shared Avalonia desktop shell"));
		_content.Children.Add(CreateDetailCard("Current platform", _host.Id.ToString()));
		_content.Children.Add(CreateSectionTitle("Port inventory"));
		if (_portFeatureInventory.Count == 0)
		{
			_content.Children.Add(CreateDetailCard("Port inventory", "The shared feature inventory is still loading."));
			return;
		}

		foreach (IGrouping<PortFeatureKind, PortFeatureStatus> group in _portFeatureInventory
			.GroupBy(static status => status.Definition.Kind)
			.OrderBy(static group => group.Key))
		{
			_content.Children.Add(CreateSectionTitle(FormatPortFeatureKind(group.Key)));
			foreach (PortFeatureStatus status in group
				.OrderBy(static entry => entry.State)
				.ThenBy(static entry => entry.Definition.Title, StringComparer.OrdinalIgnoreCase))
			{
				string details = FormatPortFeatureState(status.State) + ": " + status.Reason + " Source: " + status.Definition.Source + ". Test: " + status.Definition.TestCaseId + ".";
				if (!string.IsNullOrWhiteSpace(status.RequiredAction))
				{
					details += " Required action: " + status.RequiredAction + ".";
				}

				_content.Children.Add(CreateDetailCard(status.Definition.Title, details));
			}
		}
	}

	private static string FormatPortFeatureKind(PortFeatureKind kind)
	{
		return kind switch
		{
			PortFeatureKind.BrowserBridge => "Browser bridge",
			PortFeatureKind.Dialog => "Dialogs",
			PortFeatureKind.NativeIntegration => "Native integrations",
			PortFeatureKind.Package => "Release packages",
			_ => kind + "s"
		};
	}

	private static string FormatPortFeatureState(PortFeatureState state)
	{
		return state switch
		{
			PortFeatureState.NativeEquivalent => "Native equivalent",
			PortFeatureState.PermissionRequired => "Permission required",
			PortFeatureState.ExternalRuntimeRequired => "External runtime required",
			_ => state.ToString()
		};
	}

	private void RenderFeatureSummary(DesktopPageDefinition page)
	{
		_content.Children.Add(CreateSectionTitle("Platform availability"));
		_content.Children.Add(CreateDetailCard("Status", page.CapabilityFeature is null ? "Shared page" : "Capability state is shown above."));
	}

	private void RenderCatalogSettings(DesktopPageDefinition page)
	{
		IReadOnlyCollection<string> sourcePages = GetCatalogSourcePages(page.Id);
		if (sourcePages.Count == 0)
		{
			return;
		}

		SettingsCatalogEntry[] entries = _settingsCatalog
			.Where(entry => sourcePages.Contains(entry.SourcePage, StringComparer.Ordinal))
			.ToArray();
		if (entries.Length == 0)
		{
			return;
		}

		_content.Children.Add(CreateSectionTitle("Settings"));
		foreach (SettingsCatalogEntry entry in entries)
		{
			if (entry.Aliases.Contains("Theme", StringComparer.OrdinalIgnoreCase) || entry.Aliases.Contains("SelectedBackdrop", StringComparer.OrdinalIgnoreCase))
			{
				continue;
			}

			string title = SettingsCatalogImporter.GetDisplayText(entry.Title);
			if (string.IsNullOrWhiteSpace(title))
			{
				title = entry.Aliases.FirstOrDefault() ?? "Setting";
			}

			string description = SettingsCatalogImporter.GetDisplayText(entry.Description);
			if (string.IsNullOrWhiteSpace(description))
			{
				description = "This setting is available in the current configuration.";
			}

			string? key = _settings is null ? null : SettingsKeyResolver.Resolve(_settings, entry.Aliases);
			PortableSettingSupport support = PortableSettingSupportResolver.Resolve(_host, key);
			Control? editor = support.IsEditable && key is not null ? CreateStoredSettingEditor(key) : CreateReadOnlySettingValue(key);
			_content.Children.Add(editor is null
				? CreateDetailCard(title, description + " " + support.Reason)
				: CreateSettingCard(title, description + " " + support.Reason, editor));
		}
	}

	private Control? CreateStoredSettingEditor(string key)
	{
		if (_settings is null || _settings.Root[key] is not JsonNode node)
		{
			return null;
		}

		if (node is not JsonValue value)
		{
			return new TextBlock
			{
				Text = node.ToJsonString(),
				TextWrapping = TextWrapping.Wrap,
				Opacity = 0.76
			};
		}

		if (value.TryGetValue<bool>(out bool booleanValue))
		{
			CheckBox toggle = new CheckBox
			{
				Content = "Enabled",
				IsChecked = booleanValue,
				Tag = key
			};
			toggle.Click += OnSettingToggleClicked;
			_settingToggles.Add(toggle);
			return toggle;
		}

		if (value.TryGetValue<int>(out int intValue))
		{
			return CreateStoredTextEditor(key, intValue.ToString(CultureInfo.InvariantCulture), StoredSettingValueKind.Int32);
		}

		if (value.TryGetValue<long>(out long longValue))
		{
			return CreateStoredTextEditor(key, longValue.ToString(CultureInfo.InvariantCulture), StoredSettingValueKind.Int64);
		}

		if (value.TryGetValue<double>(out double doubleValue))
		{
			return CreateStoredTextEditor(key, doubleValue.ToString(CultureInfo.InvariantCulture), StoredSettingValueKind.Double);
		}

		if (value.TryGetValue<decimal>(out decimal decimalValue))
		{
			return CreateStoredTextEditor(key, decimalValue.ToString(CultureInfo.InvariantCulture), StoredSettingValueKind.Decimal);
		}

		if (value.TryGetValue<string>(out string? stringValue))
		{
			return CreateStoredTextEditor(key, stringValue ?? string.Empty, StoredSettingValueKind.String);
		}

		return new TextBlock
		{
			Text = node.ToJsonString(),
			TextWrapping = TextWrapping.Wrap,
			Opacity = 0.76
		};
	}

	private Control? CreateReadOnlySettingValue(string? key)
	{
		if (_settings is null || string.IsNullOrWhiteSpace(key) || _settings.Root[key] is not JsonNode node)
		{
			return null;
		}

		string value = node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string? stringValue)
			? stringValue ?? string.Empty
			: node.ToJsonString();
		return new TextBlock
		{
			Text = value,
			TextWrapping = TextWrapping.Wrap,
			Opacity = 0.76
		};
	}

	private TextBox CreateStoredTextEditor(string key, string value, StoredSettingValueKind kind)
	{
		TextBox editor = new TextBox
		{
			Text = value,
			Tag = new StoredSettingBinding(key, kind)
		};
		editor.LostFocus += OnStoredSettingEditorLostFocus;
		_storedSettingEditors.Add(editor);
		return editor;
	}

	private async void OnSettingSelectorChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_closed || _settings is null || sender is not ComboBox selector || selector.Tag is not string key || selector.SelectedItem is not string value)
		{
			return;
		}

		if (string.Equals(key, "Theme2", StringComparison.Ordinal))
		{
			SettingsEnumValueCodec.Set(_settings, key, ThemeValues, value);
		}
		else if (string.Equals(key, "WindowBackdrop", StringComparison.Ordinal))
		{
			SettingsEnumValueCodec.Set(_settings, key, BackdropValues, value);
		}
		else
		{
			_settings.Set(key, value);
		}
		if (string.Equals(key, "Theme", StringComparison.Ordinal) || string.Equals(key, "Theme2", StringComparison.Ordinal))
		{
			ApplyTheme(value);
		}
		if (string.Equals(key, "Theme2", StringComparison.Ordinal) || string.Equals(key, "WindowBackdrop", StringComparison.Ordinal))
		{
			ApplyWindowPresentation();
		}

		await SaveSettingsAsync(key);
	}

	private async void OnSettingToggleClicked(object? sender, RoutedEventArgs e)
	{
		if (_closed || _settings is null || sender is not CheckBox toggle || toggle.Tag is not string key)
		{
			return;
		}

		_settings.Set(key, toggle.IsChecked == true);
		await SaveSettingsAsync(key);
	}

	private async void OnStoredSettingEditorLostFocus(object? sender, RoutedEventArgs e)
	{
		if (_closed || _settings is null || sender is not TextBox { Tag: StoredSettingBinding binding } editor)
		{
			return;
		}

		string text = editor.Text ?? string.Empty;
		switch (binding.Kind)
		{
			case StoredSettingValueKind.String:
				_settings.Set(binding.Key, text);
				break;
			case StoredSettingValueKind.Int32 when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue):
				_settings.Set(binding.Key, intValue);
				break;
			case StoredSettingValueKind.Int64 when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue):
				_settings.Set(binding.Key, longValue);
				break;
			case StoredSettingValueKind.Double when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue):
				_settings.Set(binding.Key, doubleValue);
				break;
			case StoredSettingValueKind.Decimal when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal decimalValue):
				_settings.Set(binding.Key, decimalValue);
				break;
			default:
				return;
		}

		await SaveSettingsAsync(binding.Key);
	}

	private async void OnActionButtonClicked(object? sender, RoutedEventArgs e)
	{
		if (_closed || sender is not Button button)
		{
			return;
		}

		if (button.Tag is LaunchDeeplinkAction launch)
		{
			_launchDeeplink = launch.Deeplink;
			await LaunchRuntimeAsync(RuntimeKind.Player);
			return;
		}

		if (button.Tag is not string action)
		{
			return;
		}

		if (string.Equals(action, "RefreshRuntime", StringComparison.Ordinal))
		{
			await RefreshRuntimeStatusAsync(true);
			return;
		}

		if (string.Equals(action, "SendTestNotification", StringComparison.Ordinal))
		{
			await SendTestNotificationAsync();
			return;
		}

		if (string.Equals(action, "LaunchPlayer", StringComparison.Ordinal))
		{
			await LaunchRuntimeAsync(RuntimeKind.Player);
			return;
		}

		if (string.Equals(action, "LaunchStudio", StringComparison.Ordinal))
		{
			await LaunchRuntimeAsync(RuntimeKind.Studio);
			return;
		}

		if (string.Equals(action, "OpenRobloxWeb", StringComparison.Ordinal))
		{
			CapabilityDescriptor capability = GetEffectiveCapabilities().Get(FeatureId.EmbeddedBrowser);
			if (!capability.IsAvailable)
			{
				_deploymentStatus = $"{capability.State}: {capability.Reason}";
				RenderPage(_selectedPage!);
				return;
			}

			try
			{
				new RobloxBrowserWindow(_host).Show(this);
			}
			catch (Exception exception)
			{
				_deploymentStatus = "The embedded browser could not start: " + exception.Message;
				RenderPage(_selectedPage!);
			}
		}
	}

	private async Task SendTestNotificationAsync()
	{
		OperationResult result = await _host.Notifications.ShowAsync(
			new NotificationRequest("Voidstrap", "Native notification support is active."),
			_lifetimeCancellation.Token);
		_integrationStatus = result.Succeeded
			? "The test notification was sent."
			: result.Failure is null
				? "The test notification could not be sent."
				: result.Failure.State + ": " + result.Failure.Message;
		if (!_closed && _selectedPage is not null)
		{
			RenderPage(_selectedPage);
		}
	}

	private void OnActionInputTextChanged(object? sender, TextChangedEventArgs e)
	{
		if (sender is TextBox { Tag: "LaunchDeeplink" } input)
		{
			_launchDeeplink = input.Text ?? string.Empty;
		}
	}

	private async Task LaunchRuntimeAsync(RuntimeKind kind)
	{
		OperationResult<DeploymentLaunchResult> result = await _deploymentSession.LaunchAsync(
			kind,
			_launchDeeplink,
			_settings,
			_lifetimeCancellation.Token);
		if (result.Succeeded && result.Value is not null)
		{
			_deploymentStatus = result.Value.Summary;
		}
		else
		{
			_deploymentStatus = result.Failure is null
				? "The runtime did not accept the launch request."
				: $"{result.Failure.State}: {result.Failure.Message}";
		}
		if (!_closed && _selectedPage is not null)
		{
			RenderPage(_selectedPage);
		}
	}

	private async Task SaveSettingsAsync(string key)
	{
		if (_settingsStore is null || _settings is null)
		{
			return;
		}

		JsonNode? value = _settings.Root[key]?.DeepClone();
		OperationResult<SettingsDocument> result = await _settingsStore.UpdateAsync(document =>
		{
			if (value is null)
			{
				document.Remove(key);
			}
			else
			{
				document.Root[key] = value.DeepClone();
			}
			return true;
		}, _lifetimeCancellation.Token);
		if (result.Succeeded && result.Value is not null)
		{
			_settings = result.Value;
		}
	}

	private static void ApplyTheme(string value)
	{
		if (Application.Current is null)
		{
			return;
		}

		Application.Current.RequestedThemeVariant = value switch
		{
			"Light" => ThemeVariant.Light,
			"Default" => ThemeVariant.Default,
			_ => ThemeVariant.Dark
		};
	}

	private void ApplyWindowPresentation()
	{
		string theme = GetThemeValue();
		string backdrop = GetBackdropValue();
		bool light = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
			|| (string.Equals(theme, "Default", StringComparison.OrdinalIgnoreCase) && Application.Current?.ActualThemeVariant == ThemeVariant.Light);
		ThemePalette palette = GetThemePalette(theme);
		Color sidebarColor = palette.Sidebar;
		Color contentColor = palette.Content;
		if (string.Equals(backdrop, "Acrylic", StringComparison.OrdinalIgnoreCase))
		{
			sidebarColor = Blend(sidebarColor, light ? Color.FromRgb(255, 255, 255) : Color.FromRgb(50, 50, 54), 0.12);
			contentColor = Blend(contentColor, light ? Color.FromRgb(255, 255, 255) : Color.FromRgb(42, 42, 46), 0.12);
		}
		else if (string.Equals(backdrop, "Aero", StringComparison.OrdinalIgnoreCase))
		{
			sidebarColor = Blend(sidebarColor, light ? Color.FromRgb(233, 240, 249) : Color.FromRgb(38, 44, 54), 0.15);
			contentColor = Blend(contentColor, light ? Color.FromRgb(246, 250, 255) : Color.FromRgb(31, 36, 45), 0.15);
		}
		else if (string.Equals(backdrop, "Mica", StringComparison.OrdinalIgnoreCase))
		{
			sidebarColor = Blend(sidebarColor, light ? Color.FromRgb(236, 241, 248) : Color.FromRgb(37, 40, 46), 0.08);
			contentColor = Blend(contentColor, light ? Color.FromRgb(248, 250, 253) : Color.FromRgb(28, 30, 35), 0.08);
		}
		else if (string.Equals(backdrop, "MicaAlt", StringComparison.OrdinalIgnoreCase))
		{
			sidebarColor = Blend(sidebarColor, light ? Color.FromRgb(245, 247, 250) : Color.FromRgb(34, 36, 41), 0.06);
			contentColor = Blend(contentColor, light ? Color.FromRgb(252, 253, 255) : Color.FromRgb(24, 26, 30), 0.06);
		}
		else if (string.Equals(backdrop, "None", StringComparison.OrdinalIgnoreCase))
		{
			sidebarColor = palette.Sidebar;
			contentColor = palette.Content;
		}

		_sidebarSurface.Background = new SolidColorBrush(sidebarColor);
		_contentSurface.Background = new SolidColorBrush(contentColor);
		Background = new SolidColorBrush(contentColor);
		_cardBorderBrush.Color = Color.FromArgb(126, _accentBrush.Color.R, _accentBrush.Color.G, _accentBrush.Color.B);
	}

	private void InitializePlatformSettings()
	{
		if (_platformSettings is not null)
		{
			return;
		}

		_platformSettings = Application.Current?.PlatformSettings;
		if (_platformSettings is not null)
		{
			_platformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;
			ApplySystemColors();
		}
	}

	private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e)
	{
		if (_closed)
		{
			return;
		}

		Dispatcher.UIThread.Post(ApplySystemColors);
	}

	private void ApplySystemColors()
	{
		if (_platformSettings is null)
		{
			return;
		}

		PlatformColorValues colors = _platformSettings.GetColorValues();
		_accentBrush.Color = colors.AccentColor1;
		_accentForegroundBrush.Color = GetAccentForeground(colors.AccentColor1);
		ApplyWindowPresentation();
	}

	private string GetThemeValue()
	{
		return _settings is null ? "Dark" : SettingsEnumValueCodec.Get(_settings, "Theme2", ThemeValues, "Dark");
	}

	private string GetBackdropValue()
	{
		return _settings is null ? "Mica" : SettingsEnumValueCodec.Get(_settings, "WindowBackdrop", BackdropValues, "Mica");
	}

	private static ThemePalette GetThemePalette(string theme)
	{
		if (string.Equals(theme, "Default", StringComparison.OrdinalIgnoreCase) && Application.Current?.ActualThemeVariant == ThemeVariant.Light)
		{
			return new ThemePalette(Color.FromRgb(243, 243, 243), Color.FromRgb(243, 243, 243));
		}

		return theme switch
		{
			"Light" => new ThemePalette(Color.FromRgb(243, 243, 243), Color.FromRgb(243, 243, 243)),
			"Voidstrap" => new ThemePalette(Color.FromRgb(19, 7, 36), Color.FromRgb(30, 11, 47)),
			"UltraGray" => new ThemePalette(Color.FromRgb(24, 24, 24), Color.FromRgb(32, 32, 32)),
			"Berry" => new ThemePalette(Color.FromRgb(72, 20, 58), Color.FromRgb(34, 10, 28)),
			"Blue" => new ThemePalette(Color.FromRgb(22, 63, 107), Color.FromRgb(11, 26, 46)),
			"Cyan" => new ThemePalette(Color.FromRgb(14, 78, 73), Color.FromRgb(7, 32, 31)),
			"Green" => new ThemePalette(Color.FromRgb(19, 74, 36), Color.FromRgb(10, 30, 16)),
			"Orange" => new ThemePalette(Color.FromRgb(90, 49, 16), Color.FromRgb(36, 19, 7)),
			"Pink" => new ThemePalette(Color.FromRgb(90, 24, 56), Color.FromRgb(38, 10, 24)),
			"Purple" => new ThemePalette(Color.FromRgb(56, 23, 95), Color.FromRgb(27, 11, 48)),
			"Red" => new ThemePalette(Color.FromRgb(90, 24, 24), Color.FromRgb(38, 11, 11)),
			"Yellow" => new ThemePalette(Color.FromRgb(84, 74, 16), Color.FromRgb(34, 30, 8)),
			_ => new ThemePalette(Color.FromRgb(32, 32, 32), Color.FromRgb(32, 32, 32))
		};
	}

	private static Color Blend(Color source, Color target, double amount)
	{
		byte BlendComponent(byte start, byte end)
		{
			return (byte)Math.Clamp((int)Math.Round(start + (end - start) * amount), 0, 255);
		}

		return Color.FromRgb(
			BlendComponent(source.R, target.R),
			BlendComponent(source.G, target.G),
			BlendComponent(source.B, target.B));
	}

	private static Color GetAccentForeground(Color accent)
	{
		double luminance = (0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B) / 255.0;
		return luminance > 0.6 ? Colors.Black : Colors.White;
	}

	private string FormatResourceProfile()
	{
		if (_settings is null)
		{
			return "Loading settings.";
		}

		ResourceOptimizationProfile profile = ResourceOptimizationProfileResolver.Resolve(_settings);
		if (!profile.IsEnabled)
		{
			return "No launch resource profile is selected.";
		}

		string cpuLimit = profile.CpuLimit.HasValue
			? profile.CpuLimit.Value.ToString(CultureInfo.InvariantCulture) + " logical processors"
			: "automatic CPU selection";
		return profile.Priority + ", " + cpuLimit;
	}

	private void DetachContentHandlers()
	{
		foreach (ComboBox selector in _settingSelectors)
		{
			selector.SelectionChanged -= OnSettingSelectorChanged;
		}

		foreach (CheckBox toggle in _settingToggles)
		{
			toggle.Click -= OnSettingToggleClicked;
		}

		foreach (TextBox editor in _storedSettingEditors)
		{
			editor.LostFocus -= OnStoredSettingEditorLostFocus;
		}

		foreach (Button button in _actionButtons)
		{
			button.Click -= OnActionButtonClicked;
		}

		foreach (TextBox input in _actionInputs)
		{
			input.TextChanged -= OnActionInputTextChanged;
		}

		_settingSelectors.Clear();
		_settingToggles.Clear();
		_storedSettingEditors.Clear();
		_actionButtons.Clear();
		_actionInputs.Clear();
	}

	private static IReadOnlyCollection<string> GetCatalogSourcePages(string pageId)
	{
		return pageId switch
		{
			"Appearance" => ["AppearancePage"],
			"Deployment" => ["BehaviourPage", "BootstrapperPage"],
			"Settings" => ["ChannelPage"],
			"FastFlagSettings" => ["FastFlagsPage"],
			"Global" => ["GBSEditorPage"],
			"Integrations" => ["IntegrationsPage"],
			"Mods" => ["ModsPage"],
			"Shortcuts" => ["ShortcutsPage"],
			"NvidiaFastFlags" => ["NvidiaFastFlagsPage"],
			"Friends" => ["FriendsPage"],
			_ => Array.Empty<string>()
		};
	}

	private static TextBlock CreateSectionTitle(string text)
	{
		return new TextBlock
		{
			Text = text,
			FontSize = 18,
			FontWeight = FontWeight.SemiBold,
			Margin = new Thickness(0, 8, 0, 0)
		};
	}

	private Border CreateSettingCard(string title, string description, Control control)
	{
		StackPanel content = new StackPanel
		{
			Spacing = 8
		};
		content.Children.Add(new TextBlock
		{
			Text = title,
			FontWeight = FontWeight.SemiBold,
			TextWrapping = TextWrapping.Wrap
		});
		content.Children.Add(new TextBlock
		{
			Text = description,
			Opacity = 0.74,
			TextWrapping = TextWrapping.Wrap
		});
		content.Children.Add(control);
		return CreateCard(content);
	}

	private Border CreateCapabilityCard(CapabilityDescriptor capability)
	{
		StackPanel content = new StackPanel
		{
			Spacing = 5
		};
		content.Children.Add(new TextBlock
		{
			Text = $"{capability.Feature}: {capability.State}",
			FontWeight = FontWeight.SemiBold,
			TextWrapping = TextWrapping.Wrap
		});
		content.Children.Add(new TextBlock
		{
			Text = capability.Reason,
			Opacity = 0.76,
			TextWrapping = TextWrapping.Wrap
		});
		if (!string.IsNullOrWhiteSpace(capability.RequiredAction))
		{
			content.Children.Add(new TextBlock
			{
				Text = capability.RequiredAction,
				Opacity = 0.76,
				TextWrapping = TextWrapping.Wrap
			});
		}

		return CreateCard(content);
	}

	private Border CreateRuntimeCard(string title, RuntimeInstallation? installation)
	{
		return CreateDetailCard(title, FormatRuntime(installation));
	}

	private Button CreateActionButton(string content, string action)
	{
		Button button = new Button
		{
			Content = content,
			Tag = action,
			Padding = new Thickness(14, 8),
			Background = _accentBrush,
			Foreground = _accentForegroundBrush
		};
		button.Click += OnActionButtonClicked;
		_actionButtons.Add(button);
		return button;
	}

	private Border CreateLaunchableDetailCard(string title, string text, string deeplink)
	{
		StackPanel content = new StackPanel
		{
			Spacing = 8
		};
		content.Children.Add(new TextBlock
		{
			Text = title,
			FontWeight = FontWeight.SemiBold,
			TextWrapping = TextWrapping.Wrap
		});
		content.Children.Add(new TextBlock
		{
			Text = text,
			Opacity = 0.76,
			TextWrapping = TextWrapping.Wrap
		});
		Button button = new Button
		{
			Content = "Launch",
			Tag = new LaunchDeeplinkAction(deeplink),
			Padding = new Thickness(14, 8),
			HorizontalAlignment = HorizontalAlignment.Left,
			Background = _accentBrush,
			Foreground = _accentForegroundBrush
		};
		button.Click += OnActionButtonClicked;
		_actionButtons.Add(button);
		content.Children.Add(button);
		return CreateCard(content);
	}

	private static string BuildHistoryDetails(GameHistoryEntry entry)
	{
		string details = "Place " + entry.PlaceId.ToString(CultureInfo.InvariantCulture);
		if (entry.UniverseId > 0)
		{
			details += ", universe " + entry.UniverseId.ToString(CultureInfo.InvariantCulture);
		}

		if (entry.JoinedAt is DateTimeOffset joined)
		{
			details += ", played " + joined.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
		}

		return details;
	}

	private Border CreateDetailCard(string title, string text)
	{
		StackPanel content = new StackPanel
		{
			Spacing = 5
		};
		content.Children.Add(new TextBlock
		{
			Text = title,
			FontWeight = FontWeight.SemiBold,
			TextWrapping = TextWrapping.Wrap
		});
		content.Children.Add(new TextBlock
		{
			Text = text,
			Opacity = 0.76,
			TextWrapping = TextWrapping.Wrap
		});
		return CreateCard(content);
	}

	private Border CreateCard(Control content)
	{
		return new Border
		{
			BorderBrush = _cardBorderBrush,
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(9),
			Padding = new Thickness(14),
			Child = content
		};
	}

	private static string FormatRuntime(RuntimeInstallation? installation)
	{
		if (installation is null)
		{
			return "Checking runtime availability";
		}

		string version = string.IsNullOrWhiteSpace(installation.Version) ? string.Empty : $" {installation.Version}";
		return $"{installation.Provider}{version}, {installation.Capability.State}: {installation.Capability.Reason}";
	}
}
