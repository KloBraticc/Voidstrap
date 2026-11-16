using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voidstrap.Integrations.Keystrokes;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class KeystrokePreviewWindow : WpfUiWindow
{
    private readonly KeystrokeXmlView _view = new();
    private bool _isRunning;

    public KeystrokePreviewWindow(string? themeName = null)
    {
        InitializeComponent();
        KeystrokeTheme theme = KeystrokeThemeManager.GetTheme(themeName);
        string directory = System.IO.Path.Combine(Paths.CustomKeystrokeThemes, theme.Name);
        if (_view.Load(theme.GetXml(), directory) != null)
            _view.Load(KeystrokeXmlTemplates.Classic, directory);
        PreviewHost.Child = _view;
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _isRunning = true;
        _ = RunLoopAsync();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isRunning = false;
        Loaded -= OnWindowLoaded;
        Closed -= OnWindowClosed;
        _view.Dispose();
    }

    private async Task RunLoopAsync()
    {
        string[] keys = { "W", "A", "S", "D", "LMB", "RMB", "Shift", "Space" };

        while (_isRunning)
        {
            if (ToggleLoop.IsChecked == true)
            {
                foreach (string k in keys)
                {
                    if (!_isRunning)
                        return;

                    SetKeyPressed(k, true);
                    if (ToggleSound.IsChecked == true)
                    {
                        if (k is "LMB" or "RMB")
                            KeystrokeAudioService.PlayMousePress(force: true);
                        else
                            KeystrokeAudioService.PlayKeyboardPress(force: true);
                    }

                    await Task.Delay(140);
                    if (!_isRunning)
                        return;

                    SetKeyPressed(k, false);
                    await Task.Delay(70);
                }

                await Task.Delay(400);
            }
            else
            {
                await Task.Delay(150);
            }
        }
    }

    private void SetKeyPressed(string tag, bool isDown) => _view.SetKey(tag, isDown);

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        _isRunning = false;
        Close();
    }
}
