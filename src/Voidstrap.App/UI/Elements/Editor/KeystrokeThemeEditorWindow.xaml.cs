using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Voidstrap.Integrations.Keystrokes;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Editor;

public partial class KeystrokeThemeEditorWindow : WpfUiWindow
{
    private readonly string _themeName;
    private readonly KeystrokeTheme _workingTheme;
    private readonly string _themeDirectory;
    private readonly KeystrokeScriptView _preview = new();
    private readonly DispatcherTimer _compileTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string _savedCode;
    private Point? _lastMouse;
    private string? _lastError;

    public KeystrokeThemeEditorWindow(string themeName)
    {
        InitializeComponent();
        VisualStudioTheme.Apply(this);

        _themeName = themeName;
        _workingTheme = KeystrokeThemeManager.GetTheme(themeName).Clone(themeName);
        _themeDirectory = Path.Combine(Paths.CustomKeystrokeThemes, themeName);
        _savedCode = _workingTheme.ScriptSource ?? KeystrokeScriptTemplates.FromTheme(_workingTheme);

        Title = $"{Strings.Ui_Keystrokes_Editor_Title} - {themeName}";
        TitleBarControl.Title = Title;
        ReferenceView.Text = KeystrokeScriptTemplates.Reference;
        CodeEditor.Text = _savedCode;
        PreviewHost.Content = _preview;

        _preview.ScriptStateChanged += OnScriptStateChanged;
        _compileTimer.Tick += OnCompileTick;
        CodeEditor.TextChanged += OnCodeEditorTextChanged;
        CodeEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        Closed += OnWindowClosed;
        RegisterMcpEditor();

        CompileNow();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Closed -= OnWindowClosed;
        _compileTimer.Stop();
        _compileTimer.Tick -= OnCompileTick;
        _preview.ScriptStateChanged -= OnScriptStateChanged;
        CodeEditor.TextChanged -= OnCodeEditorTextChanged;
        CodeEditor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _preview.Dispose();
    }

    private void OnCodeEditorTextChanged(object? sender, EventArgs e)
    {
        _compileTimer.Stop();
        _compileTimer.Start();
    }

    private void OnCompileTick(object? sender, EventArgs e)
    {
        _compileTimer.Stop();
        CompileNow();
    }

    private string? CompileNow()
    {
        _compileTimer.Stop();
        return _preview.Load(CodeEditor.Text, _themeDirectory);
    }

    private void OnScriptStateChanged(string? error)
    {
        if (error == _lastError && StatusText.Tag != null)
            return;
        _lastError = error;
        StatusText.Tag = true;
        StatusText.Text = error == null ? Strings.Ui_Keystrokes_Editor_CodeValid : $"{Strings.Ui_Keystrokes_Editor_CodeInvalid}: {error}";
        StatusText.Foreground = error == null ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.IndianRed;
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        var caret = CodeEditor.TextArea.Caret;
        CaretText.Text = $"Ln {caret.Line}, Col {caret.Column}";
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        PreviewSurface.Focus();
        PreviewSurface.CaptureMouse();
        if (MouseName(e.ChangedButton) is string name)
            _preview.SetKey(name, true);
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (MouseName(e.ChangedButton) is string name)
            _preview.SetKey(name, false);
        if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released)
            PreviewSurface.ReleaseMouseCapture();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        Point now = e.GetPosition(PreviewSurface);
        if (_lastMouse is Point last)
            _preview.AddMouse(now.X - last.X, now.Y - last.Y, 0);
        _lastMouse = now;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e) => _preview.AddMouse(0, 0, e.Delta / 120.0);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PreviewKeyName(e) is string name)
        {
            _preview.SetKey(name, true);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (PreviewKeyName(e) is string name)
        {
            _preview.SetKey(name, false);
            e.Handled = true;
        }
    }

    private void OnPreviewLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CompileNow();

    private static string? MouseName(MouseButton button) =>
        button switch
        {
            MouseButton.Left => "LMB",
            MouseButton.Right => "RMB",
            MouseButton.Middle => "MMB",
            _ => null,
        };

    private static string? PreviewKeyName(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        return KeystrokeInputService.MapVirtualKeyToString(KeyInterop.VirtualKeyFromKey(key));
    }

    private void OnExampleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
            return;
        CodeEditor.Text = tag switch
        {
            "Minimal" => KeystrokeScriptTemplates.Minimal,
            "Mouse" => KeystrokeScriptTemplates.Mouse,
            "Cps" => KeystrokeScriptTemplates.Cps,
            "Animated" => KeystrokeScriptTemplates.Animated,
            _ => KeystrokeScriptTemplates.Classic,
        };
        CompileNow();
    }

    private void OnAddImageClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image Files (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files (*.*)|*.*",
            Title = Strings.Ui_Keystrokes_Editor_AddImage,
        };
        if (dialog.ShowDialog() != true)
            return;
        Directory.CreateDirectory(_themeDirectory);
        string fileName = Path.GetFileName(dialog.FileName);
        string destination = Path.Combine(_themeDirectory, fileName);
        if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            File.Copy(dialog.FileName, destination, true);
        CodeEditor.Document.Insert(CodeEditor.CaretOffset, "\"" + fileName + "\"");
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_themeDirectory);
        Utilities.ShellExecute(_themeDirectory);
    }

    private void OnRevertClicked(object sender, RoutedEventArgs e)
    {
        CodeEditor.Text = _savedCode;
        CompileNow();
    }

    private void EditorSettings_Click(object sender, RoutedEventArgs e) => new EditorSettingsWindow().ShowOwnedDialog();

    private bool SaveCode()
    {
        _workingTheme.ScriptSource = CodeEditor.Text;
        if (!KeystrokeThemeManager.SaveCustomTheme(_workingTheme))
            return false;
        _savedCode = CodeEditor.Text;
        return true;
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (CompileNow() is string error)
        {
            if (Frontend.ShowMessageBox(error, MessageBoxImage.Warning, MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                return;
        }
        SaveCode();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (CodeEditor.Text == _savedCode)
            return;
        MessageBoxResult answer = Frontend.ShowMessageBox(Strings.Ui_Keystrokes_Editor_UnsavedPrompt, MessageBoxImage.Question, MessageBoxButton.YesNoCancel);
        if (answer == MessageBoxResult.Yes)
            SaveCode();
        else if (answer == MessageBoxResult.Cancel)
            e.Cancel = true;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
