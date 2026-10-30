using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Voidstrap.Integrations.Keystrokes;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class KeystrokePreviewWindow : WpfUiWindow
{
    private readonly KeystrokeTheme _theme;
    private bool _isRunning;
    private KeystrokeScriptView? _scriptView;

    public KeystrokePreviewWindow(string? themeName = null)
    {
        InitializeComponent();
        _theme = KeystrokeThemeManager.GetTheme(themeName);
        ApplyKeystrokeTheme();
        if (!string.IsNullOrWhiteSpace(_theme.ScriptSource))
        {
            KeystrokeScriptView view = new();
            if (view.Load(_theme.ScriptSource, System.IO.Path.Combine(Paths.CustomKeystrokeThemes, _theme.Name)) == null && KeysContainer.Parent is Viewbox host)
            {
                host.Child = view;
                _scriptView = view;
            }
            else
            {
                view.Dispose();
            }
        }
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private void ApplyKeystrokeTheme()
    {
        var keyTuples = new (Border Key, Border Highlight, TextBlock Text, string Name)[]
        {
            (KeyW, HighlightW, TextW, "W"),
            (KeyA, HighlightA, TextA, "A"),
            (KeyS, HighlightS, TextS, "S"),
            (KeyD, HighlightD, TextD, "D"),
            (KeyLmb, HighlightLmb, TextLmb, "LMB"),
            (KeyRmb, HighlightRmb, TextRmb, "RMB"),
            (KeyShift, HighlightShift, TextShift, "Shift"),
            (KeySpace, HighlightSpace, TextSpace, "Space")
        };

        Brush borderBrush = _theme.GetKeyBorderBrush();
        Brush textBrush = _theme.GetKeyTextBrush();
        Brush cpsBrush = _theme.GetCpsTextBrush();
        CornerRadius cornerRadius = new(_theme.KeyCornerRadius);
        CornerRadius highlightCorner = new(Math.Max(0, _theme.KeyCornerRadius - 1));
        Thickness borderThickness = new(_theme.KeyBorderThickness);

        System.Windows.Media.FontFamily fontFamily = new(_theme.FontFamily);
        FontWeight fontWeight = _theme.FontWeight switch
        {
            "Normal" => FontWeights.Normal,
            "Medium" => FontWeights.Medium,
            "Bold" => FontWeights.Bold,
            "ExtraBold" => FontWeights.ExtraBold,
            _ => FontWeights.SemiBold
        };

        foreach (var item in keyTuples)
        {
            item.Key.Background = _theme.GetKeyBrush(item.Name, false);
            item.Key.BorderBrush = borderBrush;
            item.Key.BorderThickness = borderThickness;
            item.Key.CornerRadius = cornerRadius;
            item.Key.Effect = null;

            item.Highlight.Background = _theme.GetKeyBrush(item.Name, true);
            item.Highlight.CornerRadius = highlightCorner;

            item.Text.Foreground = textBrush;
            item.Text.FontFamily = fontFamily;
            item.Text.FontWeight = fontWeight;

            var cap = _theme.Keycaps.TryGetValue(item.Name, out var c) ? c : null;

            double defaultWidth = item.Name switch
            {
                "LMB" or "RMB" => 71,
                "Shift" => 51,
                "Space" => 91,
                _ => 46
            };
            double defaultHeight = item.Name switch
            {
                "Shift" or "Space" => 34,
                _ => 46
            };
            string defaultText = item.Name switch
            {
                "Shift" => "SHIFT",
                "Space" => "—",
                _ => item.Name
            };

            item.Key.Width = (cap?.Width.HasValue == true && cap.Width.Value > 0) ? cap.Width.Value : defaultWidth;
            item.Key.Height = (cap?.Height.HasValue == true && cap.Height.Value > 0) ? cap.Height.Value : defaultHeight;

            if (cap?.CornerRadius.HasValue == true)
            {
                item.Key.CornerRadius = new CornerRadius(cap.CornerRadius.Value);
                item.Highlight.CornerRadius = new CornerRadius(Math.Max(0, cap.CornerRadius.Value - 1));
            }
            else
            {
                item.Key.CornerRadius = cornerRadius;
                item.Highlight.CornerRadius = highlightCorner;
            }

            item.Text.Text = !string.IsNullOrEmpty(cap?.Text) ? cap.Text : defaultText;
            item.Text.Visibility = (cap?.HideText == true) ? Visibility.Collapsed : Visibility.Visible;
        }

        TextCpsLmb.Foreground = cpsBrush;
        TextCpsLmb.FontFamily = fontFamily;
        TextCpsRmb.Foreground = cpsBrush;
        TextCpsRmb.FontFamily = fontFamily;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _isRunning = true;
        _ = RunLoopAsync();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isRunning = false;
        _scriptView?.Dispose();
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

    private void SetKeyPressed(string tag, bool isDown)
    {
        if (_scriptView != null)
        {
            _scriptView.SetKey(tag, isDown);
            return;
        }

        (Border? key, Border? highlight, TextBlock? text, TextBlock? cps) = tag switch
        {
            "W" => (KeyW, HighlightW, TextW, null),
            "A" => (KeyA, HighlightA, TextA, null),
            "S" => (KeyS, HighlightS, TextS, null),
            "D" => (KeyD, HighlightD, TextD, null),
            "LMB" => (KeyLmb, HighlightLmb, TextLmb, TextCpsLmb),
            "RMB" => (KeyRmb, HighlightRmb, TextRmb, TextCpsRmb),
            "Shift" => (KeyShift, HighlightShift, TextShift, null),
            "Space" => (KeySpace, HighlightSpace, TextSpace, null),
            _ => (null, null, null, null)
        };

        if (highlight == null || text == null || key == null)
            return;

        highlight.BeginAnimation(UIElement.OpacityProperty, null);
        if (_theme.Keycaps.TryGetValue(tag, out var cap))
        {
            if (!string.IsNullOrEmpty(cap.Text))
                text.Text = cap.Text;
            text.Visibility = cap.HideText ? Visibility.Collapsed : Visibility.Visible;
        }

        if (isDown)
        {
            highlight.Opacity = 1.0;
            highlight.Background = _theme.GetKeyBrush(tag, true);
            text.Foreground = _theme.GetKeyPressedTextBrush();
            if (cps != null)
                cps.Foreground = _theme.GetKeyPressedCpsTextBrush();

            if (_theme.KeyScaleOnPress < 0.999)
            {
                key.RenderTransformOrigin = new Point(0.5, 0.5);
                key.RenderTransform = new ScaleTransform(_theme.KeyScaleOnPress, _theme.KeyScaleOnPress);
            }
        }
        else
        {
            var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120));
            highlight.BeginAnimation(UIElement.OpacityProperty, anim);
            key.Background = _theme.GetKeyBrush(tag, false);
            text.Foreground = _theme.GetKeyTextBrush();
            if (cps != null)
                cps.Foreground = _theme.GetCpsTextBrush();
            key.RenderTransform = Transform.Identity;
        }
    }

    private void OnKeyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag)
        {
            SetKeyPressed(tag, true);
            if (ToggleSound.IsChecked == true)
            {
                if (tag is "LMB" or "RMB")
                    KeystrokeAudioService.PlayMousePress(force: true);
                else
                    KeystrokeAudioService.PlayKeyboardPress(force: true);
            }
        }
    }

    private void OnKeyMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag)
        {
            SetKeyPressed(tag, false);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        _isRunning = false;
        Close();
    }
}
