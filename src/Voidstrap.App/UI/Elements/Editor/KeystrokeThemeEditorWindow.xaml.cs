using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Voidstrap.Integrations.Keystrokes;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Editor;

public partial class KeystrokeThemeEditorWindow : WpfUiWindow
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _themeName;
    private readonly KeystrokeTheme _originalTheme;
    private readonly KeystrokeTheme _workingTheme;
    private bool _isInitializing = true;
    private bool _isUpdatingJson;
    private bool _sequenceRunning;

    public KeystrokeThemeEditorWindow(string themeName)
    {
        InitializeComponent();

        _themeName = themeName;
        _originalTheme = KeystrokeThemeManager.GetTheme(themeName);
        _workingTheme = _originalTheme.Clone(themeName);

        Title = $"{Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_Title} - {themeName}";
        TitleBarControl.Title = Title;

        InitializeDropdowns();
        InitializeSnippets();
        LoadThemeIntoControls();

        JsonEditor.TextChanged += OnJsonEditorTextChanged;
        JsonEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

        _isInitializing = false;
        RefreshLivePreview();
    }

    private void InitializeDropdowns()
    {
        ComboFontFamily.ItemsSource = new List<string>
        {
            "Segoe UI",
            "Cascadia Mono",
            "Consolas",
            "Bahnschrift",
            "Arial",
            "Trebuchet MS",
            "Verdana"
        };

        ComboFontWeight.ItemsSource = new List<string>
        {
            "Normal",
            "Medium",
            "SemiBold",
            "Bold",
            "ExtraBold"
        };

        ComboImageStretch.ItemsSource = new List<string>
        {
            "UniformToFill",
            "Uniform",
            "Fill",
            "None"
        };
    }

    private void InitializeSnippets()
    {
        ComboSnippets.ItemsSource = new List<string>
        {
            Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_InsertSnippet ?? "Insert Snippet...",
            "Background Images",
            "Per-Key Images",
            "Per-Key Pressed Images",
            "Glass & Squash Scale",
            "Accent Palette"
        };
        ComboSnippets.SelectedIndex = 0;
    }

    private void LoadThemeIntoControls()
    {
        UpdateButtonColor(BtnKeyBg, _workingTheme.KeyBackground);
        UpdateButtonColor(BtnKeyPressedBg, _workingTheme.KeyPressedBackground);
        UpdateButtonColor(BtnKeyText, _workingTheme.KeyTextColor);
        UpdateButtonColor(BtnKeyPressedText, _workingTheme.KeyPressedTextColor);
        UpdateButtonColor(BtnCpsText, _workingTheme.CpsTextColor);
        UpdateButtonColor(BtnKeyPressedCpsText, _workingTheme.KeyPressedCpsTextColor);
        UpdateButtonColor(BtnBorderColor, _workingTheme.KeyBorderColor);

        SliderBorderThickness.Value = _workingTheme.KeyBorderThickness;
        TextBorderThicknessVal.Text = $"{_workingTheme.KeyBorderThickness:F1}";

        SliderCornerRadius.Value = _workingTheme.KeyCornerRadius;
        TextCornerRadiusVal.Text = $"{_workingTheme.KeyCornerRadius:F0}";

        ToggleGlass.IsChecked = _workingTheme.BackdropBlur;

        SliderPressScale.Value = Math.Round(_workingTheme.KeyScaleOnPress * 100);
        TextPressScaleVal.Text = $"{SliderPressScale.Value:F0}%";

        ComboFontFamily.SelectedItem = _workingTheme.FontFamily;
        if (ComboFontFamily.SelectedIndex < 0)
            ComboFontFamily.SelectedIndex = 0;

        ComboFontWeight.SelectedItem = _workingTheme.FontWeight;
        if (ComboFontWeight.SelectedIndex < 0)
            ComboFontWeight.SelectedIndex = 2;

        TextKeyBgImage.Text = _workingTheme.KeyBackgroundImage ?? string.Empty;
        TextKeyPressedBgImage.Text = _workingTheme.KeyPressedBackgroundImage ?? string.Empty;

        ComboImageStretch.SelectedItem = _workingTheme.KeyImageStretch;
        if (ComboImageStretch.SelectedIndex < 0)
            ComboImageStretch.SelectedIndex = 0;

        SliderImageOpacity.Value = Math.Round(_workingTheme.KeyImageOpacity * 100);
        TextImageOpacityVal.Text = $"{SliderImageOpacity.Value:F0}%";

        UpdateJsonView();
    }

    private static void UpdateButtonColor(Button btn, string hex)
    {
        bool isAccent = string.Equals(hex?.Trim(), "Accent", StringComparison.OrdinalIgnoreCase);
        Color color = KeystrokeTheme.ParseColor(hex, Colors.Transparent);
        btn.Background = new SolidColorBrush(color);
        btn.Content = isAccent ? (Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_AccentLabel ?? "ACCENT") : hex?.ToUpperInvariant();
        double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        btn.Foreground = luminance > 0.55 ? Brushes.Black : Brushes.White;
    }

    private void UpdateJsonView()
    {
        _isUpdatingJson = true;
        try
        {
            JsonEditor.Text = JsonSerializer.Serialize(_workingTheme, JsonOptions);
            TextJsonStatus.Text = Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_JsonValid ?? "Valid JSON code";
            TextJsonStatus.Foreground = Brushes.LightGreen;
        }
        finally
        {
            _isUpdatingJson = false;
        }
    }

    private void OnJsonEditorTextChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingJson)
            return;

        try
        {
            var parsed = JsonSerializer.Deserialize<KeystrokeTheme>(JsonEditor.Text);
            if (parsed != null)
            {
                TextJsonStatus.Text = Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_JsonValid ?? "Valid JSON code";
                TextJsonStatus.Foreground = Brushes.LightGreen;
            }
        }
        catch (Exception ex)
        {
            TextJsonStatus.Text = $"{Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_JsonInvalid ?? "Syntax error"}: {ex.Message}";
            TextJsonStatus.Foreground = Brushes.IndianRed;
        }
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        var caret = JsonEditor.TextArea.Caret;
        TextEditorStats.Text = $"Ln {caret.Line}, Col {caret.Column}";
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || e.Source != EditorTabs)
            return;

        if (EditorTabs.SelectedIndex == 1)
        {
            UpdateJsonView();
        }
        else if (EditorTabs.SelectedIndex == 0)
        {
            UpdateFromCurrentJson();
        }
    }

    private bool UpdateFromCurrentJson()
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<KeystrokeTheme>(JsonEditor.Text);
            if (parsed != null)
            {
                parsed.Name = _themeName;
                _workingTheme.KeyBackground = parsed.KeyBackground;
                _workingTheme.KeyPressedBackground = parsed.KeyPressedBackground;
                _workingTheme.KeyBorderColor = parsed.KeyBorderColor;
                _workingTheme.KeyBorderThickness = parsed.KeyBorderThickness;
                _workingTheme.KeyCornerRadius = parsed.KeyCornerRadius;
                _workingTheme.KeyTextColor = parsed.KeyTextColor;
                _workingTheme.KeyPressedTextColor = parsed.KeyPressedTextColor;
                _workingTheme.CpsTextColor = parsed.CpsTextColor;
                _workingTheme.KeyPressedCpsTextColor = parsed.KeyPressedCpsTextColor;
                _workingTheme.BackdropBlur = parsed.BackdropBlur;
                _workingTheme.KeyScaleOnPress = parsed.KeyScaleOnPress;
                _workingTheme.FontFamily = parsed.FontFamily;
                _workingTheme.FontWeight = parsed.FontWeight;
                _workingTheme.KeyBackgroundImage = parsed.KeyBackgroundImage;
                _workingTheme.KeyPressedBackgroundImage = parsed.KeyPressedBackgroundImage;
                _workingTheme.OverlayBackgroundImage = parsed.OverlayBackgroundImage;
                _workingTheme.KeyImages = parsed.KeyImages != null ? new Dictionary<string, string>(parsed.KeyImages, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
                _workingTheme.KeyPressedImages = parsed.KeyPressedImages != null ? new Dictionary<string, string>(parsed.KeyPressedImages, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
                _workingTheme.KeyImageStretch = parsed.KeyImageStretch;
                _workingTheme.KeyImageOpacity = parsed.KeyImageOpacity;

                _isInitializing = true;
                LoadThemeIntoControls();
                _isInitializing = false;
                RefreshLivePreview();

                TextJsonStatus.Text = Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_JsonValid ?? "Valid JSON code";
                TextJsonStatus.Foreground = Brushes.LightGreen;
                return true;
            }
        }
        catch (Exception ex)
        {
            TextJsonStatus.Text = $"{Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_JsonInvalid ?? "Syntax error"}: {ex.Message}";
            TextJsonStatus.Foreground = Brushes.IndianRed;
        }

        return false;
    }

    private void PickColor(string title, string currentHex, Action<string> onColorPicked, Button triggerBtn, bool allowAccent = false)
    {
        Color initial = KeystrokeTheme.ParseColor(currentHex, Colors.White);
        var dialog = new RinColorPickerDialog(initial, alphaEnabled: true, title: title, allowAccent: allowAccent)
        {
            Owner = this
        };

        dialog.ShowOwnedDialog();
        if (dialog.DialogResult == true)
        {
            string hex = dialog.UseAccent ? "Accent" : $"#{dialog.SelectedColor.A:X2}{dialog.SelectedColor.R:X2}{dialog.SelectedColor.G:X2}{dialog.SelectedColor.B:X2}";
            onColorPicked(hex);
            UpdateButtonColor(triggerBtn, hex);
            UpdateJsonView();
            RefreshLivePreview();
        }
    }

    private void OnPickKeyBgClicked(object sender, RoutedEventArgs e)
    {
        PickColor(Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_KeyBg, _workingTheme.KeyBackground, hex => _workingTheme.KeyBackground = hex, BtnKeyBg);
    }

    private void OnPickKeyPressedBgClicked(object sender, RoutedEventArgs e)
    {
        PickColor(Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_KeyPressedBg, _workingTheme.KeyPressedBackground, hex => _workingTheme.KeyPressedBackground = hex, BtnKeyPressedBg, allowAccent: true);
    }

    private void OnPickKeyTextClicked(object sender, RoutedEventArgs e)
    {
        PickColor(Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_KeyText, _workingTheme.KeyTextColor, hex => _workingTheme.KeyTextColor = hex, BtnKeyText);
    }

    private void OnPickKeyPressedTextClicked(object sender, RoutedEventArgs e)
    {
        PickColor(Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_KeyPressedText, _workingTheme.KeyPressedTextColor, hex => _workingTheme.KeyPressedTextColor = hex, BtnKeyPressedText);
    }

    private void OnPickCpsTextClicked(object sender, RoutedEventArgs e)
    {
        PickColor(Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_CpsText, _workingTheme.CpsTextColor, hex => _workingTheme.CpsTextColor = hex, BtnCpsText);
    }

    private void OnPickKeyPressedCpsTextClicked(object sender, RoutedEventArgs e)
    {
        PickColor(Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_KeyPressedCpsText, _workingTheme.KeyPressedCpsTextColor, hex => _workingTheme.KeyPressedCpsTextColor = hex, BtnKeyPressedCpsText);
    }

    private void OnPickBorderColorClicked(object sender, RoutedEventArgs e)
    {
        PickColor(Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_BorderColor, _workingTheme.KeyBorderColor, hex => _workingTheme.KeyBorderColor = hex, BtnBorderColor);
    }

    private void OnGeometryChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing)
            return;

        _workingTheme.KeyBorderThickness = SliderBorderThickness.Value;
        TextBorderThicknessVal.Text = $"{SliderBorderThickness.Value:F1}";

        _workingTheme.KeyCornerRadius = SliderCornerRadius.Value;
        TextCornerRadiusVal.Text = $"{SliderCornerRadius.Value:F0}";

        _workingTheme.KeyScaleOnPress = SliderPressScale.Value / 100.0;
        TextPressScaleVal.Text = $"{SliderPressScale.Value:F0}%";

        UpdateJsonView();
        RefreshLivePreview();
    }

    private void OnToggleGlassChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _workingTheme.BackdropBlur = ToggleGlass.IsChecked == true;
        UpdateJsonView();
        RefreshLivePreview();
    }

    private void OnFontSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        if (ComboFontFamily.SelectedItem is string ff)
            _workingTheme.FontFamily = ff;

        if (ComboFontWeight.SelectedItem is string fw)
            _workingTheme.FontWeight = fw;

        UpdateJsonView();
        RefreshLivePreview();
    }

    private string? ImportImageFile(string? sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            return null;

        string targetDir = Path.Combine(Paths.CustomKeystrokeThemes, _themeName);
        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        string fileName = Path.GetFileName(sourceFilePath);
        string destPath = Path.Combine(targetDir, fileName);

        if (!string.Equals(Path.GetFullPath(sourceFilePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceFilePath, destPath, true);
        }

        return fileName;
    }

    private string? PromptPickImage()
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image Files (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files (*.*)|*.*",
            Title = Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_CustomImages ?? "Custom Images"
        };

        if (ofd.ShowDialog() == true)
        {
            return ImportImageFile(ofd.FileName);
        }

        return null;
    }

    private void OnBrowseKeyBgImageClicked(object sender, RoutedEventArgs e)
    {
        string? imported = PromptPickImage();
        if (imported != null)
        {
            _workingTheme.KeyBackgroundImage = imported;
            TextKeyBgImage.Text = imported;
            UpdateJsonView();
            RefreshLivePreview();
        }
    }

    private void OnClearKeyBgImageClicked(object sender, RoutedEventArgs e)
    {
        _workingTheme.KeyBackgroundImage = null;
        TextKeyBgImage.Text = string.Empty;
        UpdateJsonView();
        RefreshLivePreview();
    }

    private void OnBrowseKeyPressedBgImageClicked(object sender, RoutedEventArgs e)
    {
        string? imported = PromptPickImage();
        if (imported != null)
        {
            _workingTheme.KeyPressedBackgroundImage = imported;
            TextKeyPressedBgImage.Text = imported;
            UpdateJsonView();
            RefreshLivePreview();
        }
    }

    private void OnClearKeyPressedBgImageClicked(object sender, RoutedEventArgs e)
    {
        _workingTheme.KeyPressedBackgroundImage = null;
        TextKeyPressedBgImage.Text = string.Empty;
        UpdateJsonView();
        RefreshLivePreview();
    }

    private void OnImageStretchChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        if (ComboImageStretch.SelectedItem is string stretch)
        {
            _workingTheme.KeyImageStretch = stretch;
            UpdateJsonView();
            RefreshLivePreview();
        }
    }

    private void OnImageOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing)
            return;

        _workingTheme.KeyImageOpacity = SliderImageOpacity.Value / 100.0;
        TextImageOpacityVal.Text = $"{SliderImageOpacity.Value:F0}%";
        UpdateJsonView();
        RefreshLivePreview();
    }

    private void OnFormatJsonClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonEditor.Text);
            JsonEditor.Text = JsonSerializer.Serialize(doc.RootElement, JsonOptions);
            TextJsonStatus.Text = Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_JsonValid ?? "Valid JSON code";
            TextJsonStatus.Foreground = Brushes.LightGreen;
        }
        catch (Exception ex)
        {
            TextJsonStatus.Text = $"{Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_JsonInvalid ?? "Syntax error"}: {ex.Message}";
            TextJsonStatus.Foreground = Brushes.IndianRed;
        }
    }

    private void OnAddImageClicked(object sender, RoutedEventArgs e)
    {
        string? imported = PromptPickImage();
        if (imported != null)
        {
            JsonEditor.Document.Insert(JsonEditor.CaretOffset, $"\"{imported}\"");
            UpdateFromCurrentJson();
        }
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        string targetDir = Path.Combine(Paths.CustomKeystrokeThemes, _themeName);
        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);
        Utilities.ShellExecute(targetDir);
    }

    private void OnSnippetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || ComboSnippets.SelectedIndex <= 0)
            return;

        string snippet = ComboSnippets.SelectedIndex switch
        {
            1 => """
  "KeyBackgroundImage": "key_bg.png",
  "KeyPressedBackgroundImage": "key_pressed.png",
  "KeyImageStretch": "UniformToFill",
  "KeyImageOpacity": 1.0,
""",
            2 => """
  "KeyImages": {
    "W": "w.png",
    "A": "a.png",
    "S": "s.png",
    "D": "d.png",
    "LMB": "mouse_left.png",
    "RMB": "mouse_right.png",
    "Space": "space.png"
  },
""",
            3 => """
  "KeyPressedImages": {
    "W": "w_pressed.png",
    "A": "a_pressed.png",
    "S": "s_pressed.png",
    "D": "d_pressed.png",
    "LMB": "mouse_left_pressed.png",
    "RMB": "mouse_right_pressed.png"
  },
""",
            4 => """
  "BackdropBlur": true,
  "KeyScaleOnPress": 0.94,
  "KeyCornerRadius": 10.0,
  "KeyBorderThickness": 1.5,
""",
            5 => """
  "KeyBackground": "#28181A1F",
  "KeyPressedBackground": "Accent",
  "KeyTextColor": "#E6FFFFFF",
  "KeyPressedTextColor": "#FFFFFF",
  "KeyBorderColor": "Accent",
""",
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(snippet))
        {
            JsonEditor.Document.Insert(JsonEditor.CaretOffset, snippet);
        }

        ComboSnippets.SelectedIndex = 0;
    }

    private void OnApplyJsonClicked(object sender, RoutedEventArgs e)
    {
        if (UpdateFromCurrentJson())
        {
            MessageBox.Show(Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_JsonValid ?? "Theme updated from code.", "Voidstrap", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(TextJsonStatus.Text, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshLivePreview()
    {
        var borders = new[]
        {
            PreviewKeyW, PreviewKeyA, PreviewKeyS, PreviewKeyD,
            PreviewKeyLmb, PreviewKeyRmb, PreviewKeyShift, PreviewKeySpace
        };

        var highlights = new[]
        {
            PreviewHighlightW, PreviewHighlightA, PreviewHighlightS, PreviewHighlightD,
            PreviewHighlightLmb, PreviewHighlightRmb, PreviewHighlightShift, PreviewHighlightSpace
        };

        var texts = new[]
        {
            PreviewTextW, PreviewTextA, PreviewTextS, PreviewTextD,
            PreviewTextLmb, PreviewTextRmb, PreviewTextShift, PreviewTextSpace
        };

        Brush borderBrush = _workingTheme.GetKeyBorderBrush();
        Brush pressedBgBrush = _workingTheme.GetKeyPressedBackgroundBrush();
        Brush textBrush = _workingTheme.GetKeyTextBrush();
        Brush cpsBrush = _workingTheme.GetCpsTextBrush();
        CornerRadius cornerRadius = new(_workingTheme.KeyCornerRadius);
        CornerRadius highlightCorner = new(Math.Max(0, _workingTheme.KeyCornerRadius - 1));
        Thickness borderThickness = new(_workingTheme.KeyBorderThickness);

        System.Windows.Media.FontFamily fontFamily = new(_workingTheme.FontFamily);
        FontWeight fontWeight = _workingTheme.FontWeight switch
        {
            "Normal" => FontWeights.Normal,
            "Medium" => FontWeights.Medium,
            "Bold" => FontWeights.Bold,
            "ExtraBold" => FontWeights.ExtraBold,
            _ => FontWeights.SemiBold
        };

        foreach (var b in borders)
        {
            string tag = b.Tag as string ?? string.Empty;
            b.Background = _workingTheme.GetKeyBrush(tag, false);
            b.BorderBrush = borderBrush;
            b.BorderThickness = borderThickness;
            b.CornerRadius = cornerRadius;
            b.Effect = null;
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

        PreviewCpsLmb.Foreground = cpsBrush;
        PreviewCpsLmb.FontFamily = fontFamily;
        PreviewCpsRmb.Foreground = cpsBrush;
        PreviewCpsRmb.FontFamily = fontFamily;
    }

    private void OnKeyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag)
        {
            SetKeyPressed(tag, true);
            if (tag is "LMB" or "RMB")
                KeystrokeAudioService.PlayMousePress();
            else
                KeystrokeAudioService.PlayKeyboardPress();
        }
    }

    private void OnKeyMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag)
        {
            SetKeyPressed(tag, false);
        }
    }

    private void SetKeyPressed(string tag, bool isDown)
    {
        (Border? key, Border? highlight, TextBlock? text, TextBlock? cps) = tag switch
        {
            "W" => (PreviewKeyW, PreviewHighlightW, PreviewTextW, null),
            "A" => (PreviewKeyA, PreviewHighlightA, PreviewTextA, null),
            "S" => (PreviewKeyS, PreviewHighlightS, PreviewTextS, null),
            "D" => (PreviewKeyD, PreviewHighlightD, PreviewTextD, null),
            "LMB" => (PreviewKeyLmb, PreviewHighlightLmb, PreviewTextLmb, PreviewCpsLmb),
            "RMB" => (PreviewKeyRmb, PreviewHighlightRmb, PreviewTextRmb, PreviewCpsRmb),
            "Shift" => (PreviewKeyShift, PreviewHighlightShift, PreviewTextShift, null),
            "Space" => (PreviewKeySpace, PreviewHighlightSpace, PreviewTextSpace, null),
            _ => (null, null, null, null)
        };

        if (highlight == null || text == null || key == null)
            return;

        highlight.BeginAnimation(UIElement.OpacityProperty, null);
        if (isDown)
        {
            key.Background = _workingTheme.GetKeyBrush(tag, true);
            highlight.Opacity = 1.0;
            text.Foreground = _workingTheme.GetKeyPressedTextBrush();
            if (cps != null)
                cps.Foreground = _workingTheme.GetKeyPressedCpsTextBrush();

            if (_workingTheme.KeyScaleOnPress < 0.999)
            {
                key.RenderTransformOrigin = new Point(0.5, 0.5);
                key.RenderTransform = new ScaleTransform(_workingTheme.KeyScaleOnPress, _workingTheme.KeyScaleOnPress);
            }
        }
        else
        {
            key.Background = _workingTheme.GetKeyBrush(tag, false);
            var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(130));
            highlight.BeginAnimation(UIElement.OpacityProperty, anim);
            text.Foreground = _workingTheme.GetKeyTextBrush();
            if (cps != null)
                cps.Foreground = _workingTheme.GetCpsTextBrush();
            key.RenderTransform = Transform.Identity;
        }
    }

    private async void OnTestSequenceClicked(object sender, RoutedEventArgs e)
    {
        if (_sequenceRunning)
            return;

        _sequenceRunning = true;
        string[] keys = { "W", "A", "S", "D", "LMB", "RMB", "Shift", "Space" };

        foreach (string k in keys)
        {
            SetKeyPressed(k, true);
            if (k is "LMB" or "RMB")
                KeystrokeAudioService.PlayMousePress();
            else
                KeystrokeAudioService.PlayKeyboardPress();

            await Task.Delay(140);
            SetKeyPressed(k, false);
            await Task.Delay(80);
        }

        _sequenceRunning = false;
    }

    private void OnRevertClicked(object sender, RoutedEventArgs e)
    {
        _workingTheme.KeyBackground = _originalTheme.KeyBackground;
        _workingTheme.KeyPressedBackground = _originalTheme.KeyPressedBackground;
        _workingTheme.KeyBorderColor = _originalTheme.KeyBorderColor;
        _workingTheme.KeyBorderThickness = _originalTheme.KeyBorderThickness;
        _workingTheme.KeyCornerRadius = _originalTheme.KeyCornerRadius;
        _workingTheme.KeyTextColor = _originalTheme.KeyTextColor;
        _workingTheme.KeyPressedTextColor = _originalTheme.KeyPressedTextColor;
        _workingTheme.CpsTextColor = _originalTheme.CpsTextColor;
        _workingTheme.KeyPressedCpsTextColor = _originalTheme.KeyPressedCpsTextColor;
        _workingTheme.BackdropBlur = _originalTheme.BackdropBlur;
        _workingTheme.KeyScaleOnPress = _originalTheme.KeyScaleOnPress;
        _workingTheme.FontFamily = _originalTheme.FontFamily;
        _workingTheme.FontWeight = _originalTheme.FontWeight;
        _workingTheme.KeyBackgroundImage = _originalTheme.KeyBackgroundImage;
        _workingTheme.KeyPressedBackgroundImage = _originalTheme.KeyPressedBackgroundImage;
        _workingTheme.OverlayBackgroundImage = _originalTheme.OverlayBackgroundImage;
        _workingTheme.KeyImages = _originalTheme.KeyImages != null ? new Dictionary<string, string>(_originalTheme.KeyImages, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        _workingTheme.KeyPressedImages = _originalTheme.KeyPressedImages != null ? new Dictionary<string, string>(_originalTheme.KeyPressedImages, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        _workingTheme.KeyImageStretch = _originalTheme.KeyImageStretch;
        _workingTheme.KeyImageOpacity = _originalTheme.KeyImageOpacity;

        _isInitializing = true;
        LoadThemeIntoControls();
        _isInitializing = false;
        RefreshLivePreview();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (EditorTabs.SelectedIndex == 1)
        {
            if (!UpdateFromCurrentJson())
            {
                var result = MessageBox.Show(TextJsonStatus.Text + "\n\nSave anyway?", "Invalid JSON", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                    return;
            }
        }

        KeystrokeThemeManager.SaveCustomTheme(_workingTheme);
        DialogResult = true;
        Close();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
