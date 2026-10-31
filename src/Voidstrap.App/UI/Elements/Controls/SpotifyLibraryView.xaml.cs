using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;
using Voidstrap.UI.Elements.Dialogs;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Elements.Controls;

public partial class SpotifyLibraryView : UserControl
{
    private readonly SpotifyEngine _engine = SpotifyEngine.Shared;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private bool _starting;

    private bool _loadingLibrary;

    private int _retryTicks;

    private List<SpotifyItem> _library = new();

    private bool _shown;

    private string? _nowImage;

    private string? _lastPosition;

    private string? _lastTitle;

    private DateTime _lastMoved;

    private bool _isPlaying;

    private bool _overrideValue;

    private DateTime _overrideUntil;

    private void ExpectPlaybackToggle()
    {
        _overrideValue = !_isPlaying;
        _overrideUntil = DateTime.UtcNow.AddSeconds(3);
        _lastMoved = _overrideValue ? DateTime.UtcNow : default;
    }

    private SpotifyItem? _opened;

    private bool _inTrackList;

    public event EventHandler? Failed;

    public event EventHandler<SpotifyState>? StateChanged;

    public event EventHandler? MiniPlayerToggled;

    public async Task TogglePlaybackAsync()
    {
        ExpectPlaybackToggle();
        await _engine.PressAsync("control-button-playpause");
        await Task.Delay(300);
        await RefreshStateAsync();
    }

    public SpotifyLibraryView()
    {
        InitializeComponent();
    }

    public bool ShowTopBarOption
    {
        set => MiniToggle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task ShowAsync()
    {
        if (!_shown)
            _engine.AddView();
        _shown = true;
        _timer.Interval = TimeSpan.FromSeconds(1);
        MiniToggle.IsChecked = App.Settings.Prop.SpotifyMiniPlayer;
        ShowSignIn(!App.Settings.Prop.SpotifySignedIn);
        if (_library.Count == 0 && App.Settings.Prop.SpotifySignedIn)
        {
            _library = await Task.Run(SpotifyEngine.ReadCachedLibrary);
            if (ItemsList.ItemsSource == null)
                ItemsList.ItemsSource = _library;
        }
        _timer.Tick -= Timer_Tick;
        _timer.Tick += Timer_Tick;
        _timer.Start();
        if (_engine.IsRunning)
        {
            await RefreshStateAsync();
            return;
        }
        if (_starting)
            return;
        _starting = true;
        Loading.Visibility = Visibility.Visible;
        bool started = await _engine.EnsureStartedAsync();
        _starting = false;
        if (!started)
        {
            Loading.Visibility = Visibility.Collapsed;
            Failed?.Invoke(this, EventArgs.Empty);
            return;
        }
        await LoadLibraryAsync();
    }

    public void Hide()
    {
        if (!_shown)
            return;
        _shown = false;
        _engine.RemoveView();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _ = Task.Run(CollectQuietly);
    }

    private static void CollectQuietly()
    {
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
    }

    public void Shutdown()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        Hide();
        if (!_engine.HasViews)
            _engine.Shutdown();
    }

    public void Detach()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        Hide();
    }

    private void ShowSignIn(bool show)
    {
        SignInButton.Content = Voidstrap.Resources.Strings.Accounts_SignInBrowser.Replace("Roblox", "Spotify");
        SignInButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadLibraryAsync()
    {
        Loading.Visibility = Visibility.Visible;
        List<SpotifyItem> loaded = await _engine.GetLibraryAsync();
        App.Logger.WriteLine("SpotifyLibraryView::LoadLibraryAsync", "Loaded " + loaded.Count + " library items");
        bool showingLibrary = ItemsList.ItemsSource == null || ReferenceEquals(ItemsList.ItemsSource, _library);
        if (loaded.Count > 0 || _library.Count == 0)
            _library = loaded;
        if (showingLibrary)
            ItemsList.ItemsSource = _library;
        Loading.Visibility = Visibility.Collapsed;
        await RefreshStateAsync();
        List<SpotifyItem> shown = _library;
        List<SpotifyItem> filled = await SpotifyEngine.FillImagesAsync(shown);
        if (ReferenceEquals(shown, _library))
        {
            bool showing = ReferenceEquals(ItemsList.ItemsSource, _library);
            _library = filled;
            if (showing)
                ItemsList.ItemsSource = _library;
            List<SpotifyItem> toSave = _library;
            if (toSave.Count > 0)
                _ = Task.Run(() => SpotifyEngine.SaveCachedLibrary(toSave));
        }
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_shown)
        {
            await RefreshStateAsync();
            if (_library.Count == 0 && !_loadingLibrary && !_starting && _engine.IsRunning && ++_retryTicks >= 10)
            {
                _retryTicks = 0;
                _loadingLibrary = true;
                await LoadLibraryAsync();
                _loadingLibrary = false;
            }
            return;
        }
        await RefreshStateAsync();
        if (!_engine.IsRunning)
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _ = Task.Run(CollectQuietly);
            StateChanged?.Invoke(this, new SpotifyState(App.Settings.Prop.SpotifySignedIn, "", "", null, false, "", "", 0, 0, false, "false", false));
        }
    }

    private async Task RefreshStateAsync()
    {
        SpotifyState? state = await _engine.GetStateAsync();
        if (state == null)
            return;
        DateTime now = DateTime.UtcNow;
        if (state.Position != _lastPosition)
        {
            if (_lastPosition != null && state.Title == _lastTitle)
                _lastMoved = now;
            _lastPosition = state.Position;
            _lastTitle = state.Title;
        }
        TimeSpan moveWindow = _shown ? TimeSpan.FromSeconds(2.5) : TimeSpan.FromSeconds(11);
        _isPlaying = now < _overrideUntil ? _overrideValue : state.Playing || now - _lastMoved < moveWindow;
        state = state with { Playing = _isPlaying };
        StateChanged?.Invoke(this, state);
        ShowSignIn(!state.SignedIn);
        if (App.Settings.Prop.SpotifySignedIn != state.SignedIn)
        {
            App.Settings.Prop.SpotifySignedIn = state.SignedIn;
            if (!state.SignedIn)
            {
                SpotifyEngine.SaveCachedLibrary(null);
                _library = new List<SpotifyItem>();
                ItemsList.ItemsSource = _library;
            }
            App.Settings.Save();
        }
        NowTitle.Text = state.Title;
        NowArtist.Text = state.Artist;
        PositionText.Text = state.Position;
        DurationText.Text = state.Duration;
        if (!SeekSlider.IsMouseCaptureWithin)
            SeekSlider.Value = state.Progress;
        if (!VolumeSlider.IsMouseCaptureWithin)
            VolumeSlider.Value = state.Volume;
        ShuffleButton.Appearance = state.Shuffle ? Wpf.Ui.Common.ControlAppearance.Primary : Wpf.Ui.Common.ControlAppearance.Transparent;
        RepeatButton.Appearance = state.Repeat == "false" ? Wpf.Ui.Common.ControlAppearance.Transparent : Wpf.Ui.Common.ControlAppearance.Primary;
        RepeatButton.Icon = state.Repeat == "mixed" ? SymbolRegular.ArrowRepeat124 : SymbolRegular.ArrowRepeatAll24;
        LikeButton.Appearance = state.Liked ? Wpf.Ui.Common.ControlAppearance.Primary : Wpf.Ui.Common.ControlAppearance.Transparent;
        PlayPause.Icon = state.Playing ? SymbolRegular.Pause24 : SymbolRegular.Play24;
        if (state.Image != _nowImage)
        {
            _nowImage = state.Image;
            NowImage.Source = Uri.TryCreate(state.Image, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps ? new BitmapImage(uri) : null;
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SpotifySignInDialog dialog = new();
        if (dialog.ShowOwnedDialog() != true)
            return;
        _engine.Reload();
        await Task.Delay(1500);
        await LoadLibraryAsync();
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(SearchBox.Text))
            return;
        Loading.Visibility = Visibility.Visible;
        _inTrackList = false;
        OpenHeader.Visibility = Visibility.Collapsed;
        ItemsList.ItemsSource = await _engine.SearchAsync(SearchBox.Text.Trim());
        Loading.Visibility = Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
            ItemsList.ItemsSource = _library;
    }

    private async void Item_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SpotifyItem item })
            return;
        if (_inTrackList)
        {
            await _engine.PlayRowAsync(item);
        }
        else if (item.Href.StartsWith("/track/", StringComparison.Ordinal))
        {
            await _engine.PlayAsync(item);
        }
        else
        {
            _opened = item;
            await ShowTracksAsync(item.Name, _engine.OpenAsync(item.Href), true);
        }
    }

    private async Task ShowTracksAsync(string title, Task<List<SpotifyItem>> loading, bool canPlayAll)
    {
        OpenTitle.Text = title;
        OpenHeader.Visibility = Visibility.Visible;
        PlayAllButton.Visibility = canPlayAll ? Visibility.Visible : Visibility.Collapsed;
        ItemsList.ItemsSource = null;
        Loading.Visibility = Visibility.Visible;
        ItemsList.ItemsSource = await loading;
        Loading.Visibility = Visibility.Collapsed;
        _inTrackList = true;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _inTrackList = false;
        _opened = null;
        OpenHeader.Visibility = Visibility.Collapsed;
        ItemsList.ItemsSource = _library;
    }

    private async void PlayAll_Click(object sender, RoutedEventArgs e)
    {
        if (_opened != null)
            await _engine.PlayAsync(_opened);
    }

    private async void Queue_Click(object sender, RoutedEventArgs e)
    {
        _opened = null;
        await ShowTracksAsync(Voidstrap.Resources.Strings.Ui_Spotify_Queue, _engine.GetQueueAsync(), false);
    }

    private void MiniToggle_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Prop.SpotifyMiniPlayer = MiniToggle.IsChecked == true;
        App.Settings.Save();
        MiniPlayerToggled?.Invoke(this, EventArgs.Empty);
    }

    private async void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        await Dispatcher.Yield(DispatcherPriority.Input);
        await _engine.SeekAsync(SeekSlider.Value);
    }

    private async void VolumeSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        await Dispatcher.Yield(DispatcherPriority.Input);
        await _engine.SetVolumeAsync(VolumeSlider.Value);
    }

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        await _engine.ToggleShuffleAsync();
        await Task.Delay(300);
        await RefreshStateAsync();
    }

    private async void Like_Click(object sender, RoutedEventArgs e)
    {
        await _engine.ToggleLikeAsync();
        await Task.Delay(300);
        await RefreshStateAsync();
    }

    private async void Control_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string control })
        {
            if (control == "control-button-playpause")
                ExpectPlaybackToggle();
            await _engine.PressAsync(control);
            await Task.Delay(300);
            await RefreshStateAsync();
        }
    }
}
