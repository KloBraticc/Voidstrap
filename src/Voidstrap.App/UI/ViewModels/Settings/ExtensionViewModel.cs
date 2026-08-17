using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Voidstrap.UI.ViewModels;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public class ExtensionViewModel : NotifyPropertyChangedViewModel
    {
        public const string TypeAll = "All";
        public const string TypeClassic = "Classic Roblox";
        public const string TypeExtensions = "Roblox Extensions";
        public const string TypeStudio = "Roblox Studio Extensions";

        private static readonly HttpClient client = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromMinutes(10));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
            return http;
        }

        private static string fleasionDir => Paths.Fleasion;
        private const string ApiDumpSha256 = "C79D898BCEA32693BDB96100DE01F47F9CD018A0CCB8346C038E92DC2A1F6FB8";
        private const long MaxDownloadBytes = 268435456L;

        private static string apiDumpDir => Paths.ApiDumpTool;

        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private static CancellationTokenSource _activeCts;
        private string _selectedExtensionType = TypeAll;

        private int _fleasionRev;
        private int _communityRev;
        private int _apiDumpRev;
        private const int ToggleDebounceMs = 450;
        private static volatile bool _userCancel;

        public event Action<string, double, bool> OnProgressChanged;

        public ExtensionViewModel()
        {
            if (App.Settings.Prop.Fleasion)
            {
                string exePath = Path.Combine(fleasionDir, "Fleasion.exe");
                if (!File.Exists(exePath))
                    _ = DownloadFleasionAsync();
            }
            else
            {
                _ = UninstallFleasionAsync();
            }

            if (App.Settings.Prop.RojoEnabled && !Voidstrap.Integrations.Rojo.RojoManager.IsInstalled)
                _ = DownloadRojoAsync();
        }

        public ObservableCollection<string> ExtensionTypes { get; } = new ObservableCollection<string>
        {
            TypeAll, TypeClassic, TypeExtensions, TypeStudio
        };

        public string SelectedExtensionType
        {
            get => _selectedExtensionType;
            set
            {
                if (string.IsNullOrEmpty(value) || _selectedExtensionType == value)
                    return;
                _selectedExtensionType = value;
                OnPropertyChanged();
                RaiseVisibilities();
            }
        }

        private string _searchText = "";

        public string SearchText
        {
            get => _searchText;
            set
            {
                string next = value ?? "";
                if (_searchText == next)
                    return;
                _searchText = next;
                OnPropertyChanged();
                RaiseVisibilities();
            }
        }

        private void RaiseVisibilities()
        {
            OnPropertyChanged(nameof(FleasionVisibility));
            OnPropertyChanged(nameof(RiShadeVisibility));
            OnPropertyChanged(nameof(CommunityContentVisibility));
            OnPropertyChanged(nameof(ApiDumpVisibility));
            OnPropertyChanged(nameof(RojoVisibility));
            OnPropertyChanged(nameof(StudioPluginVisibility));
            OnPropertyChanged(nameof(EmptyStateVisibility));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateSubtitle));
        }

        private bool ShowType(string type) => _selectedExtensionType == TypeAll || _selectedExtensionType == type;

        private bool MatchesSearch(string haystack)
        {
            string q = _searchText.Trim();
            return q.Length == 0 || haystack.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        private bool FleasionVisible => ShowType(TypeExtensions) && MatchesSearch("Fleasion replace Roblox game assets textures audio meshes animations dump Roblox Extension");

        private bool RiShadeVisible => ShowType(TypeExtensions) && MatchesSearch("RiShade shaders visuals effects bloom tonemap panel F8 Roblox Extension not recommended taking images");

        private bool CommunityVisible => ShowType(TypeClassic) && MatchesSearch("Community Content catalog items decals meshes audio maps classic Roblox clients");

        private bool ApiDumpVisible => ShowType(TypeExtensions) && MatchesSearch("Roblox API Dump Tool MaximumADHD api dump diff classes members enums Roblox");

        private bool RojoVisible => ShowType(TypeStudio) && MatchesSearch("Rojo filesystem sync studio project git version control serve build init plugin Roblox Studio Extensions");

        private bool StudioPluginVisible => ShowType(TypeStudio) && MatchesSearch("Voidstrap Studio plugin panel Discord rich presence rpc place script mode selection Roblox Studio Extensions");

        private bool AnyCardVisible => FleasionVisible || RiShadeVisible || CommunityVisible || ApiDumpVisible || RojoVisible || StudioPluginVisible;

        public Visibility FleasionVisibility => FleasionVisible ? Visibility.Visible : Visibility.Collapsed;

        public Visibility RiShadeVisibility => RiShadeVisible ? Visibility.Visible : Visibility.Collapsed;

        public Visibility EmptyStateVisibility => AnyCardVisible ? Visibility.Collapsed : Visibility.Visible;

        public string EmptyStateTitle
        {
            get
            {
                if (AnyCardVisible)
                    return "";
                string q = _searchText.Trim();
                if (q.Length > 0)
                {
                    if (q.Length > 40)
                        q = q.Substring(0, 40) + "...";
                    return "No results for \"" + q + "\"";
                }
                return "Nothing to show here";
            }
        }

        public string EmptyStateSubtitle
        {
            get
            {
                if (AnyCardVisible)
                    return "";
                if (_searchText.Trim().Length > 0)
                    return "Try a different search, or change the type filter.";
                return "Change the type filter to see more extensions.";
            }
        }

        private string _riShadeOpenLabel = Voidstrap.Integrations.RiShade.RiShadePanel.IsOpen ? "Close" : "Open";

        public string RiShadeOpenLabel
        {
            get => _riShadeOpenLabel;
            set
            {
                if (_riShadeOpenLabel == value)
                    return;
                _riShadeOpenLabel = value;
                OnPropertyChanged();
            }
        }

        public bool rishadeenabler
        {
            get => App.Settings.Prop.RiShadeEnabled;
            set
            {
                if (App.Settings.Prop.RiShadeEnabled == value)
                    return;
                Voidstrap.Integrations.RiShade.RiShadeManager.SetEnabled(value);
                OnPropertyChanged();
            }
        }

        public Visibility CommunityContentVisibility => CommunityVisible ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ApiDumpVisibility => ApiDumpVisible ? Visibility.Visible : Visibility.Collapsed;

        public Visibility RojoVisibility => RojoVisible ? Visibility.Visible : Visibility.Collapsed;

        public Visibility StudioPluginVisibility => StudioPluginVisible ? Visibility.Visible : Visibility.Collapsed;

        public bool studiopluginenabler
        {
            get => App.Settings.Prop.StudioPluginEnabled;
            set
            {
                if (App.Settings.Prop.StudioPluginEnabled == value)
                    return;
                App.Settings.Prop.StudioPluginEnabled = value;
                App.Settings.Save();
                OnPropertyChanged();
                if (value)
                {
                    Voidstrap.Integrations.Studio.StudioPluginInstaller.EnsureInstalled(force: true);
                    return;
                }
                Voidstrap.Integrations.Studio.StudioIntegration.Shutdown();
                Voidstrap.Integrations.Studio.StudioPluginInstaller.Uninstall();
            }
        }

        private int _rojoRev;

        public bool rojoenabler
        {
            get => App.Settings.Prop.RojoEnabled;
            set
            {
                if (App.Settings.Prop.RojoEnabled == value)
                    return;
                App.Settings.Prop.RojoEnabled = value;
                App.Settings.Save();
                OnPropertyChanged();
                _ = DebounceRojoAsync();
            }
        }

        public string RojoProjectPath
        {
            get => App.Settings.Prop.RojoProjectPath ?? "";
            set
            {
                string next = value ?? "";
                if ((App.Settings.Prop.RojoProjectPath ?? "") == next)
                    return;
                App.Settings.Prop.RojoProjectPath = next;
                App.Settings.Save();
                OnPropertyChanged();
            }
        }

        public string RojoServeLabel => Voidstrap.Integrations.Rojo.RojoManager.IsServing ? "Stop sync" : "Start sync";

        private async Task DebounceRojoAsync()
        {
            int rev = Interlocked.Increment(ref _rojoRev);
            if (!App.Settings.Prop.RojoEnabled)
                StopAllDownloads();
            try { await Task.Delay(ToggleDebounceMs); } catch { }
            if (rev != Volatile.Read(ref _rojoRev))
                return;
            if (App.Settings.Prop.RojoEnabled)
                await DownloadRojoAsync();
            else
                await UninstallRojoAsync();
        }

        private void RevertRojoToggle()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (App.Settings.Prop.RojoEnabled)
                {
                    App.Settings.Prop.RojoEnabled = false;
                    App.Settings.Save();
                    OnPropertyChanged(nameof(rojoenabler));
                }
            });
        }

        private async Task DownloadRojoAsync()
        {
            var (cts, ct) = BeginOperation();
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    await Voidstrap.Integrations.Rojo.RojoManager.EnsureInstalledAsync(
                        (t, f, s) => OnProgressChanged?.Invoke(t, f, s), ct);
                    OnProgressChanged?.Invoke("Rojo ready", 1.0, true);
                    await Task.Delay(700, ct);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                if (_userCancel)
                    RevertRojoToggle();
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Could not download Rojo:\n" + ex.Message, MessageBoxImage.Error);
                RevertRojoToggle();
            }
            finally
            {
                OnProgressChanged?.Invoke("", -1.0, false);
                EndOperation(cts);
            }
        }

        private async Task UninstallRojoAsync()
        {
            var (cts, ct) = BeginOperation();
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    OnProgressChanged?.Invoke("Removing Rojo", -1.0, true);
                    await Voidstrap.Integrations.Rojo.RojoManager.UninstallAsync();
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Could not uninstall Rojo: " + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                OnProgressChanged?.Invoke("", -1.0, false);
                EndOperation(cts);
                OnPropertyChanged(nameof(RojoServeLabel));
            }
        }

        private bool EnsureRojoReady()
        {
            if (!Voidstrap.Integrations.Rojo.RojoManager.IsInstalled)
            {
                Frontend.ShowMessageBox("Enable Rojo first so it can download.", MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(RojoProjectPath) || !Directory.Exists(RojoProjectPath))
            {
                Frontend.ShowMessageBox("Choose a project folder first.", MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        public void PickRojoFolder()
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a Rojo project folder" };
                if (!string.IsNullOrWhiteSpace(RojoProjectPath) && Directory.Exists(RojoProjectPath))
                    dialog.InitialDirectory = RojoProjectPath;
                if (dialog.ShowDialog() == true)
                    RojoProjectPath = dialog.FolderName;
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Could not open the folder picker: " + ex.Message, MessageBoxImage.Error);
            }
        }

        public async Task RojoInitAsync()
        {
            if (!EnsureRojoReady())
                return;
            var (cts, ct) = BeginOperation();
            try
            {
                OnProgressChanged?.Invoke("Creating Rojo project", -1.0, true);
                var (ok, output) = await Voidstrap.Integrations.Rojo.RojoManager.RunAsync("init", RojoProjectPath, ct);
                OnProgressChanged?.Invoke("", -1.0, false);
                if (ok)
                    Frontend.ShowMessageBox("Rojo project created in:\n" + RojoProjectPath, MessageBoxImage.Information);
                else
                    Frontend.ShowMessageBox("rojo init did not complete:\n" + output, MessageBoxImage.Warning);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("rojo init failed: " + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                OnProgressChanged?.Invoke("", -1.0, false);
                EndOperation(cts);
            }
        }

        public void RojoToggleServe()
        {
            if (Voidstrap.Integrations.Rojo.RojoManager.IsServing)
            {
                Voidstrap.Integrations.Rojo.RojoManager.StopServe();
                OnPropertyChanged(nameof(RojoServeLabel));
                return;
            }
            if (!EnsureRojoReady())
                return;
            bool started = Voidstrap.Integrations.Rojo.RojoManager.StartServe(RojoProjectPath);
            if (!started)
                Frontend.ShowMessageBox("Could not start rojo serve.", MessageBoxImage.Error);
            OnPropertyChanged(nameof(RojoServeLabel));
        }

        public async Task RojoBuildAsync()
        {
            if (!EnsureRojoReady())
                return;
            var (cts, ct) = BeginOperation();
            try
            {
                OnProgressChanged?.Invoke("Building place with Rojo", -1.0, true);
                string outFile = Path.Combine(RojoProjectPath, "build.rbxlx");
                var (ok, output) = await Voidstrap.Integrations.Rojo.RojoManager.RunAsync($"build -o \"{outFile}\"", RojoProjectPath, ct);
                OnProgressChanged?.Invoke("", -1.0, false);
                if (ok)
                    Frontend.ShowMessageBox("Built place file:\n" + outFile, MessageBoxImage.Information);
                else
                    Frontend.ShowMessageBox("rojo build did not complete:\n" + output, MessageBoxImage.Warning);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("rojo build failed: " + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                OnProgressChanged?.Invoke("", -1.0, false);
                EndOperation(cts);
            }
        }

        public async Task RojoInstallPluginAsync()
        {
            if (!Voidstrap.Integrations.Rojo.RojoManager.IsInstalled)
            {
                Frontend.ShowMessageBox("Enable Rojo first so it can download.", MessageBoxImage.Warning);
                return;
            }
            var (cts, ct) = BeginOperation();
            try
            {
                OnProgressChanged?.Invoke("Installing the Rojo Studio plugin", -1.0, true);
                var (ok, output) = await Voidstrap.Integrations.Rojo.RojoManager.RunAsync("plugin install", RojoProjectPath, ct);
                OnProgressChanged?.Invoke("", -1.0, false);
                if (ok)
                    Frontend.ShowMessageBox("The Rojo Studio plugin was installed. Open Studio, then click Connect on the Rojo toolbar.", MessageBoxImage.Information);
                else
                    Frontend.ShowMessageBox("Could not install the Rojo Studio plugin:\n" + output, MessageBoxImage.Warning);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Rojo plugin install failed: " + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                OnProgressChanged?.Invoke("", -1.0, false);
                EndOperation(cts);
            }
        }

        public void OpenRojoFolder()
        {
            if (!string.IsNullOrWhiteSpace(RojoProjectPath) && Directory.Exists(RojoProjectPath))
                OpenFolder(RojoProjectPath);
            else
                Frontend.ShowMessageBox("Choose a project folder first.", MessageBoxImage.Warning);
        }

        public bool apidumpenabler
        {
            get => App.Settings.Prop.RobloxApiDumpTool;
            set
            {
                if (App.Settings.Prop.RobloxApiDumpTool == value)
                    return;
                App.Settings.Prop.RobloxApiDumpTool = value;
                App.Settings.Save();
                OnPropertyChanged();
                _ = DebounceApiDumpAsync();
            }
        }

        private async Task DebounceApiDumpAsync()
        {
            int rev = Interlocked.Increment(ref _apiDumpRev);
            if (!App.Settings.Prop.RobloxApiDumpTool)
                StopAllDownloads();
            try { await Task.Delay(ToggleDebounceMs); } catch { }
            if (rev != Volatile.Read(ref _apiDumpRev))
                return;
            if (App.Settings.Prop.RobloxApiDumpTool)
                await DownloadApiDumpToolAsync();
            else
                await UninstallApiDumpToolAsync();
        }

        private void RevertApiDumpToggle()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (App.Settings.Prop.RobloxApiDumpTool)
                {
                    App.Settings.Prop.RobloxApiDumpTool = false;
                    App.Settings.Save();
                    OnPropertyChanged(nameof(apidumpenabler));
                }
            });
        }

        private async Task DownloadApiDumpToolAsync()
        {
            var (cts, ct) = BeginOperation();
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    OnProgressChanged?.Invoke("Preparing Roblox API Dump Tool", -1.0, true);
                    Directory.CreateDirectory(apiDumpDir);
                    string outputPath = Path.Combine(apiDumpDir, "RobloxAPIDumpTool.exe");

                    await KillProcessAsync("RobloxAPIDumpTool", ct);

                    if (File.Exists(outputPath) && IsFileLocked(outputPath))
                    {
                        Frontend.ShowMessageBox("RobloxAPIDumpTool.exe is still running. Close it and try again.", MessageBoxImage.Warning);
                        return;
                    }

                    var download = await ResolveApiDumpUrlsAsync(ct);
                    bool ok = await DownloadToFileAsync(download.Urls, outputPath, "Downloading Roblox API Dump Tool", ct, download.Digest);
                    if (ok)
                    {
                        OnProgressChanged?.Invoke("Roblox API Dump Tool ready", 1.0, true);
                        await Task.Delay(700, ct);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                if (_userCancel)
                    RevertApiDumpToggle();
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Could not download the Roblox API Dump Tool:\n" + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                OnProgressChanged?.Invoke("", -1.0, false);
                EndOperation(cts);
            }
        }

        private async Task UninstallApiDumpToolAsync()
        {
            var (cts, ct) = BeginOperation();
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    if (!Directory.Exists(apiDumpDir))
                        return;

                    OnProgressChanged?.Invoke("Removing Roblox API Dump Tool", -1.0, true);
                    await KillProcessAsync("RobloxAPIDumpTool", ct);

                    bool deleted = false;
                    for (int i = 0; i < 5; i++)
                    {
                        try
                        {
                            Directory.Delete(apiDumpDir, true);
                            deleted = true;
                            break;
                        }
                        catch
                        {
                            await Task.Delay(400, ct);
                        }
                    }

                    if (!deleted && Directory.Exists(apiDumpDir))
                    {
                        Frontend.ShowMessageBox("Could not remove the Roblox API Dump Tool folder automatically. Opening it for manual deletion.", MessageBoxImage.Warning);
                        OpenFolder(apiDumpDir);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Could not uninstall the Roblox API Dump Tool: " + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                OnProgressChanged?.Invoke("", -1.0, false);
                EndOperation(cts);
            }
        }

        public bool fleasionenabler
        {
            get => App.Settings.Prop.Fleasion;
            set
            {
                if (App.Settings.Prop.Fleasion == value)
                    return;
                App.Settings.Prop.Fleasion = value;
                App.Settings.Save();
                OnPropertyChanged();
                _ = DebounceFleasionAsync();
            }
        }

        private async Task DebounceFleasionAsync()
        {
            int rev = Interlocked.Increment(ref _fleasionRev);
            if (!App.Settings.Prop.Fleasion)
                StopAllDownloads();
            try { await Task.Delay(ToggleDebounceMs); } catch { }
            if (rev != Volatile.Read(ref _fleasionRev))
                return;
            if (App.Settings.Prop.Fleasion)
                await DownloadFleasionAsync();
            else
                await UninstallFleasionAsync();
        }

        public bool communitycontentenabler
        {
            get => App.Settings.Prop.ClassicCommunityContent;
            set
            {
                if (App.Settings.Prop.ClassicCommunityContent == value)
                    return;
                App.Settings.Prop.ClassicCommunityContent = value;
                App.Settings.Save();
                OnPropertyChanged();
                _ = DebounceCommunityAsync();
            }
        }

        private async Task DebounceCommunityAsync()
        {
            int rev = Interlocked.Increment(ref _communityRev);
            if (!App.Settings.Prop.ClassicCommunityContent)
                StopAllDownloads();
            try { await Task.Delay(ToggleDebounceMs); } catch { }
            if (rev != Volatile.Read(ref _communityRev))
                return;
            if (App.Settings.Prop.ClassicCommunityContent)
                await InstallCommunityContentAsync();
        }

        public void UpdateCommunityContent() => _ = InstallCommunityContentAsync();

        public void CancelDownload()
        {
            _userCancel = true;
            try
            {
                _activeCts?.Cancel();
            }
            catch
            {
            }
        }

        public void StopAllDownloads()
        {
            try
            {
                _activeCts?.Cancel();
            }
            catch
            {
            }
        }

        private (CancellationTokenSource Cts, CancellationToken Token) BeginOperation()
        {
            _userCancel = false;
            var cts = new CancellationTokenSource();
            var old = Interlocked.Exchange(ref _activeCts, cts);
            try
            {
                old?.Cancel();
            }
            catch
            {
            }
            return (cts, cts.Token);
        }

        private void EndOperation(CancellationTokenSource cts)
        {
            Interlocked.CompareExchange(ref _activeCts, null, cts);
            try
            {
                cts.Dispose();
            }
            catch
            {
            }
        }

        private void RevertFleasionToggle()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (App.Settings.Prop.Fleasion)
                {
                    App.Settings.Prop.Fleasion = false;
                    App.Settings.Save();
                    OnPropertyChanged(nameof(fleasionenabler));
                }
            });
        }

        private void RevertCommunityToggle()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (App.Settings.Prop.ClassicCommunityContent)
                {
                    App.Settings.Prop.ClassicCommunityContent = false;
                    App.Settings.Save();
                    OnPropertyChanged(nameof(communitycontentenabler));
                }
            });
        }

        private async Task<bool> DownloadToFileAsync(IReadOnlyList<string> urls, string outputPath, string label, CancellationToken ct, string expectedDigest = "")
        {
            await Voidstrap.Utility.ResilientDownload.DownloadAsync(client, urls, outputPath, MaxDownloadBytes, ct, expectedDigest,
                (read, total) =>
                {
                    double fraction = total is > 0 ? (double)read / total.Value : -1.0;
                    OnProgressChanged?.Invoke(fraction >= 0 ? $"{label} {fraction * 100:0}%" : label, fraction, true);
                });
            return true;
        }

        private static async Task<(IReadOnlyList<string> Urls, string Digest)> ResolveFleasionUrlsAsync(CancellationToken ct)
        {
            var urls = new List<string>();
            string digest = "";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(client, "https://api.github.com/repos/fleasion/Fleasion/releases/latest", ct));
                foreach (JsonElement asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.Equals("Fleasion.exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-Windows.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string url = asset.GetProperty("browser_download_url").GetString();
                        string assetDigest = asset.TryGetProperty("digest", out JsonElement digestElement) ? digestElement.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(url) && assetDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && assetDigest.Length == 71)
                        {
                            urls.Add(url);
                            digest = assetDigest.Substring(7);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
            return (urls.Distinct().ToList(), digest);
        }

        private static async Task<(IReadOnlyList<string> Urls, string Digest)> ResolveApiDumpUrlsAsync(CancellationToken ct)
        {
            var urls = new List<string>();
            string digest = ApiDumpSha256;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(client, "https://api.github.com/repos/MaximumADHD/Roblox-API-Dump-Tool/releases/latest", ct));
                foreach (JsonElement asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string url = asset.GetProperty("browser_download_url").GetString();
                        string assetDigest = asset.TryGetProperty("digest", out JsonElement digestElement) ? digestElement.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(url) && assetDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && assetDigest.Length == 71)
                        {
                            urls.Add(url);
                            digest = assetDigest.Substring(7);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
            if (urls.Count == 0)
            {
                urls.Add("https://raw.githubusercontent.com/MaximumADHD/Roblox-API-Dump-Tool/master/RobloxAPIDumpTool.exe");
                digest = ApiDumpSha256;
            }
            return (urls.Distinct().ToList(), digest);
        }

        private async Task DownloadFleasionAsync()
        {
            var (cts, ct) = BeginOperation();
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    OnProgressChanged?.Invoke("Preparing Fleasion", -1.0, true);
                    Directory.CreateDirectory(fleasionDir);
                    string outputPath = Path.Combine(fleasionDir, "Fleasion.exe");

                    await KillProcessAsync("Fleasion", ct);

                    if (File.Exists(outputPath) && IsFileLocked(outputPath))
                    {
                        Frontend.ShowMessageBox("Fleasion.exe is still running. Close it and try again.", MessageBoxImage.Warning);
                        return;
                    }

                    var download = await ResolveFleasionUrlsAsync(ct);
                    if (download.Urls.Count == 0 || string.IsNullOrEmpty(download.Digest))
                        throw new InvalidDataException("Fleasion has no verified executable release");
                    bool ok = await DownloadToFileAsync(download.Urls, outputPath, "Downloading Fleasion", ct, download.Digest);
                    if (ok)
                    {
                        OnProgressChanged?.Invoke("Fleasion ready", 1.0, true);
                        await Task.Delay(700, ct);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                if (_userCancel)
                    RevertFleasionToggle();
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Could not download Fleasion:\n" + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                OnProgressChanged?.Invoke("", -1.0, false);
                EndOperation(cts);
            }
        }

        private async Task InstallCommunityContentAsync()
        {
            var (cts, ct) = BeginOperation();
            string tempZip = Path.Combine(Path.GetTempPath(), "voidstrap-community-content.zip");
            string tempDir = Path.Combine(Path.GetTempPath(), "voidstrap-community-content");
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    if (!ClassicClients.EngineInstalled)
                    {
                        Frontend.ShowMessageBox("Install a Roblox client from the Downloads page first, then enable Community Content.", MessageBoxImage.Warning);
                        return;
                    }

                    OnProgressChanged?.Invoke("Preparing community content", -1.0, true);
                    SafeDelete(tempZip);
                    SafeDeleteDir(tempDir);

                    var urls = new[]
                    {
                        "https://codeload.github.com/hereelabs/ORRH-UGC-Repository/zip/refs/heads/main",
                        "https://github.com/hereelabs/ORRH-UGC-Repository/archive/refs/heads/main.zip"
                    };
                    bool ok = await DownloadToFileAsync(urls, tempZip, "Downloading community content", ct);
                    if (!ok)
                        return;

                    ct.ThrowIfCancellationRequested();
                    OnProgressChanged?.Invoke("Installing community content", -1.0, true);
                    Voidstrap.Utility.SafeZipExtractor.ExtractToDirectory(tempZip, tempDir, maxExpandedBytes: 2147483648L);
                    string extracted = Directory.GetDirectories(tempDir).FirstOrDefault() ?? tempDir;

                    CopyMerge(Path.Combine(extracted, "data"), Path.Combine(ClassicClients.Root, "data"));
                    CopyMerge(Path.Combine(extracted, "maps"), Path.Combine(ClassicClients.Root, "maps"));

                    OnProgressChanged?.Invoke("Community content updated", 1.0, true);
                    await Task.Delay(700, ct);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                if (_userCancel)
                    RevertCommunityToggle();
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Could not install community content: " + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                SafeDelete(tempZip);
                SafeDeleteDir(tempDir);
                OnProgressChanged?.Invoke("", -1.0, false);
                EndOperation(cts);
            }
        }

        private async Task UninstallFleasionAsync()
        {
            var (cts, ct) = BeginOperation();
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    if (!Directory.Exists(fleasionDir))
                        return;

                    OnProgressChanged?.Invoke("Removing Fleasion", -1.0, true);
                    await KillProcessAsync("Fleasion", ct);

                    bool deleted = false;
                    for (int i = 0; i < 5; i++)
                    {
                        try
                        {
                            Directory.Delete(fleasionDir, true);
                            deleted = true;
                            break;
                        }
                        catch
                        {
                            await Task.Delay(400, ct);
                        }
                    }

                    if (!deleted && Directory.Exists(fleasionDir))
                    {
                        Frontend.ShowMessageBox("Could not remove the Fleasion folder automatically. Opening it for manual deletion.", MessageBoxImage.Warning);
                        OpenFolder(fleasionDir);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Could not uninstall Fleasion: " + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                OnProgressChanged?.Invoke("", -1.0, false);
                EndOperation(cts);
            }
        }

        private static async Task KillProcessAsync(string name, CancellationToken ct)
        {
            foreach (Process proc in Process.GetProcessesByName(name))
            {
                try
                {
                    proc.Kill();
                    await proc.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    proc.Dispose();
                    throw;
                }
                catch
                {
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        private static bool IsFileLocked(string filePath)
        {
            try
            {
                using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void CopyMerge(string source, string target)
        {
            if (!Directory.Exists(source))
                return;
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".dll" || ext == ".md" || ext == ".ps1" || ext == ".gitignore")
                    continue;
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("."))
                    continue;
                CopyMerge(dir, Path.Combine(target, name));
            }
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static void SafeDeleteDir(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = Path.GetFullPath(path), UseShellExecute = true });
            }
            catch
            {
            }
        }
    }
}
