using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Voidstrap.Models;

namespace Voidstrap.UI.ViewModels.Settings;

public sealed partial class NewsViewModel : ObservableObject, IDisposable
{
    private const string FeedUrl = "https://raw.githubusercontent.com/KloBraticc/VoidstrapNews-/main/feed.json";

    private static readonly HttpClient Http = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(20));

    private CancellationTokenSource? _cts;
    private bool _disposed;

    public ObservableCollection<NewsPost> Posts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; set; }

    public bool ShowEmpty => !IsLoading && Posts.Count == 0;

    public NewsViewModel()
    {
        _ = LoadAsync();
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    [RelayCommand]
    private static void OpenLink(string? url)
    {
        if (Utilities.IsWebLink(url ?? string.Empty))
            Utilities.OpenWebLink(url!);
    }

    private async Task LoadAsync()
    {
        const string LOG_IDENT = "NewsViewModel::LoadAsync";
        if (_disposed)
            return;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        IsLoading = true;
        try
        {
            string json = await Http.GetStringAsync(FeedUrl, token);
            NewsFeed? feed = JsonConvert.DeserializeObject<NewsFeed>(json);
            if (token.IsCancellationRequested)
                return;
            Posts.Clear();
            foreach (NewsPost post in feed?.Posts ?? [])
            {
                if (!string.IsNullOrWhiteSpace(post.Title))
                    Posts.Add(post);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Feed load failed: " + ex.GetType().Name);
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoading = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
