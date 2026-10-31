using System.Windows;
using Voidstrap.UI.Elements.Base;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class AccountPasswordDialog : WpfUiWindow
{
    private readonly bool _choosing;

    public string? Password { get; private set; }

    public AccountPasswordDialog(bool choosing)
    {
        _choosing = choosing;
        InitializeComponent();
        ConfirmPanel.Visibility = choosing ? Visibility.Visible : Visibility.Collapsed;
        Loaded += Dialog_Loaded;
        Closed += Dialog_Closed;
    }

    private void Dialog_Loaded(object sender, RoutedEventArgs e) => PasswordInput.Focus();

    private void Dialog_Closed(object? sender, EventArgs e)
    {
        Loaded -= Dialog_Loaded;
        Closed -= Dialog_Closed;
        PasswordInput.Clear();
        ConfirmInput.Clear();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string password = PasswordInput.Password;
        if (password.Length == 0 || (_choosing && (password.Length < AccountVault.MinimumPasswordLength || password != ConfirmInput.Password)))
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Password = password;
        DialogResult = true;
    }
}
