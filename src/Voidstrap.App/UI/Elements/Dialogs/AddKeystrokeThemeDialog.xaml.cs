using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using Voidstrap.Integrations.Keystrokes;
using Voidstrap.UI.Elements.Base;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class AddKeystrokeThemeDialog : WpfUiWindow
{
    private string _importFilePath = string.Empty;

    public bool Created { get; private set; }

    public string ThemeName { get; private set; } = string.Empty;

    public bool OpenEditor { get; private set; } = true;

    public AddKeystrokeThemeDialog()
    {
        InitializeComponent();
        PopulateTemplates();
        ThemeNameBox.Text = GenerateUniqueThemeName();
    }

    private void PopulateTemplates()
    {
        var builtIns = KeystrokeThemeManager.GetBuiltInThemes();
        TemplateComboBox.ItemsSource = builtIns.Select(t => t.Name).ToList();
        if (TemplateComboBox.Items.Count > 0)
            TemplateComboBox.SelectedIndex = 0;
    }

    private static string GenerateUniqueThemeName()
    {
        int count = KeystrokeThemeManager.GetCustomThemes().Count + 1;
        string candidate = $"Custom Theme {count}";
        int extra = 1;
        while (KeystrokeThemeManager.GetCustomThemes().Any(t => string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"Custom Theme {count + extra++}";
        }
        return candidate;
    }

    private void OnBrowseImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Theme Files (*.json;*.zip)|*.json;*.zip|All Files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            _importFilePath = dialog.FileName;
            ImportPathText.Text = Path.GetFileName(_importFilePath);
            ImportErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedIndex == 0)
        {
            string name = ThemeNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowError(Voidstrap.Resources.Strings.Ui_Keystrokes_AddTheme_ErrorEmpty);
                return;
            }

            var validation = PathValidator.IsFileNameValid(name);
            if (validation != PathValidator.ValidationResult.Ok)
            {
                ShowError(Voidstrap.Resources.Strings.Ui_Keystrokes_AddTheme_ErrorInvalid);
                return;
            }

            if (Voidstrap.Utility.Platform.IsWindows && (name.EndsWith(' ') || name.EndsWith('.')))
            {
                ShowError(Voidstrap.Resources.Strings.Ui_Keystrokes_AddTheme_ErrorWindowsReserved);
                return;
            }

            if (KeystrokeThemeManager.GetBuiltInThemes().Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                KeystrokeThemeManager.GetCustomThemes().Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                ShowError(Voidstrap.Resources.Strings.Ui_Keystrokes_AddTheme_ErrorExists);
                return;
            }

            string templateName = TemplateComboBox.SelectedItem as string ?? "Voidstrap (Default)";
            var templateTheme = KeystrokeThemeManager.GetTheme(templateName);
            var newTheme = templateTheme.Clone(name);

            if (!KeystrokeThemeManager.SaveCustomTheme(newTheme))
            {
                ShowError(Voidstrap.Resources.Strings.Ui_Keystrokes_AddTheme_ErrorSaveFailed);
                return;
            }

            ThemeName = name;
            Created = true;
            DialogResult = true;
            Close();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_importFilePath) || !File.Exists(_importFilePath))
            {
                ShowImportError(Voidstrap.Resources.Strings.Ui_Keystrokes_AddTheme_ErrorSelectFile);
                return;
            }

            if (!KeystrokeThemeManager.ImportCustomTheme(_importFilePath, out string importedName))
            {
                ShowImportError(Voidstrap.Resources.Strings.Ui_Keystrokes_AddTheme_ErrorImportFailed);
                return;
            }

            ThemeName = importedName;
            Created = true;
            DialogResult = true;
            Close();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ShowImportError(string message)
    {
        ImportErrorText.Text = message;
        ImportErrorText.Visibility = Visibility.Visible;
    }
}
