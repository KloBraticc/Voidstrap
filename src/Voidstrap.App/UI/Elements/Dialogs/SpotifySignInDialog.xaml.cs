using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Voidstrap.Integrations;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class SpotifySignInDialog : WpfUiWindow
{
    private const string LoginUrl = "https://accounts.spotify.com/login?continue=https%3A%2F%2Fopen.spotify.com%2F";

    private WebView2? _view;

    public SpotifySignInDialog()
    {
        InitializeComponent();
        Loaded += Dialog_Loaded;
        Closed += Dialog_Closed;
    }

    private async void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, SpotifyEngine.UserDataFolder, new CoreWebView2EnvironmentOptions(SpotifyEngine.Arguments));
            _view = new WebView2();
            Host.Content = _view;
            await _view.EnsureCoreWebView2Async(environment);
            _view.CoreWebView2!.Settings.AreDevToolsEnabled = false;
            _view.CoreWebView2.NewWindowRequested += Core_NewWindowRequested;
            _view.CoreWebView2.SourceChanged += Core_SourceChanged;
            _view.CoreWebView2.Navigate(LoginUrl);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SpotifySignInDialog::Dialog_Loaded", "The sign in window could not start: " + ex.GetType().Name);
            Close();
        }
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (sender is CoreWebView2 core && e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            core.Navigate(e.Uri);
    }

    private void Core_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (sender is CoreWebView2 core && core.Source.StartsWith("https://open.spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            DialogResult = true;
        }
    }

    private void Dialog_Closed(object? sender, EventArgs e)
    {
        Loaded -= Dialog_Loaded;
        Closed -= Dialog_Closed;
        if (_view?.CoreWebView2 is { } core)
        {
            core.NewWindowRequested -= Core_NewWindowRequested;
            core.SourceChanged -= Core_SourceChanged;
        }
        _view?.Dispose();
    }
}
