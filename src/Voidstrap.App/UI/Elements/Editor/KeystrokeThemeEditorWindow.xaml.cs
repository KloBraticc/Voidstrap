using System;
using System.Collections.Generic;
using System.IO;
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
    private readonly string _themeName;
    private readonly KeystrokeTheme _originalTheme;
    private readonly KeystrokeTheme _workingTheme;
    private readonly Dictionary<string, (Border Key, Border Highlight, TextBlock Text, TextBlock? Cps)> _previewElements = new(StringComparer.OrdinalIgnoreCase);
    private ThemeCodeLanguage _currentLanguage = ThemeCodeLanguage.CSharp;
    private string _selectedKeycap = "W";
    private bool _isInitializing = true;
    private bool _isUpdatingCode;
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

        CodeEditor.TextChanged += OnCodeEditorTextChanged;
        CodeEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        Activated += (_, _) => RefreshLivePreview();

        _isInitializing = false;
        RefreshLivePreview();
    }

    private void OnMouseCircleMouseDown(object sender, MouseButtonEventArgs e)
    {
        KeystrokeAudioService.PlayMousePress();
        PreviewMouseDot.RenderTransformOrigin = new Point(0.5, 0.5);
        PreviewMouseDot.RenderTransform = new ScaleTransform(1.22, 1.22);
    }

    private void OnMouseCircleMouseUp(object sender, MouseButtonEventArgs e)
    {
        PreviewMouseDot.RenderTransform = Transform.Identity;
    }

    private void RefreshKeycapTargetItems(string? selectKey = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "W", "A", "S", "D", "LMB", "RMB", "Shift", "Space"
        };

        if (_workingTheme.LayoutRows != null)
        {
            foreach (var row in _workingTheme.LayoutRows)
            {
                foreach (var k in row)
                    set.Add(k);
            }
        }

        foreach (var k in _workingTheme.Keycaps.Keys)
            set.Add(k);

        var list = new List<string>(set);
        ComboKeycapTarget.ItemsSource = list;

        if (!string.IsNullOrEmpty(selectKey) && list.Contains(selectKey, StringComparer.OrdinalIgnoreCase))
        {
            ComboKeycapTarget.SelectedItem = list.Find(x => string.Equals(x, selectKey, StringComparison.OrdinalIgnoreCase));
        }
        else if (list.Count > 0)
        {
            ComboKeycapTarget.SelectedIndex = 0;
        }
    }

    private void OnAddKeycapClicked(object sender, RoutedEventArgs e)
    {
        string newKey = TextAddKeyName.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newKey))
            return;

        _workingTheme.GetOrCreateKeycap(newKey);
        TextAddKeyName.Text = string.Empty;
        RefreshKeycapTargetItems(newKey);
        UpdateCodeView();
        RefreshLivePreview();
    }

    private void OnRemoveKeycapClicked(object sender, RoutedEventArgs e)
    {
        if (ComboKeycapTarget.SelectedItem is string key)
        {
            _workingTheme.Keycaps.Remove(key);
            RefreshKeycapTargetItems();
            UpdateCodeView();
            RefreshLivePreview();
        }
    }

    private void OnToggleMouseCircleClicked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _workingTheme.ShowMouseCircle = ToggleMouseCircle.IsChecked == true;
        UpdateCodeView();
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

        ComboLanguage.ItemsSource = new List<string>
        {
            "C# (.cs)",
            "HTML / CSS (.html)",
            "Rust (.rs)",
            "JSON (.json)"
        };
        ComboLanguage.SelectedIndex = 0;

        RefreshKeycapTargetItems();
    }

    private void InitializeSnippets()
    {
        ComboSnippets.ItemsSource = _currentLanguage switch
        {
            ThemeCodeLanguage.CSharp => new List<string>
            {
                Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_InsertSnippet ?? "Insert Snippet...",
                "Keycap Customizer",
                "Background Images",
                "Glass & Squash Scale",
                "Accent Brushes",
                "Full Gaming Layout & Trackpad",
                "Mouse Movement Theme (Custom 1)"
            },
            ThemeCodeLanguage.HtmlCss => new List<string>
            {
                Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_InsertSnippet ?? "Insert Snippet...",
                "Keycap Override",
                "CSS Variables",
                "Pressed State",
                "Full Gaming Layout & Trackpad",
                "Animated HUD & Shine",
                "Mouse Movement Circle"
            },
            ThemeCodeLanguage.Rust => new List<string>
            {
                Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_InsertSnippet ?? "Insert Snippet...",
                "Keycap Match",
                "Theme Overrides",
                "Full Gaming Layout & Trackpad",
                "Mouse Movement Theme"
            },
            _ => new List<string>
            {
                Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_InsertSnippet ?? "Insert Snippet...",
                "Keycap Override",
                "Background Images",
                "Per-Key Images",
                "Glass & Squash Scale",
                "Accent Palette",
                "Full Gaming Layout & Trackpad",
                "Mouse Movement Theme"
            }
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

        ToggleMouseCircle.IsChecked = _workingTheme.ShowMouseCircle;
        RefreshKeycapTargetItems(_selectedKeycap);

        LoadKeycapControls(_selectedKeycap);
        UpdateCodeView();
    }

    private void LoadKeycapControls(string key)
    {
        var cap = _workingTheme.GetOrCreateKeycap(key);
        TextKeycapImage.Text = cap.Image ?? string.Empty;
        TextKeycapPressedImage.Text = cap.PressedImage ?? string.Empty;
        TextKeycapLabel.Text = cap.Text ?? string.Empty;
        ToggleKeycapHideLabel.IsChecked = cap.HideText;

        SliderKeycapWidth.Value = cap.Width ?? 0;
        TextKeycapWidthVal.Text = SliderKeycapWidth.Value > 0 ? $"{SliderKeycapWidth.Value:F0}px" : "Default";

        SliderKeycapHeight.Value = cap.Height ?? 0;
        TextKeycapHeightVal.Text = SliderKeycapHeight.Value > 0 ? $"{SliderKeycapHeight.Value:F0}px" : "Default";

        SliderKeycapCornerRadius.Value = cap.CornerRadius ?? -1;
        TextKeycapCornerRadiusVal.Text = SliderKeycapCornerRadius.Value >= 0 ? $"{SliderKeycapCornerRadius.Value:F0}px" : "Default";
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

    private void UpdateCodeView()
    {
        _isUpdatingCode = true;
        try
        {
            CodeEditor.Text = KeystrokeThemeCodeConverter.GenerateCode(_workingTheme, _currentLanguage);
            TextCodeStatus.Text = Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_CodeValid ?? "Code parsed successfully";
            TextCodeStatus.Foreground = Brushes.LightGreen;
        }
        finally
        {
            _isUpdatingCode = false;
        }
    }

    private void OnCodeEditorTextChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingCode)
            return;

        var tempTheme = new KeystrokeTheme();
        if (KeystrokeThemeCodeConverter.TryParseCode(CodeEditor.Text, _currentLanguage, tempTheme, out string? error))
        {
            TextCodeStatus.Text = Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_CodeValid ?? "Code parsed successfully";
            TextCodeStatus.Foreground = Brushes.LightGreen;
        }
        else
        {
            TextCodeStatus.Text = $"{Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_CodeInvalid ?? "Syntax error"}: {error}";
            TextCodeStatus.Foreground = Brushes.IndianRed;
        }
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        var caret = CodeEditor.TextArea.Caret;
        TextEditorStats.Text = $"Ln {caret.Line}, Col {caret.Column}";
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || e.Source != ComboLanguage)
            return;

        if (!_isUpdatingCode && !string.IsNullOrWhiteSpace(CodeEditor.Text))
        {
            _ = KeystrokeThemeCodeConverter.TryParseCode(CodeEditor.Text, _currentLanguage, _workingTheme, out _);
        }

        _currentLanguage = ComboLanguage.SelectedIndex switch
        {
            1 => ThemeCodeLanguage.HtmlCss,
            2 => ThemeCodeLanguage.Rust,
            3 => ThemeCodeLanguage.Json,
            _ => ThemeCodeLanguage.CSharp
        };

        CodeEditorBehavior.SetLanguage(CodeEditor, KeystrokeThemeCodeConverter.GetExtension(_currentLanguage));
        InitializeSnippets();
        UpdateCodeView();
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || e.Source != EditorTabs)
            return;

        if (EditorTabs.SelectedIndex == 1)
        {
            UpdateCodeView();
        }
        else if (EditorTabs.SelectedIndex == 0)
        {
            UpdateFromCurrentCode();
        }
    }

    private bool UpdateFromCurrentCode()
    {
        if (KeystrokeThemeCodeConverter.TryParseCode(CodeEditor.Text, _currentLanguage, _workingTheme, out string? error))
        {
            _workingTheme.Name = _themeName;
            _isInitializing = true;
            LoadThemeIntoControls();
            _isInitializing = false;
            RefreshLivePreview();

            TextCodeStatus.Text = Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_CodeValid ?? "Code parsed successfully";
            TextCodeStatus.Foreground = Brushes.LightGreen;
            return true;
        }

        TextCodeStatus.Text = $"{Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_CodeInvalid ?? "Syntax error"}: {error}";
        TextCodeStatus.Foreground = Brushes.IndianRed;
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
            UpdateCodeView();
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

        UpdateCodeView();
        RefreshLivePreview();
    }

    private void OnToggleGlassChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _workingTheme.BackdropBlur = ToggleGlass.IsChecked == true;
        UpdateCodeView();
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

        UpdateCodeView();
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
            UpdateCodeView();
            RefreshLivePreview();
        }
    }

    private void OnClearKeyBgImageClicked(object sender, RoutedEventArgs e)
    {
        _workingTheme.KeyBackgroundImage = null;
        TextKeyBgImage.Text = string.Empty;
        UpdateCodeView();
        RefreshLivePreview();
    }

    private void OnBrowseKeyPressedBgImageClicked(object sender, RoutedEventArgs e)
    {
        string? imported = PromptPickImage();
        if (imported != null)
        {
            _workingTheme.KeyPressedBackgroundImage = imported;
            TextKeyPressedBgImage.Text = imported;
            UpdateCodeView();
            RefreshLivePreview();
        }
    }

    private void OnClearKeyPressedBgImageClicked(object sender, RoutedEventArgs e)
    {
        _workingTheme.KeyPressedBackgroundImage = null;
        TextKeyPressedBgImage.Text = string.Empty;
        UpdateCodeView();
        RefreshLivePreview();
    }

    private void OnImageStretchChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        if (ComboImageStretch.SelectedItem is string stretch)
        {
            _workingTheme.KeyImageStretch = stretch;
            UpdateCodeView();
            RefreshLivePreview();
        }
    }

    private void OnImageOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing)
            return;

        _workingTheme.KeyImageOpacity = SliderImageOpacity.Value / 100.0;
        TextImageOpacityVal.Text = $"{SliderImageOpacity.Value:F0}%";
        UpdateCodeView();
        RefreshLivePreview();
    }

    private void OnKeycapTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboKeycapTarget.SelectedItem is string key)
        {
            _selectedKeycap = key;
            LoadKeycapControls(key);
        }
    }

    private void OnBrowseKeycapImageClicked(object sender, RoutedEventArgs e)
    {
        string? imported = PromptPickImage();
        if (imported != null)
        {
            var cap = _workingTheme.GetOrCreateKeycap(_selectedKeycap);
            cap.Image = imported;
            TextKeycapImage.Text = imported;
            UpdateCodeView();
            RefreshLivePreview();
        }
    }

    private void OnClearKeycapImageClicked(object sender, RoutedEventArgs e)
    {
        var cap = _workingTheme.GetOrCreateKeycap(_selectedKeycap);
        cap.Image = null;
        TextKeycapImage.Text = string.Empty;
        UpdateCodeView();
        RefreshLivePreview();
    }

    private void OnBrowseKeycapPressedImageClicked(object sender, RoutedEventArgs e)
    {
        string? imported = PromptPickImage();
        if (imported != null)
        {
            var cap = _workingTheme.GetOrCreateKeycap(_selectedKeycap);
            cap.PressedImage = imported;
            TextKeycapPressedImage.Text = imported;
            UpdateCodeView();
            RefreshLivePreview();
        }
    }

    private void OnClearKeycapPressedImageClicked(object sender, RoutedEventArgs e)
    {
        var cap = _workingTheme.GetOrCreateKeycap(_selectedKeycap);
        cap.PressedImage = null;
        TextKeycapPressedImage.Text = string.Empty;
        UpdateCodeView();
        RefreshLivePreview();
    }

    private void OnKeycapLabelChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        var cap = _workingTheme.GetOrCreateKeycap(_selectedKeycap);
        cap.Text = string.IsNullOrWhiteSpace(TextKeycapLabel.Text) ? null : TextKeycapLabel.Text;
        UpdateCodeView();
        RefreshLivePreview();
    }

    private void OnKeycapHideLabelClicked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        var cap = _workingTheme.GetOrCreateKeycap(_selectedKeycap);
        cap.HideText = ToggleKeycapHideLabel.IsChecked == true;
        UpdateCodeView();
        RefreshLivePreview();
    }

    private void OnKeycapGeometryChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing)
            return;

        var cap = _workingTheme.GetOrCreateKeycap(_selectedKeycap);

        cap.Width = SliderKeycapWidth.Value > 0 ? SliderKeycapWidth.Value : null;
        TextKeycapWidthVal.Text = cap.Width.HasValue ? $"{cap.Width.Value:F0}px" : "Default";

        cap.Height = SliderKeycapHeight.Value > 0 ? SliderKeycapHeight.Value : null;
        TextKeycapHeightVal.Text = cap.Height.HasValue ? $"{cap.Height.Value:F0}px" : "Default";

        cap.CornerRadius = SliderKeycapCornerRadius.Value >= 0 ? SliderKeycapCornerRadius.Value : null;
        TextKeycapCornerRadiusVal.Text = cap.CornerRadius.HasValue ? $"{cap.CornerRadius.Value:F0}px" : "Default";

        UpdateCodeView();
        RefreshLivePreview();
    }

    private void OnFormatCodeClicked(object sender, RoutedEventArgs e)
    {
        if (UpdateFromCurrentCode())
        {
            UpdateCodeView();
        }
    }

    private void OnAddImageClicked(object sender, RoutedEventArgs e)
    {
        string? imported = PromptPickImage();
        if (imported != null)
        {
            string snippet = _currentLanguage switch
            {
                ThemeCodeLanguage.HtmlCss => $"url('{imported}')",
                ThemeCodeLanguage.Rust => $"Some(\"{imported}\")",
                _ => $"\"{imported}\""
            };
            CodeEditor.Document.Insert(CodeEditor.CaretOffset, snippet);
            UpdateFromCurrentCode();
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

        int idx = ComboSnippets.SelectedIndex;
        string snippet = _currentLanguage switch
        {
            ThemeCodeLanguage.CSharp => idx switch
            {
                1 => """
        theme.Keycaps["W"] = new KeycapConfig
        {
            Image = "w.png",
            PressedImage = "w_pressed.png",
            Width = 52.0,
            Height = 52.0,
            CornerRadius = 8.0,
            Text = "W",
            HideText = false
        };
""",
                2 => """
            KeyBackgroundImage = "key_bg.png",
            KeyPressedBackgroundImage = "key_pressed.png",
            KeyImageStretch = "UniformToFill",
            KeyImageOpacity = 1.0,
""",
                3 => """
            BackdropBlur = true,
            KeyScaleOnPress = 0.94,
            KeyCornerRadius = 10.0,
            KeyBorderThickness = 1.5,
""",
                4 => """
            KeyBackground = "#28181A1F",
            KeyPressedBackground = "Accent",
            KeyTextColor = "#E6FFFFFF",
            KeyPressedTextColor = "#FFFFFF",
            KeyBorderColor = "Accent",
""",
                5 => """
            LayoutRows = new List<List<string>>
            {
                new() { "1", "2", "3", "4", "5" },
                new() { "Q", "W", "E", "R", "T" },
                new() { "A", "S", "D", "F", "G" },
                new() { "Z", "X", "C", "V", "B" },
                new() { "0", "1" }
            },
            ShowMouseCircle = true,
            MouseCircleSize = 130.0,
""",
                6 => """
using Voidstrap.Integrations.Keystrokes;

public static class ThemeDefinition
{
    public static KeystrokeTheme CreateTheme()
    {
        var theme = new KeystrokeTheme
        {
            Name = "Custom Theme 1",
            KeyBackground = "#171625",
            KeyPressedBackground = "#8B5CF6",
            KeyBorderColor = "#393352",
            KeyBorderThickness = 1.25,
            KeyCornerRadius = 9.0,
            KeyTextColor = "#E8E5F5",
            KeyPressedTextColor = "#FFFFFF",
            CpsTextColor = "#85819F",
            KeyPressedCpsTextColor = "#FFFFFF",
            BackdropBlur = true,
            KeyScaleOnPress = 0.94,
            FontFamily = "Cascadia Mono",
            FontWeight = "Bold",
            KeyBackgroundImage = null,
            KeyPressedBackgroundImage = null,
            KeyImageStretch = "Uniform",
            KeyImageOpacity = 1.0,
            ShowMouseCircle = true,
            MouseCircleSize = 88.0
        };

        AddKey(theme, "W", 48, 48, 10);
        AddKey(theme, "A", 48, 48, 10);
        AddKey(theme, "S", 48, 48, 10);
        AddKey(theme, "D", 48, 48, 10);

        AddKey(theme, "LMB", 70, 34, 8);
        AddKey(theme, "RMB", 70, 34, 8);

        AddKey(theme, "Shift", 78, 34, 8);
        AddKey(theme, "Space", 112, 34, 8);

        return theme;
    }

    private static void AddKey(
        KeystrokeTheme theme,
        string key,
        double width,
        double height,
        double radius)
    {
        theme.Keycaps[key] = new KeycapConfig
        {
            Image = null,
            PressedImage = null,
            Width = width,
            Height = height,
            CornerRadius = radius,
            Text = null,
            HideText = false
        };
    }
}
""",
                _ => string.Empty
            },
            ThemeCodeLanguage.HtmlCss => idx switch
            {
                1 => """
    .keycap[data-key="W"] {
      width: 52px;
      height: 52px;
      border-radius: 8px;
      background-image: url('w.png');
    }
    .keycap[data-key="W"].pressed {
      background-image: url('w_pressed.png');
    }
""",
                2 => """
      --key-bg: #202020;
      --key-pressed-bg: Accent;
      --key-border-color: #363638;
      --key-corner-radius: 8px;
""",
                3 => """
    .keycap.pressed {
      background-color: var(--key-pressed-bg);
      color: var(--key-pressed-text-color);
      transform: scale(var(--key-scale-on-press));
    }
""",
                4 => """
    <div class="hud">
      <div class="mouse-circle"><div class="mouse-dot"></div></div>
      <div class="keys-container">
        <div class="row"><div class="keycap" data-key="1">1</div><div class="keycap" data-key="2">2</div><div class="keycap" data-key="3">3</div><div class="keycap" data-key="4">4</div><div class="keycap" data-key="5">5</div></div>
        <div class="row"><div class="keycap" data-key="Q">Q</div><div class="keycap" data-key="W">W</div><div class="keycap" data-key="E">E</div><div class="keycap" data-key="R">R</div><div class="keycap" data-key="T">T</div></div>
        <div class="row"><div class="keycap" data-key="A">A</div><div class="keycap" data-key="S">S</div><div class="keycap" data-key="D">D</div><div class="keycap" data-key="F">F</div><div class="keycap" data-key="G">G</div></div>
        <div class="row"><div class="keycap" data-key="Z">Z</div><div class="keycap" data-key="X">X</div><div class="keycap" data-key="C">C</div><div class="keycap" data-key="V">V</div><div class="keycap" data-key="B">B</div></div>
        <div class="row"><div class="keycap" data-key="0">0</div><div class="keycap" data-key="1">1</div></div>
      </div>
    </div>
""",
                5 => """
@property --border-angle {
  syntax: "<angle>";
  initial-value: 0deg;
  inherits: false;
}

.hud {
  animation:
    hud-enter 700ms cubic-bezier(.16, 1, .3, 1) both,
    hud-float 5s ease-in-out 700ms infinite;
}

.hud-shine {
  position: absolute;
  inset: 0;
  overflow: hidden;
  border-radius: inherit;
  pointer-events: none;
  z-index: 3;
}

.hud-shine::after {
  content: "";
  position: absolute;
  top: -80%;
  left: -60%;
  width: 45%;
  height: 260%;
  transform: rotate(25deg);
  background: linear-gradient(
    90deg,
    transparent,
    rgba(255, 255, 255, 0.07),
    rgba(167, 139, 250, 0.12),
    transparent
  );
  animation: shine-sweep 6s ease-in-out infinite;
}

.keycap {
  animation: key-entrance 600ms cubic-bezier(.16, 1, .3, 1) both;
}

.row:nth-child(1) .keycap { animation-delay: 100ms; }
.row:nth-child(2) .keycap { animation-delay: 170ms; }
.row:nth-child(3) .keycap { animation-delay: 240ms; }
.row:nth-child(4) .keycap { animation-delay: 310ms; }

.keycap:hover {
  animation: hover-spring 350ms cubic-bezier(.2, 1.5, .4, 1) both;
}

.keycap.pressed {
  animation: press-pop 220ms cubic-bezier(.2, 1.4, .4, 1) both;
}

.hud::before {
  animation: border-spin 5s linear infinite;
}

.hud::after {
  animation: ambient 3.5s ease-in-out infinite alternate;
}

@keyframes hud-float {
  0%, 100% { translate: 0 0; }
  50% { translate: 0 -4px; }
}

@keyframes shine-sweep {
  0%, 15% { left: -60%; opacity: 0; }
  25% { opacity: 1; }
  55% { left: 130%; opacity: 0; }
  100% { left: 130%; opacity: 0; }
}

@keyframes key-entrance {
  from {
    opacity: 0;
    transform: translateY(12px) scale(.8);
    filter: blur(5px);
  }
  to {
    opacity: 1;
    transform: translateY(0) scale(1);
    filter: blur(0);
  }
}

@keyframes hover-spring {
  0% { transform: translateY(-2px) scale(1.02); }
  55% { transform: translateY(-5px) scale(1.08); }
  100% { transform: translateY(-3px) scale(1.04); }
}

@keyframes press-pop {
  0% { transform: scale(1); filter: brightness(1.5); }
  45% { transform: scale(.86); filter: brightness(1.25); }
  75% { transform: scale(.96); }
  100% { transform: scale(var(--key-scale-on-press)); filter: brightness(1); }
}

@media (prefers-reduced-motion: reduce) {
  .hud,
  .hud::before,
  .hud::after,
  .hud-shine::after,
  .keycap {
    animation: none !important;
    transition: none !important;
  }
}
""",
                6 => """
      --show-mouse-circle: true;
      --mouse-circle-size: 88px;
""",
                _ => string.Empty
            },
            ThemeCodeLanguage.Rust => idx switch
            {
                1 => """
        "W" => Some(KeycapConfig {
            image: Some("w.png"),
            pressed_image: Some("w_pressed.png"),
            width: Some(52.0),
            height: Some(52.0),
            corner_radius: Some(8.0),
            text: Some("W"),
            hide_text: false,
        }),
""",
                2 => """
    key_background: "#202020",
    key_pressed_background: "Accent",
    key_border_color: "#363638",
    key_corner_radius: 8.0,
""",
                3 => """
    layout_rows: vec![
        vec!["1", "2", "3", "4", "5"],
        vec!["Q", "W", "E", "R", "T"],
        vec!["A", "S", "D", "F", "G"],
        vec!["Z", "X", "C", "V", "B"],
        vec!["0", "1"],
    ],
    show_mouse_circle: true,
    mouse_circle_size: 130.0,
""",
                4 => """
    show_mouse_circle: true,
    mouse_circle_size: 88.0,
""",
                _ => string.Empty
            },
            _ => idx switch
            {
                1 => """
  "Keycaps": {
    "W": {
      "Image": "w.png",
      "PressedImage": "w_pressed.png",
      "Width": 52.0,
      "Height": 52.0,
      "CornerRadius": 8.0,
      "Text": "W",
      "HideText": false
    }
  },
""",
                2 => """
  "KeyBackgroundImage": "key_bg.png",
  "KeyPressedBackgroundImage": "key_pressed.png",
  "KeyImageStretch": "UniformToFill",
  "KeyImageOpacity": 1.0,
""",
                3 => """
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
                6 => """
  "LayoutRows": [
    [ "1", "2", "3", "4", "5" ],
    [ "Q", "W", "E", "R", "T" ],
    [ "A", "S", "D", "F", "G" ],
    [ "Z", "X", "C", "V", "B" ],
    [ "0", "1" ]
  ],
  "ShowMouseCircle": true,
  "MouseCircleSize": 130.0,
""",
                7 => """
  "ShowMouseCircle": true,
  "MouseCircleSize": 88.0,
""",
                _ => string.Empty
            }
        };

        if (!string.IsNullOrEmpty(snippet))
        {
            CodeEditor.Document.Insert(CodeEditor.CaretOffset, snippet);
        }

        ComboSnippets.SelectedIndex = 0;
    }

    private void OnApplyCodeClicked(object sender, RoutedEventArgs e)
    {
        if (UpdateFromCurrentCode())
        {
            Frontend.ShowMessageBox(Voidstrap.Resources.Strings.Ui_Keystrokes_Editor_CodeValid ?? "Theme updated from code.", MessageBoxImage.Information);
        }
        else
        {
            Frontend.ShowMessageBox(TextCodeStatus.Text, MessageBoxImage.Error);
        }
    }

    private void RefreshLivePreview()
    {
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

        if (_workingTheme.ShowMouseCircle)
        {
            PreviewMouseCircle.Visibility = Visibility.Visible;
            double size = _workingTheme.MouseCircleSize > 0 ? _workingTheme.MouseCircleSize : 88.0;
            PreviewMouseCircle.Width = size;
            PreviewMouseCircle.Height = size;
            PreviewMouseCircle.CornerRadius = new CornerRadius(size / 2.0);
            PreviewMouseCircle.BorderBrush = borderBrush;
            PreviewMouseCircle.BorderThickness = borderThickness;
            PreviewMouseCircle.Background = _workingTheme.GetKeyBackgroundBrush();

            double dotSize = Math.Max(12, Math.Round(size * 0.27));
            PreviewMouseDot.Width = dotSize;
            PreviewMouseDot.Height = dotSize;
            PreviewMouseDot.Fill = pressedBgBrush;
        }
        else
        {
            PreviewMouseCircle.Visibility = Visibility.Collapsed;
        }

        _previewElements.Clear();

        var settings = App.Settings?.Prop ?? new Voidstrap.Models.Persistable.AppSettings();

        if (_workingTheme.LayoutRows != null && _workingTheme.LayoutRows.Count > 0)
        {
            PreviewDefaultKeysPanel.Visibility = Visibility.Collapsed;
            PreviewDynamicKeysPanel.Visibility = Visibility.Visible;
            PreviewDynamicKeysPanel.Children.Clear();

            foreach (var row in _workingTheme.LayoutRows)
            {
                var visibleKeys = row.Where(k => KeystrokeThemeManager.IsKeyAllowedBySettings(k, settings)).ToList();
                if (visibleKeys.Count == 0)
                    continue;

                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 3)
                };

                for (int i = 0; i < visibleKeys.Count; i++)
                {
                    string keyName = visibleKeys[i];
                    var cap = _workingTheme.Keycaps.TryGetValue(keyName, out var c) ? c : null;

                    double width = cap?.Width ?? 46;
                    double height = cap?.Height ?? 46;
                    double cr = cap?.CornerRadius ?? _workingTheme.KeyCornerRadius;

                    var keyBorder = new Border
                    {
                        Width = width,
                        Height = height,
                        CornerRadius = new CornerRadius(cr),
                        BorderThickness = borderThickness,
                        BorderBrush = borderBrush,
                        Background = _workingTheme.GetKeyBrush(keyName, false),
                        Margin = new Thickness(0, 0, i < visibleKeys.Count - 1 ? 3 : 0, 0),
                        Cursor = Cursors.Hand,
                        Tag = keyName
                    };
                    keyBorder.MouseDown += OnKeyMouseDown;
                    keyBorder.MouseUp += OnKeyMouseUp;

                    var grid = new Grid();
                    var highlight = new Border
                    {
                        CornerRadius = new CornerRadius(Math.Max(0, cr - 1)),
                        Opacity = 0,
                        Background = _workingTheme.GetKeyBrush(keyName, true)
                    };
                    grid.Children.Add(highlight);

                    string label = !string.IsNullOrEmpty(cap?.Text) ? cap.Text : keyName;
                    var textBlock = new TextBlock
                    {
                        Text = label,
                        FontSize = Math.Max(10, Math.Min(15, height * 0.33)),
                        FontWeight = fontWeight,
                        FontFamily = fontFamily,
                        Foreground = textBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Visibility = (cap?.HideText == true) ? Visibility.Collapsed : Visibility.Visible
                    };
                    grid.Children.Add(textBlock);

                    keyBorder.Child = grid;
                    rowPanel.Children.Add(keyBorder);
                    _previewElements[keyName] = (keyBorder, highlight, textBlock, null);
                }

                PreviewDynamicKeysPanel.Children.Add(rowPanel);
            }
        }
        else
        {
            PreviewDefaultKeysPanel.Visibility = Visibility.Visible;
            PreviewDynamicKeysPanel.Visibility = Visibility.Collapsed;

            PreviewRowW.Visibility = settings.KeystrokesShowKeys ? Visibility.Visible : Visibility.Collapsed;
            PreviewRowASD.Visibility = settings.KeystrokesShowKeys ? Visibility.Visible : Visibility.Collapsed;
            PreviewRowMouse.Visibility = settings.KeystrokesShowMouseButtons ? Visibility.Visible : Visibility.Collapsed;
            PreviewRowBottom.Visibility = settings.KeystrokesShowSpaceAndShift ? Visibility.Visible : Visibility.Collapsed;
            PreviewCpsLmb.Visibility = settings.KeystrokesShowCps ? Visibility.Visible : Visibility.Collapsed;
            PreviewCpsRmb.Visibility = settings.KeystrokesShowCps ? Visibility.Visible : Visibility.Collapsed;

            var keyTuples = new (Border Key, Border Highlight, TextBlock Text, string Name)[]
            {
                (PreviewKeyW, PreviewHighlightW, PreviewTextW, "W"),
                (PreviewKeyA, PreviewHighlightA, PreviewTextA, "A"),
                (PreviewKeyS, PreviewHighlightS, PreviewTextS, "S"),
                (PreviewKeyD, PreviewHighlightD, PreviewTextD, "D"),
                (PreviewKeyLmb, PreviewHighlightLmb, PreviewTextLmb, "LMB"),
                (PreviewKeyRmb, PreviewHighlightRmb, PreviewTextRmb, "RMB"),
                (PreviewKeyShift, PreviewHighlightShift, PreviewTextShift, "Shift"),
                (PreviewKeySpace, PreviewHighlightSpace, PreviewTextSpace, "Space")
            };

            foreach (var item in keyTuples)
            {
                item.Key.Background = _workingTheme.GetKeyBrush(item.Name, false);
                item.Key.BorderBrush = borderBrush;
                item.Key.BorderThickness = borderThickness;
                item.Key.CornerRadius = cornerRadius;
                item.Key.Effect = null;

                item.Highlight.Background = pressedBgBrush;
                item.Highlight.CornerRadius = highlightCorner;

                item.Text.Foreground = textBrush;
                item.Text.FontFamily = fontFamily;
                item.Text.FontWeight = fontWeight;

                var cap = _workingTheme.Keycaps.TryGetValue(item.Name, out var c) ? c : null;

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

                TextBlock? cps = item.Name switch
                {
                    "LMB" => PreviewCpsLmb,
                    "RMB" => PreviewCpsRmb,
                    _ => null
                };
                _previewElements[item.Name] = (item.Key, item.Highlight, item.Text, cps);
            }

            PreviewCpsLmb.Foreground = cpsBrush;
            PreviewCpsLmb.FontFamily = fontFamily;
            PreviewCpsRmb.Foreground = cpsBrush;
            PreviewCpsRmb.FontFamily = fontFamily;
        }
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
        if (_workingTheme.ShowMouseCircle && tag is "LMB" or "RMB")
        {
            if (isDown)
            {
                PreviewMouseDot.RenderTransformOrigin = new Point(0.5, 0.5);
                PreviewMouseDot.RenderTransform = new ScaleTransform(1.22, 1.22);
            }
            else
            {
                PreviewMouseDot.RenderTransform = Transform.Identity;
            }
        }

        if (!_previewElements.TryGetValue(tag, out var elem))
            return;

        var (key, highlight, text, cps) = elem;
        highlight.BeginAnimation(UIElement.OpacityProperty, null);

        if (_workingTheme.Keycaps.TryGetValue(tag, out var cap))
        {
            if (!string.IsNullOrEmpty(cap.Text))
                text.Text = cap.Text;
            text.Visibility = cap.HideText ? Visibility.Collapsed : Visibility.Visible;
        }

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
            key.Effect = null;
        }
    }

    private async void OnTestSequenceClicked(object sender, RoutedEventArgs e)
    {
        if (_sequenceRunning)
            return;

        _sequenceRunning = true;
        var settings = App.Settings?.Prop ?? new Voidstrap.Models.Persistable.AppSettings();
        var keys = new List<string>();
        foreach (var row in _workingTheme.GetEffectiveLayoutRows())
        {
            foreach (var k in row)
            {
                if (KeystrokeThemeManager.IsKeyAllowedBySettings(k, settings))
                    keys.Add(k);
            }
        }

        foreach (string k in keys)
        {
            SetKeyPressed(k, true);
            if (k is "LMB" or "RMB")
                KeystrokeAudioService.PlayMousePress();
            else
                KeystrokeAudioService.PlayKeyboardPress();

            await Task.Delay(130);
            SetKeyPressed(k, false);
            await Task.Delay(70);
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

        _workingTheme.Keycaps.Clear();
        foreach (var pair in _originalTheme.Keycaps)
            _workingTheme.Keycaps[pair.Key] = pair.Value.Clone();

        _isInitializing = true;
        LoadThemeIntoControls();
        _isInitializing = false;
        RefreshLivePreview();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (EditorTabs.SelectedIndex == 1)
        {
            if (!UpdateFromCurrentCode())
            {
                var result = Frontend.ShowMessageBox(TextCodeStatus.Text + "\n\nSave anyway?", MessageBoxImage.Warning, MessageBoxButton.YesNo);
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
