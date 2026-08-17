using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voidstrap.UI.ViewModels.Pages;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public partial class HomePage : Page
    {
        private readonly HomePageViewModel _viewModel;
        private CancellationTokenSource? _lifetimeCts;
        private int _avatarGeneration;
        public HomePage()
        {
            _viewModel = new HomePageViewModel();
            DataContext = _viewModel;
            InitializeComponent();
            Loaded += OnHomePageLoaded;
            Unloaded += OnHomePageUnloaded;
        }

        private void OnHomePageLoaded(object sender, RoutedEventArgs e)
        {
            _lifetimeCts?.Cancel();
            _lifetimeCts?.Dispose();
            _lifetimeCts = new CancellationTokenSource();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.StartAutoRefresh();
            _ = _viewModel.LoadAsync();
        }

        private void OnHomePageUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Interlocked.Increment(ref _avatarGeneration);
                _lifetimeCts?.Cancel();
                _lifetimeCts?.Dispose();
                _lifetimeCts = null;
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.StopAutoRefresh();
            }
            catch { }
        }

        private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            try
            {
                if (e.PropertyName == nameof(HomePageViewModel.WebsiteAvatarUrl))
                {
                    if (!_viewModel.HasWebsiteAvatar || string.IsNullOrEmpty(_viewModel.WebsiteAvatarUrl))
                    {
                        Interlocked.Increment(ref _avatarGeneration);
                        if (WebsiteAvatarBrush is not null)
                            WebsiteAvatarBrush.ImageSource = null;
                        return;
                    }
                    CancellationTokenSource? lifetimeCts = _lifetimeCts;
                    if (lifetimeCts == null)
                        return;
                    int generation = Interlocked.Increment(ref _avatarGeneration);
                    string imageUrl = _viewModel.WebsiteAvatarUrl;
                    var bitmap = await Voidstrap.Utility.AppImage.LoadAsync(imageUrl, 256, lifetimeCts.Token).ConfigureAwait(false);
                    if (bitmap == null || generation != Volatile.Read(ref _avatarGeneration) || lifetimeCts.IsCancellationRequested)
                        return;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (generation == Volatile.Read(ref _avatarGeneration) && !lifetimeCts.IsCancellationRequested && WebsiteAvatarBrush is not null)
                            WebsiteAvatarBrush.ImageSource = bitmap;
                    });
                }
                else if (e.PropertyName == nameof(HomePageViewModel.WebsiteBorderBrush))
                {
                    if (AvatarBorderRing is not null)
                    {
                        if (_viewModel.WebsiteBorderBrush is null)
                            AvatarBorderRing.ClearValue(Border.BackgroundProperty);
                        else
                            AvatarBorderRing.Background = _viewModel.WebsiteBorderBrush;
                    }
                }
            }
            catch { }
        }

        private void GameThumbnail_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not HistoryGameEntry entry)
                return;

            if (entry.PlaceId == 0)
                return;

            e.Handled = true;
            NavigationService?.Navigate(new GamePage(entry.PlaceId, entry.UniverseId));
        }

        private void UnderratedThumbnail_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not UnderratedGameEntry entry)
                return;

            if (entry.PlaceId == 0)
                return;

            e.Handled = true;
            NavigationService?.Navigate(new GamePage(entry.PlaceId, entry.UniverseId));
        }

        private void AccountsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.ContextMenu != null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.IsOpen = true;
            }
        }

        private void EditProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new Voidstrap.UI.Elements.ContextMenu.ProfileEditorWindow
                {
                    Owner = Window.GetWindow(this)
                };
                win.ShowDialog();
                if (win.Saved)
                    _viewModel.RefreshCommand.Execute(null);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePage::EditProfile", ex);
            }
        }
    }
}
