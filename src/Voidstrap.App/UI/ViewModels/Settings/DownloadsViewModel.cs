using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Voidstrap.AppData;
using Voidstrap.Enums;
using Voidstrap.RobloxInterfaces;
using Voidstrap.UI;
using Voidstrap.Utility;
using CoreBootstrapper = Voidstrap.Bootstrapper;

namespace Voidstrap.UI.ViewModels.Settings
{
    public class DownloadsViewModel : NotifyPropertyChangedViewModel
    {

        public class DownloadItem : NotifyPropertyChangedViewModel
        {
            private readonly DownloadsViewModel _parent;
            private readonly IAppData _appData;
            private readonly string _binaryType;
            private readonly LaunchMode _launchMode;
            private readonly string _processName;

            private string _latestVersion = "";
            private CoreBootstrapper _activeBootstrapper;

            public string Title { get; }
            public string Subtitle { get; }
            public ImageSource IconImage { get; }
            public bool ShowFleasionAddon { get; }
            public bool HasAddons => ShowFleasionAddon;
            public bool ShowFleasion => ShowFleasionAddon;
            public bool ShowCommunityContent => false;

            public bool IsInstalled { get; private set; }
            public bool IsBusy { get; private set; }
            public bool UpdateAvailable { get; private set; }
            public string StatusText { get; private set; } = "";
            public string VersionText { get; private set; } = "";
            public string SizeText { get; private set; } = "";
            public string LocationText { get; private set; } = "";
            public string PrimaryButtonText { get; private set; } = "Install";
            public bool CanUninstall => IsInstalled && !IsBusy;
            public bool CanPrimary => !IsBusy;

            public double Progress { get; private set; }
            public string ProgressDetail { get; private set; } = "";

            public ICommand InstallCommand { get; }
            public ICommand UninstallCommand { get; }
            public ICommand OpenFolderCommand { get; }
            public ICommand CancelCommand { get; }
            public ICommand ChangeLocationCommand { get; }

            internal DownloadItem(DownloadsViewModel parent, IAppData appData, string title, string subtitle, string iconSource, string binaryType, LaunchMode launchMode, string processName, bool showFleasionAddon)
            {
                _parent = parent;
                _appData = appData;
                Title = title;
                Subtitle = subtitle;
                IconImage = LoadIcon(iconSource);
                ShowFleasionAddon = showFleasionAddon;
                _binaryType = binaryType;
                _launchMode = launchMode;
                _processName = processName;
                InstallCommand = new AsyncRelayCommand(InstallOrUpdateAsync);
                UninstallCommand = new AsyncRelayCommand(UninstallAsync);
                OpenFolderCommand = new RelayCommand(OpenFolder);
                CancelCommand = new RelayCommand(Cancel);
                ChangeLocationCommand = new AsyncRelayCommand(ChangeLocationAsync);
                Refresh();
            }

            public void Refresh()
            {
                bool exeExists = !string.IsNullOrEmpty(_appData.State.VersionGuid) && File.Exists(_appData.ExecutablePath);
                if (!exeExists)
                {
                    string detectedGuid = ScanForExistingInstall();
                    if (!string.IsNullOrEmpty(detectedGuid))
                    {
                        try
                        {
                            _appData.State.VersionGuid = detectedGuid;
                            App.State.Save();
                            App.Logger?.WriteLine("DownloadsViewModel::Detect", $"Adopted an existing {Title} install ({detectedGuid})");
                        }
                        catch
                        {
                        }
                        exeExists = File.Exists(_appData.ExecutablePath);
                    }
                }
                IsInstalled = exeExists;

                if (exeExists)
                {
                    VersionText = _appData.State.VersionGuid;
                    LocationText = _appData.Directory;
                    string sizeDir = _appData.Directory;
                    _ = Task.Run(delegate
                    {
                        string size = ComputeSize(sizeDir);
                        Application.Current?.Dispatcher.BeginInvoke((Action)delegate
                        {
                            SizeText = size;
                            OnPropertyChanged(nameof(SizeText));
                        });
                    });
                }
                else
                {
                    VersionText = "Not installed yet";
                    LocationText = _appData.VersionsRoot;
                    SizeText = "";
                }

                if (!IsBusy)
                {
                    if (!exeExists)
                    {
                        StatusText = "Not installed";
                        PrimaryButtonText = "Install";
                    }
                    else if (UpdateAvailable)
                    {
                        StatusText = "Update available";
                        PrimaryButtonText = "Update";
                    }
                    else
                    {
                        StatusText = "Up to date";
                        PrimaryButtonText = "Reinstall";
                    }
                }

                RaiseAll();
            }

            private void RaiseAll()
            {
                OnPropertyChanged(nameof(IsInstalled));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(UpdateAvailable));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(VersionText));
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(LocationText));
                OnPropertyChanged(nameof(PrimaryButtonText));
                OnPropertyChanged(nameof(CanUninstall));
                OnPropertyChanged(nameof(CanPrimary));
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(ProgressDetail));
            }

            public async Task CheckUpdateAsync()
            {
                if (!IsInstalled)
                    return;
                try
                {
                    string latest = await FetchLatestVersionAsync(_binaryType).ConfigureAwait(true);
                    if (!string.IsNullOrEmpty(latest))
                    {
                        _latestVersion = latest;
                        UpdateAvailable = !string.Equals(latest, _appData.State.VersionGuid, StringComparison.OrdinalIgnoreCase);
                        Refresh();
                    }
                }
                catch
                {
                }
            }

            private async Task InstallOrUpdateAsync()
            {
                if (IsBusy)
                    return;
                if (IsProcessRunning(_processName))
                {
                    Frontend.ShowMessageBox($"Close {Title} before installing or updating it.", MessageBoxImage.Warning);
                    return;
                }
                if (!_parent.TryBeginOperation())
                {
                    Frontend.ShowMessageBox("Wait for the current operation to finish.", MessageBoxImage.Warning);
                    return;
                }

                IsBusy = true;
                Progress = 0;
                ProgressDetail = "Preparing...";
                StatusText = "Preparing...";
                _parent.BeginActiveDownload(this, _binaryType);
                RaiseAll();

                CoreBootstrapper.DownloadProgressChanged += OnProgress;
                bool ok = false;
                try
                {
                    var bootstrapper = new CoreBootstrapper(_launchMode) { InstallOnly = true };
                    _activeBootstrapper = bootstrapper;
                    await Task.Run(() => bootstrapper.Run()).ConfigureAwait(true);
                    ok = true;
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteLine("DownloadsViewModel::Install", $"{Title} install failed: {ex.Message}");
                    Frontend.ShowMessageBox($"{Title} could not be installed: {ex.Message}", MessageBoxImage.Error);
                }
                finally
                {
                    CoreBootstrapper.DownloadProgressChanged -= OnProgress;
                    _activeBootstrapper = null;
                    IsBusy = false;
                    UpdateAvailable = false;
                    Progress = 0;
                    ProgressDetail = "";
                    _parent.EndActiveDownload();
                    _parent.EndOperation();
                    Refresh();
                    if (ok)
                        await CheckUpdateAsync().ConfigureAwait(true);
                }
            }

            private void OnProgress(CoreBootstrapper.DownloadProgressInfo info)
            {
                if (info == null || !string.Equals(info.BinaryType, _binaryType, StringComparison.OrdinalIgnoreCase))
                    return;
                Application.Current?.Dispatcher.BeginInvoke((Action)delegate
                {
                    Progress = info.Percent;
                    ProgressDetail = $"{FormatBytes(info.BytesDone)} of {FormatBytes(info.TotalBytes)} · {FormatSpeed(info.SpeedBytesPerSec)} · {info.PackagesDone}/{info.TotalPackages} packages";
                    StatusText = $"Downloading {info.Percent:0}%";
                    OnPropertyChanged(nameof(Progress));
                    OnPropertyChanged(nameof(ProgressDetail));
                    OnPropertyChanged(nameof(StatusText));
                    _parent.PushSpeedSample(info.SpeedBytesPerSec, info.TotalBytes, info.BytesDone);
                });
            }

            private void Cancel()
            {
                try
                {
                    _activeBootstrapper?.Cancel();
                }
                catch
                {
                }
            }

            private async Task UninstallAsync()
            {
                if (!IsInstalled || IsBusy)
                    return;
                if (IsProcessRunning(_processName))
                {
                    Frontend.ShowMessageBox($"Close {Title} before removing it.", MessageBoxImage.Warning);
                    return;
                }
                if (!_parent.TryBeginOperation())
                {
                    Frontend.ShowMessageBox("Wait for the current operation to finish.", MessageBoxImage.Warning);
                    return;
                }
                if (Frontend.ShowMessageBox($"Remove {Title}? This deletes {SizeText} of installed files. You can reinstall it any time.", MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    _parent.EndOperation();
                    return;
                }
                IsBusy = true;
                StatusText = "Removing";
                RaiseAll();
                try
                {
                    string dir = _appData.Directory;
                    if (Directory.Exists(dir))
                        await Task.Run(() => Directory.Delete(dir, true)).ConfigureAwait(true);
                    _appData.State.VersionGuid = string.Empty;
                    _appData.State.PackageHashes?.Clear();
                    _appData.State.Size = 0;
                    App.State.Save();
                    UpdateAvailable = false;
                    Refresh();
                }
                catch (Exception ex)
                {
                    Frontend.ShowMessageBox($"Could not fully remove {Title}: {ex.Message}", MessageBoxImage.Error);
                }
                finally
                {
                    IsBusy = false;
                    _parent.EndOperation();
                    Refresh();
                }
            }

            private void OpenFolder()
            {
                try
                {
                    string dir = IsInstalled ? _appData.Directory : _appData.VersionsRoot;
                    if (!Directory.Exists(dir))
                        dir = _appData.VersionsRoot;
                    Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Frontend.ShowMessageBox($"Could not open the folder: {ex.Message}", MessageBoxImage.Error);
                }
            }

            private async Task ChangeLocationAsync()
            {
                if (IsBusy)
                    return;
                if (IsProcessRunning(_processName))
                {
                    Frontend.ShowMessageBox($"Close {Title} before changing its install location.", MessageBoxImage.Warning);
                    return;
                }
                if (!_parent.TryBeginOperation())
                {
                    Frontend.ShowMessageBox("Wait for the current operation to finish before changing the install location.", MessageBoxImage.Warning);
                    return;
                }
                var dialog = new OpenFolderDialog
                {
                    Title = $"Choose where to install {Title}",
                    Multiselect = false
                };
                if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
                {
                    _parent.EndOperation();
                    return;
                }

                string source = _appData.VersionsRoot;
                string folderName = _binaryType == "WindowsPlayer" ? "RobloxPlayer" : "RobloxStudio";
                string target = Path.Combine(dialog.FolderName, folderName);

                if (string.Equals(target.TrimEnd('\\'), source.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    Frontend.ShowMessageBox($"{Title} is already installed at that location.", MessageBoxImage.Asterisk);
                    _parent.EndOperation();
                    return;
                }
                if (Frontend.ShowMessageBox($"{Title} will be installed at:\n{target}\n\nOnly {Title} moves, Voidstrap and the other Roblox app stay where they are. Continue?", MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    _parent.EndOperation();
                    return;
                }

                IsBusy = true;
                StatusText = "Moving";
                RaiseAll();
                try
                {
                    string exeName = _appData.ExecutableName;
                    await Task.Run(() => MoveBinaryInstalls(source, target, exeName)).ConfigureAwait(true);
                    if (_binaryType == "WindowsPlayer")
                        App.Settings.Prop.PlayerInstallLocation = target;
                    else
                        App.Settings.Prop.StudioInstallLocation = target;
                    App.Settings.Save();
                    _parent.RefreshAll();
                    Frontend.ShowMessageBox($"{Title} install location changed to:\n{target}", MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteLine("DownloadsViewModel::ChangeLocation", $"{Title} move failed: {ex.Message}");
                    Frontend.ShowMessageBox($"Could not move {Title}: {ex.Message}", MessageBoxImage.Error);
                    _parent.RefreshAll();
                }
                finally
                {
                    IsBusy = false;
                    _parent.EndOperation();
                    Refresh();
                }
            }

            private static void MoveBinaryInstalls(string sourceRoot, string targetRoot, string exeName)
            {
                Directory.CreateDirectory(targetRoot);
                if (!Directory.Exists(sourceRoot))
                    return;
                foreach (var dir in Directory.GetDirectories(sourceRoot, "version-*"))
                {
                    if (!File.Exists(Path.Combine(dir, exeName)))
                        continue;
                    MoveDirectory(dir, Path.Combine(targetRoot, Path.GetFileName(dir)));
                }
            }

            private string ScanForExistingInstall()
            {
                try
                {
                    string root = _appData.VersionsRoot;
                    if (!Directory.Exists(root))
                        return null;
                    foreach (var dir in Directory.GetDirectories(root, "version-*"))
                    {
                        if (File.Exists(Path.Combine(dir, _appData.ExecutableName)))
                            return Path.GetFileName(dir);
                    }
                }
                catch
                {
                }
                return null;
            }

            private static bool IsProcessRunning(string name)
            {
                Process[] processes = Array.Empty<Process>();
                try
                {
                    processes = Process.GetProcessesByName(name);
                    return processes.Length > 0;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    foreach (Process process in processes)
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }

            private static ImageSource LoadIcon(string uri)
            {
                try
                {
                    return Voidstrap.Utility.AppImage.LoadSync(uri);
                }
                catch
                {
                    return null;
                }
            }

            private static string ComputeSize(string dir)
            {
                try
                {
                    if (!Directory.Exists(dir))
                        return "";
                    long total = 0;
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { total += new FileInfo(file).Length; }
                        catch { }
                    }
                    return FormatBytes(total);
                }
                catch { return ""; }
            }
        }

        public class ClientItem : NotifyPropertyChangedViewModel
        {
            private readonly DownloadsViewModel _parent;
            private CancellationTokenSource _cts;

            public string Code { get; }
            public string Title { get; private set; }
            public string Description { get; private set; } = "";
            public bool IsInstalled { get; private set; }
            public bool IsBusy { get; private set; }
            public bool HasStudio { get; private set; }
            public string StatusText { get; private set; } = "";
            public string SizeText { get; private set; } = "";
            public double Progress { get; private set; }
            private ImageSource _iconImage;
            public ImageSource IconImage
            {
                get => _iconImage;
                private set
                {
                    if (!ReferenceEquals(_iconImage, value))
                    {
                        _iconImage = value;
                        OnPropertyChanged(nameof(IconImage));
                    }
                }
            }
            public string Subtitle => Description;
            public string ProgressDetail => StatusText;
            public bool UpdateAvailable { get; private set; }
            public string VersionText { get; private set; } = "";
            public string LocationText { get; private set; } = "";
            public string PrimaryButtonText => UpdateAvailable ? "Update" : (IsInstalled ? "Reinstall" : "Install");
            public bool CanInstall => !IsBusy;
            public bool CanPrimary => !IsBusy;
            public bool CanUninstall => IsInstalled && !IsBusy;

            public ICommand InstallCommand { get; }
            public ICommand UninstallCommand { get; }
            public ICommand CancelCommand { get; }
            public ICommand OpenFolderCommand { get; }
            public ICommand ChangeLocationCommand { get; }
            public ICommand SelectCommand { get; }

            internal ClientItem(DownloadsViewModel parent, ClassicCatalogEntry entry)
            {
                _parent = parent;
                Code = entry.Code;
                Title = entry.Name;
                Description = entry.Description;
                HasStudio = entry.HasStudio;
                InstallCommand = new AsyncRelayCommand(InstallOrReinstallAsync);
                UninstallCommand = new AsyncRelayCommand(UninstallAsync);
                CancelCommand = new RelayCommand(Cancel);
                OpenFolderCommand = new RelayCommand(OpenFolder);
                ChangeLocationCommand = new AsyncRelayCommand(InstallOrReinstallAsync);
                SelectCommand = new RelayCommand(Select);
                _iconImage = ClientImages.Get(Code);
                _ = LoadIconAsync();
                Refresh();
            }

            private bool _fullResLoaded;

            private async Task LoadIconAsync()
            {
                try
                {
                    ImageSource low = await ClientImages.LoadAsync(Code, ClientImages.LowRes).ConfigureAwait(true);
                    if (low != null && !_fullResLoaded)
                        IconImage = low;

                    ImageSource full = await ClientImages.LoadAsync(Code, ClientImages.FullRes).ConfigureAwait(true);
                    if (full != null)
                    {
                        _fullResLoaded = true;
                        IconImage = full;
                    }
                }
                catch
                {
                }
            }

            public void Refresh()
            {
                IsInstalled = ClassicClients.EngineInstalled && ClassicClients.IsClientInstalled(Code);
                if (IsInstalled)
                {
                    var config = ClassicClients.GetInstalledConfig(Code);
                    if (config != null)
                    {
                        if (!string.IsNullOrWhiteSpace(config.Name))
                            Title = config.Name;
                        HasStudio = ClassicClients.HasStudio(config);
                    }
                }
                VersionText = IsInstalled ? "Installed" : "Not installed yet";
                LocationText = IsInstalled ? Path.Combine(ClassicClients.ClientsDir, Code) : ClassicClients.Root;
                if (IsInstalled)
                    ComputeSizeAsync();
                else
                {
                    SizeText = "";
                    UpdateAvailable = false;
                }
                if (!IsBusy)
                    StatusText = IsInstalled ? (UpdateAvailable ? "Update available" : "Installed") : "Not installed";
                RaiseAll();
            }

            public bool HasAddons => true;
            public bool ShowFleasion => false;
            public bool ShowCommunityContent => true;

            private void Select()
            {
                _parent.SelectedItem = this;
                if (!IsInstalled)
                    return;
                App.Settings.Prop.LaunchSelectedClient = Code;
                App.Settings.SaveDeferred();
            }

            public async Task CheckUpdateAsync()
            {
                if (!IsInstalled || IsBusy)
                    return;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    bool available = await ClassicClients.IsClientUpdateAvailableAsync(Code, cts.Token).ConfigureAwait(true);
                    if (available)
                    {
                        UpdateAvailable = true;
                        OnPropertyChanged(nameof(UpdateAvailable));
                        OnPropertyChanged(nameof(PrimaryButtonText));
                        StatusText = "Update available";
                        OnPropertyChanged(nameof(StatusText));
                    }
                }
                catch
                {
                }
            }

            private void ComputeSizeAsync()
            {
                string dir = Path.Combine(ClassicClients.ClientsDir, Code);
                _ = Task.Run(delegate
                {
                    string size = FormatBytes(ClassicClients.GetDirectorySize(dir));
                    Application.Current?.Dispatcher.BeginInvoke((Action)delegate
                    {
                        SizeText = size;
                        OnPropertyChanged(nameof(SizeText));
                    });
                });
            }

            private void RaiseAll()
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Description));
                OnPropertyChanged(nameof(IsInstalled));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(HasStudio));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(PrimaryButtonText));
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(CanUninstall));
            }

            private void SetBusy(bool busy, string status)
            {
                IsBusy = busy;
                StatusText = status;
                if (!busy)
                    Progress = 0;
                RaiseAll();
            }

            private void ReportProgress(double percent, string status)
            {
                Application.Current?.Dispatcher.BeginInvoke((Action)delegate
                {
                    Progress = percent;
                    StatusText = status;
                    OnPropertyChanged(nameof(Progress));
                    OnPropertyChanged(nameof(StatusText));
                });
            }

            private void SetStatus(string status)
            {
                Application.Current?.Dispatcher.BeginInvoke((Action)delegate
                {
                    StatusText = status;
                    OnPropertyChanged(nameof(StatusText));
                });
            }

            private void Cancel()
            {
                try { _cts?.Cancel(); }
                catch { }
            }

            private async Task InstallOrReinstallAsync()
            {
                if (IsBusy)
                    return;
                if (!_parent.TryBeginOperation())
                {
                    Frontend.ShowMessageBox("Wait for the current operation to finish.", MessageBoxImage.Warning);
                    return;
                }
                if (Frontend.ShowMessageBox("These are a WIP and may not work all entirely yet and may produce errors, I will still continue to work on these fixes.", MessageBoxImage.Warning, MessageBoxButton.OKCancel, MessageBoxResult.OK) != MessageBoxResult.OK)
                {
                    _parent.EndOperation();
                    return;
                }
                SetBusy(true, "Preparing");
                _cts = new CancellationTokenSource();
                try
                {
                    await ClassicClients.InstallClientAsync(Code, ReportProgress, _cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    SetStatus("Canceled");
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteLine("DownloadsViewModel::ClassicInstall", $"{Code} install failed: {ex.Message}");
                    Frontend.ShowMessageBox($"Could not install {Title}: {ex.Message}\n\nCheck the classic download URL in Settings, or set a local VoidstrapClient folder to install from.", MessageBoxImage.Error);
                }
                finally
                {
                    _cts?.Dispose();
                    _cts = null;
                    SetBusy(false, "");
                    Refresh();
                    _parent.RefreshClassicMaps();
                    _parent.EndOperation();
                }
            }

            private async Task UninstallAsync()
            {
                if (!IsInstalled || IsBusy)
                    return;
                if (!_parent.TryBeginOperation())
                {
                    Frontend.ShowMessageBox("Wait for the current operation to finish.", MessageBoxImage.Warning);
                    return;
                }
                if (Frontend.ShowMessageBox($"Remove {Title}? This deletes {SizeText} of installed files. You can reinstall it any time.", MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    _parent.EndOperation();
                    return;
                }
                SetBusy(true, "Removing");
                try
                {
                    await ClassicClients.UninstallClientAsync(Code).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Frontend.ShowMessageBox($"Could not fully remove {Title}: {ex.Message}", MessageBoxImage.Error);
                }
                finally
                {
                    SetBusy(false, "");
                    _parent.EndOperation();
                    Refresh();
                    _parent.RefreshClassicMaps();
                }
            }

            private void OpenFolder()
            {
                try
                {
                    string dir = Path.Combine(ClassicClients.ClientsDir, Code);
                    if (!Directory.Exists(dir))
                        dir = ClassicClients.Root;
                    Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Frontend.ShowMessageBox($"Could not open the folder: {ex.Message}", MessageBoxImage.Error);
                }
            }
        }

        public ObservableCollection<DownloadItem> Items { get; } = new ObservableCollection<DownloadItem>();

        public string InstallRootText => Paths.Versions;

        public bool IsDownloading { get; private set; }
        public DownloadItem ActiveItem { get; private set; }
        public string GraphTitle { get; private set; } = "No active download";
        public string CurrentSpeedText { get; private set; } = "0 B/s";
        public string PeakSpeedText { get; private set; } = "0 B/s";
        public string DownloadedText { get; private set; } = "";
        public PointCollection GraphPoints { get; private set; } = new PointCollection();
        public Visibility PlaceholderVisibility => GraphPoints.Count > 1 ? Visibility.Collapsed : Visibility.Visible;

        private readonly List<double> _speedSamples = new List<double>();
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private double _peakSpeed;
        private const int MaxSamples = 90;
        private const double GraphWidth = 1000.0;
        private const double GraphHeight = 100.0;

        public ICommand OpenRootCommand { get; }
        public ICommand RefreshCommand { get; }

        public ObservableCollection<ClientItem> ClientItems { get; } = new ObservableCollection<ClientItem>();
        public ObservableCollection<string> ClientMaps { get; } = new ObservableCollection<string>();
        public string ClassicStatusText { get; private set; } = "";

        public string ClassicDownloadBaseUrl
        {
            get => string.IsNullOrWhiteSpace(App.Settings.Prop.ClassicDownloadBaseUrl) ? ClassicClients.DefaultBaseUrl : App.Settings.Prop.ClassicDownloadBaseUrl;
            set
            {
                string v = (value ?? "").Trim();
                if (string.Equals(v, ClassicClients.DefaultBaseUrl, StringComparison.OrdinalIgnoreCase) || string.Equals(v, ClassicClients.LegacyDefaultBaseUrl, StringComparison.OrdinalIgnoreCase))
                    v = "";
                if (App.Settings.Prop.ClassicDownloadBaseUrl == v)
                    return;
                App.Settings.Prop.ClassicDownloadBaseUrl = v;
                App.Settings.Save();
                OnPropertyChanged(nameof(ClassicDownloadBaseUrl));
            }
        }

        private bool _isRefreshingClassicMaps;

        public string SelectedClassicMap
        {
            get => App.Settings.Prop.ClassicSelectedMap;
            set
            {
                if (_isRefreshingClassicMaps)
                    return;
                string newMap = value ?? "";
                if (string.IsNullOrEmpty(newMap) && ClientMaps.Count > 0)
                    return;
                if (App.Settings.Prop.ClassicSelectedMap == newMap)
                    return;
                App.Settings.Prop.ClassicSelectedMap = newMap;
                App.Settings.Save();
                OnPropertyChanged(nameof(SelectedClassicMap));
            }
        }

        public ICommand LocateClassicSourceCommand { get; }
        public ICommand OpenClassicRootCommand { get; }

        public DownloadsViewModel()
        {
            Items.Add(new DownloadItem(this, new RobloxPlayerData(), "Roblox Player", "The Roblox client for playing experiences", "pack://application:,,,/Resources/RobloxPlayerIcon.png", "WindowsPlayer", LaunchMode.Player, "RobloxPlayerBeta", true));
            Items.Add(new DownloadItem(this, new RobloxStudioData(), "Roblox Studio", "Create and edit experiences", "pack://application:,,,/Resources/RobloxStudioIcon.png", "WindowsStudio64", LaunchMode.Studio, "RobloxStudioBeta", false));
            OpenRootCommand = new RelayCommand(OpenRoot);
            RefreshCommand = new RelayCommand(RefreshAll);
            SelectCommand = new RelayCommand<DownloadItem>(Select);
            LocateClassicSourceCommand = new RelayCommand(LocateClassicSource);
            OpenClassicRootCommand = new RelayCommand(OpenClassicRoot);
            _selectedItem = Items.Count > 0 ? Items[0] : null;
            RefreshClassic();
            _ = LoadManifestClientsAsync();
        }

        private bool _manifestLoading;

        private async Task LoadManifestClientsAsync()
        {
            if (_manifestLoading)
                return;
            _manifestLoading = true;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var entries = await ClassicClients.FetchManifestClientsAsync(cts.Token).ConfigureAwait(true);
                if (entries.Count == 0)
                    return;
                var comparer = Comparer<string>.Create((a, b) => ClassicClients.CompareClientNames(a, b));
                bool added = false;
                foreach (var entry in entries)
                {
                    if (ClientItems.Any(i => string.Equals(i.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    int idx = 0;
                    while (idx < ClientItems.Count && comparer.Compare(ClientItems[idx].Code, entry.Code) > 0)
                        idx++;
                    ClientItems.Insert(idx, new ClientItem(this, entry));
                    added = true;
                }
                if (added)
                    RefreshClassic();
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("DownloadsViewModel::LoadManifest", ex.Message);
            }
            finally
            {
                _manifestLoading = false;
            }
        }

        private object _selectedItem;
        public object SelectedItem
        {
            get => _selectedItem;
            set
            {
                _selectedItem = value;
                OnPropertyChanged(nameof(SelectedItem));
            }
        }

        public ICommand SelectCommand { get; }

        private void Select(DownloadItem item)
        {
            if (item != null)
                SelectedItem = item;
        }

        public void RefreshAll()
        {
            foreach (var item in Items)
                item.Refresh();
            OnPropertyChanged(nameof(InstallRootText));
            RefreshClassic();
            _ = LoadManifestClientsAsync();
            _ = CheckAllUpdatesAsync();
        }

        public void RefreshClassic()
        {
            if (ClientItems.Count == 0)
            {
                foreach (var entry in ClassicClients.Catalog.OrderByDescending(e => e.Code, Comparer<string>.Create((a, b) => ClassicClients.CompareClientNames(a, b))))
                    ClientItems.Add(new ClientItem(this, entry));
            }
            else
            {
                foreach (var item in ClientItems)
                    item.Refresh();
            }

            int installed = ClientItems.Count(i => i.IsInstalled);
            if (!ClassicClients.EngineInstalled)
                ClassicStatusText = "Install any version to set up the classic engine. Clients download from the classic download URL (set in Settings), or from a local VoidstrapClient folder if you set one.";
            else
                ClassicStatusText = $"{installed} of {ClientItems.Count} installed at {ClassicClients.Root}";
            OnPropertyChanged(nameof(ClassicStatusText));

            RefreshClassicMaps();
        }

        internal void RefreshClassicMaps()
        {
            _isRefreshingClassicMaps = true;
            try
            {
                var maps = ClassicClients.ListMaps();
                string savedMap = App.Settings.Prop.ClassicSelectedMap ?? "";
                ClientMaps.Clear();
                foreach (var map in maps)
                    ClientMaps.Add(map);

                if (maps.Count > 0)
                {
                    if (!string.IsNullOrEmpty(savedMap) && maps.Contains(savedMap))
                    {
                        App.Settings.Prop.ClassicSelectedMap = savedMap;
                    }
                    else
                    {
                        App.Settings.Prop.ClassicSelectedMap = maps[0];
                    }
                }
                else
                {
                    App.Settings.Prop.ClassicSelectedMap = "";
                }
                App.Settings.Save();
            }
            finally
            {
                _isRefreshingClassicMaps = false;
            }
            OnPropertyChanged(nameof(SelectedClassicMap));
        }

        private void LocateClassicSource()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Locate your VoidstrapClient folder",
                Multiselect = false
            };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
                return;
            if (!ClassicClients.IsValidSource(dialog.FolderName))
            {
                Frontend.ShowMessageBox("That folder does not look like an VoidstrapClient installation. It must contain VoidstrapClient.WebServer.exe and a data\\clients folder.", MessageBoxImage.Warning);
                return;
            }
            App.Settings.Prop.ClassicSourceLocation = dialog.FolderName;
            App.Settings.Save();
            RefreshClassic();
        }

        private void OpenClassicRoot()
        {
            try
            {
                string dir = ClassicClients.Root;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox($"Could not open the folder: {ex.Message}", MessageBoxImage.Error);
            }
        }

        private async Task CheckAllUpdatesAsync()
        {
            foreach (var item in Items)
                await item.CheckUpdateAsync().ConfigureAwait(true);
        }

        internal void BeginActiveDownload(DownloadItem item, string binaryType)
        {
            _speedSamples.Clear();
            _peakSpeed = 0;
            GraphPoints = new PointCollection();
            ActiveItem = item;
            IsDownloading = true;
            GraphTitle = "Downloading " + item.Title;
            OnPropertyChanged(nameof(ActiveItem));
            CurrentSpeedText = "0 B/s";
            PeakSpeedText = "0 B/s";
            DownloadedText = "";
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(GraphTitle));
            OnPropertyChanged(nameof(GraphPoints));
            OnPropertyChanged(nameof(PlaceholderVisibility));
            OnPropertyChanged(nameof(CurrentSpeedText));
            OnPropertyChanged(nameof(PeakSpeedText));
            OnPropertyChanged(nameof(DownloadedText));
        }

        internal bool TryBeginOperation()
        {
            return _operationGate.Wait(0);
        }

        internal void EndOperation()
        {
            _operationGate.Release();
        }

        internal void EndActiveDownload()
        {
            IsDownloading = false;
            GraphTitle = "No active download";
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(GraphTitle));
        }

        internal void PushSpeedSample(double speedBytesPerSec, long total, long done)
        {
            _speedSamples.Add(Math.Max(0, speedBytesPerSec));
            if (_speedSamples.Count > MaxSamples)
                _speedSamples.RemoveAt(0);
            if (speedBytesPerSec > _peakSpeed)
                _peakSpeed = speedBytesPerSec;

            double scale = _peakSpeed > 1 ? _peakSpeed : 1;
            var points = new PointCollection();
            int n = _speedSamples.Count;
            for (int i = 0; i < n; i++)
            {
                double x = n <= 1 ? 0 : i / (double)(n - 1) * GraphWidth;
                double y = GraphHeight - (_speedSamples[i] / scale) * GraphHeight;
                points.Add(new Point(x, y));
            }
            GraphPoints = points;
            CurrentSpeedText = FormatSpeed(speedBytesPerSec);
            PeakSpeedText = FormatSpeed(_peakSpeed) + " peak";
            DownloadedText = $"{FormatBytes(done)} of {FormatBytes(total)}";
            OnPropertyChanged(nameof(GraphPoints));
            OnPropertyChanged(nameof(PlaceholderVisibility));
            OnPropertyChanged(nameof(CurrentSpeedText));
            OnPropertyChanged(nameof(PeakSpeedText));
            OnPropertyChanged(nameof(DownloadedText));
        }

        private void OpenRoot()
        {
            try
            {
                Directory.CreateDirectory(Paths.Versions);
                Process.Start(new ProcessStartInfo { FileName = Paths.Versions, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox($"Could not open the folder: {ex.Message}", MessageBoxImage.Error);
            }
        }

        private static void MoveDirectory(string source, string target)
        {
            if (!Directory.Exists(source))
            {
                Directory.CreateDirectory(target);
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                Directory.Move(source, target);
            }
            catch (IOException)
            {
                CopyDirectory(source, target);
                Directory.Delete(source, true);
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        private static async Task<string> FetchLatestVersionAsync(string binaryType)
        {
            try
            {
                var info = await Deployment.GetInfo(App.Settings.Prop.Channel, binaryType: binaryType).ConfigureAwait(false);
                return info?.VersionGuid ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1073741824L)
                return (bytes / 1073741824.0).ToString("0.0") + " GB";
            if (bytes >= 1048576L)
                return (bytes / 1048576.0).ToString("0") + " MB";
            if (bytes >= 1024L)
                return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1048576.0)
                return (bytesPerSecond / 1048576.0).ToString("0.0") + " MB/s";
            if (bytesPerSecond >= 1024.0)
                return (bytesPerSecond / 1024.0).ToString("0") + " KB/s";
            return Math.Max(0, bytesPerSecond).ToString("0") + " B/s";
        }
    }
}
