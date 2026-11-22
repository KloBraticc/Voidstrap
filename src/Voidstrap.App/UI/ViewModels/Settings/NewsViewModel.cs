using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Plugins;

namespace Voidstrap.UI.ViewModels.Settings;

public sealed partial class NewsViewModel : ObservableObject, IDisposable
{
    private const int FeaturedLimit = 5;

    private CancellationTokenSource? _cts;
    private string _shownJson = string.Empty;
    private bool _disposed;

    public ObservableCollection<NewsPost> Posts { get; } = [];

    public ObservableCollection<NewsPost> Featured { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeatured))]
    public partial NewsPost? FeaturedPost { get; set; }

    public bool HasFeatured => FeaturedPost != null;

    public bool HasPosts => Posts.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; set; }

    public bool ShowEmpty => !IsLoading && Featured.Count == 0;

    public NewsViewModel()
    {
        ModVotes.Changed += OnVotesChanged;
        _ = InitializeAsync();
        _ = ModVotes.RefreshAsync(CancellationToken.None);
    }

    private void OnVotesChanged()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(LoadVotes));
    }

    private void LoadVotes()
    {
        if (_disposed)
            return;
        foreach (NewsPost post in Featured)
            post.LoadVotes();
        foreach (NewsPost post in Posts)
            post.LoadVotes();
    }

    private async Task InitializeAsync()
    {
        string? cached = await VoidstrapNewsFeed.ReadCacheAsync();
        if (!string.IsNullOrEmpty(cached) && !_disposed)
            Apply(cached);
        await LoadAsync(true);
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync(true);

    internal Task CheckForUpdatesAsync() => IsLoading ? Task.CompletedTask : LoadAsync(false);

    [RelayCommand]
    private void SelectFeatured(NewsPost? post)
    {
        if (post != null && Featured.Contains(post))
            SetFeatured(post);
    }

    [RelayCommand]
    private void PreviousFeatured() => StepFeatured(-1);

    [RelayCommand]
    private void NextFeatured() => StepFeatured(1);

    internal void StepFeatured(int step)
    {
        if (Featured.Count == 0)
            return;
        int index = FeaturedPost == null ? 0 : Math.Max(0, Featured.IndexOf(FeaturedPost));
        SetFeatured(Featured[((index + step) % Featured.Count + Featured.Count) % Featured.Count]);
    }

    private void SetFeatured(NewsPost post)
    {
        if (FeaturedPost != null)
            FeaturedPost.IsCurrent = false;
        post.IsCurrent = true;
        FeaturedPost = post;
    }

    [RelayCommand]
    private static void OpenLink(string? url)
    {
        if (VoidstrapNewsFeed.IsHttpsUrl(url))
            Utilities.OpenWebLink(url!);
    }

    private async Task LoadAsync(bool showSpinner)
    {
        const string LOG_IDENT = "NewsViewModel::LoadAsync";
        if (_disposed)
            return;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        if (showSpinner)
            IsLoading = true;
        try
        {
            string? json = await VoidstrapNewsFeed.FetchAsync(token);
            if (token.IsCancellationRequested || _disposed || json == null)
                return;
            if (Apply(json))
                await VoidstrapNewsFeed.WriteCacheAsync(json);
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
            if (!token.IsCancellationRequested && !_disposed)
                IsLoading = false;
        }
    }

    private bool Apply(string json)
    {
        if (string.Equals(json, _shownJson, StringComparison.Ordinal))
            return false;
        if (VoidstrapNewsFeed.Parse(json) is not { Count: > 0 } posts)
            return false;
        VoidstrapNewsFeed.NotifyNewPosts(posts);
        string? currentTitle = FeaturedPost?.Title;
        _shownJson = json;
        Posts.Clear();
        Featured.Clear();
        FeaturedPost = null;
        foreach (NewsPost post in posts)
        {
            if (Featured.Count < FeaturedLimit)
                Featured.Add(post);
            else
                Posts.Add(post);
        }
        SetFeatured(Featured.FirstOrDefault(post => post.Title == currentTitle) ?? Featured[0]);
        LoadVotes();
        OnPropertyChanged(nameof(HasPosts));
        OnPropertyChanged(nameof(ShowEmpty));
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ModVotes.Changed -= OnVotesChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
