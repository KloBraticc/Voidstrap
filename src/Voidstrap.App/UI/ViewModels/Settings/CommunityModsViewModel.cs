using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations.CommunityMods;
using Voidstrap.UI.Elements.Settings.Pages;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings;

public class CommunityModsViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private const string LogIdent = "CommunityModsViewModel";

	private const int SearchDebounceMs = 600;

	private const int MaxEmptyPageSkips = 4;

	private const int HoldMilliseconds = 6000;

	private readonly DispatcherTimer _searchDebounce;

	private CancellationTokenSource? _loadCancellation;

	private CancellationTokenSource? _detailCancellation;

	private static CancellationTokenSource? _installCancellation;

	private bool _isDetailBusy;

	private bool _suppressRefresh;

	private string _searchText = "";

	private string _selectedSort = GameBananaCatalog.SortOptions[0];

	private string _selectedSource = GameBananaCatalog.SourceName;

	private CommunityModCategory? _selectedCategory;

	private CommunityModEntry? _selectedMod;

	private CommunityModFile? _selectedFile;

	private bool _isBusy;

	private bool _hasLoaded;

	private bool _disposed;

	private string _statusText = "";

	private int _page;

	private bool _hasMore;

	private Task<CommunityModPage>? _prefetch;

	private string _prefetchKey = "";

	private CancellationTokenSource? _prefetchCancellation;

	public CommunityModsViewModel()
	{
		_searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(SearchDebounceMs)
		};
		_searchDebounce.Tick += OnSearchDebounceTick;

		RefreshCommand = new AsyncRelayCommand(RefreshAsync);
		LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
		ClearCategoryCommand = new RelayCommand(ClearCategory);
		OpenModPageCommand = new RelayCommand(OpenModPage);
		BackCommand = new RelayCommand(CloseDetail);
		InstallCommand = new AsyncRelayCommand(InstallManagedAsync);
		InstallAssetWarpCommand = new AsyncRelayCommand(InstallAssetWarpAsync);
		InstallFleasionCommand = new AsyncRelayCommand(InstallFleasionAsync);
	}

	public ObservableCollection<CommunityModEntry> Mods { get; } = new ObservableCollection<CommunityModEntry>();

	public ObservableCollection<CommunityModCategory> Categories { get; } = new ObservableCollection<CommunityModCategory>();

	public ObservableCollection<string> DetailParagraphs { get; } = new ObservableCollection<string>();

	public ObservableCollection<CommunityModImage> DetailImages { get; } = new ObservableCollection<CommunityModImage>();

	public ObservableCollection<CommunityModFile> DetailFiles { get; } = new ObservableCollection<CommunityModFile>();

	public IReadOnlyList<string> SortOptions => IsMarketplace ? MarketplaceCatalog.SortOptions : GameBananaCatalog.SortOptions;

	public bool IsMarketplace => _selectedSource == MarketplaceCatalog.SourceName;

	public string SourceName => _selectedSource;

	public string SelectedSource
	{
		get => _selectedSource;
		set
		{
			if (string.IsNullOrEmpty(value) || _selectedSource == value)
			{
				return;
			}
			_selectedSource = value;
			_selectedCategory = null;
			_searchText = "";
			_selectedSort = SortOptions[0];
			_suppressRefresh = true;
			OnPropertyChanged(nameof(SearchText));
			OnPropertyChanged(nameof(SelectedSource));
			OnPropertyChanged(nameof(SourceName));
			OnPropertyChanged(nameof(IsMarketplace));
			OnPropertyChanged(nameof(SortOptions));
			OnPropertyChanged(nameof(SelectedSort));
			OnPropertyChanged(nameof(SelectedCategory));
			OnPropertyChanged(nameof(CategoryHeader));
			OnPropertyChanged(nameof(CategoryVisibility));
			OnPropertyChanged(nameof(SortVisibility));
			_searchDebounce.Stop();
			CloseDetail();
			_suppressRefresh = false;
			_ = RefreshAsync();
		}
	}

	public Visibility CategoryVisibility => IsMarketplace ? Visibility.Collapsed : Visibility.Visible;

	public Visibility SortVisibility => !IsMarketplace && _selectedCategory != null ? Visibility.Collapsed : Visibility.Visible;

	public ICommand RefreshCommand { get; }

	public ICommand LoadMoreCommand { get; }

	public ICommand ClearCategoryCommand { get; }

	public ICommand OpenModPageCommand { get; }

	public ICommand BackCommand { get; }

	public ICommand InstallCommand { get; }

	public ICommand InstallAssetWarpCommand { get; }

	public ICommand InstallFleasionCommand { get; }

	public string SearchText
	{
		get => _searchText;
		set
		{
			if (_searchText == value)
			{
				return;
			}
			_searchText = value ?? "";
			OnPropertyChanged(nameof(SearchText));
			_searchDebounce.Stop();
			if (!_suppressRefresh)
			{
				_searchDebounce.Start();
			}
		}
	}

	public string SelectedSort
	{
		get => _selectedSort;
		set
		{
			if (string.IsNullOrEmpty(value) || _selectedSort == value)
			{
				return;
			}
			_selectedSort = value;
			OnPropertyChanged(nameof(SelectedSort));
			if (!_suppressRefresh)
			{
				_ = RefreshAsync();
			}
		}
	}

	public CommunityModCategory? SelectedCategory
	{
		get => _selectedCategory;
		set
		{
			if (ReferenceEquals(_selectedCategory, value))
			{
				return;
			}
			_selectedCategory = value;
			OnPropertyChanged(nameof(SelectedCategory));
			OnPropertyChanged(nameof(CategoryHeader));
			OnPropertyChanged(nameof(SortVisibility));
			if (!_suppressRefresh)
			{
				CloseDetail();
				_ = RefreshAsync();
			}
		}
	}

	public CommunityModEntry? SelectedMod
	{
		get => _selectedMod;
		private set
		{
			_selectedMod = value;
			OnPropertyChanged(nameof(SelectedMod));
			OnPropertyChanged(nameof(DetailVisibility));
			OnPropertyChanged(nameof(BrowseVisibility));
			OnPropertyChanged(nameof(GalleryVisibility));
			OnPropertyChanged(nameof(GameBananaDetailVisibility));
			OnPropertyChanged(nameof(CreatorVisibility));
			OnPropertyChanged(nameof(ManagedInstallVisibility));
			OnPropertyChanged(nameof(ReplacementInstallVisibility));
			OnPropertyChanged(nameof(FleasionInstallVisibility));
		}
	}

	public CommunityModFile? SelectedFile
	{
		get => _selectedFile;
		set
		{
			if (ReferenceEquals(_selectedFile, value))
			{
				return;
			}
			_selectedFile = value;
			OnPropertyChanged(nameof(SelectedFile));
			OnPropertyChanged(nameof(CanInstall));
			OnPropertyChanged(nameof(ManagedInstallVisibility));
			OnPropertyChanged(nameof(ReplacementInstallVisibility));
			OnPropertyChanged(nameof(FleasionInstallVisibility));
			OnPropertyChanged(nameof(InstallTargetHint));
		}
	}

	public bool IsBusy
	{
		get => _isBusy;
		private set
		{
			if (_isBusy == value)
			{
				return;
			}
			_isBusy = value;
			OnPropertyChanged(nameof(IsBusy));
			OnPropertyChanged(nameof(BusyVisibility));
			OnPropertyChanged(nameof(EmptyVisibility));
		}
	}

	public bool IsDetailBusy
	{
		get => _isDetailBusy;
		private set
		{
			if (_isDetailBusy == value)
			{
				return;
			}
			_isDetailBusy = value;
			OnPropertyChanged(nameof(IsDetailBusy));
			OnPropertyChanged(nameof(BusyVisibility));
		}
	}

	public bool IsInstalling => Volatile.Read(ref _installCancellation) != null;

	public string StatusText
	{
		get => _statusText;
		private set
		{
			_statusText = value;
			OnPropertyChanged(nameof(StatusText));
		}
	}

	public string CategoryHeader => _selectedCategory == null ? "All categories" : _selectedCategory.Name;

	public bool CanInstall => !IsInstalling && SelectedFile != null;

	public bool HasMore => _hasMore;

	public Visibility BusyVisibility => IsBusy || IsDetailBusy ? Visibility.Visible : Visibility.Collapsed;

	public Visibility EmptyVisibility => !IsBusy && Mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

	public Visibility DetailVisibility => SelectedMod == null ? Visibility.Collapsed : Visibility.Visible;

	public Visibility BrowseVisibility => SelectedMod == null ? Visibility.Visible : Visibility.Collapsed;

	public Visibility GalleryVisibility => DetailImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

	public Visibility CreatorVisibility => string.IsNullOrWhiteSpace(SelectedMod?.Submitter?.Name) ? Visibility.Collapsed : Visibility.Visible;

	public Visibility GameBananaDetailVisibility => SelectedMod?.SourceName == GameBananaCatalog.SourceName ? Visibility.Visible : Visibility.Collapsed;

	public static bool IsReplacementConfig(CommunityModFile? file)
	{
		return file != null && string.Equals(Path.GetExtension(file.FileName), ".json", StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsReplacementPackage(CommunityModEntry? entry, CommunityModFile? file)
	{
		if (file == null)
		{
			return false;
		}
		if (IsReplacementConfig(file))
		{
			return true;
		}
		if (entry == null)
		{
			return false;
		}
		string baseName = Path.GetFileNameWithoutExtension(file.FileName);
		if (baseName.Length == 0)
		{
			return false;
		}
		return entry.Files.Any(candidate => IsReplacementConfig(candidate)
			&& string.Equals(Path.GetFileNameWithoutExtension(candidate.FileName), baseName, StringComparison.OrdinalIgnoreCase));
	}

	public static (bool MyMods, bool Replacement) InstallButtonsFor(CommunityModEntry? mod, CommunityModFile? file)
	{
		if (mod == null || file == null)
		{
			return (false, false);
		}
		if (IsReplacementPackage(mod, file))
		{
			return (false, true);
		}
		if (!file.ListingKnown)
		{
			return (true, mod.RequiresFleasion);
		}
		bool myMods = file.ListingHasRobloxContent || file.ListingHasCacheEntries;
		bool replacement = file.ListingHasConfig || (mod.RequiresFleasion && file.ListingHasAssets && !myMods);
		return (myMods || !replacement, replacement);
	}

	public Visibility ManagedInstallVisibility => InstallButtonsFor(SelectedMod, SelectedFile).MyMods
		? Visibility.Visible
		: Visibility.Collapsed;

	public Visibility ReplacementInstallVisibility => InstallButtonsFor(SelectedMod, SelectedFile).Replacement
		? Visibility.Visible
		: Visibility.Collapsed;

	public Visibility FleasionInstallVisibility => Voidstrap.Utility.Platform.IsLinux
		? Visibility.Collapsed
		: ReplacementInstallVisibility;

	public string InstallTargetHint => IsReplacementPackage(SelectedMod, SelectedFile)
		? Voidstrap.Utility.Platform.IsLinux
			? "Replacement config for Voidstrap AssetWarp"
			: "Replacement config for Voidstrap AssetWarp and Fleasion"
		: InstallButtonsFor(SelectedMod, SelectedFile) is (false, true)
			? Voidstrap.Utility.Platform.IsLinux
				? "This file is a replacement config, it applies through Voidstrap AssetWarp"
				: "This file is for replacement configs, pick Voidstrap or Fleasion"
			: "Verified before install, the target is detected from the package";

	public async Task InitializeAsync()
	{
		if (_disposed)
		{
			return;
		}
		InstallingChanged -= OnInstallingChanged;
		InstallingChanged += OnInstallingChanged;
		InstallCompleted -= OnInstallCompleted;
		InstallCompleted += OnInstallCompleted;
		RaiseInstallingChanged();
		if (Categories.Count == 0)
		{
			await LoadCategoriesAsync().ConfigureAwait(true);
			OnPropertyChanged(nameof(CategoryVisibility));
		}
		if (_hasLoaded && Mods.Count > 0)
		{
			return;
		}
		_hasLoaded = true;
		await RefreshAsync().ConfigureAwait(true);
	}

	private async Task LoadCategoriesAsync()
	{
		try
		{
			IReadOnlyList<CommunityModCategory> categories = await GameBananaCatalog.GetCategoriesAsync(CancellationToken.None).ConfigureAwait(true);
			if (_disposed)
			{
				return;
			}
			Categories.Clear();
			foreach (CommunityModCategory category in categories)
			{
				Categories.Add(category);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The categories could not be loaded: " + ex.Message);
		}
	}

	private async Task RefreshAsync()
	{
		_page = 0;
		_hasMore = false;
		DropPrefetch();
		Mods.Clear();
		OnPropertyChanged(nameof(HasMore));
		await LoadMoreAsync().ConfigureAwait(true);
	}

	private async Task LoadMoreAsync()
	{
		if (_disposed)
		{
			return;
		}
		CancellationTokenSource cancellation = new CancellationTokenSource();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _loadCancellation, cancellation);
		Cancel(previous);

		bool current = true;
		IsBusy = true;
		StatusText = "Loading mods from " + SourceName;
		try
		{
			int next = _page;
			int added = 0;
			for (int attempt = 0; attempt < MaxEmptyPageSkips && added == 0; attempt++)
			{
				next++;
				CommunityModPage page = IsMarketplace
					? await MarketplaceCatalog.BrowseAsync(_selectedSort, _searchText, cancellation.Token).ConfigureAwait(true)
					: await TakePrefetchAsync(next, cancellation.Token).ConfigureAwait(true)
						?? await GameBananaCatalog.BrowseAsync(next, _selectedSort, _selectedCategory, _searchText, cancellation.Token).ConfigureAwait(true);
				current = !cancellation.Token.IsCancellationRequested && ReferenceEquals(Volatile.Read(ref _loadCancellation), cancellation);
				if (!current)
				{
					return;
				}
				_page = next;
				_hasMore = page.HasMore;
				HashSet<long> known = new HashSet<long>(Mods.Select(mod => mod.Id));
				foreach (CommunityModEntry entry in page.Entries)
				{
					if (known.Add(entry.Id))
					{
						Mods.Add(entry);
						added++;
					}
				}
				if (!_hasMore)
				{
					break;
				}
			}
			string hint = !IsMarketplace && _searchText.Trim().Length == 1
				? "   Type at least " + GameBananaCatalog.MinSearchLength + " characters to search."
				: "";
			StatusText = (Mods.Count == 0 ? "No Roblox mods matched." : Mods.Count + " Roblox mods shown") + hint;
		}
		catch (OperationCanceledException)
		{
			current = false;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The catalog could not be loaded: " + ex.Message);
			current = ReferenceEquals(Volatile.Read(ref _loadCancellation), cancellation);
			if (current)
			{
				StatusText = "The catalog could not be reached. Check your connection and try again.";
			}
		}
		finally
		{
			if (ReferenceEquals(Interlocked.CompareExchange(ref _loadCancellation, null, cancellation), cancellation))
			{
				IsBusy = false;
			}
			if (current)
			{
				OnPropertyChanged(nameof(HasMore));
				OnPropertyChanged(nameof(EmptyVisibility));
				StartPrefetch();
			}
			cancellation.Dispose();
		}
	}

	private string BrowseKey(int page)
	{
		return _selectedSource + "|" + _selectedSort + "|" + (_selectedCategory?.Id ?? 0) + "|" + _searchText.Trim() + "|" + page;
	}

	private void StartPrefetch()
	{
		if (_disposed || IsMarketplace || !_hasMore)
		{
			return;
		}
		string key = BrowseKey(_page + 1);
		if (_prefetch != null && _prefetchKey == key)
		{
			return;
		}
		DropPrefetch();
		CancellationTokenSource cancellation = new CancellationTokenSource();
		Task<CommunityModPage> prefetch = GameBananaCatalog.BrowseAsync(_page + 1, _selectedSort, _selectedCategory, _searchText, cancellation.Token);
		_prefetchCancellation = cancellation;
		_prefetchKey = key;
		_prefetch = prefetch;
		_ = ObservePrefetchAsync(prefetch);
	}

	private static async Task ObservePrefetchAsync(Task<CommunityModPage> prefetch)
	{
		try
		{
			await prefetch.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The next page could not be prepared: " + ex.Message);
		}
	}

	private async Task<CommunityModPage?> TakePrefetchAsync(int page, CancellationToken token)
	{
		Task<CommunityModPage>? prefetch = _prefetch;
		bool matches = prefetch != null && _prefetchKey == BrowseKey(page);
		CancellationTokenSource? owner = _prefetchCancellation;
		_prefetch = null;
		_prefetchKey = "";
		_prefetchCancellation = null;
		if (!matches || prefetch == null || owner == null)
		{
			Cancel(owner);
			owner?.Dispose();
			return null;
		}
		try
		{
			using CancellationTokenRegistration link = token.Register(CancelPrefetchOwner, owner);
			return await prefetch.WaitAsync(token).ConfigureAwait(true);
		}
		catch (OperationCanceledException) when (!token.IsCancellationRequested)
		{
			return null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return null;
		}
		finally
		{
			owner.Dispose();
		}
	}

	private static void CancelPrefetchOwner(object? state)
	{
		Cancel(state as CancellationTokenSource);
	}

	private void DropPrefetch()
	{
		CancellationTokenSource? owner = _prefetchCancellation;
		_prefetch = null;
		_prefetchKey = "";
		_prefetchCancellation = null;
		Cancel(owner);
		owner?.Dispose();
	}

	public async Task OpenPackAsync(ModPackInfo pack)
	{
		string source = string.Equals(pack.Source, MarketplaceCatalog.SourceName, StringComparison.OrdinalIgnoreCase)
			? MarketplaceCatalog.SourceName
			: GameBananaCatalog.SourceName;
		if (!string.Equals(_selectedSource, source, StringComparison.Ordinal))
		{
			SelectedSource = source;
		}
		CommunityModEntry? entry = IsMarketplace
			? await MarketplaceCatalog.FindAsync(pack.Slug, CancellationToken.None).ConfigureAwait(true)
			: new CommunityModEntry
			{
				Id = pack.Id,
				Name = pack.Name,
				Author = pack.Author,
				IconUrl = pack.IconUrl,
				ProfileUrl = pack.ProfileUrl,
				SourceName = GameBananaCatalog.SourceName
			};
		if (entry == null)
		{
			OpenExternal(pack.ProfileUrl);
			return;
		}
		await OpenDetailAsync(entry).ConfigureAwait(true);
	}

	public async Task OpenDetailAsync(CommunityModEntry entry)
	{
		if (entry == null || _disposed || IsDetailBusy)
		{
			return;
		}
		CancellationTokenSource cancellation = new CancellationTokenSource();
		Cancel(Interlocked.Exchange(ref _detailCancellation, cancellation));
		IsDetailBusy = true;
		try
		{
			CommunityModEntry? detail = IsMarketplace
				? await MarketplaceCatalog.GetDetailAsync(entry, cancellation.Token).ConfigureAwait(true)
				: await GameBananaCatalog.GetDetailAsync(entry, cancellation.Token).ConfigureAwait(true);
			if (cancellation.Token.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref _detailCancellation), cancellation))
			{
				return;
			}
			if (detail == null)
			{
				Mods.Remove(entry);
				StatusText = "That mod was hidden because it did not pass the safety checks.";
				OnPropertyChanged(nameof(EmptyVisibility));
				return;
			}
			DetailParagraphs.Clear();
			IEnumerable<string> lines = detail.DetailLines.Count > 0
				? detail.DetailLines
				: GameBananaCatalog.ToParagraphs(detail.DescriptionHtml);
			foreach (string paragraph in lines)
			{
				DetailParagraphs.Add(paragraph);
			}
			DetailImages.Clear();
			foreach (CommunityModImage image in detail.Images)
			{
				DetailImages.Add(image);
			}
			DetailFiles.Clear();
			foreach (CommunityModFile file in detail.Files)
			{
				if (CommunityModGuard.IsInstallableFile(file))
				{
					DetailFiles.Add(file);
				}
			}
			SelectedFile = DetailFiles.Count > 0 ? DetailFiles[0] : null;
			SelectedMod = detail;
			OnPropertyChanged(nameof(GalleryVisibility));
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The mod page could not be opened: " + ex.Message);
			StatusText = "That mod could not be opened. Check your connection and try again.";
		}
		finally
		{
			if (ReferenceEquals(Interlocked.CompareExchange(ref _detailCancellation, null, cancellation), cancellation))
			{
				IsDetailBusy = false;
			}
			cancellation.Dispose();
		}
	}

	private Task InstallManagedAsync()
	{
		return InstallAsync(CommunityModInstallTarget.ManagedMods);
	}

	private Task InstallAssetWarpAsync()
	{
		return InstallAsync(CommunityModInstallTarget.AssetWarp);
	}

	private Task InstallFleasionAsync()
	{
		return InstallAsync(Voidstrap.Utility.Platform.IsLinux
			? CommunityModInstallTarget.AssetWarp
			: CommunityModInstallTarget.Fleasion);
	}

	private Task InstallAsync(CommunityModInstallTarget target)
	{
		CommunityModEntry? entry = SelectedMod;
		CommunityModFile? file = SelectedFile;
		if (entry == null || file == null || IsInstalling)
		{
			return Task.CompletedTask;
		}
		CommunityModVerdict verdict = CommunityModGuard.InspectFile(file);
		if (!verdict.Allowed)
		{
			Report("Blocked: " + verdict.Reason, 1.0, HoldMilliseconds);
			return Task.CompletedTask;
		}
		CancellationTokenSource cancellation = new CancellationTokenSource();
		if (Interlocked.CompareExchange(ref _installCancellation, cancellation, null) != null)
		{
			cancellation.Dispose();
			return Task.CompletedTask;
		}
		ExtensionViewModel.RegisterCancelAction(CancelActiveInstall);
		RaiseInstallingChanged();
		_ = RunInstallAsync(entry, file, target, cancellation);
		return Task.CompletedTask;
	}

	private static async Task RunInstallAsync(CommunityModEntry entry, CommunityModFile file, CommunityModInstallTarget target, CancellationTokenSource cancellation)
	{
		Progress<string> progress = new Progress<string>(message => Report(message, -1.0, 0));
		try
		{
			CancellationToken token = cancellation.Token;
			string result = await Task.Run(() => MarketplaceCatalog.IsMarketplaceUrl(file.DownloadUrl)
				? MarketplaceCatalog.InstallAsync(entry, file, progress, token)
				: GameBananaCatalog.InstallAsync(entry, file, progress, token, target), token).ConfigureAwait(true);
			string message;
			if (result.StartsWith(FleasionModInstaller.AssetWarpResultPrefix, StringComparison.Ordinal))
			{
				message = FastFlagsViewModel.TryEnableAssetWarp()
					? entry.Name + " was installed for Voidstrap AssetWarp and is ready for the next Roblox launch."
					: entry.Name + " was installed for Voidstrap AssetWarp. Turn on AssetWarp before launching Roblox.";
			}
			else if (result.StartsWith(FleasionModInstaller.ResultPrefix, StringComparison.Ordinal))
			{
				message = FleasionModInstaller.IsInstalled
					? entry.Name + " was installed and enabled in Fleasion. Restart Fleasion if it is already open."
					: entry.Name + " was installed and enabled in Fleasion. Turn on Fleasion in Extensions before launching Roblox.";
			}
			else
			{
				message = entry.Name + " was verified, placed into the Roblox layout and enabled in My Mods. Relaunch Roblox to see it.";
				if (Voidstrap.Integrations.AssetProxy.AssetWarpAutoEnable.ModsNeedAssetWarp() && !Voidstrap.Integrations.AssetProxy.AssetWarpAutoEnable.EnsureEnabled(allowPrompt: true, userAction: true))
				{
					message = entry.Name + " was installed, but part of it only works through AssetWarp. Turn on AssetWarp before launching Roblox.";
				}
			}
			Report(message, 1.0, HoldMilliseconds);
			InstallCompleted?.Invoke(null, EventArgs.Empty);
		}
		catch (OperationCanceledException)
		{
			Report("The install was cancelled.", 1.0, HoldMilliseconds);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "Install failed for " + entry.Id + ": " + ex.Message);
			Report(ex is System.Net.Http.HttpRequestException or TimeoutException or IOException
				? "The download failed. Check your connection and try again."
				: "Blocked: " + ex.Message, 1.0, HoldMilliseconds);
		}
		finally
		{
			if (ReferenceEquals(Volatile.Read(ref _installCancellation), cancellation))
			{
				Interlocked.Exchange(ref _installCancellation, null);
			}
			ExtensionViewModel.UnregisterCancelAction(CancelActiveInstall);
			cancellation.Dispose();
			InstallingChanged?.Invoke(null, EventArgs.Empty);
		}
	}

	public static void CancelActiveInstall()
	{
		Cancel(Volatile.Read(ref _installCancellation));
	}

	private static void Report(string message, double fraction, int holdMilliseconds)
	{
		ExtensionViewModel.ReportProgress(message, fraction, true);
		if (holdMilliseconds <= 0)
		{
			return;
		}
		_ = HideAfterAsync(holdMilliseconds);
	}

	private static async Task HideAfterAsync(int holdMilliseconds)
	{
		await Task.Delay(holdMilliseconds).ConfigureAwait(true);
		if (Volatile.Read(ref _installCancellation) == null)
		{
			ExtensionViewModel.ReportProgress("", -1.0, false);
		}
	}

	private void RaiseInstallingChanged()
	{
		OnPropertyChanged(nameof(IsInstalling));
		OnPropertyChanged(nameof(CanInstall));
	}

	private void OnInstallingChanged(object? sender, EventArgs e)
	{
		if (_disposed)
		{
			return;
		}
		RaiseInstallingChanged();
	}

	private void OnInstallCompleted(object? sender, EventArgs e)
	{
		if (_disposed)
		{
			return;
		}
		ManagedModsChanged?.Invoke(this, EventArgs.Empty);
	}

	public static event EventHandler? InstallingChanged;

	public static event EventHandler? InstallCompleted;

	public event EventHandler? ManagedModsChanged;

	private void CloseDetail()
	{
		Cancel(Interlocked.Exchange(ref _detailCancellation, null));
		IsDetailBusy = false;
		SelectedMod = null;
		SelectedFile = null;
		DetailFiles.Clear();
		DetailImages.Clear();
		DetailParagraphs.Clear();
	}

	private void ClearCategory()
	{
		SelectedCategory = null;
	}

	public static void OpenEntryPage(CommunityModEntry entry)
	{
		OpenExternal(entry.ProfileUrl);
	}

	private void OpenModPage()
	{
		OpenExternal(SelectedMod?.ProfileUrl);
	}

	private static void OpenExternal(string? url)
	{
		if (!CommunityModGuard.IsTrustedUrl(url))
		{
			return;
		}
		try
		{
			using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = url,
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The link could not be opened: " + ex.Message);
		}
	}

	private void OnSearchDebounceTick(object? sender, EventArgs e)
	{
		_searchDebounce.Stop();
		if (_disposed)
		{
			return;
		}
		_ = RefreshAsync();
	}

	private static void Cancel(CancellationTokenSource? source)
	{
		if (source == null)
		{
			return;
		}
		try
		{
			source.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	public void CancelTransientOperations()
	{
		InstallingChanged -= OnInstallingChanged;
		InstallCompleted -= OnInstallCompleted;
		_searchDebounce.Stop();
		CancellationTokenSource? interruptedLoad = Interlocked.Exchange(ref _loadCancellation, null);
		if (interruptedLoad != null)
		{
			_hasLoaded = false;
			StatusText = string.Empty;
		}
		Cancel(interruptedLoad);
		Cancel(Interlocked.Exchange(ref _detailCancellation, null));
		DropPrefetch();
		IsBusy = false;
		IsDetailBusy = false;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_searchDebounce.Stop();
		_searchDebounce.Tick -= OnSearchDebounceTick;
		InstallingChanged -= OnInstallingChanged;
		InstallCompleted -= OnInstallCompleted;
		Cancel(Interlocked.Exchange(ref _loadCancellation, null));
		Cancel(Interlocked.Exchange(ref _detailCancellation, null));
		DropPrefetch();
		GC.SuppressFinalize(this);
	}
}
