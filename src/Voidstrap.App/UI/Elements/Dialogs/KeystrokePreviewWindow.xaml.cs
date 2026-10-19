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

    public KeystrokePreviewWindow(string? themeName = null)
    {
        InitializeComponent();
        _theme = KeystrokeThemeManager.GetTheme(themeName);
        ApplyKeystrokeTheme();
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private void ApplyKeystrokeTheme()
    {
        var borders = new[]
        {
            KeyW, KeyA, KeyS, KeyD,
            KeyLmb, KeyRmb, KeyShift, KeySpace
        };

        var highlights = new[]
        {
            HighlightW, HighlightA, HighlightS, HighlightD,
            HighlightLmb, HighlightRmb, HighlightShift, HighlightSpace
        };

        var texts = new[]
        {
            TextW, TextA, TextS, TextD,
            TextLmb, TextRmb, TextShift, TextSpace
        };

        Brush bgBrush = _theme.GetKeyBackgroundBrush();
        Brush borderBrush = _theme.GetKeyBorderBrush();
        Brush pressedBgBrush = _theme.GetKeyPressedBackgroundBrush();
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

        DropShadowEffect? glow = null;
        if (_theme.KeyGlowRadius > 0 && _theme.KeyGlowOpacity > 0)
        {
            glow = new DropShadowEffect
            {
                Color = _theme.GetGlowColor(),
                BlurRadius = _theme.KeyGlowRadius,
                Opacity = _theme.KeyGlowOpacity,
                ShadowDepth = 0
            };
        }

        foreach (var b in borders)
        {
            b.Background = bgBrush;
            b.BorderBrush = borderBrush;
            b.BorderThickness = borderThickness;
            b.CornerRadius = cornerRadius;
            b.Effect = glow;
        }

        foreach (var h in highlights)
        {
            h.Background = pressedBgBrush;
            h.CornerRadius = highlightCorner;
        }

        foreach (var t in texts)
        {
            t.Foreground = textBrush;
            t.FontFamily = fontFamily;
            t.FontWeight = fontWeight;
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
        if (isDown)
        {
            highlight.Opacity = 1.0;
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
