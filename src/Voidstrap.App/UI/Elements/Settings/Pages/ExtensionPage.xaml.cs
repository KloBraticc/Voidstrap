using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Voidstrap.Utility;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class ExtensionPage : UiPage
{
	internal static string RequestedExtension { get; set; } = "";
	private readonly ExtensionViewModel _vm;

	private readonly List<ExtensionEntry> _assets;

	private object? _detailItem;

	private bool _compactDetail;
	private bool _refreshingRiShadePresets;

	public ExtensionPage()
	{
		_vm = new ExtensionViewModel();
		base.DataContext = _vm;
		InitializeComponent();
		_assets = ExtensionCatalog.Build(_vm);
		AssetList.ItemsSource = _assets;
		Loaded += ExtensionPage_Loaded;
		Unloaded += ExtensionPage_Unloaded;
		Breadcrumb.AddBackRequestedHandler(this, OnBreadcrumbBack);
	}

	private void OnBreadcrumbBack(object sender, RoutedEventArgs e)
	{
		BackToBrowse_Click(sender, e);
	}

	private void ExtensionPage_Loaded(object sender, RoutedEventArgs e)
	{
		UpdateInstallerBackButtonVisibility();
		_vm.RefreshSwiftTunnel();
		_vm.PropertyChanged -= Vm_PropertyChanged;
		_vm.PropertyChanged += Vm_PropertyChanged;
		Voidstrap.Integrations.RiShade.RiShadePanel.OpenChanged -= RiShadePanel_OpenChanged;
		Voidstrap.Integrations.RiShade.RiShadePanel.OpenChanged += RiShadePanel_OpenChanged;
		Voidstrap.Integrations.RiShade.RiShadeDepthModels.StateChanged -= RiShadeDepthModels_StateChanged;
		Voidstrap.Integrations.RiShade.RiShadeDepthModels.StateChanged += RiShadeDepthModels_StateChanged;
		ApplyRiShadeDepthModelState(Voidstrap.Integrations.RiShade.RiShadeDepthModels.State);
		_vm.RiShadeOpenLabel = Voidstrap.Integrations.RiShade.RiShadePanel.IsOpen ? "Close" : "Open";
		DynamicRenderSystem.Prefetch(_assets.Select(a => a.Icon).Where(i => i.Length > 0), 128);
		foreach (ExtensionEntry asset in _assets)
			asset.RefreshEnabled();
		if (_detailItem is ExtensionEntry current)
			RefreshDetailButtons(current);
		ApplyAssetFilter();
		if (RequestedExtension.Length > 0)
		{
			ShowExtension(RequestedExtension);
			RequestedExtension = "";
		}
		_ = PrefetchVersionsAsync();
	}

	private void ExtensionPage_Unloaded(object sender, RoutedEventArgs e)
	{
		_vm.PropertyChanged -= Vm_PropertyChanged;
		Voidstrap.Integrations.RiShade.RiShadePanel.OpenChanged -= RiShadePanel_OpenChanged;
		Voidstrap.Integrations.RiShade.RiShadeDepthModels.StateChanged -= RiShadeDepthModels_StateChanged;
		StopRiShadeKeyRecording();
		if (_detailItem is ExtensionEntry asset)
			asset.PropertyChanged -= Asset_PropertyChanged;
		Voidstrap.Integrations.VoidstrapPresence.Clear(nameof(ExtensionPage));
	}

	private bool _recordingRiShadeKey;

	private void ShowRiShadeKey(string? status)
	{
		Voidstrap.Models.Persistable.OverlayShortcut shortcut = App.Settings.Prop.RiShadeOverlayShortcut ??= new Voidstrap.Models.Persistable.OverlayShortcut { Modifiers = 0, Key = 0x24 };
		RiShadeKeyText.Text = shortcut.Describe();
		RiShadeKeyBox.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
		RiShadeKeyStatus.Text = status ?? string.Empty;
		RiShadeKeyStatus.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
	}

	private void RiShadeKeyBox_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (_recordingRiShadeKey)
			return;
		_recordingRiShadeKey = true;
		Voidstrap.UI.Elements.ContextMenu.MenuContainer.SuspendRiShadeHotkey(true);
		RiShadeKeyText.Text = Voidstrap.Resources.Strings.Ui_RiShade_OverlayKey_Press;
		RiShadeKeyBox.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
		RiShadeKeyBox.Focus();
		System.Windows.Input.Keyboard.Focus(RiShadeKeyBox);
	}

	private void RiShadeKeyBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (!_recordingRiShadeKey)
			return;
		e.Handled = true;
		System.Windows.Input.Key key = e.Key switch
		{
			System.Windows.Input.Key.System => e.SystemKey,
			System.Windows.Input.Key.ImeProcessed => e.ImeProcessedKey,
			_ => e.Key
		};
		if (key == System.Windows.Input.Key.Escape)
		{
			StopRiShadeKeyRecording();
			return;
		}
		if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
			or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
			return;
		System.Windows.Input.ModifierKeys held = System.Windows.Input.Keyboard.Modifiers;
		uint modifiers = 0;
		if (held.HasFlag(System.Windows.Input.ModifierKeys.Control))
			modifiers |= Voidstrap.Models.Persistable.OverlayShortcut.Control;
		if (held.HasFlag(System.Windows.Input.ModifierKeys.Alt))
			modifiers |= Voidstrap.Models.Persistable.OverlayShortcut.Alt;
		if (held.HasFlag(System.Windows.Input.ModifierKeys.Shift))
			modifiers |= Voidstrap.Models.Persistable.OverlayShortcut.Shift;
		uint virtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
		if (virtualKey == 0)
			return;
		Voidstrap.Models.Persistable.OverlayShortcut candidate = new() { Modifiers = modifiers, Key = virtualKey };
		if (!Voidstrap.UI.Elements.ContextMenu.MenuContainer.TryShortcut(candidate))
		{
			RiShadeKeyStatus.Text = string.Format(Voidstrap.Resources.Strings.Ui_RiShade_OverlayKey_InUse, candidate.Describe());
			RiShadeKeyStatus.Visibility = Visibility.Visible;
			return;
		}
		_recordingRiShadeKey = false;
		Voidstrap.Integrations.RiShade.RiShadeManager.SetOverlayShortcut(candidate);
		Voidstrap.UI.Elements.ContextMenu.MenuContainer.SuspendRiShadeHotkey(false);
		ShowRiShadeKey(string.Format(Voidstrap.Resources.Strings.Ui_RiShade_OverlayKey_Saved, candidate.Describe()));
	}

	private void RiShadeKeyBox_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => StopRiShadeKeyRecording();

	private void StopRiShadeKeyRecording()
	{
		if (!_recordingRiShadeKey)
			return;
		_recordingRiShadeKey = false;
		Voidstrap.UI.Elements.ContextMenu.MenuContainer.SuspendRiShadeHotkey(false);
		ShowRiShadeKey(null);
	}

	private void RiShadeKeyReset_Click(object sender, RoutedEventArgs e)
	{
		StopRiShadeKeyRecording();
		Voidstrap.Models.Persistable.OverlayShortcut home = new() { Modifiers = 0, Key = 0x24 };
		Voidstrap.Integrations.RiShade.RiShadeManager.SetOverlayShortcut(home);
		ShowRiShadeKey(string.Format(Voidstrap.Resources.Strings.Ui_RiShade_OverlayKey_Saved, home.Describe()));
	}

	private void DetailRoot_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		bool compact = e.NewSize.Width < 840.0;
		if (compact == _compactDetail)
			return;

		_compactDetail = compact;
		DetailSidebarColumn.Width = new GridLength(compact ? 0.0 : 300.0);
		Grid.SetRow(DetailSidebar, compact ? 1 : 0);
		Grid.SetColumn(DetailSidebar, compact ? 0 : 1);
		DetailMain.Margin = compact ? new Thickness(0.0) : new Thickness(0.0, 0.0, 20.0, 0.0);
		DetailSidebar.Margin = compact ? new Thickness(0.0, 20.0, 0.0, 0.0) : new Thickness(0.0, 38.0, 0.0, 0.0);
	}

	private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(ExtensionViewModel.SearchText) or nameof(ExtensionViewModel.SelectedExtensionType))
		{
			ApplyAssetFilter();
			return;
		}
		if (e.PropertyName is "rishadeenabler" or "apidumpenabler" or "communitycontentenabler" or "rojoenabler" or "studiopluginenabler" or nameof(ExtensionViewModel.SwiftTunnelInstalled) or nameof(ExtensionViewModel.SwiftTunnelBusy))
		{
			foreach (ExtensionEntry asset in _assets)
				asset.RefreshEnabled();
			if (_detailItem is ExtensionEntry entry)
				RefreshDetailButtons(entry);
			return;
		}
		if (e.PropertyName == nameof(ExtensionViewModel.RiShadeOpenLabel) && _detailItem is ExtensionEntry { Id: "rishade" } rishade)
		{
			RefreshDetailButtons(rishade);
		}
	}

	private void RiShadePanel_OpenChanged(bool open)
	{
		Dispatcher.BeginInvoke((Action)delegate
		{
			_vm.RiShadeOpenLabel = open ? "Close" : "Open";
		});
	}

	private void ApplyAssetFilter()
	{
		string query = _vm.SearchText.Trim();
		string type = _vm.SelectedExtensionType;
		foreach (ExtensionEntry asset in _assets)
		{
			bool show = (type == ExtensionViewModel.TypeAll || asset.Type == type)
				&& (query.Length == 0 || asset.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase) || asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
			asset.Visible = show;
		}
	}

	private void ViewAsset_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: ExtensionEntry asset })
			ShowAssetDetail(asset);
	}

	private void ToggleAsset_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: ExtensionEntry asset })
			asset.IsEnabled = !asset.IsEnabled;
	}

	private void ShowAssetDetail(ExtensionEntry asset)
	{
		if (_detailItem is ExtensionEntry previous)
			previous.PropertyChanged -= Asset_PropertyChanged;
		_detailItem = asset;
		SetDetailImage(DetailIcon, asset.Icon, 192);
		DetailName.Text = asset.Name;
		DetailAuthor.Text = asset.Author;
		DetailCreator.Text = asset.Author;
		DetailSummary.Text = asset.Summary;
		DetailSourceButton.Visibility = asset.HasSource ? Visibility.Visible : Visibility.Collapsed;
		DetailParagraphs.ItemsSource = asset.Description;
		RojoTools.Visibility = asset.HasTools ? Visibility.Visible : Visibility.Collapsed;
		RiShadeTools.Visibility = asset.Id == "rishade" ? Visibility.Visible : Visibility.Collapsed;
		if (asset.Id == "rishade")
		{
			RefreshRiShadePresets();
			RefreshRiShadeDepthModel();
			RefreshRiShadeDlss();
			ShowRiShadeKey(null);
		}
		_ = LoadReleasesAsync(asset);
		DetailVersion.Text = asset.Version;
		asset.PropertyChanged -= Asset_PropertyChanged;
		asset.PropertyChanged += Asset_PropertyChanged;
		DetailUpdatedLabel.Visibility = Visibility.Collapsed;
		DetailUpdated.Visibility = Visibility.Collapsed;
		FillChips(DetailWorksWith, asset.WorksWith);
		DetailTags.ItemsSource = asset.Tags;
		RefreshDetailButtons(asset);
		DetailTabs.SelectedIndex = 0;
		OpenDetail();
		Voidstrap.UI.BreadcrumbHeader.Set(this, asset.Name);
		Voidstrap.Integrations.VoidstrapPresence.Set(new Voidstrap.Integrations.VoidstrapPresenceContext(
			nameof(ExtensionPage),
			Voidstrap.Utility.RpcText.Mark("Viewing") + " " + asset.Name,
			!string.IsNullOrWhiteSpace(asset.Summary) ? Voidstrap.Utility.RpcText.Mark(asset.Summary) : string.IsNullOrWhiteSpace(asset.Author) ? Voidstrap.Utility.RpcText.Mark("A Voidstrap extension") : Voidstrap.Utility.RpcText.Mark("Made by") + " " + asset.Author,
			Voidstrap.Integrations.VoidstrapPresence.WebIcon(asset.Icon, asset.Id == "swifttunnel" ? "https://github.com/Swift-tunnel.png" : Voidstrap.Utility.Branding.RpcLogoUrl),
			asset.Name + (string.IsNullOrWhiteSpace(asset.Author) ? "" : " " + Voidstrap.Utility.RpcText.Mark("by") + " " + asset.Author),
			Voidstrap.Utility.RpcText.Mark("View extension"),
			asset.Source));
	}

	private void RefreshDetailButtons(object item)
	{
		if (item is ExtensionEntry asset)
		{
			DetailPrimaryButton.Content = asset.EnableLabel;
			DetailPrimaryButton.Icon = new SymbolIcon { Symbol = asset.IsEnabled ? SymbolRegular.Dismiss24 : SymbolRegular.Checkmark24 };
			DetailPrimaryButton.Appearance = asset.IsEnabled ? ControlAppearance.Danger : ControlAppearance.Primary;
			DetailPrimaryButton.IsEnabled = asset.CanChange;
			bool hasAction = asset.CanOpen || asset.Id == "community";
			DetailOpenButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
			DetailOpenButton.IsEnabled = asset.IsEnabled && asset.CanChange;
			DetailOpenButton.Content = asset.Id switch
			{
				"rishade" => _vm.RiShadeOpenLabel,
				"community" => "Update now",
				_ => "Open"
			};
			DetailStatus.Text = asset.StatusLabel;
		}
	}

	private void Asset_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ExtensionEntry.Version) && ReferenceEquals(sender, _detailItem) && sender is ExtensionEntry asset)
			DetailVersion.Text = asset.Version;
	}

	private int _releaseGeneration;

	private async Task PrefetchVersionsAsync()
	{
		foreach (ExtensionEntry asset in _assets)
		{
			if (asset.GitHubRepo.Length == 0 || asset.VersionResolved)
				continue;
			try
			{
				List<Voidstrap.Models.APIs.GitHub.GithubRelease>? releases = await GitHubCache.GetJsonAsync<List<Voidstrap.Models.APIs.GitHub.GithubRelease>>("https://api.github.com/repos/" + asset.GitHubRepo + "/releases?per_page=10", TimeSpan.FromMinutes(30));
				asset.ApplyReleases(releases);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("ExtensionPage::PrefetchVersions", ex.Message);
			}
		}
	}

	private async Task LoadReleasesAsync(ExtensionEntry asset)
	{
		int generation = Interlocked.Increment(ref _releaseGeneration);
		DetailReleases.ItemsSource = null;
		if (asset.GitHubRepo.Length == 0)
		{
			DetailVersionsNote.Text = asset.Name + " is bundled with Voidstrap and updates together with it.";
			DetailReleases.ItemsSource = new List<ReleaseRow> { new ReleaseRow(asset.Name, "No version for this content", "") };
			return;
		}
		DetailVersionsNote.Text = "Loading releases from GitHub";
		List<ReleaseRow> rows = new List<ReleaseRow>();
		bool loaded = false;
		try
		{
			List<Voidstrap.Models.APIs.GitHub.GithubRelease>? releases = await GitHubCache.GetJsonAsync<List<Voidstrap.Models.APIs.GitHub.GithubRelease>>("https://api.github.com/repos/" + asset.GitHubRepo + "/releases?per_page=10", TimeSpan.FromMinutes(30));
			if (releases != null)
			{
				loaded = true;
				asset.ApplyReleases(releases);
				foreach (Voidstrap.Models.APIs.GitHub.GithubRelease release in releases)
				{
					if (release.Draft || string.IsNullOrEmpty(release.TagName))
						continue;
					string title = string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name;
					string when = DateTime.TryParse(release.CreatedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime created) ? created.ToLocalTime().ToString("d MMMM yyyy") : "";
					string meta = release.TagName + (when.Length > 0 ? ", published " + when : "") + (release.Prerelease ? ", pre release" : "");
					rows.Add(new ReleaseRow(title, meta, "https://github.com/" + asset.GitHubRepo + "/releases/tag/" + release.TagName));
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ExtensionPage::LoadReleases", ex.Message);
		}
		if (generation != Volatile.Read(ref _releaseGeneration) || !ReferenceEquals(_detailItem, asset))
			return;
		DetailReleases.ItemsSource = rows;
		DetailVersionsNote.Text = rows.Count > 0 ? "Releases from " + asset.GitHubRepo + " on GitHub." : loaded ? "No releases have been published on GitHub yet." : "Releases could not be loaded from GitHub right now.";
	}

	private void OpenRelease_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string url } && url.Length > 0)
			Utilities.OpenWebLink(url);
	}

	private void FillChips(WrapPanel panel, IEnumerable<string> values)
	{
		panel.Children.Clear();
		foreach (string value in values)
		{
			panel.Children.Add(new Border
			{
				Style = (Style)FindResource("Chip"),
				Child = new TextBlock { Style = (Style)FindResource("ChipText"), Text = value }
			});
		}
	}

	private static void SetDetailImage(Image image, string source, int decodeWidth)
	{
		if (string.IsNullOrEmpty(source))
		{
			DynamicRenderSystem.SetLazyImageSource(image, null);
			image.Source = null;
			return;
		}
		DynamicRenderSystem.SetLazyImageSource(image, source);
	}

	private void UpdateInstallerBackButtonVisibility()
	{
		InstallerBackButton.Visibility = Window.GetWindow(this) is Voidstrap.UI.Elements.Installer.MainWindow
			? Visibility.Visible
			: Visibility.Collapsed;
	}

	private void OpenDetail()
	{
		UpdateInstallerBackButtonVisibility();
		BrowseRoot.Visibility = Visibility.Collapsed;
		DetailRoot.Visibility = Visibility.Visible;
	}

	private void BackToBrowse_Click(object sender, RoutedEventArgs e)
	{
		if (_detailItem is ExtensionEntry leaving)
			leaving.PropertyChanged -= Asset_PropertyChanged;
		_detailItem = null;
		DetailRoot.Visibility = Visibility.Collapsed;
		BrowseRoot.Visibility = Visibility.Visible;
		Voidstrap.UI.BreadcrumbHeader.Clear(this);
		Voidstrap.Integrations.VoidstrapPresence.Clear(nameof(ExtensionPage));
	}

	private void DetailPrimary_Click(object sender, RoutedEventArgs e)
	{
		if (_detailItem is ExtensionEntry asset)
		{
			asset.IsEnabled = !asset.IsEnabled;
			RefreshDetailButtons(asset);
		}
	}

	private void DetailOpen_Click(object sender, RoutedEventArgs e)
	{
		if (_detailItem is not ExtensionEntry asset)
			return;
		switch (asset.Id)
		{
			case "swifttunnel":
				SwiftTunnel.Open();
				break;
			case "rishade":
				OpenRiShade();
				break;
			case "apidump":
				OpenApiDumpTool();
				break;
			case "community":
				_vm.UpdateCommunityContent();
				break;
		}
	}

	private void OpenSource_Click(object sender, RoutedEventArgs e)
	{
		if (_detailItem is ExtensionEntry { HasSource: true } asset)
			Utilities.OpenWebLink(asset.Source);
	}

	private void RefreshRiShadePresets()
	{
		string? selectedPath = Voidstrap.Integrations.RiShade.RiShadePanel.PreferredPresetPath;
		var presets = Voidstrap.Integrations.RiShade.RiShadePanel.AvailablePresets
			.Select(path => new RiShadePresetEntry(path))
			.ToList();
		_refreshingRiShadePresets = true;
		try
		{
			RiShadePresetPicker.ItemsSource = presets;
			RiShadePresetPicker.SelectedItem = presets.FirstOrDefault(preset =>
				string.Equals(preset.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			_refreshingRiShadePresets = false;
		}
		ShowRiShadePresetStatus(presets.Count == 0 ? Voidstrap.Resources.Strings.Ui_RiShade_Preset_None : null);
	}

	private void ShowRiShadePresetStatus(string? text)
	{
		RiShadePresetStatus.Text = text ?? string.Empty;
		RiShadePresetStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
	}

	private void RiShadePreset_Refresh_Click(object sender, RoutedEventArgs e) => RefreshRiShadePresets();

	private void RefreshRiShadeDepthModel()
	{
		bool available = Voidstrap.Utility.Platform.IsWindows && Voidstrap.Integrations.RiShade.RiShadeManager.DepthBridgeAvailable;
		RiShadeDepthModel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
		if (!available)
			return;
		ApplyRiShadeDepthModelState(Voidstrap.Integrations.RiShade.RiShadeDepthModels.State);
	}

	private void RiShadeDepthModels_StateChanged(object? sender, Voidstrap.Integrations.RiShade.RiShadeDepthModelState state)
	{
		Dispatcher.BeginInvoke(new Action(() => ApplyRiShadeDepthModelState(state)));
	}

	private void ApplyRiShadeDepthModelState(Voidstrap.Integrations.RiShade.RiShadeDepthModelState state)
	{
		RiShadeDepthModelToggle.IsChecked = state.Enabled;
		if (state.Error != null)
			ShowRiShadeDepthModelStatus(string.Format(Voidstrap.Resources.Strings.Ui_RiShade_DepthModel_Failed, state.Error));
		else if (state.Busy)
			ShowRiShadeDepthModelStatus(string.Format(Voidstrap.Resources.Strings.Ui_RiShade_DepthModel_Downloading, state.Percent));
		else
			ShowRiShadeDepthModelStatus(null);
	}

	private void ShowRiShadeDepthModelStatus(string? text)
	{
		RiShadeDepthModelStatus.Text = text ?? "";
		RiShadeDepthModelStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
	}

	private void RiShadeDepthModel_Click(object sender, RoutedEventArgs e)
	{
		bool enabled = RiShadeDepthModelToggle.IsChecked == true;
		if (enabled != Voidstrap.Integrations.RiShade.RiShadeDepthModels.Enabled)
			_ = Voidstrap.Integrations.RiShade.RiShadeDepthModels.SetEnabledAsync(enabled);
	}

	private void RefreshRiShadeDlss()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			RiShadeDlss.Visibility = Visibility.Collapsed;
			RiShadeDlssStatus.Visibility = Visibility.Collapsed;
			return;
		}
		bool installed = Voidstrap.Integrations.RiShade.RiShadeManager.DlssInstalled;
		bool rtx = Voidstrap.Integrations.RiShade.RiShadeManager.HasRtxGpu;
		RiShadeDlss.Visibility = Visibility.Visible;
		RiShadeDlssToggle.IsChecked = installed;
		RiShadeDlssToggle.IsEnabled = rtx || installed;
		ShowRiShadeDlssStatus(rtx ? null : Voidstrap.Resources.Strings.Ui_RiShade_Dlss_NeedsRtx);
	}

	private void ShowRiShadeDlssStatus(string? text)
	{
		RiShadeDlssStatus.Text = text ?? "";
		RiShadeDlssStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
	}

	private async void RiShadeDlss_Click(object sender, RoutedEventArgs e)
	{
		bool enable = RiShadeDlssToggle.IsChecked == true;
		RiShadeDlssToggle.IsEnabled = false;
		ShowRiShadeDlssStatus(enable ? Voidstrap.Resources.Strings.Ui_RiShade_Dlss_Downloading : null);
		(bool success, string message) = await Voidstrap.Integrations.RiShade.RiShadeManager.SetDlssAsync(enable);
		RefreshRiShadeDlss();
		if (!success)
			ShowRiShadeDlssStatus(string.Format(Voidstrap.Resources.Strings.Ui_RiShade_Dlss_Failed, message));
		else
			ShowRiShadeDlssStatus(enable ? Voidstrap.Resources.Strings.Ui_RiShade_Dlss_Ready : Voidstrap.Resources.Strings.Ui_RiShade_Dlss_Removed);
	}

	private void RiShadePreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!IsLoaded || _refreshingRiShadePresets || RiShadePresetPicker.SelectedItem is not RiShadePresetEntry preset)
			return;
		bool live = Voidstrap.Integrations.RiShade.RiShadePanel.HasRuntime;
		if (Voidstrap.Integrations.RiShade.RiShadePanel.SelectPreset(preset.Path))
			ShowRiShadePresetStatus(live ? Voidstrap.Resources.Strings.Ui_RiShade_Preset_Applied : Voidstrap.Resources.Strings.Ui_RiShade_Preset_Saved);
		else
			ShowRiShadePresetStatus(Voidstrap.Resources.Strings.Ui_RiShade_Preset_Failed);
	}

	private sealed record RiShadePresetEntry(string Path)
	{
		public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
	}

	private void ShowExtension(string id)
	{
		ExtensionEntry? asset = _assets.Find(entry => entry.Id == id);
		if (asset != null)
			ShowAssetDetail(asset);
	}

	private static void OpenApiDumpTool()
	{
		string directory = Paths.ApiDumpTool;
		string exe = Path.Combine(directory, "RobloxAPIDumpTool.exe");
		if (Directory.Exists(directory) && File.Exists(exe))
		{
			try
			{
				if (!Voidstrap.Utility.ProcessScan.Any("RobloxAPIDumpTool"))
					Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = directory });
				return;
			}
			catch (Exception ex)
			{
				Frontend.ShowMessageBox("Failed to open the Roblox API Dump Tool: " + ex.Message);
				return;
			}
		}
		Frontend.ShowMessageBox("The Roblox API Dump Tool is not enabled or installed yet, turn its toggle on first.");
	}

	private static void OpenRiShade()
	{
		try
		{
			Voidstrap.Integrations.RiShade.RiShadePanel.Toggle(fromUi: true);
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to open the RiShade panel: " + ex.Message);
		}
	}

	private void BrowseRojoFolder_Click(object sender, RoutedEventArgs e) => _vm.PickRojoFolder();

	private async void RojoInit_Click(object sender, RoutedEventArgs e) => await _vm.RojoInitAsync();

	private void RojoServe_Click(object sender, RoutedEventArgs e) => _vm.RojoToggleServe();

	private async void RojoBuild_Click(object sender, RoutedEventArgs e) => await _vm.RojoBuildAsync();

	private async void RojoInstallPlugin_Click(object sender, RoutedEventArgs e) => await _vm.RojoInstallPluginAsync();

	private void OpenRojo_Click(object sender, RoutedEventArgs e) => _vm.OpenRojoFolder();
}
