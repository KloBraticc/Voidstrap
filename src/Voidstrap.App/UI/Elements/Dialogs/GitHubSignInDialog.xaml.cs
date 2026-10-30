using System;
using System.Threading;
using System.Windows;
using Voidstrap.Integrations.Plugins;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class GitHubSignInDialog : WpfUiWindow
{
    private readonly CancellationTokenSource _cts = new();

    private GitHubPublisher.DeviceCode? _code;

    public GitHubPublisher.Account? Account { get; private set; }

    public GitHubSignInDialog()
    {
        InitializeComponent();
        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;
    }

    public static GitHubPublisher.Account? EnsureSignedIn(Window? owner)
    {
        if (GitHubPublisher.SignedIn is { } account)
            return account;
        if (!GitHubPublisher.IsConfigured)
        {
            Frontend.ShowMessageBox(Strings.Ui_VoidstrapMods_PublishingNotSetUp, MessageBoxImage.Information);
            return null;
        }
        GitHubSignInDialog dialog = new();
        if (owner != null)
            dialog.Owner = owner;
        return dialog.ShowOwnedDialog() == true ? dialog.Account : null;
    }

    private async void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _code = await GitHubPublisher.StartSignInAsync(_cts.Token);
            CodeText.Text = _code.UserCode;
            OpenButton.IsEnabled = true;
            CopyButton.IsEnabled = true;
            Account = await GitHubPublisher.CompleteSignInAsync(_code, _cts.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Spinner.Visibility = Visibility.Collapsed;
            StatusText.Text = string.Format(Strings.Ui_VoidstrapMods_SignInFailed, ex.Message);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_code == null)
            return;
        CopyUserCode();
        Utilities.OpenWebLink(_code.VerificationUri);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_code != null)
            CopyUserCode();
    }

    private void CopyUserCode()
    {
        if (_code == null)
            return;
        try
        {
            Clipboard.SetText(_code.UserCode);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("GitHubSignInDialog", "The code could not be copied: " + ex.Message);
        }
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        Loaded -= OnDialogLoaded;
        Closed -= OnDialogClosed;
        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch
        {
        }
    }
}
