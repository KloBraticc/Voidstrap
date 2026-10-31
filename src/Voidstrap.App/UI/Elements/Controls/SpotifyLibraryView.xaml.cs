using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;
using Voidstrap.UI.Elements.Dialogs;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Elements.Controls;

public partial class SpotifyLibraryView : UserControl
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(3);

    private readonly SpotifyEngine _engine = new();

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    private List<SpotifyItem> _library = new();

    private bool _shown;

    private DateTime _silentSince = DateTime.UtcNow;

    private string? _nowImage;

    public event EventHandler? Failed;

    public SpotifyLibraryView()
    {
        InitializeComponent();
    }

    public async Task ShowAsync()
    {
        _shown = true;
        _engine.SetLowMemory(false);
        _timer.Tick -= Timer_Tick;
        _timer.Tick += Timer_Tick;
        _timer.Start();
        if (_engine.IsRunning)
        {
            await RefreshStateAsync();
            return;
        }
        Loading.Visibility = Visibility.Visible;
        IntPtr handle = new WindowInteropHelper(Window.GetWindow(this)).Handle;
        if (!await _engine.EnsureStartedAsync(handle))
        {
            Loading.Visibility = Visibility.Collapsed;
            Failed?.Invoke(this, EventArgs.Empty);
            return;
        }
        await LoadLibraryAsync();
    }

    public void Hide()
    {
        _shown = false;
        _silentSince = DateTime.UtcNow;
        _engine.SetLowMemory(true);
    }

    public void Shutdown()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _engine.Shutdown();
    }

    private async Task LoadLibraryAsync()
    {
        Loading.Visibility = Visibility.Visible;
        _library = await _engine.GetLibraryAsync();
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
            ItemsList.ItemsSource = _library;
        Loading.Visibility = Visibility.Collapsed;
        await RefreshStateAsync();
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_shown)
        {
            await RefreshStateAsync();
            return;
        }
        if (_engine.IsPlayingAudio)
        {
            _silentSince = DateTime.UtcNow;
            return;
        }
        if (DateTime.UtcNow - _silentSince >= IdleLimit)
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _engine.Shutdown();
        }
    }

    private async Task RefreshStateAsync()
    {
        SpotifyState? state = await _engine.GetStateAsync();
        if (state == null)
            return;
        SignInButton.Visibility = state.SignedIn ? Visibility.Collapsed : Visibility.Visible;
        NowTitle.Text = state.Title;
        NowArtist.Text = state.Artist;
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
        if (sender is FrameworkElement { Tag: SpotifyItem item })
            await _engine.PlayAsync(item);
    }

    private async void Control_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string control })
        {
            await _engine.PressAsync(control);
            await Task.Delay(300);
            await RefreshStateAsync();
        }
    }
}
