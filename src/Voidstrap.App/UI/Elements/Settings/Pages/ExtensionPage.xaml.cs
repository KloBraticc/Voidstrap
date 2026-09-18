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

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class ExtensionPage : UiPage
{
	private readonly ExtensionViewModel _vm;

	private readonly List<ExtensionEntry> _assets;

	private object? _detailItem;

	private bool _compactDetail;

	public ExtensionPage()
	{
		_vm = new ExtensionViewModel();
		base.DataContext = _vm;
		InitializeComponent();
		_assets = ExtensionCatalog.Build(_vm);
		AssetList.ItemsSource = _assets;
		Loaded += ExtensionPage_Loaded;
		Unloaded += ExtensionPage_Unloaded;
	}

	private void ExtensionPage_Loaded(object sender, RoutedEventArgs e)
	{
		_vm.PropertyChanged -= Vm_PropertyChanged;
		_vm.PropertyChanged += Vm_PropertyChanged;
		Voidstrap.Integrations.RiShade.RiShadePanel.OpenChanged -= RiShadePanel_OpenChanged;
		Voidstrap.Integrations.RiShade.RiShadePanel.OpenChanged += RiShadePanel_OpenChanged;
		_vm.RiShadeOpenLabel = Voidstrap.Integrations.RiShade.RiShadePanel.IsOpen ? "Close" : "Open";
		DynamicRenderSystem.Prefetch(_assets.Select(a => a.Icon).Where(i => i.Length > 0), 128);
		foreach (ExtensionEntry asset in _assets)
			asset.RefreshEnabled();
		ApplyAssetFilter();
		_ = PrefetchVersionsAsync();
	}

	private void ExtensionPage_Unloaded(object sender, RoutedEventArgs e)
	{
		_vm.PropertyChanged -= Vm_PropertyChanged;
		Voidstrap.Integrations.RiShade.RiShadePanel.OpenChanged -= RiShadePanel_OpenChanged;
		Voidstrap.Integrations.VoidstrapPresence.Clear(nameof(ExtensionPage));
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
		if (e.PropertyName is "fleasionenabler" or "rishadeenabler" or "apidumpenabler" or "communitycontentenabler" or "rojoenabler" or "studiopluginenabler")
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
		_detailItem = asset;
		SetDetailImage(DetailIcon, asset.Icon, 192);
		DetailName.Text = asset.Name;
		DetailAuthor.Text = asset.Author;
		DetailCreator.Text = asset.Author;
		DetailSummary.Text = asset.Summary;
		DetailSourceButton.Visibility = asset.HasSource ? Visibility.Visible : Visibility.Collapsed;
		DetailParagraphs.ItemsSource = asset.Description;
		RojoTools.Visibility = asset.HasTools ? Visibility.Visible : Visibility.Collapsed;
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
		Voidstrap.Integrations.VoidstrapPresence.Set(new Voidstrap.Integrations.VoidstrapPresenceContext(
			nameof(ExtensionPage),
			"Viewing " + asset.Name,
			string.IsNullOrWhiteSpace(asset.Summary) ? "Extension by " + asset.Author : asset.Summary,
			asset.Icon,
			asset.Name + (string.IsNullOrWhiteSpace(asset.Author) ? "" : " by " + asset.Author),
			"View extension",
			asset.Source));
	}

	private void RefreshDetailButtons(object item)
	{
		if (item is ExtensionEntry asset)
		{
			DetailPrimaryButton.Content = asset.EnableLabel;
			DetailPrimaryButton.Icon = asset.IsEnabled ? SymbolRegular.Dismiss24 : SymbolRegular.Checkmark24;
			DetailPrimaryButton.Appearance = asset.IsEnabled ? ControlAppearance.Danger : ControlAppearance.Primary;
			DetailPrimaryButton.IsEnabled = true;
			bool hasAction = asset.CanOpen || asset.Id == "community";
			DetailOpenButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
			DetailOpenButton.IsEnabled = asset.IsEnabled;
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
			Utilities.ShellExecute(url);
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

	private void OpenDetail()
	{
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
			case "fleasion":
				OpenFleasion();
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
			Utilities.ShellExecute(asset.Source);
	}

	private static void OpenFleasion()
	{
		string directory = Paths.Fleasion;
		string exe = Path.Combine(directory, "Fleasion.exe");
		if (Directory.Exists(directory) && File.Exists(exe))
		{
			try
			{
				if (Process.GetProcessesByName("Fleasion").Length == 0)
					Process.Start(exe);
				return;
			}
			catch (Exception ex)
			{
				Frontend.ShowMessageBox("Failed to open Fleasion: " + ex.Message);
				return;
			}
		}
		Frontend.ShowMessageBox("Fleasion is not enabled or installed yet, turn its toggle on first.");
	}

	private static void OpenApiDumpTool()
	{
		string directory = Paths.ApiDumpTool;
		string exe = Path.Combine(directory, "RobloxAPIDumpTool.exe");
		if (Directory.Exists(directory) && File.Exists(exe))
		{
			try
			{
				if (Process.GetProcessesByName("RobloxAPIDumpTool").Length == 0)
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
