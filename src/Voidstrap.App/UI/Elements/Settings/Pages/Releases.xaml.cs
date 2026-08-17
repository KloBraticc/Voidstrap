using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Navigation;
using Wpf.Ui.Controls;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public partial class ReleasesPage
    {
        private const int MaxReleaseJsonBytes = 4 * 1024 * 1024;
        private const string ReleasesApiUrl = "https://api.github.com/repos/KloBraticc/Voidstrap/releases";
        private const string FallbackReleasesApiUrl = "https://api.github.com/repos/KloBraticc/Voidstrap/releases";

        private static readonly HttpClient HttpClient = CreateHttpClient();
        private static readonly string CacheFile =
            Path.Combine(Paths.Cache, "Releases.json");

        public ObservableCollection<GithubRelease> Releases { get; } = new();
        private readonly ICollectionView _releasesView;

        private FileSystemWatcher? _cacheWatcher;
        private int _cacheReloadQueued;
        private string? _etag;
        private readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(5);

        private static HttpClient CreateHttpClient()
        {
            var client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(15));
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "VoidstrapApp/1.0 (+https://github.com/KloBraticc/Voidstrap)");
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        private System.Threading.CancellationTokenSource? _refreshCts;

        public ReleasesPage()
        {
            InitializeComponent();
            DataContext = this;

            _releasesView = CollectionViewSource.GetDefaultView(Releases);

            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);

            StartCacheWatcher();
            StartAutoRefresh();

            Unloaded += OnReleasesPageUnloaded;

            _ = LoadReleasesAsync(true, _refreshCts.Token);
        }

        private void OnReleasesPageUnloaded(object sender, RoutedEventArgs e)
        {
            Unloaded -= OnReleasesPageUnloaded;
            try
            {
                _refreshCts?.Cancel();
                _refreshCts?.Dispose();
            }
            catch
            {
            }
            _refreshCts = null;
            if (_cacheWatcher != null)
            {
                _cacheWatcher.EnableRaisingEvents = false;
                _cacheWatcher.Changed -= OnCacheFileChanged;
                _cacheWatcher.Created -= OnCacheFileChanged;
                _cacheWatcher.Dispose();
                _cacheWatcher = null;
            }
            Releases.Clear();
        }

        private void StartAutoRefresh()
        {
            _refreshCts = new System.Threading.CancellationTokenSource();
            var token = _refreshCts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_refreshInterval, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    await LoadReleasesAsync(token: token);
                }
            });
        }

        private void StartCacheWatcher()
        {
            _cacheWatcher = new FileSystemWatcher
            {
                Path = Path.GetDirectoryName(CacheFile)!,
                Filter = Path.GetFileName(CacheFile),
                NotifyFilter = NotifyFilters.LastWrite |
                               NotifyFilters.Size |
                               NotifyFilters.FileName
            };

            _cacheWatcher.Changed += OnCacheFileChanged;
            _cacheWatcher.Created += OnCacheFileChanged;

            _cacheWatcher.EnableRaisingEvents = true;
        }

        private void OnCacheFileChanged(object sender, FileSystemEventArgs e)
        {
            if (Interlocked.Exchange(ref _cacheReloadQueued, 1) != 0)
                return;
            try
            {
                Dispatcher.BeginInvoke((Action)ProcessCacheChange);
            }
            catch
            {
                Interlocked.Exchange(ref _cacheReloadQueued, 0);
            }
        }

        private async void ProcessCacheChange()
        {
            try
            {
                CancellationTokenSource? cts = _refreshCts;
                if (cts == null)
                    return;
                CancellationToken token = cts.Token;
                await Task.Delay(500, token);
                await LoadFromCacheAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _cacheReloadQueued, 0);
            }
        }

        private async Task LoadReleasesAsync(bool force = false, CancellationToken token = default)
        {
            await LoadFromCacheAsync(token);

            try
            {
                token.ThrowIfCancellationRequested();
                var json = await Voidstrap.Utility.GitHubCache.GetStringWithFallbackAsync(ReleasesApiUrl, FallbackReleasesApiUrl, force ? TimeSpan.Zero : _refreshInterval, token);
                if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MaxReleaseJsonBytes)
                    return;
                var releases = JsonSerializer.Deserialize<GithubRelease[]>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? Array.Empty<GithubRelease>();
				await Task.Run(() => Voidstrap.Utility.JsonFile.WriteAtomicText(CacheFile, json), token);

                UpdateReleasesCollection(releases, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        private async Task LoadFromCacheAsync(CancellationToken token)
        {
            if (!File.Exists(CacheFile))
                return;

            try
            {
                var json = await ReadCacheAsync(token);

                var releases = JsonSerializer.Deserialize<GithubRelease[]>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? Array.Empty<GithubRelease>();

                UpdateReleasesCollection(releases, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        private static async Task<string> ReadCacheAsync(CancellationToken token)
        {
            await using FileStream stream = new FileStream(CacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < 0 || stream.Length > MaxReleaseJsonBytes)
                throw new InvalidDataException("Release cache is too large");
            byte[] bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token);
            return Encoding.UTF8.GetString(bytes);
        }

        private void UpdateReleasesCollection(GithubRelease[] releases, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested)
                    return;
                Releases.Clear();
                foreach (var rel in releases)
                {
                    rel.CalculateTotals();
                    Releases.Add(rel);
                }
            });
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query =
                (sender as System.Windows.Forms.TextBox)?.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(query))
            {
                _releasesView.Filter = null;
            }
            else
            {
                _releasesView.Filter = obj =>
                {
                    if (obj is not GithubRelease r) return false;

                    bool Matches(string? s) =>
                        !string.IsNullOrEmpty(s) &&
                        s.IndexOf(query,
                            StringComparison.OrdinalIgnoreCase) >= 0;

                    return Matches(r.Name) ||
                           Matches(r.TagName) ||
                           Matches(r.Body);
                };
            }

            _releasesView.Refresh();
        }

        private void Hyperlink_RequestNavigate(
            object sender,
            RequestNavigateEventArgs e)
        {
            try
            {
                e.Handled = true;
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public class GithubRelease
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("tag_name")]
            public string? TagName { get; set; }

            [JsonPropertyName("body")]
            public string? Body { get; set; }

            [JsonPropertyName("prerelease")]
            public bool Prerelease { get; set; }

            [JsonPropertyName("draft")]
            public bool Draft { get; set; }

            [JsonPropertyName("published_at")]
            public DateTimeOffset PublishedAt { get; set; }

            [JsonPropertyName("html_url")]
            public string? HtmlUrl { get; set; }

            [JsonPropertyName("assets")]
            public GithubAsset[] Assets { get; set; } = Array.Empty<GithubAsset>();

            public int TotalDownloads { get; private set; }

            public void CalculateTotals()
            {
                TotalDownloads =
                    Assets?.Sum(a => a.DownloadCount) ?? 0;
            }
        }

        public class GithubAsset
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("content_type")]
            public string? ContentType { get; set; }

            [JsonPropertyName("browser_download_url")]
            public string? BrowserDownloadUrl { get; set; }

            [JsonPropertyName("size")]
            public long Size { get; set; }

            [JsonPropertyName("download_count")]
            public int DownloadCount { get; set; }

            public double SizeMb =>
                Size / 1024d / 1024d;
        }
    }
}
