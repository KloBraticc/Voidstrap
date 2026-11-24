using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.ContextMenu;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionOverlayPresetSaveWindow : Window
{
    public const string ToolKey = "overlay-preset-save";

    private static readonly Regex ControlCharacters = new("[\\x00-\\x1F\\x7F]", RegexOptions.Compiled);
    private readonly Func<string, bool> _savePreset;

    public SessionOverlayPresetSaveWindow(Func<string, bool> savePreset)
    {
        _savePreset = savePreset;
        InitializeComponent();
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        PresetNameBox.Focus();
        Keyboard.Focus(PresetNameBox);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Loaded -= OnWindowLoaded;
        Closed -= OnWindowClosed;
    }

    private void PresetNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(PresetNameBox.Text);
        StatusText.Text = "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = ControlCharacters.Replace(PresetNameBox.Text ?? "", "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (!_savePreset(name))
        {
            StatusText.Text = Strings.CustomTheme_Add_Errors_NameTaken;
            PresetNameBox.Focus();
            return;
        }

        CloseTool();
    }

    private void PresetNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        Save_Click(sender, e);
        e.Handled = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseTool();

    private static void CloseTool() => SessionDock.ActiveInstance?.CloseTool(ToolKey);
}
