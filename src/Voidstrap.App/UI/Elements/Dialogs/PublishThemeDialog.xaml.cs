using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using Voidstrap.Integrations;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class PublishThemeDialog : WpfUiWindow
{
    private const string LogIdent = "PublishThemeDialog";

    private readonly string _folder;

    private ThemePublishRecord? _existing;

    private bool _busy;

    public PublishThemeDialog(string folder, ThemePublishRecord? existing)
    {
        _folder = folder;
        _existing = existing;

        InitializeComponent();

        bool isUpdate = existing != null && !string.IsNullOrEmpty(existing.Id);

        Title = isUpdate ? "Update theme" : "Publish theme";
        RootTitleBar.Title = Title;
        SubmitButton.Content = isUpdate ? "Push update" : "Publish";

        NameBox.Text = isUpdate ? existing!.Name : folder;
        DescriptionBox.Text = isUpdate ? existing!.Description ?? "" : "";

        if (isUpdate)
        {
            IntroText.Text = "This theme is already on the website. Pushing an update replaces its source and bumps its version.";
            NotePanel.Visibility = Visibility.Visible;
            OpenPageText.Visibility = Visibility.Visible;

            WarningBorder.Visibility = Visibility.Visible;
            WarningText.Text = "If the source changes, staff verification is removed until it is reviewed again.";
        }
        else
        {
            IntroText.Text = "Anyone will be able to read the source of this theme and install it.";
        }
    }

    private void SwitchToPublishMode(string reason)
    {
        _existing = null;

        BootstrapperThemes.ClearPublishRecord(_folder);

        Title = "Publish theme";
        RootTitleBar.Title = Title;
        SubmitButton.Content = "Publish";

        IntroText.Text = "Anyone will be able to read the source of this theme and install it.";

        NotePanel.Visibility = Visibility.Collapsed;
        OpenPageText.Visibility = Visibility.Collapsed;
        WarningBorder.Visibility = Visibility.Collapsed;

        ShowStatus(reason);
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        string name = NameBox.Text.Trim();

        if (name.Length == 0)
        {
            ShowStatus("Give the theme a name.");
            return;
        }

        _busy = true;
        SubmitButton.IsEnabled = false;
        ShowStatus(_existing == null ? "Publishing..." : "Pushing the update...");

        Submit(name, DescriptionBox.Text.Trim(), NoteBox.Text.Trim());
    }

    private async void Submit(string name, string description, string note)
    {
        try
        {
            if (_existing != null && !string.IsNullOrEmpty(_existing.Id))
            {
                ThemeUpdateResult result = await BootstrapperThemes.UpdateAsync(_folder, _existing.Id, name, description, note);

                if (result.ClearedVerification)
                {
                    Frontend.ShowMessageBox(
                        "Update pushed as version " + result.Version + ".\n\nThe source changed, so staff verification was removed until it is reviewed again.",
                        MessageBoxImage.Information);
                }
                else
                {
                    Frontend.ShowMessageBox("Update pushed as version " + result.Version + ".", MessageBoxImage.Information);
                }
            }
            else
            {
                PublishedTheme published = await BootstrapperThemes.PublishAsync(_folder, name, description);

                Frontend.ShowMessageBox(
                    "Published. It is listed under Bootstrapper themes on the website, waiting for staff verification.",
                    MessageBoxImage.Information);
            }

            DialogResult = true;
            Close();
        }
        catch (ThemeNotFoundException)
        {
            App.Logger.WriteLine(LogIdent, "The theme is gone from the website, offering a fresh publish");

            _busy = false;
            SubmitButton.IsEnabled = true;

            SwitchToPublishMode("This theme is no longer on the website. Press Publish to put it back up.");
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Publish failed");
            App.Logger.WriteException(LogIdent, ex);

            _busy = false;
            SubmitButton.IsEnabled = true;
            ShowStatus(ex.Message);
        }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (_existing == null || string.IsNullOrEmpty(_existing.Id))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = App.WebsiteBaseUrl + "/pages/theme.html?id=" + Uri.EscapeDataString(_existing.Id),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Could not open the website: " + ex.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
