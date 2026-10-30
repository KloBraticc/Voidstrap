using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voidstrap.Models.Persistable;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.ContextMenu;
using Wpf.Ui.Controls;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class OverlaysPage : UiPage
{
    private static readonly OverlayShortcut DefaultCrosshairShortcut = new() { Modifiers = OverlayShortcut.Alt, Key = 0x58 };

    private bool _recordingCrosshairKey;

    private Border? _crosshairKeyBox;

    private System.Windows.Controls.TextBlock? _crosshairKeyStatus;

    public OverlaysPage()
    {
        DataContext = new ModsViewModel();
        InitializeComponent();
        Loaded += OnOverlaysPageLoaded;
        Unloaded += OnOverlaysPageUnloaded;
    }

    public OverlaysPage(ModsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnOverlaysPageLoaded;
        Unloaded += OnOverlaysPageUnloaded;
    }

    private void OnOverlaysPageLoaded(object sender, RoutedEventArgs e)
    {
        DataContext ??= new ModsViewModel();
        (DataContext as ModsViewModel)?.PopulateKeystrokeThemes();
        ShowCrosshairKey(null);
    }

    private void OnOverlaysPageUnloaded(object sender, RoutedEventArgs e)
    {
        StopCrosshairKeyRecording();
    }

    private void CrosshairKeyBox_Loaded(object sender, RoutedEventArgs e)
    {
        _crosshairKeyBox = sender as Border;
        ShowCrosshairKey(null);
    }

    private void CrosshairKeyStatus_Loaded(object sender, RoutedEventArgs e)
    {
        _crosshairKeyStatus = sender as System.Windows.Controls.TextBlock;
    }

    private void SetCrosshairKeyText(string text)
    {
        if (_crosshairKeyBox?.Child is System.Windows.Controls.TextBlock label)
            label.Text = text;
    }

    private void SetCrosshairKeyStatus(string? status)
    {
        if (_crosshairKeyStatus == null)
            return;
        _crosshairKeyStatus.Text = status ?? string.Empty;
        _crosshairKeyStatus.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowCrosshairKey(string? status)
    {
        OverlayShortcut shortcut = App.Settings.Prop.CrosshairShortcut ??= DefaultCrosshairShortcut.Copy();
        SetCrosshairKeyText(shortcut.Describe());
        _crosshairKeyBox?.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        SetCrosshairKeyStatus(status);
    }

    private void CrosshairKeyBox_Click(object sender, MouseButtonEventArgs e)
    {
        if (_recordingCrosshairKey || _crosshairKeyBox == null)
            return;
        _recordingCrosshairKey = true;
        MenuContainer.SuspendCrosshairHotkey(true);
        SetCrosshairKeyText(Strings.Ui_RiShade_OverlayKey_Press);
        _crosshairKeyBox.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        _crosshairKeyBox.Focus();
        Keyboard.Focus(_crosshairKeyBox);
    }

    private void CrosshairKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingCrosshairKey)
            return;
        e.Handled = true;
        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };
        if (key == Key.Escape)
        {
            StopCrosshairKeyRecording();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        ModifierKeys held = Keyboard.Modifiers;
        uint modifiers = 0;
        if (held.HasFlag(ModifierKeys.Control))
            modifiers |= OverlayShortcut.Control;
        if (held.HasFlag(ModifierKeys.Alt))
            modifiers |= OverlayShortcut.Alt;
        if (held.HasFlag(ModifierKeys.Shift))
            modifiers |= OverlayShortcut.Shift;
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
            return;
        OverlayShortcut candidate = new() { Modifiers = modifiers, Key = virtualKey };
        if (!MenuContainer.TryShortcut(candidate))
        {
            SetCrosshairKeyStatus(string.Format(Strings.Ui_RiShade_OverlayKey_InUse, candidate.Describe()));
            return;
        }
        _recordingCrosshairKey = false;
        SaveCrosshairShortcut(candidate);
    }

    private void CrosshairKeyBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => StopCrosshairKeyRecording();

    private void StopCrosshairKeyRecording()
    {
        if (!_recordingCrosshairKey)
            return;
        _recordingCrosshairKey = false;
        MenuContainer.SuspendCrosshairHotkey(false);
        ShowCrosshairKey(null);
    }

    private void CrosshairKeyReset_Click(object sender, RoutedEventArgs e)
    {
        _recordingCrosshairKey = false;
        SaveCrosshairShortcut(DefaultCrosshairShortcut.Copy());
    }

    private void SaveCrosshairShortcut(OverlayShortcut shortcut)
    {
        App.Settings.Prop.CrosshairShortcut = shortcut;
        App.Settings.SaveDeferred();
        MenuContainer.SuspendCrosshairHotkey(false);
        App.Logger.WriteLine("OverlaysPage", "Crosshair shortcut set to " + shortcut.Describe());
        ShowCrosshairKey(string.Format(Strings.Ui_Mods_CrosshairShortcut_Saved, shortcut.Describe()));
    }
}
