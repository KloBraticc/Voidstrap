using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Voidstrap.Exceptions;
using Voidstrap.Integrations.Studio;
using Voidstrap.Resources;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class StudioPluginsView : UserControl
{
	private const string LogIdent = "StudioPluginsView";

	private readonly ObservableCollection<StudioPluginEntry> _entries = [];

	private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

	private CancellationTokenSource? _loading;

	private string? _nextPageToken;

	private bool _started;

	private bool _compactDetail;

	private StudioPluginEntry? _detail;

	public StudioPluginsView()
	{
		InitializeComponent();
		PluginList.ItemsSource = _entries;
		SourcePicker.SelectedIndex = 0;
	}

	private bool StoreMode => SourcePicker.SelectedIndex != 1;

	private void View_Loaded(object sender, RoutedEventArgs e)
	{
		_searchTimer.Tick -= SearchTimer_Tick;
		_searchTimer.Tick += SearchTimer_Tick;
		if (_started)
			return;
		_started = true;
		_ = ReloadAsync(false);
	}

	private void View_Unloaded(object sender, RoutedEventArgs e)
	{
		_searchTimer.Stop();
		_searchTimer.Tick -= SearchTimer_Tick;
		_loading?.Cancel();
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (!_started)
			return;
		_searchTimer.Stop();
		_searchTimer.Start();
	}

	private void SearchTimer_Tick(object? sender, EventArgs e)
	{
		_searchTimer.Stop();
		_ = ReloadAsync(false);
	}

	private void SourcePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_started)
			_ = ReloadAsync(false);
	}

	private void LoadMore_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync(true);

	private async Task ReloadAsync(bool append)
	{
		CancellationTokenSource source = new();
		Interlocked.Exchange(ref _loading, source)?.Cancel();
		CancellationToken token = source.Token;
		string query = SearchBox.Text.Trim();
		if (!append)
		{
			_entries.Clear();
			_nextPageToken = null;
		}
		ShowStatus(null);
		LoadingRing.Visibility = Visibility.Visible;
		LoadMoreButton.Visibility = Visibility.Collapsed;
		EmptyState.Visibility = Visibility.Collapsed;
		try
		{
			IReadOnlyList<InstalledStudioPlugin> installed = await Task.Run(StudioPluginCatalog.ListInstalled, token);
			List<StudioPluginEntry> added = [];
			if (StoreMode)
			{
				StudioPluginSearchPage page = await StudioPluginCatalog.SearchAsync(query, append ? _nextPageToken : null, token);
				token.ThrowIfCancellationRequested();
				HashSet<long> shown = _entries.Where(entry => entry.AssetId.HasValue).Select(entry => entry.AssetId!.Value).ToHashSet();
				foreach (StudioPluginListing listing in page.Plugins.Where(listing => shown.Add(listing.Id)))
					added.Add(new StudioPluginEntry(listing, installed.FirstOrDefault(plugin => plugin.AssetId == listing.Id)));
				_nextPageToken = page.NextPageToken;
			}
			else
			{
				foreach (InstalledStudioPlugin plugin in installed.Where(plugin => query.Length == 0 || plugin.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
					added.Add(new StudioPluginEntry(null, plugin));
			}
			foreach (StudioPluginEntry entry in added)
				_entries.Add(entry);
			_ = EnrichAsync(added, !StoreMode, token);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			return;
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException or InvalidHTTPResponseException)
		{
			App.Logger.WriteException(LogIdent + "::Reload", ex);
			ShowStatus(Strings.Ui_Mods_StudioPlugins_StoreUnavailable);
		}
		finally
		{
			if (Interlocked.CompareExchange(ref _loading, null, source) == source)
			{
				LoadingRing.Visibility = Visibility.Collapsed;
				LoadMoreButton.Visibility = StoreMode && _nextPageToken != null ? Visibility.Visible : Visibility.Collapsed;
				EmptyTitle.Text = StoreMode ? Strings.Ui_Mods_StudioPlugins_EmptyTitle : Strings.Ui_Mods_StudioPlugins_EmptyInstalled;
				EmptyState.Visibility = _entries.Count == 0 && BrowseStatus.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
			}
			source.Dispose();
		}
	}

	private static async Task EnrichAsync(IReadOnlyList<StudioPluginEntry> entries, bool fetchDetails, CancellationToken token)
	{
		List<long> ids = entries.Where(entry => entry.AssetId.HasValue).Select(entry => entry.AssetId!.Value).Distinct().ToList();
		if (ids.Count == 0)
			return;
		try
		{
			if (fetchDetails)
			{
				IReadOnlyDictionary<long, StudioPluginListing> details = await StudioPluginCatalog.GetDetailsAsync(ids, token);
				foreach (StudioPluginEntry entry in entries)
					if (entry.AssetId is long id && details.TryGetValue(id, out StudioPluginListing? listing))
						entry.Apply(listing, entry.Installed);
			}
			IReadOnlyDictionary<long, string> icons = await StudioPluginCatalog.GetIconsAsync(ids, token);
			foreach (StudioPluginEntry entry in entries)
				if (entry.AssetId is long id && icons.TryGetValue(id, out string? icon))
					entry.Icon = icon;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException or InvalidHTTPResponseException)
		{
			App.Logger.WriteException(LogIdent + "::Enrich", ex);
		}
	}

	private void ShowStatus(string? text)
	{
		BrowseStatus.Text = text ?? "";
		BrowseStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
		if (_detail != null)
			DetailStatus.Text = text ?? "";
	}

	private void View_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: StudioPluginEntry entry })
			ShowDetail(entry);
	}

	private void Primary_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: StudioPluginEntry entry })
			_ = ActAsync(entry);
	}

	private void DetailPrimary_Click(object sender, RoutedEventArgs e)
	{
		if (_detail != null)
			_ = ActAsync(_detail);
	}

	private async Task ActAsync(StudioPluginEntry entry)
	{
		if (entry.Busy || !entry.CanAct)
			return;
		if (entry.Installed is InstalledStudioPlugin installed && !entry.UpdateAvailable)
		{
			if (Frontend.ShowMessageBox(string.Format(Strings.Ui_Mods_StudioPlugins_RemoveConfirm, entry.Name), MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
				return;
			try
			{
				StudioPluginCatalog.Remove(installed);
				entry.Apply(entry.Listing, null);
				if (entry.Listing == null || !StoreMode)
					_entries.Remove(entry);
				ShowStatus(Strings.Ui_Mods_StudioPlugins_RemovedRestart);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
			{
				App.Logger.WriteException(LogIdent + "::Remove", ex);
				ShowStatus(Strings.Ui_Mods_StudioPlugins_Unavailable);
			}
			RefreshDetail(entry);
			return;
		}
		if (entry.Listing is not StudioPluginListing listing)
			return;

		entry.Busy = true;
		RefreshDetail(entry);
		try
		{
			string path = await StudioPluginCatalog.InstallAsync(listing, CancellationToken.None);
			InstalledStudioPlugin? record = StudioPluginCatalog.ListInstalled().FirstOrDefault(plugin => string.Equals(plugin.Path, path, StringComparison.OrdinalIgnoreCase));
			entry.Apply(listing, record);
			ShowStatus(Strings.Ui_Mods_StudioPlugins_InstalledRestart);
		}
		catch (StudioPluginException ex)
		{
			App.Logger.WriteLine(LogIdent, $"Installing plugin {listing.Id} failed: {ex.Message}");
			ShowStatus(ex.Failure switch
			{
				StudioPluginFailure.NotSignedIn => Strings.Ui_Mods_StudioPlugins_NeedsLogin,
				StudioPluginFailure.NotOwned => Strings.Ui_Mods_StudioPlugins_NeedsPurchase,
				StudioPluginFailure.InvalidFile => Strings.Ui_Mods_StudioPlugins_InvalidFile,
				_ => Strings.Ui_Mods_StudioPlugins_Unavailable
			});
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
		{
			App.Logger.WriteException(LogIdent + "::Install", ex);
			ShowStatus(Strings.Ui_Mods_StudioPlugins_Unavailable);
		}
		finally
		{
			entry.Busy = false;
			RefreshDetail(entry);
		}
	}

	private void Import_Click(object sender, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog dialog = new()
		{
			Title = Strings.Ui_Mods_StudioPlugins_Import,
			Filter = Strings.Ui_Mods_StudioPlugins_FileFilter,
			Multiselect = true
		};
		if (dialog.ShowDialog(Window.GetWindow(this)) != true)
			return;
		int imported = 0;
		foreach (string file in dialog.FileNames)
		{
			try
			{
				StudioPluginCatalog.Import(file);
				imported++;
			}
			catch (StudioPluginException ex)
			{
				App.Logger.WriteLine(LogIdent, "Import refused: " + ex.Message);
				ShowStatus(Strings.Ui_Mods_StudioPlugins_InvalidFile);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger.WriteException(LogIdent + "::Import", ex);
				ShowStatus(Strings.Ui_Mods_StudioPlugins_InvalidFile);
			}
		}
		if (imported == 0)
			return;
		if (StoreMode)
			SourcePicker.SelectedIndex = 1;
		else
			_ = ReloadAsync(false);
		ShowStatus(Strings.Ui_Mods_StudioPlugins_InstalledRestart);
	}

	private void OpenFolder_Click(object sender, RoutedEventArgs e)
	{
		Directory.CreateDirectory(StudioPluginCatalog.PluginsDirectory);
		PlatformShell.TryOpenFolder(StudioPluginCatalog.PluginsDirectory);
	}

	private void ShowDetail(StudioPluginEntry entry)
	{
		_detail = entry;
		DynamicRenderSystem.SetLazyImageSource(DetailIcon, string.IsNullOrEmpty(entry.Icon) ? null : entry.Icon);
		if (string.IsNullOrEmpty(entry.Icon))
			DetailIcon.Source = null;
		DetailName.Text = entry.Name;
		DetailAuthor.Text = entry.CreatorLine;
		DetailSummary.Text = entry.Summary;
		DetailDescription.Text = entry.Description.Length > 0 ? entry.Description.Trim() : entry.Summary;
		DetailCreator.Text = entry.Creator;
		DetailSourceButton.Visibility = entry.AssetId.HasValue ? Visibility.Visible : Visibility.Collapsed;
		DetailStatus.Text = "";
		RefreshDetail(entry);
		BrowseRoot.Visibility = Visibility.Collapsed;
		DetailRoot.Visibility = Visibility.Visible;
	}

	private void RefreshDetail(StudioPluginEntry entry)
	{
		if (!ReferenceEquals(_detail, entry))
			return;
		DetailPrimaryButton.Content = entry.Busy ? entry.StatusLabel : entry.PrimaryLabel;
		DetailPrimaryButton.IsEnabled = entry.CanAct;
		DetailOpenButton.Content = entry.AssetId.HasValue ? Strings.Ui_Mods_StudioPlugins_OpenStore : Strings.Ui_Mods_StudioPlugins_Folder;
		DetailPrice.Text = entry.Listing == null ? entry.Category : entry.PriceText;
		DetailRating.Text = entry.Rating;
		DetailUpdated.Text = entry.UpdatedText;
		if (entry.IsPaid && entry.Installed == null && DetailStatus.Text.Length == 0)
			DetailStatus.Text = Strings.Ui_Mods_StudioPlugins_NeedsPurchase;
	}

	private void DetailOpen_Click(object sender, RoutedEventArgs e)
	{
		if (_detail?.AssetId is long)
			OpenStore_Click(sender, e);
		else
			OpenFolder_Click(sender, e);
	}

	private void OpenStore_Click(object sender, RoutedEventArgs e)
	{
		if (_detail?.AssetId is long id)
			Utilities.OpenWebLink(StudioPluginCatalog.StorePageUrl + id);
	}

	private void Back_Click(object sender, RoutedEventArgs e)
	{
		_detail = null;
		DetailRoot.Visibility = Visibility.Collapsed;
		BrowseRoot.Visibility = Visibility.Visible;
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
}
